using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.PotionPools;

namespace Sts2CardGuard;

/// <summary>
/// Potion filter policy + config, sibling to <see cref="CardGuardService"/> and
/// <see cref="RelicGuardService"/>, configured through the same per-character panel framework.
///
/// UNIVERSAL, like the individual card blocks and unlike the relic filter: base-game potions are
/// blockable one by one, not just mod-added ones. There is no per-character base potion "pack" to
/// keep out of reach — the panel lists a pool's potions directly — and "never roll me this potion
/// again" is the whole request.
///
/// One hook covers every source. All random potion generation funnels through
/// <c>PotionPoolModel.GetUnlockedPotions</c>: <c>PotionFactory</c> (combat rewards, shop stock,
/// Phial Holster, Entropic Brew, Alchemize, Crystal Sphere) builds its candidate set from it, and the
/// five events that skip the factory and read the pools themselves (Battleworn Dummy, Endless
/// Conveyor, Potion Courier, The Legends Were True, Wellspring) read the very same method. See
/// <c>Patches/PotionFilterPatches.cs</c>.
///
/// Which scope applies is decided per POOL, not per call, because <c>GetUnlockedPotions</c> is handed
/// no player: a character's own potion pool is only ever read for that character, so it uses that
/// character's block-set; the SHARED pool is character-agnostic and uses the UNION of the run's
/// characters (the <c>RelicGrabBag</c> shared-bag rule), which is deterministic across co-op peers.
///
/// Default is PERMISSIVE: with the mod merely loaded every potion is allowed.
/// </summary>
internal static class PotionGuardService
{
    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;

    /// <summary>Per-scope BLOCKED potion-adding mod names.</summary>
    private static readonly ScopedBlockSet ModBlock = new();

    /// <summary>Per-scope BLOCKED individual potion ids (see <see cref="PotionIdOf"/>). Base-game
    /// potions included.</summary>
    private static readonly ScopedBlockSet PotionBlock = new();

    /// <summary>Filtering disabled for this (networked) run — every guard must no-op.</summary>
    private static volatile bool _mpPassThrough;

    /// <summary>Assembly that owns all base-game potion models (sts2.dll).</summary>
    private static readonly Assembly BaseGameAssembly = typeof(PotionModel).Assembly;

    public static bool IsPassThrough => _mpPassThrough;

    // ---- Identity ----

    /// <summary>Stable per-potion key. <c>ModelId.ToString()</c> is the game's own save-file identity,
    /// so it survives restarts and matches across peers.</summary>
    public static string PotionIdOf(PotionModel potion)
    {
        try { return potion.Id.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Stable per-pool key, for the panel's pack rows.</summary>
    public static string PoolKeyOf(PotionPoolModel pool)
    {
        try { return pool.Id.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static bool IsModPotion(PotionModel potion)
    {
        try { return potion != null && potion.GetType().Assembly != BaseGameAssembly; }
        catch { return false; }
    }

    /// <summary>Mod (assembly) name that added this potion, or "" for base-game potions.</summary>
    public static string ModNameOf(PotionModel potion)
    {
        try { return IsModPotion(potion) ? potion.GetType().Assembly.GetName().Name ?? string.Empty : string.Empty; }
        catch { return string.Empty; }
    }

    // ---- Exact-scope state (what the panel edits) ----

    public static bool GetPotionAllowed(string scope, string potionId) => PotionBlock.GetAllowed(scope, potionId);
    public static void SetPotionAllowed(string scope, string potionId, bool allowed) => PotionBlock.SetAllowed(scope, potionId, allowed);

    public static bool GetModAllowed(string scope, string modName) => ModBlock.GetAllowed(scope, modName);
    public static void SetModAllowed(string scope, string modName, bool allowed) => ModBlock.SetAllowed(scope, modName, allowed);

    // ---- Snapshots for persistence ----

    public static List<string> GetBlockedPotions(string scope) => PotionBlock.Blocked(scope);
    public static List<string> GetBlockedMods(string scope) => ModBlock.Blocked(scope);

    public static List<string> GetConfiguredTitles() =>
        PotionBlock.Scopes().Concat(ModBlock.Scopes()).Distinct(OIC).ToList();

    // ---- Multiplayer control (called only from MultiplayerSync, once per run at lock-in) ----

    public static (Dictionary<string, List<string>> mod, Dictionary<string, List<string>> potion) SnapshotLocalBlocks() =>
        (ModBlock.SnapshotLocal(), PotionBlock.SnapshotLocal());

    public static void ActivateMpLocal()
    {
        ModBlock.UseLocal(); PotionBlock.UseLocal(); _mpPassThrough = false;
    }

    public static void ActivateMpOverride(Dictionary<string, List<string>>? mod, Dictionary<string, List<string>>? potion)
    {
        ModBlock.ApplyOverride(mod); PotionBlock.ApplyOverride(potion); _mpPassThrough = false;
    }

    public static void DisableMpFiltering()
    {
        ModBlock.UseLocal(); PotionBlock.UseLocal(); _mpPassThrough = true;
    }

    public static void ClearMpState()
    {
        ModBlock.ClearOverride(); PotionBlock.ClearOverride(); _mpPassThrough = false;
    }

    // ---- Core predicate ----

    /// <summary>Whether <paramref name="potion"/> is blocked for the character pool
    /// <paramref name="characterTitle"/> — individually, or through its mod pack — under that
    /// character's own scope or the all-characters scope.</summary>
    public static bool IsPotionBlockedForCharacter(PotionModel potion, string characterTitle)
    {
        if (_mpPassThrough || potion == null || string.IsNullOrEmpty(characterTitle)) return false;
        if (!PotionBlock.GetAllowedEffective(characterTitle, PotionIdOf(potion))) return true;
        var mod = ModNameOf(potion);
        return mod.Length > 0 && !ModBlock.GetAllowedEffective(characterTitle, mod);
    }

    /// <summary>Whether <paramref name="potion"/> is blocked for ANY of the given characters — used for
    /// the SHARED (character-agnostic) potion pool so every co-op peer drops the same potions.</summary>
    public static bool IsPotionBlockedForAny(PotionModel potion, IEnumerable<string> characterTitles)
    {
        if (_mpPassThrough || potion == null) return false;
        foreach (var t in characterTitles)
            if (IsPotionBlockedForCharacter(potion, t)) return true;
        return false;
    }

    // ---- Core filter ----

    /// <summary>
    /// <paramref name="potions"/> with blocked entries removed, or the input unchanged when nothing is
    /// blocked / there is no run to decide against.
    ///
    /// Never mutates the input: <c>PotionPoolModel.AllPotions</c> is cached on the pool and handed
    /// straight back by the default <c>GetUnlockedPotions</c>, so removing from it in place would
    /// corrupt the pool for the rest of the process (including the panel's own listing).
    ///
    /// Outside a run this does nothing, so the potion compendium in the main menu still shows
    /// everything — a blocked potion is one you don't want ROLLED, not one you want erased from the
    /// game's own reference screens.
    ///
    /// Thinning a pool to almost nothing is safe: the five events that read pools directly all
    /// null-check their <c>NextItem</c> result, and <c>PotionFactory</c> clamps a starved draw to
    /// whatever is left (measured — see <c>Patches/PotionFilterPatches.cs</c>). Emptying one completely
    /// is not, which is what the panel's floor exists to prevent (<see cref="AnyPotionAllowedFor"/>).
    /// </summary>
    public static IEnumerable<PotionModel> FilterPool(PotionPoolModel pool, IEnumerable<PotionModel> potions)
    {
        if (_mpPassThrough || pool == null || potions == null) return potions!;

        var runTitles = Patches.RelicFilterUtil.RunCharacterTitles();
        if (runTitles.Count == 0) return potions; // menus / compendium — no run context to filter for

        // A character's own pool is only ever read for that character, so scope it to them; the shared
        // pool (and any pool we can't attribute) takes the union of the run's characters.
        string owner = OwnerTitleOf(pool);
        IEnumerable<string> titles = owner.Length > 0 && ContainsTitle(runTitles, owner)
            ? new[] { owner }
            : runTitles;

        List<PotionModel> all;
        try { all = potions.ToList(); }
        catch { return potions; }

        var kept = new List<PotionModel>(all.Count);
        foreach (var p in all)
        {
            if (p == null) continue;
            try { if (!IsPotionBlockedForAny(p, titles)) kept.Add(p); }
            catch { kept.Add(p); } // unreadable potion — leave the game's own behaviour alone
        }

        int removed = all.Count - kept.Count;
        if (removed == 0) return all;
        Log.Info($"potion pool '{PoolKeyOf(pool)}' filtered: removed {removed} blocked potion(s), {kept.Count} remain.");
        return kept;
    }

    private static bool ContainsTitle(IEnumerable<string> titles, string title)
    {
        foreach (var t in titles)
            if (string.Equals(t, title, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---- Dynamic discovery: potion pools + mod potion packs ----

    /// <summary>One blockable potion pool: a character's own, or the shared pool
    /// (<see cref="OwnerTitle"/> empty).</summary>
    public sealed class PotionGroup
    {
        public string Key = string.Empty;
        /// <summary>Card-pool title of the character this pool belongs to; "" for the shared pool.</summary>
        public string OwnerTitle = string.Empty;
        public readonly List<PotionModel> Potions = new();
    }

    public sealed class PotionPack
    {
        public int Total;
        public readonly List<PotionModel> Potions = new();
    }

    private static List<PotionGroup>? _groups;
    private static Dictionary<string, string>? _ownerByPoolKey;
    private static Dictionary<string, PotionPack>? _modPacks;

    /// <summary>
    /// The potion pools random generation actually draws from: every listed character's own pool, plus
    /// <see cref="SharedPotionPool"/>. The other shared pools (event / token / deprecated / mock) are
    /// deliberately absent — <c>PotionFactory.GetPotionOptions</c> never reads them, so a toggle over
    /// them would be a switch that silently does nothing (same rule as starter relics).
    /// </summary>
    public static IReadOnlyList<PotionGroup> GetPotionGroups()
    {
        if (_groups != null) return _groups;
        var groups = new List<PotionGroup>();
        var owners = new Dictionary<string, string>(OIC);
        var seen = new HashSet<string>(OIC);
        try
        {
            // Only characters the panel itself lists, so a mock/test pool can't slip in through here.
            var listed = new HashSet<string>(CardGuardService.GetAllCharacters().Select(c => c.Title), OIC);
            foreach (var ch in ModelDb.AllCharacters)
            {
                if (ch == null) continue;
                string title;
                PotionPoolModel pool;
                try { title = ch.CardPool?.Title ?? string.Empty; pool = ch.PotionPool; }
                catch { continue; }
                if (pool == null || title.Length == 0 || !listed.Contains(title)) continue;

                string key = PoolKeyOf(pool);
                if (key.Length == 0) continue;
                owners[key] = title;
                if (!seen.Add(key)) continue;

                var g = new PotionGroup { Key = key, OwnerTitle = title };
                AddPotions(g, pool);
                if (g.Potions.Count > 0) groups.Add(g);
            }

            var shared = ModelDb.PotionPool<SharedPotionPool>();
            string sharedKey = shared != null ? PoolKeyOf(shared) : string.Empty;
            if (shared != null && sharedKey.Length > 0 && seen.Add(sharedKey))
            {
                var g = new PotionGroup { Key = sharedKey };
                AddPotions(g, shared);
                if (g.Potions.Count > 0) groups.Add(g);
            }
        }
        catch (Exception ex) { Log.Warn($"potion pool scan failed: {ex.Message}"); }

        _ownerByPoolKey = owners;
        _groups = groups;
        return _groups;
    }

    private static void AddPotions(PotionGroup group, PotionPoolModel pool)
    {
        var seen = new HashSet<string>(OIC);
        IEnumerable<PotionModel> potions;
        // AllPotions, not GetUnlockedPotions: the panel runs on the character-select screen with no
        // run state, and the filter patch rides GetUnlockedPotions — reading it here would list the
        // pool already pruned by our own blocks.
        try { potions = pool.AllPotions; }
        catch { return; }
        foreach (var p in potions)
        {
            if (p == null) continue;
            string id = PotionIdOf(p);
            if (id.Length == 0 || !seen.Add(id)) continue;
            group.Potions.Add(p);
        }
    }

    /// <summary>Card-pool title of the character owning <paramref name="pool"/>, or "" when it is not
    /// a character pool (the shared pool).</summary>
    private static string OwnerTitleOf(PotionPoolModel pool)
    {
        if (_ownerByPoolKey == null) GetPotionGroups();
        string key = PoolKeyOf(pool);
        if (_ownerByPoolKey != null && key.Length > 0 && _ownerByPoolKey.TryGetValue(key, out var t)) return t;
        return string.Empty;
    }

    /// <summary>Every loaded mod that adds potions to a filterable pool, with its potion list.</summary>
    public static IReadOnlyDictionary<string, PotionPack> GetModPotionPacks()
    {
        if (_modPacks != null) return _modPacks;
        var map = new Dictionary<string, PotionPack>(OIC);
        var seenPerMod = new Dictionary<string, HashSet<string>>(OIC);
        try
        {
            foreach (var g in GetPotionGroups())
                foreach (var p in g.Potions)
                {
                    var mod = ModNameOf(p);
                    if (mod.Length == 0) continue;
                    if (!seenPerMod.TryGetValue(mod, out var seen)) { seen = new HashSet<string>(OIC); seenPerMod[mod] = seen; }
                    if (!seen.Add(PotionIdOf(p))) continue;
                    if (!map.TryGetValue(mod, out var pack)) { pack = new PotionPack(); map[mod] = pack; }
                    pack.Total++;
                    pack.Potions.Add(p);
                }
        }
        catch (Exception ex) { Log.Warn($"mod potion pack scan failed: {ex.Message}"); }
        _modPacks = map;
        return _modPacks;
    }

    /// <summary>
    /// Whether <paramref name="characterTitle"/> can still be rolled ANY potion — the floor the panel
    /// enforces. Only that character's own pool and the shared pool count, since those are the two
    /// <c>PotionFactory.GetPotionOptions</c> concatenates; another character's pool is never read for
    /// them. Stops at the first hit: this runs on every checkbox click.
    ///
    /// One potion is enough even when it is the wrong rarity: the game clamps a draw to what is
    /// available (measured, see <c>Patches/PotionFilterPatches.cs</c>). ZERO is what breaks —
    /// <c>CreateRandomPotionOutOfCombat</c> calls <c>.First()</c> on the empty result and throws, which
    /// the self-test asserts so this floor cannot later be dropped as unnecessary.
    /// </summary>
    /// <summary>
    /// Whether <paramref name="player"/> has blocked EVERY potion they could be rolled — the
    /// "no potions this run" state that <c>Patches/FullBanPatches.cs</c> turns into actual absence
    /// (no potion rewards, no shop shelf, potion-granting relics inert) instead of a crash.
    ///
    /// Per player, not per run: in co-op one character can be fully banned while the other is not, and
    /// every suppression site has a Player in hand. Deterministic on every peer, since the block-set is
    /// the host's.
    /// </summary>
    public static bool IsFullyBannedFor(MegaCrit.Sts2.Core.Entities.Players.Player? player)
    {
        if (_mpPassThrough || player == null) return false;
        string title;
        try { title = player.Character?.CardPool?.Title ?? string.Empty; }
        catch { return false; }
        if (title.Length == 0) return false;
        try { return !AnyPotionAllowedFor(title); }
        catch { return false; } // unreadable pools — behave as vanilla rather than suppress blindly
    }

    public static bool AnyPotionAllowedFor(string characterTitle)
    {
        if (string.IsNullOrEmpty(characterTitle)) return true;
        foreach (var g in GetPotionGroups())
        {
            if (g.OwnerTitle.Length > 0 && !string.Equals(g.OwnerTitle, characterTitle, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var p in g.Potions)
                if (!IsPotionBlockedForCharacter(p, characterTitle)) return true;
        }
        return false;
    }
}
