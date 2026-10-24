using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace ClickToPlay.Scripts;

public static class AutoEndTurnHelper
{
    private static readonly HashSet<ulong> PlayersAutoEndingTurn = new();
    private static readonly HashSet<ulong> PlayersPendingAutoEndCheck = new();

    public static Task RequestAutoEndTurn(Player player)
    {
        if (!ClickToPlayConfig.EnableAutoEndTurn)
        {
            ClearAutoEnding(player);
            return Task.CompletedTask;
        }

        if (!LocalContext.IsMe(player) || player.PlayerCombatState == null)
        {
            return Task.CompletedTask;
        }

        if (!PlayersPendingAutoEndCheck.Add(player.NetId))
        {
            return Task.CompletedTask;
        }

        TaskHelper.RunSafely(RunDelayedAutoEndTurnCheck(player));
        return Task.CompletedTask;
    }

    private static async Task RunDelayedAutoEndTurnCheck(Player player)
    {
        try
        {
            await WaitForActionAndUiToSettle();
            await TryAutoEndTurn(player);
        }
        finally
        {
            PlayersPendingAutoEndCheck.Remove(player.NetId);
        }
    }

    private static async Task WaitForActionAndUiToSettle()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        int stableFrames = 0;
        int requiredStableFrames = ClickToPlayConfig.AutoEndTurnSettleFrames;
        while (stableFrames < requiredStableFrames)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null)
            {
                stableFrames++;
            }
            else
            {
                stableFrames = 0;
            }
        }
    }

    private static Task TryAutoEndTurn(Player player)
    {
        if (!CanAutoEndTurnNow(player))
        {
            ClearAutoEnding(player);
            return Task.CompletedTask;
        }

        if (!PlayersAutoEndingTurn.Add(player.NetId))
        {
            return Task.CompletedTask;
        }

        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
        return Task.CompletedTask;
    }

    public static bool IsAutoEnding(Player player)
    {
        return PlayersAutoEndingTurn.Contains(player.NetId);
    }

    public static bool CanAutoEndTurnNow(Player player)
    {
        if (!ClickToPlayConfig.EnableAutoEndTurn)
        {
            return false;
        }

        if (!LocalContext.IsMe(player) || player.PlayerCombatState == null)
        {
            return false;
        }

        var combatState = player.Creature.CombatState;
        if (combatState == null
            || combatState.CurrentSide != CombatSide.Player
            || CombatManager.Instance.PlayerActionsDisabled
            || CombatManager.Instance.IsPlayerReadyToEndTurn(player)
            || CombatManager.Instance.IsEnding
            || NTargetManager.Instance.IsInSelection)
        {
            return false;
        }

        var hand = NCombatRoom.Instance?.Ui.Hand;
        if (hand == null || hand.CurrentMode != NPlayerHand.Mode.Play || hand.InCardPlay)
        {
            return false;
        }

        if (HasPlayableCard(player)
            || (ClickToPlayConfig.RequireNoPotionsForAutoEndTurn && HasPotion(player)))
        {
            return false;
        }

        return true;
    }

    public static void ClearAutoEnding(Player player)
    {
        PlayersAutoEndingTurn.Remove(player.NetId);
        PlayersPendingAutoEndCheck.Remove(player.NetId);
    }

    private static bool HasPlayableCard(Player player)
    {
        return player.PlayerCombatState?.Hand.Cards.Any(card => card.CanPlay()) == true;
    }

    private static bool HasPotion(Player player)
    {
        return player.Potions.Any(potion => !potion.HasBeenRemovedFromState);
    }
}

[HarmonyPatch(typeof(EndPlayerTurnAction), "ExecuteAction")]
public static class EndPlayerTurnAction_ExecuteAction_AutoEndTurnGuard_Patch
{
    public static bool Prefix(EndPlayerTurnAction __instance, ref Task __result)
    {
        if (AccessTools.Field(typeof(EndPlayerTurnAction), "_player").GetValue(__instance) is not Player player
            || !AutoEndTurnHelper.IsAutoEnding(player))
        {
            return true;
        }

        if (!AutoEndTurnHelper.CanAutoEndTurnNow(player))
        {
            AutoEndTurnHelper.ClearAutoEnding(player);
            __result = Task.CompletedTask;
            return false;
        }

        AutoEndTurnHelper.ClearAutoEnding(player);
        return true;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
public static class CardModel_OnPlayWrapper_AutoEndTurn_Patch
{
    public static void Postfix(CardModel __instance, ref Task __result)
    {
        __result = TryAutoEndTurnAfterOriginal(__instance.Owner, __result);
    }

    private static async Task TryAutoEndTurnAfterOriginal(Player player, Task original)
    {
        await original;
        await AutoEndTurnHelper.RequestAutoEndTurn(player);
    }
}

[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.OnUseWrapper))]
public static class PotionModel_OnUseWrapper_AutoEndTurn_Patch
{
    public static void Postfix(PotionModel __instance, ref Task __result)
    {
        __result = TryAutoEndTurnAfterOriginal(__instance.Owner, __result);
    }

    private static async Task TryAutoEndTurnAfterOriginal(Player player, Task original)
    {
        await original;
        await AutoEndTurnHelper.RequestAutoEndTurn(player);
    }
}

[HarmonyPatch(typeof(DiscardPotionGameAction), "ExecuteAction")]
public static class DiscardPotionGameAction_ExecuteAction_AutoEndTurn_Patch
{
    public static void Postfix(DiscardPotionGameAction __instance, ref Task __result)
    {
        __result = TryAutoEndTurnAfterOriginal(__instance, __result);
    }

    private static async Task TryAutoEndTurnAfterOriginal(DiscardPotionGameAction action, Task original)
    {
        await original;
        if (AccessTools.Field(typeof(DiscardPotionGameAction), "_player").GetValue(action) is Player player)
        {
            await AutoEndTurnHelper.RequestAutoEndTurn(player);
        }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
public static class Hook_AfterPlayerTurnStart_AutoEndTurn_Patch
{
    public static void Postfix(Player player, ref Task __result)
    {
        __result = TryAutoEndTurnAfterOriginal(player, __result);
    }

    private static async Task TryAutoEndTurnAfterOriginal(Player player, Task original)
    {
        await original;
        AutoEndTurnHelper.ClearAutoEnding(player);
        await AutoEndTurnHelper.RequestAutoEndTurn(player);
    }
}


