using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2CardGuard.Patches;

/// <summary>
/// Core event filter: skip blocked events instead of removing them.
///
/// Each act shuffles its event list once at run start and then walks it with a cursor
/// (<c>RoomSet.eventsVisited</c>). <c>RoomSet.EnsureNextEventIsValid</c> is the game's own "advance
/// past events I don't want" loop — it steps over anything failing <c>IsAllowed</c> or already in
/// <c>VisitedEventIds</c>, and <c>ActModel.PullNextEvent</c> is the sole caller, so it is the single
/// gate every placed event passes. Continuing that walk past blocked events is all the filter needs.
///
/// Why here and not on <c>EventModel.IsAllowed</c>: that method is virtual with 35 overrides, so the
/// patch would need all of them, and mod events with their own override would still slip through. Why
/// not drop blocked events from the list at generation time: the list is serialized into the run save
/// (so the block would have to survive save/load, and would not react to a setting changed mid-run),
/// and an emptied list makes <c>RoomSet.NextEvent</c> divide by zero.
///
/// Runs as a Postfix, after vanilla has parked the cursor on an allowed, unvisited event, and re-checks
/// vanilla's own two conditions on each step so we can never land on something the game had rejected
/// (skipping a blocked event can easily move the cursor onto an already-visited one).
///
/// Co-op safe: room generation and this walk run deterministically on every peer from the same
/// host-supplied block-set, and an event room is shared, so the block-set is applied as the UNION of
/// the run's characters (blocked for anyone → blocked for the map). Gated by the run's pass-through
/// flag, so an unsynced networked run does nothing.
///
/// Not covered, deliberately: <c>Lantern Key</c> forces War Historian Repy in act 3 through
/// <c>Hook.ModifyNextEvent</c>, AFTER this gate. Suppressing that would break the card rather than
/// filter the map.
/// </summary>
[HarmonyPatch(typeof(RoomSet), nameof(RoomSet.EnsureNextEventIsValid))]
internal static class RoomSet_EnsureNextEventIsValid_Patch
{
    private static void Postfix(RoomSet __instance, RunState runState)
    {
        try
        {
            if (__instance == null || runState == null || EventGuardService.IsPassThrough) return;
            var events = __instance.events;
            if (events == null || events.Count == 0) return;

            var titles = RunCharacterTitles(runState);
            int skipped = 0;

            for (int i = 0; i < events.Count; i++)
            {
                var next = events[__instance.eventsVisited % events.Count];
                if (next != null
                    && !EventGuardService.IsEventBlockedForAny(next, titles)
                    && next.IsAllowed(runState)
                    && !runState.VisitedEventIds.Contains(next.Id))
                {
                    if (skipped > 0) Log.Info($"skipped {skipped} blocked event(s); next event is '{next.Id}'.");
                    return;
                }
                __instance.eventsVisited++;
                skipped++;
            }

            // Nothing in the list satisfies every condition. The loop advanced the cursor by exactly
            // events.Count, i.e. back onto vanilla's own pick, so the run carries on with that rather
            // than stalling — the same "allow repetition" fallback the game uses.
            Log.Info("every remaining event is blocked or already seen — keeping the game's own pick.");
        }
        catch (Exception ex) { Log.Warn($"event skip error: {ex.Message}"); }
    }

    /// <summary>Distinct card-pool titles of every player in the run. An event room is shared, so the
    /// block-set is applied as their union.</summary>
    private static IReadOnlyCollection<string> RunCharacterTitles(RunState runState)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (Player p in runState.Players)
            {
                string t;
                try { t = p?.Character?.CardPool?.Title ?? string.Empty; }
                catch { continue; }
                if (t.Length > 0) set.Add(t);
            }
        }
        catch { }
        return set;
    }
}
