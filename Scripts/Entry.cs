using BaseLib.Config;
using Godot;
using Godot.Bridge;
using GodotFileAccess = Godot.FileAccess;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ClickToPlay.Scripts;

[ModInitializer("Init")]
public static class Entry
{
    public static void Init()
    {
        var hasBaseLibConfig = GodotFileAccess.FileExists("user://mod_configs/ClickToPlay.cfg");
        var config = new ClickToPlayConfig();
        if (!hasBaseLibConfig)
        {
            config.MigrateLegacySettings();
        }

        ModConfigRegistry.Register("ClickToPlay", config);

        var harmony = new Harmony("sts2.llxs.clicktoplay");
        harmony.PatchAll();
        HookCompatibility.PatchTurnEndHook(harmony);
        TargetLockCompatibility.PatchTargetingHooks(harmony);

        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);
        Log.Info("ClickToPlay initialized v0.6.6");
    }
}
