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
/// This postfix drops options whose target character is blocked for the current character. If every
/// option is blocked the event finishes with no cross-character offer — the game handles a
/// zero-option event by ending it (<c>SetEventState</c> sets <c>_isFinished</c>), so this is safe.
/// Read-only: it only removes options, never adds or reorders, so it can't desync a co-op run
/// beyond what card filtering already does (and it honours the same multiplayer pass-through).
///
/// An option is matched to its pool by reading the <c>CardPoolModel</c> captured in the option's
/// <c>OnChosen</c> closure (<c>() =&gt; OfferRewards(cardPool)</c>) — the pool object itself, so the
/// match is exact. The older approach parsed the <c>EnergyColorName</c> suffix the event bakes into
/// <see cref="EventOption.TextKey"/> and looked it up in a BASE-GAME-ONLY color map (to stop a mod
/// character reusing a base color name from shadowing the base entry). That map is why mod
/// characters were unblockable: RitsuLib's <c>ColorfulPhilosophersCardPoolColorOrderPatch</c>
/// appends opted-in mod character pools to the event's candidate order, so mod characters DO get
/// offered — and every one of them missed the base-only map, was never recognised, and survived the
/// filter. Picking it then handed the reward to the card filter, which found the whole pool blocked
/// and substituted allowed cards instead — the reported "blocked mod character is still offered,
/// and choosing it gives a mixed bag of the classes you did not block".
///
/// The color-suffix lookup is kept only as a fallback for when the closure can't be read, and now
/// includes mod pools (base pools still win a color-name collision).
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

            Dictionary<string, string>? colorToTitle = null; // built lazily — only if a closure read fails

            var kept = new List<EventOption>(__result.Count);
            foreach (var opt in __result)
            {
                if (opt == null) continue;

                var pool = ResolveTargetPool(opt);
                if (pool != null)
                {
                    string title = pool.Title ?? string.Empty;
                    if (CardGuardService.IsCrossCharacterBlocked(player, title)) continue;
                    // Character allowed, but individual card blocks may have emptied its pool —
                    // offering it would silently substitute another character's cards.
                    if (!CardGuardService.HasAnyAllowedCard(player, pool)) continue;
                    kept.Add(opt);
                    continue;
                }

                colorToTitle ??= BuildColorToTitle(player);
                string key = opt.TextKey ?? string.Empty;
                int dot = key.LastIndexOf('.');
                string color = dot >= 0 ? key.Substring(dot + 1) : key;
                if (colorToTitle.TryGetValue(color, out var poolTitle)
                    && CardGuardService.IsCrossCharacterBlocked(player, poolTitle))
                {
                    continue; // blocked character — hide this choice
                }
                kept.Add(opt);
            }

            if (kept.Count != __result.Count)
            {
                Log.Info($"ColorfulPhilosophers: dropped {__result.Count - kept.Count} blocked character option(s) (char={currentPool.Title})");
                __result = kept;
            }
        }
        catch (Exception ex) { Log.Warn($"ColorfulPhilosophers filter error: {ex.Message}"); }
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
