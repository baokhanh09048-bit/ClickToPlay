using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rooms;

namespace ClickToPlay.Scripts;

/// <summary>
/// Right clicking an empty spot of the battlefield ends the turn, so a hand played out with the mouse does not
/// require travelling to the end turn button or the keyboard.
///
/// The gesture reuses the end turn button itself, so it is only accepted while the game offers that button, and
/// every click that lands on a monster, a hand card, any clickable UI or another screen blocks it.
/// </summary>
public static class RightClickEndTurn
{
    private static bool _errorLogged;

    public static void HandleInput(InputEvent inputEvent)
    {
        try
        {
            Handle(inputEvent);
        }
        catch (Exception e)
        {
            if (_errorLogged)
            {
                return;
            }

            _errorLogged = true;
            Log.Warn($"[ClickToPlay] Right click end turn hit an unexpected error: {e}");
        }
    }

    private static void Handle(InputEvent inputEvent)
    {
        if (!ClickToPlayConfig.EnableRightClickEndTurn)
        {
            return;
        }

        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        {
            return;
        }

        var room = NCombatRoom.Instance;
        if (room == null || !GodotObject.IsInstanceValid(room) || room.Mode != CombatRoomMode.ActiveCombat)
        {
            return;
        }

        if (!ActiveScreenContext.Instance.IsCurrent(room))
        {
            return;
        }

        var ui = room.Ui;
        var button = ui?.EndTurnButton;
        if (button == null || !GodotObject.IsInstanceValid(button) || !button.IsEnabled)
        {
            return;
        }

        var hand = ui!.Hand;
        if (hand == null || !GodotObject.IsInstanceValid(hand))
        {
            return;
        }

        // A right click already cancels a dragged card or a target selection, so never end the turn then.
        if (hand.InCardPlay
            || hand.CurrentMode != NPlayerHand.Mode.Play
            || hand.Get(NPlayerHand.PropertyName.HasDraggedHolder).AsBool()
            || hand.PeekButton?.IsPeeking == true)
        {
            return;
        }

        var targets = NTargetManager.Instance;
        if (targets.IsInSelection || targets.LastTargetingFinishedFrame == room.GetTree().GetFrame())
        {
            return;
        }

        if (!IsEmptyBattlefieldSpot(room, hand, room.GetViewport().GetMousePosition()))
        {
            return;
        }

        button.CallReleaseLogic();
    }

    private static bool IsEmptyBattlefieldSpot(NCombatRoom room, NPlayerHand hand, Vector2 mousePosition)
    {
        // The card strip at the bottom is never an empty battlefield spot.
        var cardArea = hand.CardHolderContainer;
        if (cardArea != null && GodotObject.IsInstanceValid(cardArea))
        {
            var cardRect = cardArea.GetGlobalRect();
            if (cardRect.Size.X > 1f && cardRect.Size.Y > 1f && cardRect.HasPoint(mousePosition))
            {
                return false;
            }
        }

        // Monsters block the gesture even when their hitbox does not take mouse input, for example while a
        // creature cannot be interacted with.
        foreach (var creatureNode in room.CreatureNodes)
        {
            if (creatureNode == null || !GodotObject.IsInstanceValid(creatureNode))
            {
                continue;
            }

            var entity = creatureNode.Entity;
            if (entity == null || !entity.IsEnemy || entity.IsDead)
            {
                continue;
            }

            var hitbox = creatureNode.Hitbox;
            if (hitbox != null && GodotObject.IsInstanceValid(hitbox) && hitbox.GetGlobalRect().HasPoint(mousePosition))
            {
                return false;
            }
        }

        // Whatever the game itself would treat as a click target blocks the gesture, and so does every screen
        // element that is not part of the combat room (top bar, relic inventory, overlays...).
        var hovered = room.GetViewport().GuiGetHoveredControl();
        if (hovered == null)
        {
            return true;
        }

        for (Node? node = hovered; node != null; node = node.GetParent())
        {
            if (node == room)
            {
                return true;
            }

            if (node is NCreature creature)
            {
                if (creature.Entity is { IsEnemy: true, IsDead: false })
                {
                    return false;
                }
            }
            else if (node is NClickableControl or NPlayerHand or NHandCardHolder)
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>
/// Reuses the target manager input hook, which receives every mouse event during a run.
/// </summary>
[HarmonyPatch(typeof(NTargetManager), nameof(NTargetManager._Input))]
public static class NTargetManager_Input_RightClickEndTurn_Patch
{
    public static void Prefix(InputEvent inputEvent)
    {
        RightClickEndTurn.HandleInput(inputEvent);
    }
}
