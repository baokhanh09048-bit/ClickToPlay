using Godot;
using HarmonyLib;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace ClickToPlay.Scripts;

public static class QuickPlayState
{
    // Use the same input gate as dragging, including temporary mod input locks and extra turns.
    private static readonly Func<NPlayerHand, bool> AreCardActionsAllowed =
        AccessTools.MethodDelegate<Func<NPlayerHand, bool>>(
            AccessTools.Method(typeof(NPlayerHand), "AreCardActionsAllowed"));

    private static readonly HashSet<CardModel> QueuedCards = new();
    private static readonly HashSet<ulong> PlayersEndingTurn = new();
    private static ulong SuppressQuickPlayUntilMsec;

    public static void SuppressBriefly()
    {
        SuppressQuickPlayUntilMsec = Time.GetTicksMsec() + 150;
    }

    public static bool CanQuickPlay(Player player, ICombatState combatState)
    {
        var hand = NCombatRoom.Instance?.Ui.Hand;

        return !PlayersEndingTurn.Contains(player.NetId)
            && Time.GetTicksMsec() > SuppressQuickPlayUntilMsec
            && combatState.CurrentSide == CombatSide.Player
            && !CombatManager.Instance.IsPlayerReadyToEndTurn(player)
            && !CombatManager.Instance.IsEnding
            && !IsTargetingActiveOrJustFinished()
            && hand != null
            && AreCardActionsAllowed(hand)
            && (NOverlayStack.Instance?.ScreenCount ?? 0) == 0
            && hand.CurrentMode == NPlayerHand.Mode.Play
            && !hand.InCardPlay
            && !hand.Get(NPlayerHand.PropertyName.HasDraggedHolder).AsBool()
            && !NTargetManager.Instance.IsInSelection;
    }

    private static bool IsTargetingActiveOrJustFinished()
    {
        var targetManager = NTargetManager.Instance;
        return targetManager.IsInSelection
            || targetManager.LastTargetingFinishedFrame == targetManager.GetTree().GetFrame();
    }

    public static bool TryMarkQueued(CardModel card)
    {
        return QueuedCards.Add(card);
    }

    public static void ClearQueued(CardModel card)
    {
        QueuedCards.Remove(card);
    }

    public static void ClearQueuedFor(Player player)
    {
        QueuedCards.RemoveWhere(card => card.Owner == player);
    }

    public static void MarkPlayersEndingTurn(ICombatState combatState)
    {
        foreach (var player in combatState.Players)
        {
            PlayersEndingTurn.Add(player.NetId);
        }
    }

    public static void ClearPlayerEndingTurn(Player player)
    {
        PlayersEndingTurn.Remove(player.NetId);
    }
}

public static class QuickPlayHelper
{
    public static MouseButton ConfiguredMouseButton => ClickToPlayConfig.UseRightClickToPlay
        ? MouseButton.Right
        : MouseButton.Left;

    public static bool TryQuickPlayCard(CardModel? card, Creature? target)
    {
        if (card == null || !QuickPlayState.TryMarkQueued(card))
        {
            return false;
        }

        try
        {
            if (card.TryManualPlay(target))
            {
                return true;
            }
        }
        catch
        {
            QuickPlayState.ClearQueued(card);
            throw;
        }

        QuickPlayState.ClearQueued(card);
        return false;
    }

    public static bool TryGetAutoTarget(CardModel card, out Creature? target)
    {
        target = null;
        var combatState = card.CombatState ?? card.Owner?.Creature.CombatState;
        if (combatState == null)
        {
            return false;
        }

        switch (card.TargetType)
        {
            case TargetType.Self:
            case TargetType.None:
            case TargetType.AllEnemies:
            case TargetType.RandomEnemy:
                return true;
            case TargetType.AnyEnemy:
                if (TargetLockState.IsLockOn)
                {
                    return TargetLockState.TryGetLockedTarget(out target);
                }

                target = combatState.HittableEnemies.Take(2).ToList() is { Count: 1 } enemies ? enemies[0] : null;
                return target != null;
            default:
                return false;
        }
    }

    public static bool TryQuickPlayHolder(NHandCardHolder handHolder)
    {
        var card = handHolder.CardModel;
        var player = card?.Owner;
        var combatState = card?.CombatState ?? player?.Creature.CombatState;
        return card != null
            && player != null
            && combatState != null
            && QuickPlayState.CanQuickPlay(player, combatState)
            && TryGetAutoTarget(card, out var target)
            && card.CanPlayTargeting(target)
            && TryQuickPlayCard(card, target);
    }
}

[HarmonyPatch(typeof(NCardPlay), "TryPlayCard")]
public static class NCardPlay_TryPlayCard_QuickPlay_Patch
{
    public static void Postfix()
    {
        QuickPlayState.SuppressBriefly();
    }
}

[HarmonyPatch(typeof(NPlayerHand), "OnHolderPressed")]
public static class NPlayerHand_OnHolderPressed_QuickPlay_Patch
{
    public static bool Prefix(NCardHolder holder)
    {
        if (holder is not NHandCardHolder handHolder)
        {
            return true;
        }

        if (ClickToPlayConfig.UseRightClickToPlay)
        {
            return true;
        }

        return !QuickPlayHelper.TryQuickPlayHolder(handHolder);
    }
}

[HarmonyPatch(typeof(NHandCardHolder), "OnMousePressed")]
public static class NHandCardHolder_OnMousePressed_QuickPlay_Patch
{
    public static void Prefix(NHandCardHolder __instance, InputEvent inputEvent)
    {
        if (!ClickToPlayConfig.UseRightClickToPlay
            || inputEvent is not InputEventMouseButton
            {
                ButtonIndex: MouseButton.Right,
                Pressed: true,
            })
        {
            return;
        }

        QuickPlayHelper.TryQuickPlayHolder(__instance);
    }
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
public static class NCreature_Ready_QuickPlay_Patch
{
    public static void Postfix(NCreature __instance)
    {
        __instance.Hitbox.GuiInput += inputEvent => TryPlayAttackOnClick(__instance, inputEvent);
    }

    private static void TryPlayAttackOnClick(NCreature targetNode, InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton mouseButton
            || mouseButton.ButtonIndex != QuickPlayHelper.ConfiguredMouseButton
            || mouseButton.Pressed)
        {
            return;
        }

        var target = targetNode.Entity;
        if (target == null || target.IsPlayer || !target.IsAlive)
        {
            return;
        }

        var combatState = target.CombatState;
        if (combatState == null)
        {
            return;
        }

        var player = LocalContext.GetMe(combatState);
        if (player?.PlayerCombatState == null || !QuickPlayState.CanQuickPlay(player, combatState))
        {
            return;
        }

        if (!ClickToPlayConfig.EnableClickEnemyAttack)
        {
            // Playing an attack by clicking an enemy is turned off, so a deliberate click only picks the target.
            TargetLockState.TryLockTo(target);
            return;
        }

        // Clicking an enemy directly moves the active target lock onto it.
        TargetLockState.RetargetTo(target);

        var attack = FindAttackCard(player, target, combatState, out var playTarget);
        QuickPlayHelper.TryQuickPlayCard(attack, playTarget);
    }

    private static CardModel? FindAttackCard(Player player, Creature clickedTarget, ICombatState combatState, out Creature? playTarget)
    {
        playTarget = null;
        var hand = player.PlayerCombatState?.Hand;
        if (hand == null)
        {
            return null;
        }

        foreach (var card in hand.Cards.Where(card => card.Type == CardType.Attack))
        {
            Creature? candidateTarget = card.TargetType switch
            {
                TargetType.AnyEnemy => clickedTarget,
                TargetType.AllEnemies or TargetType.RandomEnemy => null,
                _ => null,
            };

            if (card.TargetType == TargetType.AnyEnemy && !card.CanPlayTargeting(candidateTarget))
            {
                var enemies = combatState.HittableEnemies.Take(2).ToList();
                candidateTarget = enemies.Count == 1 ? enemies[0] : null;
            }

            if (card.CanPlayTargeting(candidateTarget))
            {
                playTarget = candidateTarget;
                return card;
            }
        }

        return null;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
public static class CardModel_OnPlayWrapper_QuickPlay_Patch
{
    public static void Postfix(CardModel __instance, ref Task __result)
    {
        __result = ClearQueuedAfterOriginal(__instance, __result);
    }

    private static async Task ClearQueuedAfterOriginal(CardModel card, Task original)
    {
        try
        {
            await original;
        }
        finally
        {
            QuickPlayState.ClearQueued(card);
        }
    }
}

public static class HookCompatibility
{
    private static readonly Type[] TurnEndHookParameters =
    [
        typeof(ICombatState),
        typeof(CombatSide),
        typeof(IEnumerable<Creature>),
    ];

    public static void PatchTurnEndHook(Harmony harmony)
    {
        var target = FindTurnEndHook()
            ?? throw new MissingMethodException(typeof(Hook).FullName, "BeforeSideTurnEnd/BeforeTurnEnd");

        var postfix = AccessTools.Method(
            typeof(Hook_BeforeTurnEnd_QuickPlay_Patch),
            nameof(Hook_BeforeTurnEnd_QuickPlay_Patch.Postfix));

        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
    }

    public static MethodInfo? FindTurnEndHook()
    {
        return AccessTools.Method(typeof(Hook), "BeforeSideTurnEnd", TurnEndHookParameters)
            ?? AccessTools.Method(typeof(Hook), "BeforeTurnEnd", TurnEndHookParameters);
    }
}

public static class Hook_BeforeTurnEnd_QuickPlay_Patch
{
    public static void Postfix(ICombatState combatState, CombatSide side, ref Task __result)
    {
        if (side == CombatSide.Player)
        {
            QuickPlayState.MarkPlayersEndingTurn(combatState);
        }

        __result = ClearQueuedAfterOriginal(combatState, side, __result);
    }

    private static async Task ClearQueuedAfterOriginal(ICombatState combatState, CombatSide side, Task original)
    {
        await original;

        if (side != CombatSide.Player)
        {
            return;
        }

        foreach (var player in combatState.Players)
        {
            QuickPlayState.ClearQueuedFor(player);
        }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
public static class Hook_AfterPlayerTurnStart_QuickPlay_Patch
{
    public static void Postfix(Player player)
    {
        QuickPlayState.ClearPlayerEndingTurn(player);
    }
}
