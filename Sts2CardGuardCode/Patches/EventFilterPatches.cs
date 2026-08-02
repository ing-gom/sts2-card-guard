using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;

namespace Sts2CardGuard.Patches;

/// <summary>
/// The <c>ColorfulPhilosophers</c> event offers a card reward drawn from ANOTHER character's pool,
/// letting you pick which foreign character via its initial options (one per unlocked non-own
/// character). The reward cards themselves funnel through <c>CardCreationOptions.GetPossibleCards</c>
/// and are already filtered by <see cref="CardGuardService"/> — but the *options* (which foreign
/// character to choose) are built directly from the unlock state and bypass that hook, so a blocked
/// character still shows up as a choice. That is the event-side counterpart of the Kaleidoscope /
/// Prismatic Gem "other character shows up" complaint.
///
/// A blocked option is RETARGETED, not dropped: the event rolls a fixed number of choices before we
/// see them, so merely removing a blocked option shrank the event ("战士、猎手、MOD角色 → only the
/// first two show") and an all-blocked roll left the event with nothing but Skip. Instead each
/// blocked option is redirected to an allowed character pool that is not already offered: the
/// <c>CardPoolModel</c> captured in the option's <c>OnChosen</c> closure (<c>() =&gt;
/// OfferRewards(cardPool)</c>) is swapped to the stand-in and the option is REBUILT through the
/// public <c>EventOption</c> constructor with the stand-in's text key, so the game derives the
/// button's title and description itself and they cannot drift from the reward
/// (see <see cref="TryBuildRetargeted" /> — editing <c>TextKey</c> in place does not relabel).
/// Base-game pools are preferred as stand-ins (their option strings always exist) and a pool with
/// no option string is skipped; which pool is picked is derived from the blocked pool's title via
/// the same stable hash the card substitute uses, so a given blocked character always maps to the
/// same stand-in on every peer, save and platform. Only when no usable stand-in remains is the
/// option dropped as before — the game
/// handles a zero-option event by ending it (<c>SetEventState</c> sets <c>_isFinished</c>), so
/// that fallback is safe. The multiplayer pass-through is honoured throughout: with filtering
/// disabled nothing is blocked, so options are never touched.
///
/// An option is matched to its pool by reading the closure field — the pool object itself, so the
/// match is exact. The older approach parsed the <c>EnergyColorName</c> suffix the event bakes into
/// <see cref="EventOption.TextKey"/> and looked it up in a BASE-GAME-ONLY color map (to stop a mod
/// character reusing a base color name from shadowing the base entry). That map is why mod
/// characters were unblockable: RitsuLib's <c>ColorfulPhilosophersCardPoolColorOrderPatch</c>
/// appends opted-in mod character pools to the event's candidate order, so mod characters DO get
/// offered — and every one of them missed the base-only map, was never recognised, and survived the
/// filter. The color-suffix lookup is kept only as a fallback for when the closure can't be read
/// (such an option can be hidden but not retargeted), and includes mod pools with base pools
/// winning a color-name collision.
/// </summary>
[HarmonyPatch(typeof(ColorfulPhilosophers), "GenerateInitialOptions")]
internal static class ColorfulPhilosophers_Patch
{
    /// <summary><c>EventOption.OnChosen</c> — private get-only auto-property holding the option's action.</summary>
    private static readonly PropertyInfo? OnChosenProp = AccessTools.Property(typeof(EventOption), "OnChosen");

    private static void Postfix(EventModel __instance, ref IReadOnlyList<EventOption> __result)
    {
        try
        {
            if (__result == null || __result.Count == 0) return;
            var player = __instance.Owner;
            var currentPool = CardGuardService.TryGetCurrentPool(player);
            if (currentPool == null) return;
            string currentTitle = currentPool.Title ?? string.Empty;

            Dictionary<string, string>? colorToTitle = null; // built lazily — only if a closure read fails

            // Pass 1: resolve every option and decide its fate. Titles that stay offered are
            // collected so a retarget never duplicates a character already on the list.
            var entries = new List<(EventOption opt, CardPoolModel? pool, bool blocked)>(__result.Count);
            var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var opt in __result)
            {
                if (opt == null) continue;

                var pool = ResolveTargetPool(opt);
                if (pool != null)
                {
                    string title = pool.Title ?? string.Empty;
                    // Blocked character, or a character whose pool individual card blocks have
                    // emptied — offering the latter would silently substitute another class's cards.
                    bool blocked = CardGuardService.IsCrossCharacterBlocked(player, title)
                                   || !CardGuardService.HasAnyAllowedCard(player, pool);
                    entries.Add((opt, pool, blocked));
                    if (!blocked) offered.Add(title);
                    continue;
                }

                colorToTitle ??= BuildColorToTitle(player);
                string color = KeySuffix(opt.TextKey ?? string.Empty);
                if (colorToTitle.TryGetValue(color, out var poolTitle))
                {
                    bool blocked = CardGuardService.IsCrossCharacterBlocked(player, poolTitle);
                    entries.Add((opt, null, blocked));
                    if (!blocked) offered.Add(poolTitle);
                }
                else
                {
                    entries.Add((opt, null, false)); // unrecognisable — never touch it
                }
            }

            if (!entries.Exists(e => e.blocked)) return;

            // Pass 2: retarget each blocked option to an allowed stand-in pool; drop it only when
            // no stand-in remains or the option can't be rewritten (unreadable closure / TextKey).
            var candidates = BuildStandInPools(player, currentTitle, offered);
            var kept = new List<EventOption>(entries.Count);
            int retargeted = 0, dropped = 0;
            foreach (var (opt, pool, blocked) in entries)
            {
                if (!blocked) { kept.Add(opt); continue; }

                // Walk the stand-ins in stable-index order until one produces a usable option: a
                // candidate is discarded (not silently kept) when it has no option string of its
                // own, so a rejected pool never gets tried again for a later blocked option either.
                EventOption? rebuilt = null;
                while (pool != null && candidates.Count > 0)
                {
                    int idx = CardGuardService.StableIndex(pool.Title ?? string.Empty, candidates.Count);
                    var standIn = candidates[idx];
                    candidates.RemoveAt(idx);
                    rebuilt = TryBuildRetargeted(__instance, opt, pool, standIn);
                    if (rebuilt != null) break;
                }

                if (rebuilt != null) { kept.Add(rebuilt); retargeted++; }
                else dropped++;
            }

            if (retargeted > 0 || dropped > 0)
            {
                Log.Info($"ColorfulPhilosophers: {retargeted} blocked option(s) retargeted, {dropped} dropped (char={currentTitle})");
                __result = kept;
            }
        }
        catch (Exception ex) { Log.Warn($"ColorfulPhilosophers filter error: {ex.Message}"); }
    }

    /// <summary>
    /// Allowed stand-in pools for retargeting: unlocked, non-colorless, not the player's own class,
    /// not already offered, not blocked, not emptied by individual card blocks, and with an
    /// EnergyColorName to bake into the option label. Base-game pools are listed first — their
    /// option strings are guaranteed to exist, whereas a mod pool that never opted into the event
    /// may lack one — so they win the stable-index pick while any remain.
    /// </summary>
    private static List<CardPoolModel> BuildStandInPools(
        MegaCrit.Sts2.Core.Entities.Players.Player? player, string currentTitle, HashSet<string> offered)
    {
        var result = new List<CardPoolModel>();
        if (player == null) return result;
        var baseAsm = typeof(EventModel).Assembly;
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantBase = pass == 0;
                foreach (var p in player.UnlockState.CharacterCardPools)
                {
                    if (p == null || p.IsColorless) continue;
                    if ((p.GetType().Assembly == baseAsm) != wantBase) continue;
                    if (string.IsNullOrEmpty(p.EnergyColorName)) continue;
                    string t = p.Title ?? string.Empty;
                    if (t.Length == 0 || t.Equals(currentTitle, StringComparison.OrdinalIgnoreCase)) continue;
                    if (offered.Contains(t)) continue;
                    if (CardGuardService.IsCrossCharacterBlocked(player, t)) continue;
                    if (!CardGuardService.HasAnyAllowedCard(player, p)) continue;
                    result.Add(p);
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Build the replacement option that offers <paramref name="standIn"/> in place of
    /// <paramref name="from"/>, or null when this stand-in can't be used (caller then tries another).
    ///
    /// ★A retargeted option is REBUILT, not edited in place. <c>EventOption</c>'s constructor bakes
    /// <c>Title</c>/<c>Description</c>/<c>HistoryName</c> from the text key once
    /// (<c>Title = eventModel.GetOptionTitle(textKey)</c>), and <c>NEventOptionButton</c> renders
    /// <c>Option.Title.GetFormattedText()</c> — it never reads <c>TextKey</c>. Rewriting only the key
    /// therefore left the button showing the BLOCKED character's name above the stand-in's cards
    /// (measured: pool=defect, key=…options.DEFECT, but label still 'Cyan'). Running the public
    /// constructor makes the game derive all three strings itself, so label and reward can't drift,
    /// and it needs no reflection beyond the closure swap.
    ///
    /// The pool swap targets the option's own closure — the event allocates one display class per
    /// loop iteration, so this cannot disturb the other options — and the old option is discarded.
    /// </summary>
    private static EventOption? TryBuildRetargeted(EventModel ev, EventOption opt, CardPoolModel from, CardPoolModel standIn)
    {
        try
        {
            string newKey = RebuildTextKey(opt.TextKey ?? string.Empty, standIn);
            if (newKey.Length == 0) return null;

            // A pool that never opted into this event has no "…options.<COLOR>" string; taking it
            // would leave the button labelled with a raw color word. Skip it and try the next.
            if (ev.GetOptionTitle(newKey) == null) return null;

            if (OnChosenProp?.GetValue(opt) is not Func<Task> action) return null;
            if (!TrySetPool(opt, standIn)) return null;

            var rebuilt = new EventOption(ev, action, newKey) { HoverTips = opt.HoverTips };
            if (!ReferenceEquals(ResolveTargetPool(rebuilt), standIn))
            {
                TrySetPool(opt, from); // never hand back a half-retargeted option
                return null;
            }
            return rebuilt;
        }
        catch
        {
            TrySetPool(opt, from);
            return null;
        }
    }

    /// <summary>New TextKey for a retargeted option: same prefix, suffix replaced by the stand-in
    /// pool's EnergyColorName. The event always bakes the suffix with
    /// <c>EnergyColorName.ToUpperInvariant()</c>, so match that unconditionally rather than trying
    /// to mirror the original's casing (a pool whose color name is already upper-case defeats a
    /// mirror and leaks its raw casing into the key).</summary>
    private static string RebuildTextKey(string originalKey, CardPoolModel standIn)
    {
        string color = standIn.EnergyColorName ?? string.Empty;
        if (color.Length == 0) return string.Empty;
        int dot = originalKey.LastIndexOf('.');
        if (dot < 0) return string.Empty; // not the "….options.<color>" shape we know — don't guess
        return originalKey.Substring(0, dot + 1) + color.ToUpperInvariant();
    }

    /// <summary>Write the OnChosen closure's captured pool — the same field
    /// <see cref="ResolveTargetPool"/> reads — and verify the write took.</summary>
    private static bool TrySetPool(EventOption opt, CardPoolModel pool)
    {
        try
        {
            if (OnChosenProp?.GetValue(opt) is not Delegate del) return false;
            var target = del.Target;
            if (target == null) return false;
            foreach (var f in target.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (typeof(CardPoolModel).IsAssignableFrom(f.FieldType))
                {
                    f.SetValue(target, pool);
                    return ReferenceEquals(ResolveTargetPool(opt), pool);
                }
            }
        }
        catch { }
        return false;
    }

    private static string KeySuffix(string key)
    {
        int dot = key.LastIndexOf('.');
        return dot >= 0 ? key.Substring(dot + 1) : key;
    }

    /// <summary>
    /// The pool an option offers, read from the <c>CardPoolModel</c> captured by its <c>OnChosen</c>
    /// closure. Null when the option has no action (locked) or the closure shape is not what the
    /// event builds — the caller then falls back to the color-suffix map.
    /// </summary>
    internal static CardPoolModel? ResolveTargetPool(EventOption opt)
    {
        try
        {
            if (OnChosenProp?.GetValue(opt) is not Delegate del) return null;
            var target = del.Target;
            if (target == null) return null;
            foreach (var f in target.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (typeof(CardPoolModel).IsAssignableFrom(f.FieldType))
                    return f.GetValue(target) as CardPoolModel;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// EnergyColorName (as baked into the option TextKey) → pool title, for every unlocked character
    /// pool. Base-game pools are written last so they win a collision with a mod pool reusing a base
    /// color name — a wrong base match is better than silently mapping a base option to a mod pool.
    /// </summary>
    private static Dictionary<string, string> BuildColorToTitle(MegaCrit.Sts2.Core.Entities.Players.Player? player)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (player == null) return map;
        var baseAsm = typeof(EventModel).Assembly;
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantBase = pass == 1;
            foreach (var p in player.UnlockState.CharacterCardPools)
            {
                if (p == null || p.IsColorless) continue;
                if ((p.GetType().Assembly == baseAsm) != wantBase) continue;
                string color = p.EnergyColorName ?? string.Empty;
                if (color.Length > 0) map[color] = p.Title ?? string.Empty;
            }
        }
        return map;
    }
}
