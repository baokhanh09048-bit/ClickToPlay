using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rooms;

namespace ClickToPlay.Scripts;

/// <summary>
/// Locked enemy target.
///
/// When a combat starts the left-most enemy is locked automatically and a lock box is drawn around it.
/// The mouse wheel cycles the lock between living enemies and the middle mouse button releases it.
/// While the lock is held, every card (or potion) that asks for a target is resolved on the locked enemy,
/// so the card is played without a manual target selection.
/// </summary>
public static class TargetLockState
{
    private const float BoxPadding = 8f;
    private const int BoxBorderWidth = 3;
    private const int BoxCornerRadius = 6;
    private const string BoxThemeType = "panel";
    private const ulong PulsePeriodMsec = 1200;

    private static readonly Color BoxColor = new(1f, 0.72f, 0.18f);

    /// <summary>
    /// <see cref="NTargetManager"/> resolves the awaiting <c>StartTargeting</c> caller through this field,
    /// so filling it in lets us answer a targeting request without opening the selection UI.
    /// </summary>
    private static readonly FieldInfo? CompletionSourceField =
        AccessTools.Field(typeof(NTargetManager), "_completionSource");

    private static Creature? _target;
    private static bool _releasedByUser;
    private static Control? _box;
    private static bool _boxFailed;
    private static bool _errorLogged;

    /// <summary>The master setting is on, combat is running and the player has not released the lock.</summary>
    public static bool IsLockOn =>
        ClickToPlayConfig.EnableTargetLock && !_releasedByUser && GetActiveCombatRoom() != null;

    /// <summary>The locked creature, or null when there is nothing valid to lock.</summary>
    public static Creature? LockedTarget => ResolveTarget();

    public static void OnCombatRoomReady()
    {
        _target = null;
        _releasedByUser = false;
        HideBox();
    }

    /// <summary>
    /// Returns the locked enemy when a card that needs a target may be played on it.
    /// </summary>
    public static bool TryGetLockedTarget(out Creature? target)
    {
        target = null;
        if (!IsLockOn)
        {
            return false;
        }

        var locked = ResolveTarget();
        if (locked == null || !IsTargetable(locked))
        {
            return false;
        }

        target = locked;
        return true;
    }

    /// <summary>
    /// Moves the lock onto a creature the player interacted with directly, e.g. an enemy click.
    /// </summary>
    public static void RetargetTo(Creature? creature)
    {
        if (!IsLockOn || !IsLockableEnemy(creature))
        {
            return;
        }

        _target = creature;
    }

    /// <summary>
    /// Locks the enemy the player clicked on. Used when clicking an enemy no longer plays a card, so the click
    /// picks the target instead. An explicit click also takes the lock back from the middle mouse button release.
    /// </summary>
    public static bool TryLockTo(Creature? creature)
    {
        var room = ClickToPlayConfig.EnableTargetLock ? GetActiveCombatRoom() : null;
        if (room == null || !IsLockableEnemy(creature))
        {
            return false;
        }

        _releasedByUser = false;
        _target = creature;
        UpdateBox(room);
        return true;
    }

    /// <summary>
    /// Runs once per frame from <see cref="NTargetManager._Process"/> to keep the locked enemy and the box up to date.
    /// </summary>
    public static void Tick()
    {
        RunSafely(() =>
        {
            var room = ClickToPlayConfig.EnableTargetLock && !_releasedByUser
                ? GetActiveCombatRoom()
                : null;
            if (room == null)
            {
                ClearTarget();
                return;
            }

            var locked = _target;
            if (locked == null || locked.IsDead || !locked.IsEnemy)
            {
                _target = FindLeftMostEnemy(room);
            }

            UpdateBox(room);
        });
    }

    /// <summary>
    /// Runs from <see cref="NTargetManager._Input"/>. Wheel switches the target, middle click releases the lock.
    /// </summary>
    public static void HandleInput(InputEvent inputEvent)
    {
        RunSafely(() =>
        {
            if (!ClickToPlayConfig.EnableTargetLock)
            {
                return;
            }

            if (inputEvent is not InputEventMouseButton mouseButton || !mouseButton.Pressed)
            {
                return;
            }

            var button = mouseButton.ButtonIndex;
            if (button is not (MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.Middle))
            {
                return;
            }

            if (button != MouseButton.Middle && !ClickToPlayConfig.WheelSwitchesTarget)
            {
                return;
            }

            var room = GetActiveCombatRoom();
            if (room == null || !ActiveScreenContext.Instance.IsCurrent(room))
            {
                return;
            }

            switch (button)
            {
                case MouseButton.WheelUp:
                    CycleLock(room, -1);
                    break;
                case MouseButton.WheelDown:
                    CycleLock(room, 1);
                    break;
                case MouseButton.Middle:
                    ReleaseLock();
                    break;
            }
        });
    }

    /// <summary>
    /// Answers a single enemy targeting request with the locked enemy instead of opening the selection UI.
    /// Returns true when the request was answered, which tells the patched game method to skip its own work.
    /// </summary>
    public static bool TryAutoCompleteTargeting(
        NTargetManager manager,
        TargetType validTargetsType,
        TargetMode startingMode,
        Func<bool>? exitEarlyCondition,
        Func<Node, bool>? nodeFilter)
    {
        var completed = false;

        RunSafely(() =>
        {
            var field = CompletionSourceField;
            if (validTargetsType != TargetType.AnyEnemy
                || manager == null
                || manager.IsInSelection
                || field == null
                || field.FieldType != typeof(TaskCompletionSource<Node>)
                || exitEarlyCondition?.Invoke() == true
                || !IsTargetSelectionReady(manager, startingMode)
                || !TryGetLockedTarget(out var locked)
                || locked == null)
            {
                return;
            }

            var node = NCombatRoom.Instance?.GetCreatureNode(locked);
            if (node == null || !GodotObject.IsInstanceValid(node)
                || nodeFilter?.Invoke(node) == false)
            {
                return;
            }

            var completion = new TaskCompletionSource<Node?>();
            completion.SetResult(node);
            field.SetValue(manager, completion);
            completed = true;
        });

        return completed;
    }

    /// <summary>
    /// A mouse driven card play is only played once the pointer sits inside the play zone, and a card started from a
    /// keyboard shortcut is never dragged up there. Those requests are left to the game unless the pointer already is
    /// in the play zone, so a shortcut play keeps working instead of being cancelled. Dragged cards, potions and
    /// controller input are always answered.
    /// </summary>
    private static bool IsTargetSelectionReady(NTargetManager manager, TargetMode startingMode)
    {
        if (startingMode != TargetMode.ClickMouseToTarget)
        {
            return true;
        }

        var hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand == null || !hand.InCardPlay)
        {
            return true;
        }

        var viewport = manager.GetViewport();
        if (viewport == null)
        {
            return true;
        }

        // Mirrors NMouseCardPlay.PlayZoneThreshold for a card play that was started with a shortcut.
        const float playZoneScreenProportion = 0.75f;
        const float fakeLowerEnterPlayZoneDistance = 100f;
        var playZoneThreshold = viewport.GetVisibleRect().Size.Y * playZoneScreenProportion
            + fakeLowerEnterPlayZoneDistance;
        return viewport.GetMousePosition().Y <= playZoneThreshold;
    }

    private static void CycleLock(NCombatRoom room, int direction)
    {
        var enemies = GetLivingEnemyNodes(room);
        if (enemies.Count == 0)
        {
            ClearTarget();
            return;
        }

        var currentIndex = enemies.FindIndex(node => node.Entity == _target);
        int nextIndex = currentIndex < 0
            ? (direction >= 0 ? 0 : enemies.Count - 1)
            : ((currentIndex + direction) % enemies.Count + enemies.Count) % enemies.Count;

        _releasedByUser = false;
        _target = enemies[nextIndex].Entity;
        UpdateBox(room);
    }

    private static void ReleaseLock()
    {
        _releasedByUser = true;
        ClearTarget();
    }

    private static NCombatRoom? GetActiveCombatRoom()
    {
        var room = NCombatRoom.Instance;
        if (room == null || !GodotObject.IsInstanceValid(room) || room.Mode != CombatRoomMode.ActiveCombat)
        {
            return null;
        }

        var combatManager = CombatManager.Instance;
        if (!combatManager.IsInProgress || combatManager.IsOverOrEnding)
        {
            return null;
        }

        return room;
    }

    private static Creature? ResolveTarget()
    {
        var target = _target;
        if (target == null || target.IsDead || !target.IsEnemy)
        {
            return null;
        }

        return target;
    }

    private static bool IsTargetable(Creature creature)
    {
        var combatState = creature.CombatState;
        return combatState != null && Hook.ShouldAllowTargeting(combatState, creature, out _);
    }

    private static bool IsLockableEnemy(Creature? creature)
    {
        return creature != null && creature.IsEnemy && !creature.IsDead;
    }

    private static Creature? FindLeftMostEnemy(NCombatRoom room)
    {
        Creature? result = null;
        var leftMost = float.MaxValue;

        foreach (var node in GetLivingEnemyNodes(room))
        {
            var x = node.GlobalPosition.X;
            if (result == null || x < leftMost)
            {
                result = node.Entity;
                leftMost = x;
            }
        }

        return result;
    }

    private static List<NCreature> GetLivingEnemyNodes(NCombatRoom room)
    {
        var enemies = new List<NCreature>();
        foreach (var node in room.CreatureNodes)
        {
            if (node == null || !GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            var entity = node.Entity;
            if (entity == null || !entity.IsEnemy || entity.IsDead)
            {
                continue;
            }

            enemies.Add(node);
        }

        enemies.Sort((left, right) => left.GlobalPosition.X.CompareTo(right.GlobalPosition.X));
        return enemies;
    }

    /// <summary>
    /// The lock box is only a marker for choosing between several enemies, so it stays hidden while a single
    /// enemy is on the field.
    /// </summary>
    private static bool HasSeveralLivingEnemies(NCombatRoom room)
    {
        var found = 0;
        foreach (var node in room.CreatureNodes)
        {
            if (node == null || !GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            var entity = node.Entity;
            if (entity == null || !entity.IsEnemy || entity.IsDead)
            {
                continue;
            }

            if (++found > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearTarget()
    {
        _target = null;
        HideBox();
    }

    private static void UpdateBox(NCombatRoom room)
    {
        var creature = ResolveTarget();
        var node = creature == null || !HasSeveralLivingEnemies(room) ? null : room.GetCreatureNode(creature);
        if (node == null || !GodotObject.IsInstanceValid(node))
        {
            HideBox();
            return;
        }

        var hitbox = node.Hitbox;
        if (hitbox == null || !GodotObject.IsInstanceValid(hitbox))
        {
            HideBox();
            return;
        }

        var keepBox = _box != null
            && GodotObject.IsInstanceValid(_box)
            && (_box.GetParent() == node || (!_box.IsInsideTree() && _box.GetParent() == null));
        if (!keepBox)
        {
            HideBox();
            _box = CreateBox(node);
        }

        if (_box == null || !GodotObject.IsInstanceValid(_box) || !_box.IsInsideTree())
        {
            return;
        }

        _box.Size = hitbox.Size + Vector2.One * (BoxPadding * 2f);
        _box.GlobalPosition = hitbox.GlobalPosition - Vector2.One * BoxPadding;
        _box.Modulate = new Color(1f, 1f, 1f, CurrentPulseAlpha());
        _box.Visible = true;
    }

    private static Control? CreateBox(NCreature creature)
    {
        if (_boxFailed)
        {
            return null;
        }

        try
        {
            var style = new StyleBoxFlat
            {
                BgColor = new Color(0f, 0f, 0f, 0f),
                DrawCenter = false,
                BorderColor = BoxColor,
                BorderWidthLeft = BoxBorderWidth,
                BorderWidthTop = BoxBorderWidth,
                BorderWidthRight = BoxBorderWidth,
                BorderWidthBottom = BoxBorderWidth,
                CornerRadiusTopLeft = BoxCornerRadius,
                CornerRadiusTopRight = BoxCornerRadius,
                CornerRadiusBottomRight = BoxCornerRadius,
                CornerRadiusBottomLeft = BoxCornerRadius,
                AntiAliasing = true,
            };

            var box = new Panel
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                FocusMode = Control.FocusModeEnum.None,
                Visible = false,
            };
            box.AddThemeStyleboxOverride(BoxThemeType, style);
            creature.AddChildSafely(box);
            return box;
        }
        catch (Exception e)
        {
            _boxFailed = true;
            Log.Warn($"[ClickToPlay] Failed to create the target lock box, the lock marker is disabled: {e}");
            return null;
        }
    }

    private static void HideBox()
    {
        var box = _box;
        _box = null;
        if (box != null && GodotObject.IsInstanceValid(box))
        {
            box.QueueFreeSafely();
        }
    }

    private static float CurrentPulseAlpha()
    {
        var phase = (float)(Time.GetTicksMsec() % PulsePeriodMsec) / PulsePeriodMsec;
        return 0.65f + 0.35f * (0.5f + 0.5f * Mathf.Sin(phase * Mathf.Tau));
    }

    private static void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            if (_errorLogged)
            {
                return;
            }

            _errorLogged = true;
            Log.Warn($"[ClickToPlay] Target lock hit an unexpected error: {e}");
        }
    }
}
