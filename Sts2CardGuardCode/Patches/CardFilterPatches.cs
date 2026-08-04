using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2CardGuard.Patches;

/// <summary>
/// Reward + non-combat card creation (card rewards, and events/treasures that build their
/// card choices via <c>CardFactory.CreateForReward</c>). Every such path funnels through
/// <see cref="CardCreationOptions.GetPossibleCards"/> to obtain the candidate pool before the
/// rarity roll, so filtering the result covers all of them in one place — and does NOT touch
/// the card library / compendium (which reads <c>CardPoolModel.GetUnlockedCards</c> directly).
/// </summary>
[HarmonyPatch(typeof(CardCreationOptions), nameof(CardCreationOptions.GetPossibleCards))]
internal static class GetPossibleCards_Patch
{
    private static void Postfix(Player player, ref IEnumerable<CardModel> __result)
    {
        try { __result = CardGuardService.Filter(__result, player); }
        catch (Exception ex) { Log.Warn($"GetPossibleCards filter error: {ex.Message}"); }
    }
}

/// <summary>
/// In-combat card generation ("add/generate a random card" effects). These call
/// <c>CardFactory.GetForCombat</c> / <c>GetDistinctForCombat</c> with a candidate pool the
/// caller supplies (usually the character pool or the colorless pool). Filtering the pool
/// BEFORE the random pick constrains generation the same way as rewards, without touching the
/// effect logic. Count is preserved because the game still picks from the filtered set.
/// </summary>
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.GetForCombat))]
internal static class GetForCombat_Patch
{
    private static void Prefix(Player player, ref IEnumerable<CardModel> cards)
    {
        try { cards = CardGuardService.Filter(cards, player); }
        catch (Exception ex) { Log.Warn($"GetForCombat filter error: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.GetDistinctForCombat))]
internal static class GetDistinctForCombat_Patch
{
    private static void Prefix(Player player, ref IEnumerable<CardModel> cards)
    {
        try { cards = CardGuardService.Filter(cards, player); }
        catch (Exception ex) { Log.Warn($"GetDistinctForCombat filter error: {ex.Message}"); }
    }
}

/// <summary>
/// Shop cards. The merchant natively stocks only the character pool + colorless pool, so the
/// "other character" case is already handled — but filtering here still enforces
/// <see cref="CardGuardService.BlockOtherModCards"/> against mod cards injected into those
/// pools. Both overloads (by CardType and by CardRarity) share the <c>options</c> pool param.
/// </summary>
[HarmonyPatch]
internal static class CreateForMerchant_Patch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        typeof(CardFactory)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(CardFactory.CreateForMerchant))
            .Cast<MethodBase>();

    private static void Prefix(Player player, ref IEnumerable<CardModel> options)
    {
        try { options = CardGuardService.Filter(options, player); }
        catch (Exception ex) { Log.Warn($"CreateForMerchant filter error: {ex.Message}"); }
    }
}

/// <summary>
/// Reduces the number of cards a reward ASKS for to the number that survives the filter, so a block
/// never has to be softened to keep the game running.
///
/// <c>CreateForReward</c> builds its options one at a time, each pick added to a blacklist that the
/// next iteration subtracts (<c>GetPossibleCards().Except(blacklist)</c>). Ask for more cards than
/// the filter leaves and that set empties, at which point <c>RollForRarity</c> returns
/// <c>CardRarity.None</c> and the game throws "couldn't generate a valid rarity". Our own
/// <see cref="CardGuardService.Filter"/> cannot see this: it runs as a postfix on
/// <c>GetPossibleCards</c>, BEFORE the blacklist subtraction, so it only ever sees the full set.
///
/// Vanilla never reaches it (the pool always dwarfs the 3 cards a reward wants), but individual
/// base-card blocking makes it reachable — Insanity draws 1 card 30 times, Draft 3 cards 10 times,
/// SealedDeck 30 at once, and all three CLEAR THE DECK first, so throwing there loses the run.
/// Clamping the count keeps every offered card an allowed one and simply shows fewer of them.
///
/// The count is deliberately never clamped to 0: an empty reward screen crashes on
/// <c>NCardRewardSelectionScreen.DefaultFocusedControl</c> (<c>list[list.Count / 2]</c> on an empty
/// list). "At least one card stays allowed" is enforced up front by the settings panel.
///
/// Slightly conservative by construction: relics that WIDEN the pool (Dingy Rug adds colorless, the
/// CharacterCards modifier adds another character's pool) run inside the private overload, after
/// this prefix, so a run carrying one may see one card fewer than it could have. That direction is
/// harmless — the alternative, over-estimating, is what throws.
///
/// Co-op safe: every peer filters with the host's block-set against an identical pool, so all peers
/// clamp to the same number and consume the reward RNG identically.
/// </summary>
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.CreateForReward),
    new[] { typeof(Player), typeof(int), typeof(CardCreationOptions) })]
internal static class CreateForReward_Clamp_Patch
{
    private static void Prefix(Player player, ref int cardCount, CardCreationOptions options)
    {
        try
        {
            if (player == null || options == null || cardCount <= 1) return;
            if (CardGuardService.IsPassThrough) return;

            // Our GetPossibleCards postfix filters this for us, so what comes back is exactly the
            // set the game will draw from. FilterForPlayerCount runs after us inside the private
            // overload and only ever REMOVES, so apply it here too or the clamp over-estimates.
            IEnumerable<CardModel> possible;
            try { possible = options.GetPossibleCards(player); }
            catch { return; } // neither pool nor custom pool set — leave the game to its own error

            int allowed;
            try { allowed = CardFactory.FilterForPlayerCount(player.RunState, possible).Distinct().Count(); }
            catch { allowed = possible.Distinct().Count(); }

            if (allowed > 0 && allowed < cardCount)
            {
                Log.Info($"reward count clamped {cardCount} -> {allowed} (blocked cards left too few candidates).");
                cardCount = allowed;
            }
        }
        catch (Exception ex) { Log.Warn($"CreateForReward clamp error: {ex.Message}"); }
    }
}

/// <summary>
/// Card TRANSFORMATION (Bottled Change, transform events, …). These bypass every hook above:
/// <c>GetDefaultTransformationOptions</c> reads <c>CardPoolModel.GetUnlockedCards</c> directly, so a
/// blocked card would still be handed out as a transform result. Filtering the option list closes
/// that hole.
///
/// The empty case has to be handled at the CALLER, not here: the game's private
/// <c>GetFilteredTransformationOptions</c> throws "All transformation options provided are invalid!"
/// on an empty set, and it applies further filters of its own after ours. So when nothing allowed
/// remains, the transform is turned into a no-op (the card becomes a fresh copy of itself) rather
/// than either throwing or handing back a blocked card. No RNG is consumed on that path, and the
/// decision is identical on every peer (same host block-set, same pool), so co-op stays in step.
/// </summary>
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.GetDefaultTransformationOptions))]
internal static class GetDefaultTransformationOptions_Patch
{
    private static void Postfix(CardModel original, ref IEnumerable<CardModel> __result)
    {
        try
        {
            var owner = original?.Owner;
            if (owner == null || __result == null) return;
            __result = CardGuardService.FilterStrict(__result, owner);
        }
        catch (Exception ex) { Log.Warn($"transformation options filter error: {ex.Message}"); }
    }
}

/// <summary>Turns a transform with no allowed outcome into a no-op instead of a crash or a blocked
/// card. Shared by both <c>CreateRandomCardForTransform</c> overloads.</summary>
internal static class TransformGuard
{
    /// <summary>A stand-in "transformed" card that is really the original again. CardScope is
    /// nullable; with none there is nothing to create through, so the original is handed straight
    /// back — still a no-op, still never a blocked card.</summary>
    public static CardModel NoOp(CardModel original)
    {
        var scope = original.CardScope;
        return scope != null ? scope.CreateCard(original.CanonicalInstance, original.Owner) : original;
    }
}

/// <summary>Transform using the game's own default options — skipped when the filter leaves nothing
/// to transform into. <c>GetDefaultTransformationOptions</c> already applies the game's own
/// validity pass (rarity band, combat-generatable, not the original card) before ours, so an
/// <c>Any()</c> here is the real answer, not an optimistic one.</summary>
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.CreateRandomCardForTransform),
    new[] { typeof(CardModel), typeof(bool), typeof(Rng) })]
internal static class CreateRandomCardForTransform_Patch
{
    private static bool Prefix(CardModel original, bool isInCombat, ref CardModel __result)
    {
        try
        {
            if (original == null || CardGuardService.IsPassThrough) return true;
            if (CardFactory.GetDefaultTransformationOptions(original, isInCombat).Any()) return true;

            __result = TransformGuard.NoOp(original);
            Log.Info($"transform of '{original.Id}' skipped — every option is blocked.");
            return false; // no-op transform; never consumes the caller's Rng
        }
        catch (Exception ex) { Log.Warn($"transform guard error: {ex.Message}"); return true; }
    }
}

/// <summary>
/// Transform against a CALLER-SUPPLIED option list. No vanilla code reaches this overload — every
/// base-game <c>CardTransformation</c> is built with no options or with one fixed replacement, so
/// <c>ReplacementOptions</c> is always null — but it is public API a card/relic mod can use, and an
/// unfiltered list here would be a way for a blocked card to reach the player.
///
/// Residual risk worth naming: the game runs its own validity pass on whatever we hand back, and
/// that pass throws on an empty set. Our filtering can only shrink the list, so a mod-supplied list
/// that survives our filter could in principle still be emptied by that pass. Nothing in the base
/// game exercises it, and passing the list through unfiltered would break the guarantee that a
/// blocked card never appears — so the filter stays and this stays a known edge.
/// </summary>
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.CreateRandomCardForTransform),
    new[] { typeof(CardModel), typeof(IEnumerable<CardModel>), typeof(bool), typeof(Rng) })]
internal static class CreateRandomCardForTransform_WithOptions_Patch
{
    private static bool Prefix(CardModel original, ref IEnumerable<CardModel> options, ref CardModel __result)
    {
        try
        {
            if (original == null || options == null || CardGuardService.IsPassThrough) return true;
            var owner = original.Owner;
            if (owner == null) return true;

            var kept = CardGuardService.FilterStrict(options, owner).ToList();
            if (kept.Count > 0) { options = kept; return true; }

            __result = TransformGuard.NoOp(original);
            Log.Info($"transform of '{original.Id}' skipped — every supplied option is blocked.");
            return false;
        }
        catch (Exception ex) { Log.Warn($"transform (options) guard error: {ex.Message}"); return true; }
    }
}
