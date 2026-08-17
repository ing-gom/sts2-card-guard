using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2CardGuard.Patches;

/// <summary>
/// Core potion filter. Every potion the game rolls at random comes from
/// <see cref="PotionPoolModel.GetUnlockedPotions"/>:
///
///   • <c>PotionFactory.GetPotionOptions</c> builds its candidate set from the player's own pool plus
///     the shared pool — that covers combat rewards, shop stock, Phial Holster, Entropic Brew,
///     Alchemize and the Crystal Sphere;
///   • five events skip the factory and read those two pools themselves (Battleworn Dummy, Endless
///     Conveyor, Potion Courier, The Legends Were True, Wellspring). Their potion list is a LOCAL
///     variable, so there is nothing to patch at the event — but they call this same method.
///
/// Filtering here therefore needs one hook instead of ten, and none of the call sites can grow a new
/// unfiltered path without going through it.
///
/// The method is virtual and every pool overrides it by calling <c>GenerateAllPotions</c> / a pruned
/// copy of <c>AllPotions</c> rather than <c>base</c>, so patching only the base declaration would miss
/// them all: <see cref="TargetMethods"/> patches the base plus every override, in MOD assemblies too —
/// a mod that adds potions through its own pool type is exactly what a "filter custom potions" request
/// is about, and its override would otherwise never be reached.
///
/// RNG-neutral. <c>PotionFactory</c> rolls a RARITY first and only then picks from the entries of that
/// rarity, and <c>Rng.NextItem</c> is a single draw whatever the list length — so removing candidates
/// leaves the RNG stream exactly where vanilla would have left it, and every peer sharing the host's
/// block-set picks identically. Gated by the run's pass-through flag, so an unsynced networked run
/// does nothing.
///
/// ── What a starved pool does, measured rather than read ──────────────────────────────────────────
/// Because the rarity is rolled first, a heavily blocked pool can leave the rolled rarity with nothing
/// to draw. That does NOT crash: measured on 0.110 (the potion case in <c>SoloTest</c>), a draw asking
/// for 3 potions from a pool holding exactly ONE Rare potion comes back with that one potion — a single
/// entry, no nulls — so the game clamps to what is available.
///
/// ★Do not trust a decompile here. ilspycmd renders <c>CreateRandomPotion</c> as adding
/// <c>Rng.NextItem</c>'s result unconditionally, which would return three entries with two nulls and
/// crash every caller (<c>.ToMutable()</c> / <c>.First()</c>). A repair patch written against that
/// reading lived in this file through 22 measured draws — including the deliberately starved one — and
/// fired zero times, so it was deleted rather than shipped as a switch that does nothing.
///
/// The case that IS fatal is a pool filtered to EMPTY: <c>CreateRandomPotionOutOfCombat</c> calls
/// <c>.First()</c> on an empty list and throws. That is what the panel's "at least one potion has to
/// stay allowed" floor is for (<c>PotionGuardService.AnyPotionAllowedFor</c>), and the self-test
/// asserts the throw so the floor can never be dropped later as apparently unnecessary.
///
/// Visible consequence, by design: block enough potions and a merchant that would have stocked three
/// may stock fewer. Fewer potions is the honest result of blocking potions; handing a blocked one back
/// would not be.
/// </summary>
[HarmonyPatch]
internal static class PotionPoolModel_GetUnlockedPotions_Patch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var methods = new List<MethodBase>();
        var signature = new[] { typeof(UnlockState) };
        try
        {
            var baseMethod = AccessTools.Method(
                typeof(PotionPoolModel), nameof(PotionPoolModel.GetUnlockedPotions), signature);
            if (baseMethod != null) methods.Add(baseMethod);

            // EVERY loaded assembly, not just the game's. A potion-adding mod that declares its own
            // pool and overrides this without calling base would otherwise hand out unfiltered potions
            // — the exact case a "filter custom potions" request is about. Mod assemblies are loaded
            // before initializers run, so they are visible here; one that somehow arrives later just
            // falls back to whatever it inherits (the base entry above).
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type?[] types;
                // A mod assembly with unresolvable references throws here and hands back the types it
                // COULD load — take those rather than losing the whole scan to one bad assembly.
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || t == typeof(PotionPoolModel) || !typeof(PotionPoolModel).IsAssignableFrom(t)) continue;
                    var m = t.GetMethod(nameof(PotionPoolModel.GetUnlockedPotions),
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                        null, signature, null);
                    if (m != null && !m.IsAbstract && m.DeclaringType == t) methods.Add(m);
                }
            }
        }
        catch (Exception ex) { Log.Warn($"potion pool target scan failed: {ex.Message}"); }
        Log.Info($"potion filter attached to {methods.Count} GetUnlockedPotions target(s).");
        return methods;
    }

    private static void Postfix(PotionPoolModel __instance, ref IEnumerable<PotionModel> __result)
    {
        try
        {
            if (__instance == null || __result == null) return;
            __result = PotionGuardService.FilterPool(__instance, __result);
        }
        catch (Exception ex) { Log.Warn($"potion pool filter error: {ex.Message}"); }
    }
}
