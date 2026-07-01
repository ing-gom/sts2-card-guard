using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace Sts2CardGuard;

/// <summary>
/// Central filter policy + user configuration state.
///
/// The mod never patches individual card-generation *effects*. Instead it filters the candidate
/// <see cref="CardModel"/> pool that reward / shop / event / in-combat generation all draw from,
/// BEFORE the game's RNG picks. Because we only ever *subtract* disallowed cards (and refuse to
/// empty a pool), the game keeps rolling the normal number of cards.
///
/// Default is PERMISSIVE: with the mod merely loaded, every card pack is allowed (all boxes
/// checked). The per-character sets below track what has been explicitly BLOCKED, not allowed.
///
/// Characters (base + mod-added) and mod card packs are discovered dynamically from ModelDb.
/// </summary>
internal static class CardGuardService
{
    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;

    /// <summary>The five base-character pool titles — fallback + config default seeding.</summary>
    public static readonly string[] CharacterTitles = { "ironclad", "silent", "defect", "regent", "necrobinder" };

    /// <summary>Per-character BLOCKED other-character titles. Empty by default = all allowed.</summary>
    private static readonly Dictionary<string, HashSet<string>> CrossBlock = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-character BLOCKED mod names. Empty by default = all allowed.</summary>
    private static readonly Dictionary<string, HashSet<string>> ModBlock = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object _lock = new();

    /// <summary>Assembly that owns all base-game card models (sts2.dll).</summary>
    private static readonly System.Reflection.Assembly BaseGameAssembly = typeof(CardModel).Assembly;

    // ---- Character cross-allow (default: allowed) ----

    public static bool GetCrossAllowed(string fromTitle, string toTitle)
    {
        lock (_lock) { return !(CrossBlock.TryGetValue(fromTitle, out var s) && s.Contains(toTitle)); }
    }

    public static void SetCrossAllowed(string fromTitle, string toTitle, bool allowed)
    {
        if (string.IsNullOrEmpty(fromTitle) || string.IsNullOrEmpty(toTitle)) return;
        lock (_lock)
        {
            var s = Ensure(CrossBlock, fromTitle);
            if (allowed) s.Remove(toTitle); else s.Add(toTitle);
        }
    }

    // ---- Mod allow (default: allowed) ----

    public static bool GetModAllowed(string characterTitle, string modName)
    {
        lock (_lock) { return !(ModBlock.TryGetValue(characterTitle, out var s) && s.Contains(modName)); }
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

    // ---- Snapshots for persistence (what has been blocked) ----

    public static List<string> GetBlockedCharacters(string fromTitle)
    {
        lock (_lock) { return CrossBlock.TryGetValue(fromTitle, out var s) ? s.ToList() : new List<string>(); }
    }

    public static List<string> GetBlockedMods(string characterTitle)
    {
        lock (_lock) { return ModBlock.TryGetValue(characterTitle, out var s) ? s.ToList() : new List<string>(); }
    }

    /// <summary>All character titles that currently have any block entry (for lossless save).</summary>
    public static List<string> GetConfiguredTitles()
    {
        lock (_lock) { return CrossBlock.Keys.Concat(ModBlock.Keys).Distinct(OIC).ToList(); }
    }

    private static HashSet<string> Ensure(Dictionary<string, HashSet<string>> d, string key)
    {
        if (!d.TryGetValue(key, out var s))
        {
            s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            d[key] = s;
        }
        return s;
    }

    // ---- Dynamic discovery: characters (base + mod) ----

    public readonly record struct CharInfo(string Title, string Display, int Count);

    private static List<CharInfo>? _allChars;
    private static HashSet<string>? _characterModNames;

    /// <summary>All selectable characters (base + mod-added): card-pool title, display name, card count.</summary>
    public static IReadOnlyList<CharInfo> GetAllCharacters()
    {
        if (_allChars != null) return _allChars;
        var list = new List<CharInfo>();
        var modNames = new HashSet<string>(OIC);
        var seen = new HashSet<string>(OIC);
        try
        {
            foreach (var ch in ModelDb.AllCharacters)
            {
                if (ch == null) continue;
                if (ch.GetType().Name.IndexOf("Random", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                CardPoolModel pool;
                try { pool = ch.CardPool; } catch { continue; }
                if (pool == null || pool.IsColorless) continue;
                string title = pool.Title ?? string.Empty;
                if (string.IsNullOrEmpty(title) || title.Equals("mock", StringComparison.OrdinalIgnoreCase)
                    || title.Equals("test", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(title)) continue;
                int count = 0;
                try { count = pool.AllCards.Count(); } catch { }
                list.Add(new CharInfo(title, ResolveCharDisplay(ch, title), count));

                // Record mod-added characters so their cards are controlled by the character
                // checkbox only (not duplicated as a mod card pack).
                try
                {
                    var asm = ch.GetType().Assembly;
                    if (asm != BaseGameAssembly)
                    {
                        var nm = asm.GetName().Name;
                        if (!string.IsNullOrEmpty(nm)) modNames.Add(nm);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Warn($"character discovery failed: {ex.Message}"); }

        if (list.Count == 0)
            foreach (var t in CharacterTitles) list.Add(new CharInfo(t, Capitalize(t), 0));

        _characterModNames = modNames;
        _allChars = list;
        return _allChars;
    }

    /// <summary>Whether <paramref name="modName"/> is a mod that adds a custom character (so it's shown/controlled as a character, not a separate mod pack).</summary>
    public static bool IsCharacterMod(string modName)
    {
        if (string.IsNullOrEmpty(modName)) return false;
        if (_characterModNames == null) GetAllCharacters();
        return _characterModNames != null && _characterModNames.Contains(modName);
    }

    private static string ResolveCharDisplay(CharacterModel ch, string fallbackTitle)
    {
        try
        {
            var s = ch.Title?.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        catch { }
        try { var e = ch.Id.Entry; if (!string.IsNullOrWhiteSpace(e)) return Capitalize(e); }
        catch { }
        return Capitalize(fallbackTitle);
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    // ---- Dynamic discovery: mod card packs (with counts) ----

    public sealed class ModPack
    {
        public int Total;
        public int Colorless;
        public readonly Dictionary<string, int> PerPool = new(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, ModPack>? _modPacks;

    /// <summary>
    /// Every loaded mod that adds registered cards, mapped to its card counts (total, colorless,
    /// and per-pool). Built by scanning all card pools' AllCards for cards from foreign assemblies.
    /// </summary>
    public static IReadOnlyDictionary<string, ModPack> GetModCardPacks()
    {
        if (_modPacks != null) return _modPacks;
        var map = new Dictionary<string, ModPack>(OIC);
        try
        {
            foreach (var pool in ModelDb.AllCardPools)
            {
                if (pool == null) continue;
                string ptitle = pool.Title ?? string.Empty;
                bool pcolorless = pool.IsColorless;
                IEnumerable<CardModel> cards;
                try { cards = pool.AllCards; } catch { continue; }
                foreach (var c in cards)
                {
                    if (c == null || !IsModCard(c)) continue;
                    string mod = GetModName(c);
                    if (string.IsNullOrEmpty(mod)) continue;
                    if (!map.TryGetValue(mod, out var mp)) { mp = new ModPack(); map[mod] = mp; }
                    mp.Total++;
                    if (pcolorless) mp.Colorless++;
                    mp.PerPool.TryGetValue(ptitle, out int n);
                    mp.PerPool[ptitle] = n + 1;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"mod card pack scan failed: {ex.Message}"); }
        _modPacks = map;
        return _modPacks;
    }

    // ---- Core filter ----

    /// <summary>
    /// Returns <paramref name="cards"/> with disallowed entries removed. Crash-safe: if every
    /// candidate would be removed, the original set is returned unchanged.
    /// </summary>
    public static IEnumerable<CardModel> Filter(IEnumerable<CardModel> cards, Player? player)
    {
        if (cards == null) return cards!;

        var currentPool = TryGetCurrentPool(player);
        if (currentPool == null) return cards;

        List<CardModel> materialized;
        try { materialized = cards.ToList(); }
        catch { return cards; }

        if (materialized.Count == 0) return materialized;

        var kept = new List<CardModel>(materialized.Count);
        foreach (var c in materialized)
        {
            if (c == null) continue;
            if (IsAllowed(c, currentPool)) kept.Add(c);
        }

        int removed = materialized.Count - kept.Count;
        if (removed == 0) return materialized;

        if (kept.Count == 0)
        {
            // Every candidate is blocked (e.g. a relic like Kaleidoscope offering a reward drawn
            // ENTIRELY from another, blocked character). Rather than pass the foreign cards
            // through, substitute the current character's own allowed cards so the block holds
            // without emptying the pool (which would crash the RNG).
            var sub = TryBuildOwnPoolSubstitute(player, currentPool);
            if (sub.Count > 0)
            {
                Log.Info($"all {materialized.Count} candidates blocked (char={currentPool.Title}); substituted {sub.Count} own-character card(s)");
                return sub;
            }
            Log.Warn($"filter would empty pool (char={currentPool.Title}) and no substitute available — passing through unfiltered");
            return materialized;
        }

        Log.FilterHit(currentPool.Title, removed, kept.Count);
        return kept;
    }

    /// <summary>The current character's own unlocked, still-allowed cards — used to replace a
    /// fully-blocked candidate pool without emptying it.</summary>
    private static List<CardModel> TryBuildOwnPoolSubstitute(Player? player, CardPoolModel currentPool)
    {
        var result = new List<CardModel>();
        if (player == null) return result;
        try
        {
            foreach (var c in currentPool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint))
                if (c != null && IsAllowed(c, currentPool)) result.Add(c);
        }
        catch { }
        return result;
    }

    /// <summary>Whether <paramref name="card"/> would pass the filter for <paramref name="player"/>'s character (no empty-safety). For diagnostics/console.</summary>
    public static bool WouldAppear(CardModel card, Player? player)
    {
        var pool = TryGetCurrentPool(player);
        if (pool == null) return true;
        try { return IsAllowed(card, pool); }
        catch { return true; }
    }

    public static CardPoolModel? TryGetCurrentPool(Player? player)
    {
        try { return player?.Character?.CardPool; }
        catch { return null; }
    }

    private static bool IsAllowed(CardModel card, CardPoolModel currentPool)
    {
        string currentTitle = currentPool.Title ?? string.Empty;

        CardPoolModel pool;
        try { pool = card.Pool; }
        catch { return GetModAllowed(currentTitle, GetModName(card)); }
        if (pool == null) return true;

        string title = pool.Title ?? string.Empty;
        bool isOwn = ReferenceEquals(pool, currentPool)
                     || string.Equals(title, currentTitle, StringComparison.OrdinalIgnoreCase);

        // Colorless is always allowed (mod-added colorless is still gated by the mod pack below).
        // Other non-own pools are gated by the character pack, matched by title — base OR custom
        // character. System pools (curse/status/token/event/quest) default to allowed because the
        // UI never blocks them.
        if (!isOwn && !pool.IsColorless)
        {
            if (!GetCrossAllowed(currentTitle, title)) return false;
        }

        // Mod-pack block applies regardless of own/other, so blocking a mod removes ITS cards
        // even from the current character's own pool. (Base-game cards are never mod cards.)
        if (IsModCard(card))
        {
            string mn = GetModName(card);
            // Custom-character cards are controlled by the character checkbox above (by pool title),
            // not by a separate mod pack — so don't re-block them here.
            if (!IsCharacterMod(mn) && !GetModAllowed(currentTitle, mn)) return false;
        }

        return true;
    }

    /// <summary>Mod (assembly) name that added this card, or "" for base-game cards. For diagnostics.</summary>
    public static string ModNameOf(CardModel card) => IsModCard(card) ? GetModName(card) : string.Empty;

    private static bool IsModCard(CardModel card)
    {
        try { return card.GetType().Assembly != BaseGameAssembly; }
        catch { return false; }
    }

    private static string GetModName(CardModel card)
    {
        try { return card.GetType().Assembly.GetName().Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}
