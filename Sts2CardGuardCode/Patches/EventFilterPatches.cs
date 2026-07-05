using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;

namespace Sts2CardGuard.Patches;

/// <summary>
/// The <c>ColorfulPhilosophers</c> event offers a card reward drawn from ANOTHER character's pool,
/// letting you pick which foreign character via its initial options (one per unlocked non-own base
/// character). The reward cards themselves funnel through <c>CardCreationOptions.GetPossibleCards</c>
/// and are already filtered by <see cref="CardGuardService"/> — but the *options* (which foreign
/// character to choose) are built directly from the unlock state and bypass that hook, so a blocked
/// character still shows up as a choice. That is the event-side counterpart of the Kaleidoscope /
/// Prismatic Gem "other character shows up" complaint.
///
/// This postfix drops options whose target character is blocked for the current character. Each
/// option is matched to its pool by the <c>EnergyColorName</c> suffix the event bakes into the
/// option's <see cref="EventOption.TextKey"/> (<c>"...INITIAL.options.&lt;ENERGYCOLORNAME&gt;"</c>).
/// If every option is blocked the event finishes with no cross-character offer — the game handles a
/// zero-option event by ending it (<c>SetEventState</c> sets <c>_isFinished</c>), so this is safe.
/// Read-only: it only removes options, never adds or reorders, so it can't desync a co-op run
/// beyond what card filtering already does (and it honours the same multiplayer pass-through).
/// </summary>
[HarmonyPatch(typeof(ColorfulPhilosophers), "GenerateInitialOptions")]
internal static class ColorfulPhilosophers_Patch
{
    private static void Postfix(EventModel __instance, ref IReadOnlyList<EventOption> __result)
    {
        try
        {
            if (__result == null || __result.Count == 0) return;
            var player = __instance.Owner;
            var currentPool = CardGuardService.TryGetCurrentPool(player);
            if (currentPool == null) return;

            // Map each unlocked character pool's ENERGYCOLORNAME (as baked into the option TextKey)
            // to its pool title, so we can turn an option back into the character it offers.
            // The event only ever offers the five BASE characters, so restrict the map to base-game
            // pools — otherwise a mod character reusing a base energy color name (e.g. "red") could
            // overwrite the base entry and make us look up the wrong character.
            var baseAsm = typeof(EventModel).Assembly;
            var colorToTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in player!.UnlockState.CharacterCardPools)
            {
                if (p == null || p.IsColorless) continue;
                if (p.GetType().Assembly != baseAsm) continue;
                string color = p.EnergyColorName ?? string.Empty;
                if (color.Length > 0) colorToTitle[color] = p.Title ?? string.Empty;
            }
            if (colorToTitle.Count == 0) return;

            var kept = new List<EventOption>(__result.Count);
            foreach (var opt in __result)
            {
                if (opt == null) continue;
                string key = opt.TextKey ?? string.Empty;
                int dot = key.LastIndexOf('.');
                string color = dot >= 0 ? key.Substring(dot + 1) : key;
                if (colorToTitle.TryGetValue(color, out var poolTitle)
                    && CardGuardService.IsCrossCharacterBlocked(player, poolTitle))
                {
                    continue; // blocked character — hide this choice
                }
                kept.Add(opt);
            }

            if (kept.Count != __result.Count)
            {
                Log.Info($"ColorfulPhilosophers: dropped {__result.Count - kept.Count} blocked character option(s) (char={currentPool.Title})");
                __result = kept;
            }
        }
        catch (Exception ex) { Log.Warn($"ColorfulPhilosophers filter error: {ex.Message}"); }
    }
}
