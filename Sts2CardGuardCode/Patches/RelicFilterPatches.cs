using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2CardGuard.Patches;

/// <summary>
/// Core relic filter. Every common/uncommon/rare/shop relic obtained in a run is drawn from a
/// <see cref="RelicGrabBag"/> populated ONCE at run start. Removing blocked mod relics from the bag
/// right after it is populated filters ALL of those draws (rewards, treasure rooms, shops) WITHOUT
/// disturbing the relic RNG stream: the game shuffles first, we only drop entries afterward, so draw
/// order is unchanged and every peer that shares the host's block-set drops identically. Base-game
/// relics are never removed; an emptied rarity already falls back to the game's FallbackRelic (Circlet).
///
/// Two populate paths, filtered per their character context:
///   • Populate(Player,Rng)              — a player's own reward bag → filter by THAT player's character.
///   • Populate(IEnumerable&lt;RelicModel&gt;,Rng) — the SHARED treasure bag (character-agnostic) → filter by
///                                          the UNION of every run player's block-set (deterministic).
/// The private <c>_deques</c>/<c>_originalRelics</c> fields are reachable directly (sts2.dll publicized).
/// </summary>
[HarmonyPatch(typeof(RelicGrabBag), nameof(RelicGrabBag.Populate), new[] { typeof(Player), typeof(MegaCrit.Sts2.Core.Random.Rng) })]
internal static class RelicGrabBag_PopulatePlayer_Patch
{
    private static void Postfix(RelicGrabBag __instance, Player player)
    {
        try
        {
            if (__instance == null || RelicGuardService.IsPassThrough) return;
            string title = RelicFilterUtil.TitleOf(player);
            if (string.IsNullOrEmpty(title)) return;
            RelicFilterUtil.FilterBag(__instance, r => RelicGuardService.IsRelicBlockedForCharacter(r, title));
        }
        catch (Exception ex) { Log.Warn($"relic bag (player) filter error: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(RelicGrabBag), nameof(RelicGrabBag.Populate), new[] { typeof(IEnumerable<RelicModel>), typeof(MegaCrit.Sts2.Core.Random.Rng) })]
internal static class RelicGrabBag_PopulateShared_Patch
{
    private static void Postfix(RelicGrabBag __instance)
    {
        try
        {
            if (__instance == null || RelicGuardService.IsPassThrough) return;
            var titles = RelicFilterUtil.RunCharacterTitles();
            if (titles.Count == 0) return; // no run context → cannot decide; leave shared bag untouched
            RelicFilterUtil.FilterBag(__instance, r => RelicGuardService.IsRelicBlockedForAny(r, titles));
        }
        catch (Exception ex) { Log.Warn($"relic bag (shared) filter error: {ex.Message}"); }
    }
}

internal static class RelicFilterUtil
{
    public static string TitleOf(Player player)
    {
        try { return player?.Character?.CardPool?.Title ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Distinct character-pool titles of every player in the current run (for the shared bag /
    /// ancient filter). Falls back to DebugOnlyGetState because room generation runs during run init,
    /// before RunManager.State is fully wired.</summary>
    public static IReadOnlyCollection<string> RunCharacterTitles()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rm = RunManager.Instance;
            IEnumerable<Player>? players = null;
            try { players = rm?.State?.Players; } catch { }
            if (players == null) { try { players = rm?.DebugOnlyGetState()?.Players; } catch { } }
            if (players != null)
                foreach (var p in players)
                {
                    var t = TitleOf(p);
                    if (!string.IsNullOrEmpty(t)) set.Add(t);
                }
        }
        catch { }
        return set;
    }

    /// <summary>Remove entries matching <paramref name="blocked"/> from every rarity deque and the
    /// refresh source (RefreshRarity re-adds from _originalRelics when a rarity empties), so blocked
    /// mod relics don't come back on refresh. Deterministic → co-op refresh stays consistent.</summary>
    public static void FilterBag(RelicGrabBag bag, Func<RelicModel, bool> blocked)
    {
        int removed = 0;
        var deques = bag._deques;
        if (deques != null)
            foreach (var deque in deques.Values)
                if (deque != null) removed += deque.RemoveAll(r => r != null && blocked(r));

        bag._originalRelics?.RemoveAll(r => r != null && blocked(r));

        if (removed > 0) Log.Info($"relic bag filtered: removed {removed} blocked mod relic(s).");
    }
}

/// <summary>
/// Covers relics that never enter the grab bag — Ancient- and Event-rarity relics granted DIRECTLY by
/// event code. Every relic acquisition funnels through <see cref="RelicCmd.Obtain"/> (rewards call it
/// too), so swapping a blocked mod relic here — for the receiving player's character — catches the
/// ancient/event grants the grab-bag filter can't reach. Grab-bag rewards are already filtered out of
/// the bag, so this only ever fires on a direct grant of a blocked mod relic.
///
/// Co-op safe: Obtain runs deterministically on every peer (the same relic, in lockstep), the block-set
/// is the host-synced override, and the substitute is a grab-bag pull — the very mechanism the game
/// uses for relic rewards, which replicates consistently. Gated only by the run's pass-through flag, so
/// an unsynced networked run does nothing (never a desync). A blocked relic becomes a normal one rather
/// than the option being hidden (hiding an ancient event's choice would be event-specific).
/// </summary>
[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Obtain), new[] { typeof(RelicModel), typeof(Player), typeof(int) })]
internal static class RelicCmd_Obtain_Patch
{
    private static void Prefix(ref RelicModel relic, Player player)
    {
        try
        {
            if (relic == null || player == null || RelicGuardService.IsPassThrough) return;

            string title = RelicFilterUtil.TitleOf(player);
            if (string.IsNullOrEmpty(title) || !RelicGuardService.IsRelicBlockedForCharacter(relic, title)) return;

            var substitute = RelicFactory.PullNextRelicFromFront(player);
            if (substitute == null) return; // bag empty resolves to FallbackRelic upstream anyway

            var original = relic.Id;
            relic = substitute.ToMutable();
            Log.Info($"blocked mod relic '{original}' swapped to '{substitute.Id}' on obtain.");
        }
        catch (Exception ex) { Log.Warn($"relic obtain swap error: {ex.Message}"); }
    }
}
