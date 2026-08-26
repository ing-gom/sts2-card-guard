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
/// ── What a starved pool does ─────────────────────────────────────────────────────────────────────
/// Because the rarity is rolled FIRST, a heavily blocked pool can leave the rolled rarity with nothing
/// to draw. <c>Rng.NextItem</c> returns <c>default(T)</c> for an empty sequence and the factory adds it
/// unconditionally, so the draw hands back NULLS — and vanilla's own consumers do not check:
/// <c>MerchantInventory.PopulatePotionEntries</c> calls <c>item.ToMutable()</c> on each entry and
/// throws <see cref="NullReferenceException"/>. <see cref="PotionFactory_StarvedDraw_Patch"/> is what
/// keeps that from reaching them.
///
/// ★★This comment previously said the opposite — that the game "clamps to what is available, no
/// nulls", measured on 0.110 — and a repair patch was deleted on the strength of it after firing zero
/// times in 22 draws. That measurement was under-powered, not wrong-headed: the rarity roll is
/// Common 65% / Uncommon 25% / Rare 10%, so when the surviving potion happens to be a COMMON one the
/// draw almost always succeeds and the bug hides. It surfaced the moment a run left a single RARE
/// potion allowed — 20 of 20 single draws came back null (measured on v0.111.0). Both game builds
/// decompile identically here, so this was never branch-specific; it was a sampling artefact.
/// **A conclusion labelled "measured" is still only as good as the sample behind it.**
///
/// The case that stays fatal on purpose is a pool filtered to EMPTY: <c>CreateRandomPotionOutOfCombat</c>
/// calls <c>.First()</c> on an empty list and throws. That is what the panel's "at least one potion has
/// to stay allowed" floor is for (<c>PotionGuardService.AnyPotionAllowedFor</c>), and the self-test
/// asserts the throw so the floor can never be dropped later as apparently unnecessary. The repair
/// below deliberately does not paper over it: with nothing allowed there is nothing to substitute, so
/// the result stays empty and the floor keeps doing its job.
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

/// <summary>
/// Keeps a null out of a starved draw, so a thinned pool yields FEWER potions rather than broken ones.
///
/// The rarity is rolled before the pick, so blocking enough potions leaves some rolls with no
/// candidate of that rarity; the factory then adds <c>Rng.NextItem</c>'s <c>null</c> to the list and
/// vanilla's consumers dereference it (see the type remarks above). This substitutes the first
/// still-unused allowed potion for each null, and drops the entry when there is none left.
///
/// ★RNG-neutral, like the filter itself. The substitute is chosen by pool order, NOT by asking
/// <c>rng</c> again: an extra draw would shift every later roll in the run, and this file's whole
/// contract with co-op is that filtering leaves the RNG stream where vanilla left it. The cost is that
/// a starved pool substitutes predictably instead of randomly — for a pool thin enough to starve a
/// rarity that is a handful of candidates anyway, and a repeatable potion beats a crash.
///
/// ★Patched on the PRIVATE draw both public entry points funnel through, so the in-combat path
/// (<c>CreateRandomPotionInCombat</c>, which calls <c>.First()</c> on the result) is covered by the
/// same fix. That method is named <c>CreateRandomPotions</c> on public-beta v0.111.0 and
/// <c>CreateRandomPotion</c> on public v0.107.1, and its return type differs too — so it is resolved
/// by hand rather than named in an attribute. ★A private rename like that is the silent kind of
/// break: an attribute patch on the old name compiles, throws nothing, and simply never attaches.
///
/// Gated on <c>IsPassThrough</c>: if this run is not filtering, the pool is vanilla-complete, there is
/// nothing to repair, and nothing here should alter what the game would have produced.
/// </summary>
internal static class PotionFactory_StarvedDraw_Patch
{
    private static readonly Type[] Signature =
        { typeof(IEnumerable<PotionModel>), typeof(int), typeof(MegaCrit.Sts2.Core.Random.Rng) };

    /// <summary>Attaches the postfix whose <c>__result</c> matches this build's return type.</summary>
    internal static void Apply(Harmony harmony)
    {
        try
        {
            MethodInfo? original = null;
            foreach (string name in new[] { "CreateRandomPotions", "CreateRandomPotion" })
            {
                original = typeof(PotionFactory).GetMethod(
                    name, BindingFlags.NonPublic | BindingFlags.Static, null, Signature, null);
                if (original != null) break;
            }

            if (original == null)
            {
                Log.Warn("PotionFactory's private draw was not found under any known name — "
                         + "a heavily blocked potion pool may hand nulls to the shop.");
                return;
            }

            string post = original.ReturnType == typeof(List<PotionModel>)
                ? nameof(PostfixList)          // public v0.107.1 and earlier
                : nameof(PostfixEnumerable);   // public-beta v0.111.0 and later

            harmony.Patch(original, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(PotionFactory_StarvedDraw_Patch), post)));
            Log.Info($"starved-draw repair bound to {original.Name} via {post} "
                     + $"(returns {original.ReturnType.Name}).");
        }
        catch (Exception ex)
        {
            Log.Warn($"could not attach the starved-draw repair: {ex.Message}");
        }
    }

    private static void PostfixEnumerable(IEnumerable<PotionModel> options, ref IEnumerable<PotionModel> __result)
    {
        List<PotionModel>? fixedUp = Repair(options, __result);
        if (fixedUp != null) __result = fixedUp;
    }

    private static void PostfixList(IEnumerable<PotionModel> options, ref List<PotionModel> __result)
    {
        List<PotionModel>? fixedUp = Repair(options, __result);
        if (fixedUp != null) __result = fixedUp;
    }

    /// <returns>A null-free draw, or null when there was nothing to change.</returns>
    private static List<PotionModel>? Repair(IEnumerable<PotionModel>? options, IEnumerable<PotionModel>? drawn)
    {
        try
        {
            if (PotionGuardService.IsPassThrough || drawn == null) return null;

            var entries = drawn.ToList();
            int holes = entries.Count(p => p == null);
            if (holes == 0) return null;

            // Pool order is the substitute's only source of randomness — see the type remarks.
            var pool = options?.Where(p => p != null).ToList() ?? new List<PotionModel>();
            var used = new HashSet<PotionModel>(entries.Where(p => p != null));

            var repaired = new List<PotionModel>(entries.Count);
            int filled = 0;
            foreach (PotionModel entry in entries)
            {
                if (entry != null) { repaired.Add(entry); continue; }

                PotionModel? substitute = pool.FirstOrDefault(p => !used.Contains(p));
                if (substitute == null) continue;   // nothing allowed is left: one fewer potion
                used.Add(substitute);
                repaired.Add(substitute);
                filled++;
            }

            Log.Info($"starved potion draw: {holes} empty rarity roll(s) — "
                     + $"substituted {filled}, dropped {holes - filled} "
                     + $"(asked {entries.Count}, returning {repaired.Count}).");
            return repaired;
        }
        catch (Exception ex)
        {
            Log.Warn($"starved-draw repair error: {ex.Message}");
            return null;
        }
    }
}
