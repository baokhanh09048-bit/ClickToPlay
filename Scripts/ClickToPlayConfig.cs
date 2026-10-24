using System.Text.Json;
using BaseLib.Config;
using Godot;
using GodotFileAccess = Godot.FileAccess;

namespace ClickToPlay.Scripts;

public sealed class ClickToPlayConfig : SimpleModConfig
{
    public const int MinAutoEndTurnSettleFrames = 3;
    public const int MaxAutoEndTurnSettleFrames = 60;

    private const string LegacyConfigPath = "user://ModConfig/ClickToPlay.json";

    [ConfigSection("QuickPlaySettings")]
    [ConfigHoverTip]
    public static bool UseRightClickToPlay { get; set; } = false;

    [ConfigHoverTip]
    public static bool EnableShortcutQuickPlay { get; set; } = true;

    [ConfigSection("ClickEnemySettings")]
    [ConfigHoverTip]
    public static bool EnableClickEnemyAttack { get; set; } = false;

    [ConfigSection("TargetLockSettings")]
    [ConfigHoverTip]
    public static bool EnableTargetLock { get; set; } = true;

    [ConfigHoverTip]
    public static bool WheelSwitchesTarget { get; set; } = true;

    [ConfigSection("CardSelectionSettings")]
    [ConfigHoverTip]
    public static bool EnableAutoConfirmCardSelection { get; set; } = true;

    [ConfigSection("AutoEndTurnSettings")]
    [ConfigHoverTip]
    public static bool EnableAutoEndTurn { get; set; } = true;

    [ConfigHoverTip]
    public static bool RequireNoPotionsForAutoEndTurn { get; set; } = true;

    [ConfigSection("RightClickEndTurnSettings")]
    [ConfigHoverTip]
    public static bool EnableRightClickEndTurn { get; set; } = false;

    [ConfigSlider(MinAutoEndTurnSettleFrames, MaxAutoEndTurnSettleFrames, 1, Format = "{0:0}")]
    [ConfigHoverTip]
    public static int AutoEndTurnSettleFrames
    {
        get => _autoEndTurnSettleFrames;
        set => _autoEndTurnSettleFrames = Math.Clamp(
            value,
            MinAutoEndTurnSettleFrames,
            MaxAutoEndTurnSettleFrames);
    }

    private static int _autoEndTurnSettleFrames = 10;

    /// <summary>
    /// Imports settings written by the former ModConfig integration once, before BaseLib saves them.
    /// </summary>
    public void MigrateLegacySettings()
    {
        if (!GodotFileAccess.FileExists(LegacyConfigPath))
            return;

        try
        {
            using var file = GodotFileAccess.Open(LegacyConfigPath, GodotFileAccess.ModeFlags.Read);
            if (file == null)
                return;

            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(file.GetAsText());
            if (values == null)
                return;

            ApplyBool(values, "enableClickEnemyAttack", value => EnableClickEnemyAttack = value);
            ApplyBool(values, "enableAutoEndTurn", value => EnableAutoEndTurn = value);
            ApplyBool(values, "requireNoPotionsForAutoEndTurn", value => RequireNoPotionsForAutoEndTurn = value);

            if (values.TryGetValue("autoEndTurnSettleFrames", out var settleFrames) &&
                settleFrames.TryGetSingle(out var settleFramesValue))
            {
                AutoEndTurnSettleFrames = (int)MathF.Round(settleFramesValue);
            }

            Save();
            GD.Print("[ClickToPlay] Migrated legacy ModConfig settings to BaseLib.");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[ClickToPlay] Failed to migrate legacy ModConfig settings: {e}");
        }
    }

    private static void ApplyBool(
        IReadOnlyDictionary<string, JsonElement> values,
        string key,
        Action<bool> setter)
    {
        if (values.TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            setter(value.GetBoolean());
    }
}
