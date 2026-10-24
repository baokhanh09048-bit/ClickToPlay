using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace ClickToPlay.Scripts;

/// <summary>
/// Resets the lock whenever a new combat room is created, so every combat starts locked on the left-most enemy.
/// </summary>
[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom._Ready))]
public static class NCombatRoom_Ready_TargetLock_Patch
{
    public static void Postfix()
    {
        TargetLockState.OnCombatRoomReady();
    }
}

/// <summary>
/// Drives the per-frame part of the lock (target validation and the lock box) from a node that lives for the whole run.
/// </summary>
[HarmonyPatch(typeof(NTargetManager), nameof(NTargetManager._Process))]
public static class NTargetManager_Process_TargetLock_Patch
{
    public static void Prefix()
    {
        TargetLockState.Tick();
    }
}

/// <summary>
/// Wheel cycles the locked enemy, middle mouse button releases the lock.
/// </summary>
[HarmonyPatch(typeof(NTargetManager), nameof(NTargetManager._Input))]
public static class NTargetManager_Input_TargetLock_Patch
{
    public static void Prefix(InputEvent inputEvent)
    {
        TargetLockState.HandleInput(inputEvent);
    }
}

/// <summary>
/// Patched by name so a game update that changes the targeting signature only disables the auto targeting
/// instead of breaking every other patch of this mod. The target type is read through <c>__args</c> so the patch
/// does not depend on the game's parameter names.
/// </summary>
public static class TargetLockStartTargetingPatch
{
    public static bool Prefix(NTargetManager __instance, object[] __args)
    {
        if (__args.Length != 5
            || __args[0] is not TargetType validTargetsType
            || __args[2] is not TargetMode startingMode)
        {
            return true;
        }

        return !TargetLockState.TryAutoCompleteTargeting(
            __instance, validTargetsType, startingMode,
            __args[3] as Func<bool>, __args[4] as Func<Node, bool>);
    }
}

public static class TargetLockCompatibility
{
    private const string StartTargetingMethodName = "StartTargeting";

    private static readonly Type[] StartTargetModes =
    [
        typeof(Control),
        typeof(Vector2),
    ];

    public static void PatchTargetingHooks(Harmony harmony)
    {
        var prefix = AccessTools.Method(
            typeof(TargetLockStartTargetingPatch),
            nameof(TargetLockStartTargetingPatch.Prefix));

        if (prefix == null)
        {
            throw new MissingMethodException(
                typeof(TargetLockStartTargetingPatch).FullName,
                nameof(TargetLockStartTargetingPatch.Prefix));
        }

        var harmonyMethod = new HarmonyMethod(prefix);
        var patched = 0;

        foreach (var method in typeof(NTargetManager).GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.Name != StartTargetingMethodName || !MatchesTargetingSignature(method))
            {
                continue;
            }

            try
            {
                harmony.Patch(method, prefix: harmonyMethod);
                patched++;
            }
            catch (Exception e)
            {
                Log.Warn($"[ClickToPlay] Could not hook {method}: {e}");
            }
        }

        if (patched == 0)
        {
            Log.Warn("[ClickToPlay] NTargetManager.StartTargeting was not found; the target lock will not auto-play targeted cards.");
            return;
        }

        Log.Info($"[ClickToPlay] Target lock hooked into {patched} StartTargeting variant(s).");
    }

    private static bool MatchesTargetingSignature(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return parameters.Length == 5
            && parameters[0].ParameterType == typeof(TargetType)
            && StartTargetModes.Contains(parameters[1].ParameterType)
            && parameters[2].ParameterType == typeof(TargetMode)
            && parameters[3].ParameterType == typeof(Func<bool>)
            && parameters[4].ParameterType == typeof(Func<Node, bool>);
    }
}
