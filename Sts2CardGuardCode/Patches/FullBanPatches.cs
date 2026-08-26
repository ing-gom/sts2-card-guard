using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2CardGuard.Patches;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Full ban: when EVERY potion (or every event) is blocked, close the routes instead of thinning them.
//
// Blocking most of a pool is a filter; blocking all of it is a different request — "no potions in this
// run", "no events in this run" — and the honest answer is for the sources to stop appearing at all.
// Half-measures are worse than either: an empty potion pool makes
// `PotionFactory.CreateRandomPotionOutOfCombat` call `.First()` on nothing and throw
// (InvalidOperationException, measured), and an all-blocked event list makes the skip gate run out of
// candidates and hand back an event it was told to hide.
//
// Every suppression below is gated on a per-player (potions) or per-run (events) "is everything
// blocked?" question, so a partial block-set behaves exactly as before. All of them are deterministic
// from the host-synced block-set, and each honours the run's pass-through flag through those helpers.
//
// Potion-granting relics and cards stay in the pools and simply do nothing (user's call): Phial
// Holster, Alchemical Coffer, Delicate Frond, Entropic Brew, Alchemize, the Crystal Sphere's potion
// slot, Cauldron / Lost Coffer / White Beast Statue and the potion events. Their offer still appears;
// it just has no potion to give. Suppressing the crash is mandatory, removing the relic is not.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Batched potion draws return an EMPTY list instead of throwing. Covers Phial Holster, Alchemical
/// Coffer and the merchant's 3-potion stock in one place, because a list-returning method can express
/// "nothing" without a null anybody has to check.
///
/// The original is skipped, so its RNG draws are not made. That shifts the rest of the run's
/// <c>Shops</c> / <c>CombatPotionGeneration</c> streams away from vanilla — deliberately, and
/// identically on every peer, which is the only property co-op needs.
///
/// ★NOT attribute-patched — applied by hand from <c>MainFile</c> via <see cref="Apply"/>.
///
/// Harmony binds <c>__result</c> to the ORIGINAL method's return type, and v0.111.0 changed this one
/// from <c>List&lt;PotionModel&gt;</c> to <c>IEnumerable&lt;PotionModel&gt;</c>. A mismatch is not a
/// quiet no-op: Harmony throws while patching, that takes down the whole <c>PatchAll</c> call, and
/// <c>MainFile.Initialize</c> never finishes — so ONE drifted return type killed every feature of the
/// mod on public-beta ("init failed: Patching exception in method ... CreateRandomPotionsOutOfCombat",
/// measured on v0.111.0). No attribute can express "either of two return types", hence the runtime
/// choice in <see cref="Apply"/>.
///
/// ★This class of break is invisible to metadata diffing: a Harmony target is named by
/// <c>typeof</c> + <c>nameof</c>, so no member reference to it exists in the mod's IL for a MemberRef
/// scan to resolve. It only shows up by patching against the real build.
/// </summary>
internal static class PotionFactory_CreateRandomPotions_FullBan_Patch
{
    /// <summary>Applies the prefix whose <c>__result</c> matches this build's return type.</summary>
    internal static void Apply(Harmony harmony)
    {
        MethodInfo? original = AccessTools.Method(
            typeof(PotionFactory), nameof(PotionFactory.CreateRandomPotionsOutOfCombat));
        if (original == null)
        {
            Log.Warn("PotionFactory.CreateRandomPotionsOutOfCombat not found — "
                     + "a full potion ban may throw when something asks for a batch of potions.");
            return;
        }

        string prefix = original.ReturnType == typeof(List<PotionModel>)
            ? nameof(PrefixList)          // public v0.107.1 and earlier
            : nameof(PrefixEnumerable);   // public-beta v0.111.0 and later

        harmony.Patch(original, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(PotionFactory_CreateRandomPotions_FullBan_Patch), prefix)));
        Log.Info($"potion batch full-ban guard bound via {prefix} "
                 + $"(returns {original.ReturnType.Name}).");
    }

    private static bool PrefixList(Player player, ref List<PotionModel> __result)
    {
        if (!Suppress(player)) return true;
        __result = new List<PotionModel>();
        return false;
    }

    private static bool PrefixEnumerable(Player player, ref IEnumerable<PotionModel> __result)
    {
        if (!Suppress(player)) return true;
        __result = Array.Empty<PotionModel>();
        return false;
    }

    private static bool Suppress(Player player)
    {
        try { return PotionGuardService.IsFullyBannedFor(player); }
        catch (Exception ex)
        {
            Log.Warn($"potion batch full-ban guard error: {ex.Message}");
            return false;   // let the original run
        }
    }
}

/// <summary>
/// A potion reward populates to nothing rather than throwing. Paired with
/// <see cref="RewardsSet_GenerateWithoutOffering_FullBan_Patch"/>, which then drops it from the set —
/// this half only has to make sure the factory is never asked for a potion that cannot exist.
/// </summary>
[HarmonyPatch(typeof(PotionReward), nameof(PotionReward.Populate))]
internal static class PotionReward_Populate_FullBan_Patch
{
    private static bool Prefix(PotionReward __instance)
    {
        try { return !PotionGuardService.IsFullyBannedFor(__instance.Player); }
        catch (Exception ex) { Log.Warn($"potion reward full-ban guard error: {ex.Message}"); return true; }
    }
}

/// <summary>
/// Drops potion rewards out of a reward set entirely, so the screen shows one fewer reward rather than
/// an empty potion pedestal.
///
/// Both ends of the method, because rewards arrive at two different times: the set's own rewards exist
/// before it runs (combat's potion roll, and events / relics that build a custom set), while
/// <c>Hook.ModifyRewards</c> — how Cauldron and Lost Coffer add theirs — fires in the middle. The
/// Prefix keeps the first group from ever being seen by those hooks with a null potion; the Postfix
/// catches whatever the hooks added afterwards.
///
/// The combat roll itself is left alone on purpose: <c>PotionRewardOdds.Roll</c> carries a pity value
/// that persists in the save, so skipping the roll would quietly change the odds state. It rolls, and
/// the reward is dropped after.
/// </summary>
[HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.GenerateWithoutOffering))]
internal static class RewardsSet_GenerateWithoutOffering_FullBan_Patch
{
    private static void Prefix(RewardsSet __instance) => Strip(__instance);

    private static void Postfix(RewardsSet __instance) => Strip(__instance);

    private static void Strip(RewardsSet set)
    {
        try
        {
            if (set == null || !PotionGuardService.IsFullyBannedFor(set.Player)) return;
            int removed = set.Rewards.RemoveAll(r => r is PotionReward);
            if (removed > 0) Log.Info($"every potion is blocked — dropped {removed} potion reward(s) from a reward set.");
        }
        catch (Exception ex) { Log.Warn($"reward strip error: {ex.Message}"); }
    }
}

/// <summary>
/// Empties the merchant's potion shelf. The 3-potion draw is left to run (its RNG is already skipped by
/// the batch guard above) and the entries are cleared afterwards, so nothing here depends on the order
/// the inventory populates its shelves in.
/// </summary>
[HarmonyPatch(typeof(MerchantInventory), "PopulatePotionEntries")]
internal static class MerchantInventory_PopulatePotionEntries_FullBan_Patch
{
    private static void Postfix(MerchantInventory __instance)
    {
        try
        {
            if (__instance == null || !PotionGuardService.IsFullyBannedFor(__instance.Player)) return;
            int n = __instance._potionEntries.Count;
            __instance._potionEntries.Clear();
            if (n > 0) Log.Info($"every potion is blocked — cleared {n} merchant potion slot(s).");
        }
        catch (Exception ex) { Log.Warn($"merchant potion clear error: {ex.Message}"); }
    }
}

/// <summary>
/// Hides the shop's empty potion shelf from the screen's own slot enumeration.
///
/// ★Not cosmetic — this closes a crash the empty shelf would otherwise introduce. The screen fills slots
/// with <c>for (l = 0; l &lt; Inventory.PotionEntries.Count; l++)</c>, so an empty shelf leaves the scene's
/// three <c>NMerchantPotion</c> children un-filled with <c>Entry == null</c>, and
/// <c>DefaultFocusedControl</c> then runs <c>GetAllSlots().FirstOrDefault(s =&gt; s.Entry.IsStocked)</c>
/// with no null check. It only walks as far as a potion slot once every card and relic slot is sold out,
/// so it would surface as a rare late-shop crash — one vanilla can never hit, because vanilla always
/// fills all three.
///
/// ★Patched at <c>GetAllSlots</c>, the one method every slot consumer goes through
/// (<c>DefaultFocusedControl</c>, <c>SubscribeToEntries</c>, <c>UpdateNavigation</c>), rather than
/// removing the nodes from an <c>Initialize</c> postfix. Measured, with this mod set:
/// <c>NMerchantInventory.Initialize</c> carries our postfix (owner list confirmed) and still never runs
/// for a shop room, so node surgery there silently did nothing while the assertion kept reporting
/// "3 potion slots, 3 entry-less". Filtering the enumeration cannot miss its moment, and it also drops
/// the empty slots out of keyboard navigation. Entry-less slots only, and only under a full ban, so no
/// other consumer's view of the shop changes.
///
/// The empty pedestals are hidden as a side effect where possible; if that fails the slots are still
/// unreachable and unfocusable, which is the part that matters.
/// </summary>
[HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory.GetAllSlots))]
internal static class NMerchantInventory_GetAllSlots_FullBan_Patch
{
    private static void Postfix(NMerchantInventory __instance, ref IEnumerable<NMerchantSlot> __result)
    {
        try
        {
            if (__instance == null || __result == null) return;
            var inv = __instance.Inventory;
            if (inv == null || !PotionGuardService.IsFullyBannedFor(inv.Player)) return;

            var slots = __result as IList<NMerchantSlot> ?? __result.ToList();
            var kept = new List<NMerchantSlot>(slots.Count);
            int dropped = 0;
            foreach (var s in slots)
            {
                if (s == null) continue;
                // An entry-less slot is one the screen never filled — under a full ban that is the potion
                // shelf, and it is exactly what the null-unsafe consumer trips over.
                if (s.Entry == null) { dropped++; continue; }
                kept.Add(s);
            }
            if (dropped == 0) return;

            HidePotionShelf(__instance);
            __result = kept;
        }
        catch (Exception ex) { Log.Warn($"shop slot filter error: {ex.Message}"); }
    }

    /// <summary>Best-effort: take the empty pedestals off screen too. Idempotent, and a failure costs
    /// nothing — the slots are already out of the enumeration above.</summary>
    private static void HidePotionShelf(NMerchantInventory screen)
    {
        try
        {
            var container = screen._potionContainer;
            if (container == null || !GodotObject.IsInstanceValid(container) || !container.Visible) return;
            container.Visible = false;
            Log.Info("every potion is blocked — the shop's potion shelf is hidden and its slots unreachable.");
        }
        catch { }
    }
}

/// <summary>
/// The three single-potion grants that create THEN procure, so the empty-pool throw has to be stopped
/// at the caller rather than in the factory (a method returning a bare <c>PotionModel</c> has no way to
/// say "none" that its callers check).
///
/// Each becomes a no-op: the relic/card/potion still appears and still triggers, it just produces
/// nothing. Skipping the whole method also skips Alchemize's cast animation, which is the honest
/// reading of a card that can no longer do anything.
/// </summary>
[HarmonyPatch]
internal static class SinglePotionGrant_FullBan_Patch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        var methods = new List<System.Reflection.MethodBase>();
        void Add(Type t, string name)
        {
            var m = AccessTools.Method(t, name);
            if (m != null) methods.Add(m);
            else Log.Warn($"full-ban target not found: {t.Name}.{name} — that grant stays unguarded.");
        }
        Add(typeof(EntropicBrew), "OnUse");          // potion: fills every open slot
        Add(typeof(DelicateFrond), "BeforeCombatStart"); // relic: same, per combat
        Add(typeof(Alchemize), "OnPlay");             // card
        return methods;
    }

    private static bool Prefix(AbstractModel __instance, ref Task __result)
    {
        try
        {
            Player? owner = OwnerOf(__instance);
            if (!PotionGuardService.IsFullyBannedFor(owner)) return true;
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex) { Log.Warn($"single potion grant guard error: {ex.Message}"); return true; }
    }

    /// <summary>The player a relic / card / potion belongs to. Read through the concrete types rather
    /// than one shared base, because <c>Owner</c> is declared separately on each.</summary>
    private static Player? OwnerOf(AbstractModel model) => model switch
    {
        RelicModel r => r.Owner,
        CardModel c => c.Owner,
        PotionModel p => p.Owner,
        _ => null,
    };
}

/// <summary>
/// The Crystal Sphere event's potion slot. Its <c>ToReward</c> already returns a nullable
/// <see cref="Reward"/>, so returning null is a supported "this slot holds nothing" answer — no reward
/// appears where a potion would have.
/// </summary>
[HarmonyPatch(typeof(CrystalSpherePotion), nameof(CrystalSpherePotion.ToReward))]
internal static class CrystalSpherePotion_ToReward_FullBan_Patch
{
    private static bool Prefix(Player owner, ref Reward? __result)
    {
        try
        {
            if (!PotionGuardService.IsFullyBannedFor(owner)) return true;
            __result = null;
            return false;
        }
        catch (Exception ex) { Log.Warn($"crystal sphere potion guard error: {ex.Message}"); return true; }
    }
}

/// <summary>
/// Keeps <c>?</c> map points from ever resolving to an event room when every event is blocked.
///
/// ★This is the game's OWN extension point for exactly this — <c>Hook.ModifyUnknownMapPointRoomTypes</c>
/// hands the candidate room types to every model in the run (Lantern Key uses it in the opposite
/// direction, to force an event), and <c>UnknownMapPointOdds.Roll</c> takes what comes back. There is no
/// dedicated event map point: apart from the Ancient node, EVERY event room in the game comes from a
/// <c>?</c>, so removing the type here closes the route completely.
///
/// The fallback is sound rather than lucky: with <c>Event</c> gone, <c>Roll</c> seeds its answer with
/// <c>roomTypes.Order().First()</c>, and <c>RoomType</c> orders Monster(1) before Elite(2) — so a <c>?</c>
/// becomes a normal fight, or the shop / treasure the odds table already allowed. Elite cannot be it
/// (its odds are −1, the game's "never roll this" marker). The RNG draw still happens either way, so
/// the stream does not shift.
///
/// Patched at the Hook rather than at the odds table so it applies after every model has had its say,
/// including Lantern Key: with all events banned, "force an event" has nothing to force.
///
/// Two events survive by design, both unreachable from here: the Ancient node (Neow) is a separate pull,
/// and a player's very first run gets two scripted <c>?</c> events from an early return inside
/// <c>Roll</c> that never consults this hook.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyUnknownMapPointRoomTypes))]
internal static class Hook_ModifyUnknownMapPointRoomTypes_FullBan_Patch
{
    private static void Postfix(IRunState runState, ref IReadOnlySet<RoomType> __result)
    {
        try
        {
            if (runState == null || __result == null || !__result.Contains(RoomType.Event)) return;

            var titles = RunTitles(runState);
            if (!EventGuardService.AreAllEventsBannedForAct(runState, titles)) return;

            var kept = new HashSet<RoomType>(__result);
            kept.Remove(RoomType.Event);
            if (kept.Count == 0) return; // nothing else could be rolled — leave vanilla alone
            __result = kept;
            Log.Info("every event is blocked — a '?' map point will not resolve to an event room.");
        }
        catch (Exception ex) { Log.Warn($"unknown map point guard error: {ex.Message}"); }
    }

    private static IReadOnlyCollection<string> RunTitles(IRunState runState)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in runState.Players)
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
