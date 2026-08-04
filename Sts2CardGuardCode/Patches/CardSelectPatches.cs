using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace Sts2CardGuard.Patches;

/// <summary>
/// Keeps a card-selection screen's REQUIRED pick count within what it was actually given.
///
/// A selector is built from two independent numbers: the cards to offer, and how many the player
/// must pick (<c>CardSelectorPrefs.MinSelect</c> / <c>MaxSelect</c>). Vanilla always offers at least
/// as many as it demands, so the two never disagree — but individual card blocking can thin the
/// offer while the demand stays hard-coded, and a screen that wants 10 picks out of 8 cards can
/// never satisfy its confirm button. Sealed Deck is the concrete case: it asks for 30 cards, demands
/// exactly 10, sets <c>RequireManualConfirmation = true</c> and <c>Cancelable = false</c>, and clears
/// the player's deck before any of it — a soft-lock with no way out.
///
/// Clamping here rather than at each event keeps it general: every reward-style selection funnels
/// through these two commands, so content added later is covered without another patch. Screens that
/// already degrade on their own are left alone — <c>FromSimpleGridForRewards</c> auto-takes the whole
/// set when <c>!RequireManualConfirmation &amp;&amp; cards.Count &lt;= MinSelect</c>, and both commands
/// already return empty for a zero-card list.
///
/// Rebuilt through the public 3-arg constructor because <c>MinSelect</c> / <c>MaxSelect</c> are
/// get-only. That constructor re-stamps the prompt's Amount/MinCount/MaxCount variables, and
/// <c>LocString</c> stores those in a dictionary by key (<c>_variables[key] = value</c>), so
/// re-stamping overwrites rather than duplicating — the prompt ends up quoting the clamped number,
/// which is what the player is actually being asked for.
///
/// Co-op safe: peers share the host's block-set and therefore the same offered count, so every peer
/// clamps to the same numbers and the choice synchronizer still matches index-for-index.
/// </summary>
internal static class SelectorClamp
{
    /// <summary>Prefs whose demand fits <paramref name="available"/>, or the original when it already
    /// does. Init-only properties are copied across; the two counts are the only things that move.</summary>
    public static CardSelectorPrefs Fit(CardSelectorPrefs prefs, int available)
    {
        if (available <= 0 || prefs.MinSelect <= available) return prefs;

        int min = Math.Min(prefs.MinSelect, available);
        int max = Math.Min(prefs.MaxSelect, available);
        if (max < min) max = min;

        Log.Info($"selection demand clamped {prefs.MinSelect}..{prefs.MaxSelect} -> {min}..{max} ({available} card(s) offered).");

        return new CardSelectorPrefs(prefs.Prompt, min, max)
        {
            RequireManualConfirmation = prefs.RequireManualConfirmation,
            Cancelable = prefs.Cancelable,
            Comparison = prefs.Comparison,
            UnpoweredPreviews = prefs.UnpoweredPreviews,
            PretendCardsCanBePlayed = prefs.PretendCardsCanBePlayed,
            ShouldGlowGold = prefs.ShouldGlowGold,
        };
    }
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGridForRewards))]
internal static class FromSimpleGridForRewards_Patch
{
    private static void Prefix(List<CardCreationResult> cards, ref CardSelectorPrefs prefs)
    {
        try
        {
            if (cards == null || CardGuardService.IsPassThrough) return;
            prefs = SelectorClamp.Fit(prefs, cards.Count);
        }
        catch (Exception ex) { Log.Warn($"reward selector clamp error: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid))]
internal static class FromSimpleGrid_Patch
{
    private static void Prefix(IReadOnlyList<CardModel> cardsIn, ref CardSelectorPrefs prefs)
    {
        try
        {
            if (cardsIn == null || CardGuardService.IsPassThrough) return;
            prefs = SelectorClamp.Fit(prefs, cardsIn.Count);
        }
        catch (Exception ex) { Log.Warn($"grid selector clamp error: {ex.Message}"); }
    }
}
