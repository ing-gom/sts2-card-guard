using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace Sts2CardGuard;

/// <summary>
/// Relic-pack filter policy + config, sibling to <see cref="CardGuardService"/> and configured through
/// the same per-character panel framework.
///
/// Filters MOD-added relics as whole bundles, PER CHARACTER (mirroring the card filter): for the
/// character you are configuring FOR, each loaded mod that registers relics can be blocked so none of
/// its relics enter that character's relic grab bag (common / uncommon / rare / shop). Base-game
/// relics are never touched.
///
/// Co-op note: a character's own relic bag (rewards) is filtered by that character, deterministically.
/// The SHARED treasure-room bag is not tied to one character, so it is filtered by the UNION of every
/// run player's block-set (a relic blocked for ANY player is dropped) — deterministic across peers.
///
/// Default is PERMISSIVE: with the mod merely loaded every relic pack is allowed. The map below tracks
/// only what has been explicitly BLOCKED, keyed by character pool title.
///
/// (Ancient- and Event-rarity relics are granted directly by event code, not drawn from the grab bag,
/// so those can only be handled best-effort — see <c>RelicFilterPatches</c>.)
/// </summary>
internal static class RelicGuardService
{
    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;
    private static readonly object _lock = new();

    /// <summary>Per-character (pool title) BLOCKED relic-mod names. Empty by default = all allowed.</summary>
    private static readonly Dictionary<string, HashSet<string>> ModBlock = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-character (pool title) BLOCKED individual relic ids (see <see cref="RelicIdOf"/>). MOD
    /// relics only, matching the pack-level scope — base-game relics are never touched. Independent
    /// of the pack checkbox: a blocked pack drops everything regardless of what is ticked here.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> RelicBlock = new(StringComparer.OrdinalIgnoreCase);

    // ---- Multiplayer effective-config override (locked in once per run; see MultiplayerSync) ----
    private static volatile bool _mpPassThrough;
    private static volatile bool _mpUseOverride;
    // True for the duration of any networked run (host or client). Best-effort reward substitution is
    // disabled while set: it mutates shared run state (pulls from the grab bag) and can't be proven to
    // run identically on every peer — only the grab-bag filter, RNG-neutral and host-locked, runs in co-op.
    private static volatile bool _networked;
    private static readonly Dictionary<string, HashSet<string>> _ovrModBlock = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> _ovrRelicBlock = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assembly that owns all base-game relic models (sts2.dll).</summary>
    private static readonly Assembly BaseGameAssembly = typeof(RelicModel).Assembly;

    // ---- Block state (default: allowed), keyed by the character being configured FOR ----

    public static bool GetModAllowed(string characterTitle, string modName)
    {
        lock (_lock)
        {
            var d = _mpUseOverride ? _ovrModBlock : ModBlock;
            return !(d.TryGetValue(characterTitle, out var s) && s.Contains(modName));
        }
    }

    public static void SetModAllowed(string characterTitle, string modName, bool allowed)
    {
        if (string.IsNullOrEmpty(characterTitle) || string.IsNullOrEmpty(modName)) return;
        lock (_lock)
        {
            var s = Ensure(ModBlock, characterTitle);
            if (allowed) s.Remove(modName); else s.Add(modName);
        }
    }

    /// <summary>The blocked relic-mod names for one character (for lossless save).</summary>
    public static List<string> GetBlockedMods(string characterTitle)
    {
        lock (_lock) { return ModBlock.TryGetValue(characterTitle, out var s) ? s.ToList() : new List<string>(); }
    }

    // ---- Individual relic allow (default: allowed) ----

    /// <summary>Stable per-relic key. <c>ModelId.ToString()</c> is the game's own save-file identity
    /// (round-tripped by <c>ModelId.Deserialize</c>), so it survives restarts and matches across peers.</summary>
    public static string RelicIdOf(RelicModel relic)
    {
        try { return relic.Id.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static bool GetRelicAllowed(string characterTitle, string relicId)
    {
        if (string.IsNullOrEmpty(relicId)) return true;
        lock (_lock)
        {
            var d = _mpUseOverride ? _ovrRelicBlock : RelicBlock;
            return !(d.TryGetValue(characterTitle, out var s) && s.Contains(relicId));
        }
    }

    public static void SetRelicAllowed(string characterTitle, string relicId, bool allowed)
    {
        if (string.IsNullOrEmpty(characterTitle) || string.IsNullOrEmpty(relicId)) return;
        lock (_lock)
        {
            var s = Ensure(RelicBlock, characterTitle);
            if (allowed) s.Remove(relicId); else s.Add(relicId);
        }
    }

    /// <summary>The blocked individual relic ids for one character (for lossless save).</summary>
    public static List<string> GetBlockedRelics(string characterTitle)
    {
        lock (_lock) { return RelicBlock.TryGetValue(characterTitle, out var s) ? s.ToList() : new List<string>(); }
    }

    /// <summary>All character titles that currently have any relic block entry.</summary>
    public static List<string> GetConfiguredTitles()
    {
        lock (_lock) { return ModBlock.Keys.Concat(RelicBlock.Keys).Distinct(OIC).ToList(); }
    }

    private static HashSet<string> Ensure(Dictionary<string, HashSet<string>> d, string key)
    {
        if (!d.TryGetValue(key, out var s)) { s = new HashSet<string>(StringComparer.OrdinalIgnoreCase); d[key] = s; }
        return s;
    }

    // ---- Multiplayer control (called only from MultiplayerSync, once per run at lock-in) ----

    /// <summary>A copy of the LOCAL blocked config (never the override) — what the host sends to peers.</summary>
    public static (Dictionary<string, List<string>> mod, Dictionary<string, List<string>> relic) SnapshotLocalBlocks()
    {
        var mod = new Dictionary<string, List<string>>(OIC);
        var relic = new Dictionary<string, List<string>>(OIC);
        lock (_lock)
        {
            foreach (var (k, s) in ModBlock) if (s.Count > 0) mod[k] = s.ToList();
            foreach (var (k, s) in RelicBlock) if (s.Count > 0) relic[k] = s.ToList();
        }
        return (mod, relic);
    }

    public static void ActivateMpLocal()
    {
        lock (_lock) { _mpUseOverride = false; _mpPassThrough = false; _networked = true; }
    }

    public static void ActivateMpOverride(Dictionary<string, List<string>> mod, Dictionary<string, List<string>> relic)
    {
        lock (_lock)
        {
            _ovrModBlock.Clear();
            _ovrRelicBlock.Clear();
            foreach (var (k, v) in mod) _ovrModBlock[k] = new HashSet<string>(v, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in relic) _ovrRelicBlock[k] = new HashSet<string>(v, StringComparer.OrdinalIgnoreCase);
            _mpUseOverride = true; _mpPassThrough = false; _networked = true;
        }
    }

    public static void DisableMpFiltering()
    {
        lock (_lock) { _mpPassThrough = true; _mpUseOverride = false; _networked = true; }
    }

    public static void ClearMpState()
    {
        lock (_lock) { _mpPassThrough = false; _mpUseOverride = false; _networked = false; _ovrModBlock.Clear(); _ovrRelicBlock.Clear(); }
    }

    /// <summary>True when filtering is disabled for the current (networked) run.</summary>
    public static bool IsPassThrough => _mpPassThrough;

    /// <summary>True during any networked run (host or client). Best-effort reward substitution is SP-only.</summary>
    public static bool IsNetworkedRun => _networked;

    // ---- Core predicate ----

    public static bool IsModRelic(RelicModel relic)
    {
        try { return relic != null && relic.GetType().Assembly != BaseGameAssembly; }
        catch { return false; }
    }

    public static string ModNameOf(RelicModel relic)
    {
        try { return relic.GetType().Assembly.GetName().Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Whether <paramref name="relic"/> is blocked for the character pool <paramref name="characterTitle"/>.
    /// Blocked when EITHER the relic is individually blocked or its whole mod pack is. Base relics: never.</summary>
    public static bool IsRelicBlockedForCharacter(RelicModel relic, string characterTitle)
    {
        if (_mpPassThrough) return false;
        if (string.IsNullOrEmpty(characterTitle)) return false;
        if (!IsModRelic(relic)) return false;
        if (!GetRelicAllowed(characterTitle, RelicIdOf(relic))) return true;
        var mn = ModNameOf(relic);
        if (string.IsNullOrEmpty(mn)) return false;
        return !GetModAllowed(characterTitle, mn);
    }

    /// <summary>Whether <paramref name="relic"/> is blocked for ANY of the given characters — used for the
    /// SHARED (character-agnostic) treasure bag so every peer drops the same relics.</summary>
    public static bool IsRelicBlockedForAny(RelicModel relic, IEnumerable<string> characterTitles)
    {
        if (_mpPassThrough) return false;
        if (!IsModRelic(relic)) return false;
        foreach (var t in characterTitles)
            if (IsRelicBlockedForCharacter(relic, t)) return true;
        return false;
    }

    /// <summary>Whether a mod (by name) is blocked for ANY of the given characters. Type-agnostic, so
    /// non-relic content (e.g. a mod's Ancient event) can be gated by the same per-character block-set:
    /// blocking a mod's relics also removes its Ancient beings.</summary>
    public static bool IsModBlockedForAny(string modName, IEnumerable<string> characterTitles)
    {
        if (_mpPassThrough || string.IsNullOrEmpty(modName)) return false;
        foreach (var t in characterTitles)
            if (!GetModAllowed(t, modName)) return true;
        return false;
    }

    // ---- Dynamic discovery: mod relic packs (with counts) ----

    public sealed class RelicPack
    {
        public int Total;
        public readonly Dictionary<string, int> PerRarity = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The pack's distinct relics, for the panel's per-relic detail list.</summary>
        public readonly List<RelicModel> Relics = new();
    }

    private static Dictionary<string, RelicPack>? _packs;

    /// <summary>
    /// Every loaded mod that adds registered relics, mapped to its relic counts (total + per-rarity).
    /// Built by scanning all relic pools' AllRelics for relics from foreign assemblies, deduped by id.
    /// </summary>
    public static IReadOnlyDictionary<string, RelicPack> GetModRelicPacks()
    {
        if (_packs != null) return _packs;
        var map = new Dictionary<string, RelicPack>(OIC);
        var seenPerMod = new Dictionary<string, HashSet<string>>(OIC);
        try
        {
            foreach (var pool in ModelDb.AllRelicPools)
            {
                if (pool == null) continue;
                IEnumerable<RelicModel> relics;
                try { relics = pool.AllRelics; } catch { continue; }
                foreach (var r in relics)
                {
                    if (r == null || !IsModRelic(r)) continue;
                    var mod = ModNameOf(r);
                    if (string.IsNullOrEmpty(mod)) continue;

                    string rid;
                    try { rid = r.Id.ToString() ?? string.Empty; } catch { rid = r.GetType().FullName ?? string.Empty; }
                    if (!seenPerMod.TryGetValue(mod, out var seen)) { seen = new HashSet<string>(OIC); seenPerMod[mod] = seen; }
                    if (!seen.Add(rid)) continue;

                    if (!map.TryGetValue(mod, out var mp)) { mp = new RelicPack(); map[mod] = mp; }
                    mp.Total++;
                    mp.Relics.Add(r);
                    string rar;
                    try { rar = r.Rarity.ToString(); } catch { rar = "?"; }
                    mp.PerRarity.TryGetValue(rar, out int n);
                    mp.PerRarity[rar] = n + 1;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"mod relic pack scan failed: {ex.Message}"); }
        _packs = map;
        return _packs;
    }
}
