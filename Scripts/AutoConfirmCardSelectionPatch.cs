using HarmonyLib;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace ClickToPlay.Scripts;

public static class AutoConfirmCardSelection
{
    private static readonly FieldInfo? CompletionSourceField =
        AccessTools.Field(typeof(NPlayerHand), "_selectionCompletionSource");

    private static readonly Action<NPlayerHand> CheckIfSelectionComplete =
        AccessTools.MethodDelegate<Action<NPlayerHand>>(
            AccessTools.Method(typeof(NPlayerHand), "CheckIfSelectionComplete"));

    public static void TryComplete(NPlayerHand hand)
    {
        if (!ClickToPlayConfig.EnableAutoConfirmCardSelection
            || !hand.IsInCardSelection
            || CompletionSourceField?.GetValue(hand) is not TaskCompletionSource<IEnumerable<CardModel>> completion
            || completion.Task.IsCompleted)
        {
            return;
        }

        CheckIfSelectionComplete(hand);
    }
}

[HarmonyPatch(typeof(NPlayerHand), "SelectCardInSimpleMode")]
public static class NPlayerHand_SelectCardInSimpleMode_AutoConfirm_Patch
{
    public static void Postfix(NPlayerHand __instance)
    {
        AutoConfirmCardSelection.TryComplete(__instance);
    }
}

[HarmonyPatch(typeof(NPlayerHand), "SelectCardInUpgradeMode")]
public static class NPlayerHand_SelectCardInUpgradeMode_AutoConfirm_Patch
{
    public static void Postfix(NPlayerHand __instance)
    {
        AutoConfirmCardSelection.TryComplete(__instance);
    }
}
