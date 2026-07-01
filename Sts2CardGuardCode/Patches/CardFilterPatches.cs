using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
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
