using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;

namespace Sts2CardGuard.Patches;

/// <summary>
/// Per-act cache of each act's VANILLA ancient list, captured before a content mod overwrites it.
/// A mod like SlayTheUniverse replaces the whole per-act ancient roster (e.g. Overgrowth's vanilla
/// [Neow] becomes [Gangzi]) via a Postfix on the act's <c>AllAncients</c> getter. We snapshot the
/// original list first so that, when the mod's ancient is blocked, we can restore the act's REAL
/// vanilla ancient rather than a generic fallback.
/// </summary>
internal static class AncientVanillaCache
{
    private static readonly Dictionary<Type, List<AncientEventModel>> _byAct = new();
    private static readonly object _lock = new();

    public static void Set(Type actType, List<AncientEventModel> vanilla)
    {
        if (actType == null || vanilla == null) return;
        lock (_lock) { _byAct[actType] = vanilla; }
    }

    /// <summary>A deterministic vanilla ancient for the act (first captured), or null if none known.</summary>
    public static AncientEventModel? VanillaFor(Type actType)
    {
        lock (_lock)
        {
            if (actType != null && _byAct.TryGetValue(actType, out var list) && list.Count > 0)
                return list[0];
        }
        return null;
    }
}

/// <summary>
/// Captures each act's vanilla <c>AllAncients</c> before any content mod's Postfix overwrites it.
/// Runs at Priority.First so it observes the original getter result ahead of the target mod's
/// (Normal-priority) replacement. Non-invasive: it only reads, never changes the result.
/// </summary>
[HarmonyPatch]
internal static class ActModel_AllAncients_Capture_Patch
{
    private static readonly Assembly BaseGameAssembly = typeof(AncientEventModel).Assembly;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var methods = new List<MethodBase>();
        try
        {
            foreach (var t in typeof(ActModel).Assembly.GetTypes())
            {
                if (t == null || t.IsAbstract || !typeof(ActModel).IsAssignableFrom(t)) continue;
                var getter = t.GetProperty("AllAncients",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.GetGetMethod();
                if (getter != null) methods.Add(getter);
            }
        }
        catch (Exception ex) { Log.Warn($"ancient capture target scan failed: {ex.Message}"); }
        return methods;
    }

    [HarmonyPriority(Priority.First)]
    private static void Postfix(ActModel __instance, IEnumerable<AncientEventModel> __result)
    {
        try
        {
            if (__instance == null || __result == null) return;
            var vanilla = __result.Where(a => a != null && a.GetType().Assembly == BaseGameAssembly).ToList();
            AncientVanillaCache.Set(__instance.GetType(), vanilla);
        }
        catch { }
    }
}

/// <summary>
/// Replaces a blocked mod's Ancient being with a VANILLA ancient at map generation, so blocking a
/// mod's relics also removes its Ancient events (and everything they display — the being's art and
/// its relic-choice options). The relic filter alone only stops you RECEIVING those relics; this
/// removes the cosmetic remnant.
///
/// The substitute is the act's OWN captured vanilla ancient (see <see cref="AncientVanillaCache"/>) —
/// e.g. Overgrowth restores Neow, not a generic shared ancient — falling back to Darv only if that
/// act's vanilla ancient was never observed. Substituting (rather than nulling) is required: the
/// RoomSet.Ancient getter throws when read null, and content mods wipe the vanilla shared pool.
///
/// Runs AFTER the target mod's own GenerateRooms patch (Priority.Last). RNG-neutral (pure value swap)
/// and deterministic, so it stays consistent across co-op peers; gated by the run's pass-through flag.
/// ★Fragile by nature: it depends on how such mods inject their ancients and could break if they change.
/// </summary>
[HarmonyPatch(typeof(ActModel), "GenerateRooms")]
[HarmonyPriority(Priority.Last)]
internal static class ActModelGenerateRooms_AncientFilter_Patch
{
    private static readonly Assembly BaseGameAssembly = typeof(AncientEventModel).Assembly;

    private static void Postfix(ActModel __instance)
    {
        try
        {
            if (__instance == null || RelicGuardService.IsPassThrough) return;

            var rooms = __instance._rooms;
            if (rooms == null || !rooms.HasAncient) return;

            AncientEventModel current;
            try { current = rooms.Ancient; } catch { return; }
            if (current == null) return;

            var type = current.GetType();
            if (type.Assembly == BaseGameAssembly) return; // vanilla ancient — keep
            string mod = type.Assembly.GetName().Name ?? string.Empty;
            if (string.IsNullOrEmpty(mod)) return;

            var titles = RelicFilterUtil.RunCharacterTitles();
            if (titles.Count == 0 || !RelicGuardService.IsModBlockedForAny(mod, titles)) return;

            var substitute = AncientVanillaCache.VanillaFor(__instance.GetType()) ?? ModelDb.AncientEvent<Darv>();
            rooms.Ancient = substitute;
            Log.Info($"substituted blocked mod ancient '{current.Id}' -> '{substitute.Id}' (mod={mod}, act={__instance.GetType().Name}).");
        }
        catch (Exception ex) { Log.Warn($"ancient filter error: {ex.Message}"); }
    }
}
