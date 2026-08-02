using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2CardGuard;

/// <summary>
/// Entry point. Installs Harmony patches that filter the candidate card pool used by card
/// rewards, shops, events, and in-combat card generation so that only the current character's
/// cards (+ colorless, + any cross-allowed characters) and non-foreign-mod cards can appear.
/// Behaviour is configurable in-game via ModConfig; defaults are strict (own character +
/// colorless only). See <see cref="CardGuardService"/>.
/// </summary>
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Sts2CardGuard";
    public const string Version = "v0.5.3";

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
            Logger.Info($"[{ModId}] Harmony patches applied.");

            // Register UI strings for the current language now; the SetLanguage patch re-applies on change.
            try { Loc.Apply(MegaCrit.Sts2.Core.Localization.LocManager.Instance); } catch { }

            Logger.Info($"[{ModId}] initialized ({Version}). Default policy: current character + colorless only. "
                        + "Settings: main menu → 'Card Guard' button (below the play button).");

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
