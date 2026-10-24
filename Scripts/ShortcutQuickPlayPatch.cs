using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace ClickToPlay.Scripts;

/// <summary>
/// The number key shortcuts normally only pick a card and start the game's select and drag flow.
/// With this feature the shortcut plays the card right away, exactly like clicking it, so the auto
/// target logic (target lock included) applies to keyboard play as well.
/// </summary>
[HarmonyPatch(typeof(NPlayerHand), "StartCardPlay")]
public static class NPlayerHand_StartCardPlay_ShortcutQuickPlay_Patch
{
    public static bool Prefix(NHandCardHolder holder, bool startedViaShortcut)
    {
        if (!startedViaShortcut || !ClickToPlayConfig.EnableShortcutQuickPlay)
        {
            return true;
        }

        return !QuickPlayHelper.TryQuickPlayHolder(holder);
    }
}
