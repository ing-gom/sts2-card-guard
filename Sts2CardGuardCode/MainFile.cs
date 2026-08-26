using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2CardGuard;

/// <summary>
/// Entry point. Installs Harmony patches that filter what a run can offer, all before the game's RNG
/// picks, so nothing needs to be un-picked afterwards:
///
///   • cards   — the candidate pool used by rewards, shops, events and in-combat generation
///               (<see cref="CardGuardService"/>);
///   • relics  — the run's relic grab bags plus direct event grants (<see cref="RelicGuardService"/>);
///   • potions — every pool random potion generation draws from (<see cref="PotionGuardService"/>);
///   • events  — the act's event list, by continuing the game's own skip walk
///               (<see cref="EventGuardService"/>).
///
/// Everything defaults to ALLOWED and is configured from the character-select screen's Content Filter
/// button; nothing depends on ModConfig.
/// </summary>
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Sts2CardGuard";
    public const string Version = "v0.9.0";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; }
        = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        try
        {
            // Apply saved settings before patches start filtering, so the very first screen honors them.
            CardGuardConfig.Load();

            var harmony = new Harmony(ModId);
            harmony.PatchAll(typeof(MainFile).Assembly);

            // One target's return type differs between game branches, so its __result cannot be
            // spelled in an attribute — see PotionFactory_CreateRandomPotions_FullBan_Patch. Applied
            // after PatchAll so a failure here cannot take the attribute-driven patches with it.
            Patches.PotionFactory_CreateRandomPotions_FullBan_Patch.Apply(harmony);
            Patches.PotionFactory_StarvedDraw_Patch.Apply(harmony);

            Logger.Info($"[{ModId}] Harmony patches applied.");

            // Register UI strings for the current language now; the SetLanguage patch re-applies on change.
            try { Loc.Apply(MegaCrit.Sts2.Core.Localization.LocManager.Instance); } catch { }

            Logger.Info($"[{ModId}] initialized ({Version}). Default policy: everything allowed. "
                        + "Settings: character select → 'Content Filter' button (bottom right).");

#if DEBUG
            // No-op unless a matching sentinel sits next to the mod DLL (solo-verify / coop-verify).
            // Debug-only: the test scaffolding is compiled out of Release/Workshop builds.
            SoloTest.ArmIfRequested();
            CoopTest.ArmIfRequested();
#endif
        }
        catch (Exception ex)
        {
            Logger.Warn($"[{ModId}] init failed: {ex.Message}");
        }
    }
}
