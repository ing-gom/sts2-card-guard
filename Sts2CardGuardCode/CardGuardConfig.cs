using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2CardGuard;

/// <summary>
/// Loads and saves the mod's settings to a JSON file next to the mod DLL. The custom settings
/// panel is the only editor (there is no ModConfig dependency). The runtime source of truth is
/// <see cref="CardGuardService"/>; this type just mirrors it to/from disk.
///
/// The default is permissive (everything allowed), so the file only records what the user has
/// explicitly BLOCKED. Stored with a <c>.txt</c> extension so the ModManager never mistakes it
/// for a mod manifest.
/// </summary>
internal static class CardGuardConfig
{
    private sealed class Model
    {
        [JsonPropertyName("crossBlock")] public Dictionary<string, List<string>> CrossBlock { get; set; } = new();
        [JsonPropertyName("modBlock")] public Dictionary<string, List<string>> ModBlock { get; set; } = new();
        // Per-character (pool title) blocked relic-mod names. See RelicGuardService.
        [JsonPropertyName("relicModBlock")] public Dictionary<string, List<string>> RelicModBlock { get; set; } = new();
        // Per-character blocked INDIVIDUAL mod card / relic ids (ModelId.ToString()). Absent in
        // configs written before per-item blocking existed — the empty default reads as "none".
        [JsonPropertyName("cardBlock")] public Dictionary<string, List<string>> CardBlock { get; set; } = new();
        [JsonPropertyName("relicBlock")] public Dictionary<string, List<string>> RelicBlock { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string ConfigPath()
    {
        string modDir;
        try { modDir = Path.GetDirectoryName(typeof(CardGuardConfig).Assembly.Location) ?? AppContext.BaseDirectory; }
        catch { modDir = AppContext.BaseDirectory; }
        var dir = Path.Combine(modDir, "Settings");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "card_guard_config.txt");
    }

    /// <summary>Reads the file (if present) and applies it to <see cref="CardGuardService"/>.</summary>
    public static void Load()
    {
        try
        {
            var path = ConfigPath();
            if (!File.Exists(path)) { Log.Info("no saved config; all card packs allowed by default."); return; }

            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(path)) ?? new Model();

            foreach (var (from, tos) in model.CrossBlock)
                foreach (var to in tos)
                    CardGuardService.SetCrossAllowed(from, to, false);

            foreach (var (character, mods) in model.ModBlock)
                foreach (var mod in mods)
                    CardGuardService.SetModAllowed(character, mod, false);

            foreach (var (character, relicMods) in model.RelicModBlock)
                foreach (var relicMod in relicMods)
                    RelicGuardService.SetModAllowed(character, relicMod, false);

            foreach (var (character, cardIds) in model.CardBlock)
                foreach (var cardId in cardIds)
                    CardGuardService.SetCardAllowed(character, cardId, false);

            foreach (var (character, relicIds) in model.RelicBlock)
                foreach (var relicId in relicIds)
                    RelicGuardService.SetRelicAllowed(character, relicId, false);

            Log.Info("config loaded.");
        }
        catch (Exception ex)
        {
            Log.Warn($"config load failed ({ex.Message}); all card packs allowed by default.");
        }
    }

    /// <summary>Snapshots <see cref="CardGuardService"/> to disk. Best-effort.</summary>
    public static void Save()
    {
        try
        {
            var model = new Model();
            foreach (var from in CardGuardService.GetConfiguredTitles())
            {
                var chars = CardGuardService.GetBlockedCharacters(from);
                if (chars.Count > 0) model.CrossBlock[from] = chars;
                var mods = CardGuardService.GetBlockedMods(from);
                if (mods.Count > 0) model.ModBlock[from] = mods;
                var cards = CardGuardService.GetBlockedCards(from);
                if (cards.Count > 0) model.CardBlock[from] = cards;
            }
            foreach (var title in RelicGuardService.GetConfiguredTitles())
            {
                var relicMods = RelicGuardService.GetBlockedMods(title);
                if (relicMods.Count > 0) model.RelicModBlock[title] = relicMods;
                var relics = RelicGuardService.GetBlockedRelics(title);
                if (relics.Count > 0) model.RelicBlock[title] = relics;
            }
            File.WriteAllText(ConfigPath(), JsonSerializer.Serialize(model, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Warn($"config save failed: {ex.Message}");
        }
    }
}
