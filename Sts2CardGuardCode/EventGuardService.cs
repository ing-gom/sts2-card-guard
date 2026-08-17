using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2CardGuard;

/// <summary>
/// Event filter policy + config, sibling to <see cref="CardGuardService"/> /
/// <see cref="RelicGuardService"/> / <see cref="PotionGuardService"/>, configured through the same
/// per-character panel framework.
///
/// UNIVERSAL, like the individual card and potion blocks: base-game events are blockable one by one,
/// because "never give me this event again" is the request, and a mod-only event filter would cover
/// almost nothing (nearly every event is base-game).
///
/// Blocking is a SKIP, not a removal. Each act pre-shuffles its event list once at run start
/// (<c>ActModel.GenerateRooms</c>) and then walks it with a cursor, and the game already knows how to
/// step over an event it doesn't want: <c>RoomSet.EnsureNextEventIsValid</c> advances the cursor past
/// events failing <c>IsAllowed</c> or already visited. We advance it past blocked ones the same way
/// (see <c>Patches/EventBlockPatches.cs</c>) rather than deleting entries from the list — the list is
/// serialized into the run save, so a deletion would also have to survive save/load, and an emptied
/// list makes <c>RoomSet.NextEvent</c> divide by zero. Skipping needs neither: it works on a loaded
/// save, and it reacts to settings changed mid-run.
///
/// Scope: events belong to the MAP, not to a player — in co-op both players walk into the same event
/// — so a block holds when it is set for the all-characters scope or for ANY character in the run
/// (the <c>RelicGrabBag</c> shared-bag rule). That is deterministic on every peer, which is what
/// lockstep requires.
///
/// Default is PERMISSIVE: with the mod merely loaded every event is allowed.
/// </summary>
internal static class EventGuardService
{
    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;

    /// <summary>Per-scope BLOCKED event-adding mod names.</summary>
    private static readonly ScopedBlockSet ModBlock = new();

    /// <summary>Per-scope BLOCKED individual event ids (see <see cref="EventIdOf"/>). Base-game
    /// events included.</summary>
    private static readonly ScopedBlockSet EventBlock = new();

    private static volatile bool _mpPassThrough;

    /// <summary>Assembly that owns all base-game event models (sts2.dll).</summary>
    private static readonly Assembly BaseGameAssembly = typeof(EventModel).Assembly;

    /// <summary>Stable key for the shared-event group (not an act).</summary>
    public const string SharedGroupKey = "#shared";

    public static bool IsPassThrough => _mpPassThrough;

    // ---- Identity ----

    public static string EventIdOf(EventModel ev)
    {
        try { return ev.Id.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static bool IsModEvent(EventModel ev)
    {
        try { return ev != null && ev.GetType().Assembly != BaseGameAssembly; }
        catch { return false; }
    }

    public static string ModNameOf(EventModel ev)
    {
        try { return IsModEvent(ev) ? ev.GetType().Assembly.GetName().Name ?? string.Empty : string.Empty; }
        catch { return string.Empty; }
    }

    // ---- Exact-scope state (what the panel edits) ----

    public static bool GetEventAllowed(string scope, string eventId) => EventBlock.GetAllowed(scope, eventId);
    public static void SetEventAllowed(string scope, string eventId, bool allowed) => EventBlock.SetAllowed(scope, eventId, allowed);

    public static bool GetModAllowed(string scope, string modName) => ModBlock.GetAllowed(scope, modName);
    public static void SetModAllowed(string scope, string modName, bool allowed) => ModBlock.SetAllowed(scope, modName, allowed);

    // ---- Snapshots for persistence ----

    public static List<string> GetBlockedEvents(string scope) => EventBlock.Blocked(scope);
    public static List<string> GetBlockedMods(string scope) => ModBlock.Blocked(scope);

    public static List<string> GetConfiguredTitles() =>
        EventBlock.Scopes().Concat(ModBlock.Scopes()).Distinct(OIC).ToList();

    // ---- Multiplayer control (called only from MultiplayerSync, once per run at lock-in) ----

    public static (Dictionary<string, List<string>> mod, Dictionary<string, List<string>> ev) SnapshotLocalBlocks() =>
        (ModBlock.SnapshotLocal(), EventBlock.SnapshotLocal());

    public static void ActivateMpLocal()
    {
        ModBlock.UseLocal(); EventBlock.UseLocal(); _mpPassThrough = false;
    }

    public static void ActivateMpOverride(Dictionary<string, List<string>>? mod, Dictionary<string, List<string>>? ev)
    {
        ModBlock.ApplyOverride(mod); EventBlock.ApplyOverride(ev); _mpPassThrough = false;
    }

    public static void DisableMpFiltering()
    {
        ModBlock.UseLocal(); EventBlock.UseLocal(); _mpPassThrough = true;
    }

    public static void ClearMpState()
    {
        ModBlock.ClearOverride(); EventBlock.ClearOverride(); _mpPassThrough = false;
    }

    // ---- Core predicate ----

    /// <summary>Whether <paramref name="ev"/> is blocked for the character pool
    /// <paramref name="characterTitle"/> — individually or through its mod pack, under that
    /// character's scope or the all-characters scope.</summary>
    public static bool IsEventBlockedForCharacter(EventModel ev, string characterTitle)
    {
        if (_mpPassThrough || ev == null || string.IsNullOrEmpty(characterTitle)) return false;
        if (!EventBlock.GetAllowedEffective(characterTitle, EventIdOf(ev))) return true;
        var mod = ModNameOf(ev);
        return mod.Length > 0 && !ModBlock.GetAllowedEffective(characterTitle, mod);
    }

    /// <summary>
    /// Whether <paramref name="ev"/> is blocked for ANY of the given characters — the question the map
    /// asks, since an event room is shared by every player in the run.
    ///
    /// The all-characters scope is checked separately rather than through
    /// <paramref name="characterTitles"/> so a global block still holds when the run's characters
    /// can't be read (room generation runs while the run is still being wired up).
    /// </summary>
    public static bool IsEventBlockedForAny(EventModel ev, IEnumerable<string> characterTitles)
    {
        if (_mpPassThrough || ev == null) return false;

        string id = EventIdOf(ev);
        string mod = ModNameOf(ev);
        if (!EventBlock.GetAllowed(CardGuardService.AllScope, id)) return true;
        if (mod.Length > 0 && !ModBlock.GetAllowed(CardGuardService.AllScope, mod)) return true;

        foreach (var t in characterTitles)
        {
            if (string.IsNullOrEmpty(t)) continue;
            if (!EventBlock.GetAllowed(t, id)) return true;
            if (mod.Length > 0 && !ModBlock.GetAllowed(t, mod)) return true;
        }
        return false;
    }

    // ---- Dynamic discovery: events by act, plus mod event packs ----

    /// <summary>One blockable group of events: an act's own events, or the shared pool
    /// (<see cref="Key"/> == <see cref="SharedGroupKey"/>).</summary>
    public sealed class EventGroup
    {
        public string Key = string.Empty;
        /// <summary>Act name from the game's own loc table; empty for the shared group (the panel
        /// labels that one itself).</summary>
        public string Display = string.Empty;
        public readonly List<EventModel> Events = new();
    }

    public sealed class EventPack
    {
        public int Total;
        public readonly List<EventModel> Events = new();
    }

    private static List<EventGroup>? _groups;
    private static Dictionary<string, EventPack>? _modPacks;

    /// <summary>
    /// Every event that can be placed on a map, grouped the way the game draws them: per act, plus the
    /// shared pool every act adds to its own list.
    ///
    /// Read from <c>ModelDb.Acts</c> → <c>act.AllEvents</c> (the live getter) rather than the cached
    /// <c>ModelDb.AllEvents</c>, so events a content mod appends by patching that getter are listed
    /// under the act they were added to.
    ///
    /// Ancient beings (Neow and friends) are NOT here: they come from <c>RoomSet.Ancient</c> via
    /// <c>PullAncient</c>, which never consults the skip logic, so a toggle over them would do nothing.
    /// Blocking a mod's ancient already works through the relic tab
    /// (<c>Patches/AncientFilterPatches.cs</c>).
    /// </summary>
    public static IReadOnlyList<EventGroup> GetEventGroups()
    {
        if (_groups != null) return _groups;
        var groups = new List<EventGroup>();
        try
        {
            foreach (var act in ModelDb.Acts)
            {
                if (act == null) continue;
                string key;
                try { key = act.Id.ToString() ?? string.Empty; }
                catch { continue; }
                if (key.Length == 0) continue;

                var g = new EventGroup { Key = key, Display = ActDisplay(act) };
                IEnumerable<EventModel> events;
                try { events = act.AllEvents; }
                catch { continue; }
                AddEvents(g, events);
                if (g.Events.Count > 0) groups.Add(g);
            }

            var shared = new EventGroup { Key = SharedGroupKey };
            AddEvents(shared, ModelDb.AllSharedEvents);
            if (shared.Events.Count > 0) groups.Add(shared);
        }
        catch (Exception ex) { Log.Warn($"event scan failed: {ex.Message}"); }

        _groups = groups;
        return _groups;
    }

    private static void AddEvents(EventGroup group, IEnumerable<EventModel>? events)
    {
        if (events == null) return;
        var seen = new HashSet<string>(OIC);
        foreach (var e in events)
        {
            if (e == null) continue;
            // Ancients ride the same base type but are pulled through a path the skip logic can't
            // reach, and a deprecated event only exists so an old save still loads.
            if (e is AncientEventModel || e is DeprecatedEvent) continue;
            string id = EventIdOf(e);
            if (id.Length == 0 || !seen.Add(id)) continue;
            group.Events.Add(e);
        }
    }

    private static string ActDisplay(ActModel act)
    {
        try
        {
            var s = act.Title?.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(s)) return s!;
        }
        catch { }
        try { return act.Id.Entry ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Every loaded mod that adds map events, with its event list.</summary>
    public static IReadOnlyDictionary<string, EventPack> GetModEventPacks()
    {
        if (_modPacks != null) return _modPacks;
        var map = new Dictionary<string, EventPack>(OIC);
        var seenPerMod = new Dictionary<string, HashSet<string>>(OIC);
        try
        {
            foreach (var g in GetEventGroups())
                foreach (var e in g.Events)
                {
                    var mod = ModNameOf(e);
                    if (mod.Length == 0) continue;
                    if (!seenPerMod.TryGetValue(mod, out var seen)) { seen = new HashSet<string>(OIC); seenPerMod[mod] = seen; }
                    if (!seen.Add(EventIdOf(e))) continue;
                    if (!map.TryGetValue(mod, out var pack)) { pack = new EventPack(); map[mod] = pack; }
                    pack.Total++;
                    pack.Events.Add(e);
                }
        }
        catch (Exception ex) { Log.Warn($"mod event pack scan failed: {ex.Message}"); }
        _modPacks = map;
        return _modPacks;
    }

    /// <summary>
    /// Whether <paramref name="characterTitle"/> can still be given ANY event — the floor the panel
    /// enforces. Not a crash guard (the game answers an all-blocked act by reusing an event it would
    /// otherwise have skipped) but a truthfulness one: past that point further blocks stop having any
    /// effect, and a switch that does nothing is worse than a refused one.
    /// </summary>
    /// <summary>
    /// Whether the CURRENT ACT has no event left it could place — the "no events this run" state that
    /// <c>Patches/FullBanPatches.cs</c> turns into actual absence by keeping <c>?</c> map points from
    /// ever resolving to an event room.
    ///
    /// Judged against the act's own shuffled list (<c>RoomSet.events</c>), which is the set
    /// <c>PullNextEvent</c> actually draws from, for two reasons:
    ///
    /// ★1. Correctness, and it is measurable. An act draws only from its OWN events plus the shared pool,
    /// and its list is bigger than this mod's per-act discovery (measured: 111 entries where discovery
    /// sees 64 distinct ids). Blocking every event of act 2 while act 3 keeps some is a full ban *for act
    /// 2* — asking "is every event everywhere blocked" would answer no, that act would go on resolving
    /// <c>?</c> to events, and the skip gate, out of candidates, would hand back a blocked one.
    ///
    /// ★2. It asks run state instead of a cached scan. <see cref="GetEventGroups"/> is discovered once per
    /// process from live getters that content mods patch; the act's list is generated deterministically
    /// and carried in the save, so every peer in a co-op run is judging the identical set. (Both peers
    /// were measured deriving the same 64 ids, so this is not fixing an observed divergence — it removes
    /// the dependency on discovery timing being identical across processes, which nothing guarantees.)
    ///
    /// Fails to "not banned" whenever the list can't be read, so an unreadable state leaves the map
    /// exactly as vanilla would have built it.
    /// </summary>
    public static bool AreAllEventsBannedForAct(IRunState? runState, IEnumerable<string> characterTitles)
    {
        if (_mpPassThrough || runState == null) return false;
        try
        {
            List<EventModel>? events = null;
            try { events = runState.Act?._rooms?.events; }
            catch { }
            if (events == null || events.Count == 0) return false;

            var titles = characterTitles as IReadOnlyCollection<string> ?? characterTitles.ToList();
            foreach (var ev in events)
                if (ev != null && !IsEventBlockedForAny(ev, titles)) return false;
            return true;
        }
        catch { return false; } // unreadable — behave as vanilla rather than delete every event room
    }

    public static bool AnyEventAllowedFor(string characterTitle)
    {
        if (string.IsNullOrEmpty(characterTitle)) return true;
        var groups = GetEventGroups();

        // Every act draws from its OWN events plus the shared pool, so the floor has to hold act by
        // act — one act stripped bare is one act's worth of repeated events, even if others are full.
        var shared = groups.FirstOrDefault(g => g.Key == SharedGroupKey);
        bool anySharedAllowed = shared != null && shared.Events.Any(e => !IsEventBlockedForCharacter(e, characterTitle));
        if (anySharedAllowed) return true;

        foreach (var g in groups)
        {
            if (g.Key == SharedGroupKey) continue;
            if (!g.Events.Any(e => !IsEventBlockedForCharacter(e, characterTitle))) return false;
        }
        return true;
    }
}
