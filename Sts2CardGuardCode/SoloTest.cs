// solo-verify — single-player self-test for the relic-pack filter (v0.4.0).
//
// Drop `selftest.sp.flag` next to the mod DLL and launch one instance (solo-selftest.ps1). This
// auto-starts a single-player run (no save), then exercises the grab-bag relic filter directly:
// it populates a bag with NO block (baseline), then blocks a relic-adding mod and populates a fresh
// bag, and asserts the blocked mod's relics are gone from the second bag while base-game relics
// remain. Deterministic (no probability). Everything is file-based; the window opens but drives itself.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2CardGuard;

internal static class SoloTest
{
    private const string Tag = "CardGuard";
    private const double StepTimeoutSec = 90;

    private static readonly StringBuilder _out = new();
    private static bool _started, _done;
    private static string _step = "(not started)";
    private static DateTime _stepAt = DateTime.UtcNow;

    private static string ModDir() => Path.GetDirectoryName(typeof(SoloTest).Assembly.Location) ?? ".";

    public static void ArmIfRequested()
    {
        try
        {
            if (!File.Exists(Path.Combine(ModDir(), "selftest.sp.flag"))) return;
            W("solo selftest armed");
            Poll();
        }
        catch (Exception e) { Log($"solo arm failed: {e.Message}"); }
    }

    private static void Poll()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || _done) return;
        try { Tick(tree); } catch (Exception e) { W("tick exception: " + e.Message); }
        if (!_done) tree.CreateTimer(2.0).Timeout += Poll;
    }

    private static void Tick(SceneTree tree)
    {
        var run = RunManager.Instance;
        if (!_started && (run == null || !run.IsInProgress))
        {
            if (NGame.Instance == null) { W("waiting for NGame…"); return; }
            if (ModelCount() == 0) { W("waiting for ModelDb to populate…"); return; }
            // ★ModelDb-populated is still not enough in a heavy modded environment: the registry fills
            // long before the MAIN MENU finishes loading, so starting the run here transitions before the
            // game is ready — NTransition.RoomFadeOut() NREs inside StartNewSingleplayerRun. Gate on the
            // main-menu node existing (this bit the first run of this very test).
            if (FindNode<NMainMenu>(tree.Root) == null) { W("waiting for main menu…"); return; }
            _started = true;
            Step("starting single-player run");
            TaskHelper.RunSafely(StartRunThenTest());
            return;
        }

        if (_started && !_done && (DateTime.UtcNow - _stepAt).TotalSeconds > StepTimeoutSec)
        {
            W($"WATCHDOG: no progress for {StepTimeoutSec:F0}s at step '{_step}' — flushing partial result.");
            W($"WATCHDOG: overlay on top = {TopScreenName()}.");
            Flush(false);
        }
    }

    private static void Step(string name)
    {
        _step = name;
        _stepAt = DateTime.UtcNow;
        W($"— {name}");
    }

    /// <summary>Depth-first search for the first node of type T — gates run start on the main menu.</summary>
    private static T? FindNode<T>(Node n) where T : class
    {
        if (n is T t) return t;
        foreach (var c in n.GetChildren())
        {
            var r = FindNode<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    private static int ModelCount()
    {
        try
        {
            var f = typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
            return (f?.GetValue(null) as System.Collections.IDictionary)?.Count ?? 0;
        }
        catch { return 0; }
    }

    private static async Task StartRunThenTest()
    {
        try
        {
            var character = ModelDb.AllCharacters.First();
            var acts = ActModel.GetDefaultList().ToList();

            // ★Simulate what character select does before any run: constructing a StartRunLobby (even a
            // solo one) puts every guard into the pass-through fail-safe. The harness starts runs by
            // calling StartNewSingleplayerRun directly, so without this it never exercised the reset
            // that brings them back — which is exactly how potion/event filtering shipped doing nothing
            // in singleplayer while every assert here passed.
            Multiplayer.MultiplayerSync.DisableAllFiltering();
            W($"pre-run: forced the lobby fail-safe — anyPassThrough={Multiplayer.MultiplayerSync.AnyPassThrough}");
            await NGame.Instance.StartNewSingleplayerRun(character, shouldSave: false, acts,
                Array.Empty<ModifierModel>(), "SOLOTEST", GameMode.Standard, 0);
            await Task.Delay(3000);

            var run = RunManager.Instance;
            if (run?.IsInProgress != true || (run.State?.Players?.Count ?? 0) == 0)
            { W("run did not start"); Flush(false); return; }
            var player = run.State!.Players.First();
            W($"run started: {player.Character?.Id.Entry}, floor {run.State.TotalFloor}");

            // The reset the run start is supposed to perform. Every filter below is meaningless if a
            // guard is still in pass-through, so this is asserted before anything else runs.
            bool okMpState = !Multiplayer.MultiplayerSync.AnyPassThrough;
            W($"assert: no guard left in pass-through after a singleplayer run start = {okMpState} "
              + $"(card={CardGuardService.IsPassThrough}, relic={RelicGuardService.IsPassThrough}, "
              + $"potion={PotionGuardService.IsPassThrough}, event={EventGuardService.IsPassThrough})");

            StartAutomation();
            await Shot("1_run");

            bool okBag = await RunRelicFilterTest(player);
            bool okRelicOne = RunIndividualRelicTest(player);
            bool okCardOne = RunIndividualCardTest(player);
            bool okCfg = RunConfigRoundTripTest(player);
            bool okLink = RunPackLinkageTest(player);
            bool okPhil = await RunPhilosophersOptionTest(player);
            bool okPhilEdge = await RunPhilosophersEdgeTest(player);
            bool okBaseClamp = RunBaseCardClampTest(player);
            bool okAllScope = RunAllScopeTest(player);
            bool okPotion = RunPotionFilterTest(player);
            bool okEvent = RunEventBlockTest(player);
            bool okShopPartial = RunShopPartialBanTest(player);
            bool okFullBan = await RunFullBanTest(player);
            await ShotPanel(player);

            bool ok = okBag && okRelicOne && okCardOne && okCfg && okLink && okPhil && okPhilEdge
                      && okBaseClamp && okAllScope && okPotion && okEvent && okShopPartial && okFullBan
                      && okMpState && _hoverOk;
            W($"=== summary: grabbag/ancient={okBag}, individual-relic={okRelicOne}, "
              + $"individual-card={okCardOne}, config-roundtrip={okCfg}, pack-linkage={okLink}, "
              + $"philosophers={okPhil}, philosophers-edge={okPhilEdge}, base-clamp={okBaseClamp}, "
              + $"all-scope={okAllScope}, potion={okPotion}, event={okEvent}, "
              + $"shop-partial={okShopPartial}, full-ban={okFullBan}, mp-state={okMpState}, "
              + $"hover-preview={_hoverOk} ===");

            await Shot("2_final");
            W("=== solo test done ===");
            Flush(ok);
        }
        catch (Exception e) { W("test exception: " + e); Flush(false); }
    }

    private static readonly HashSet<string> GrabBagRarities =
        new(StringComparer.OrdinalIgnoreCase) { "Common", "Uncommon", "Rare", "Shop" };

    /// <summary>
    /// Core assertion. Build the universe of grab-bag-rarity relics from every relic pool (so the test
    /// is not tied to the run character), populate a bag with nothing blocked (baseline), then block a
    /// mod that actually contributes such relics and populate a fresh bag from the same list. The
    /// blocked mod's relics must be gone from the second bag while base-game relics remain. Fully
    /// deterministic — no probability, no live rewards. Uses the (IEnumerable,Rng) Populate overload,
    /// which the same Postfix filters.
    /// </summary>
    private static async Task<bool> RunRelicFilterTest(Player player)
    {
        Step("discover relic-adding mods");
        var packs = RelicGuardService.GetModRelicPacks();
        W($"relic mods discovered: {packs.Count}");
        foreach (var p in packs.OrderByDescending(p => p.Value.Total))
            W($"  {p.Key}: {p.Value.Total} total, rarities [{string.Join(", ", p.Value.PerRarity.Select(r => $"{r.Key}={r.Value}"))}]");
        if (packs.Count == 0)
        {
            W("assert: NO relic-adding mod installed — cannot exercise the filter (install e.g. SlayTheUniverse).");
            return false;
        }

        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";
        W($"configuring FOR character: {charTitle}");

        // The universe of grab-bag-rarity relics across ALL pools (base + every mod), unlock-agnostic.
        var us = UnlockState.all;
        List<RelicModel> universe;
        try
        {
            universe = ModelDb.AllRelicPools
                .SelectMany(pool => { try { return pool.GetUnlockedRelics(us); } catch { return Enumerable.Empty<RelicModel>(); } })
                .Where(r => r != null && GrabBagRarities.Contains(r.Rarity.ToString()))
                .Distinct()
                .ToList();
        }
        catch (Exception e) { W($"could not build relic universe: {e.Message}"); return false; }

        // Pick a mod that actually contributes grab-bag-rarity relics to that universe.
        var modCounts = universe.Where(RelicGuardService.IsModRelic)
            .GroupBy(RelicGuardService.ModNameOf, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        W($"grab-bag universe: {universe.Count} relic(s); mod contributors [{string.Join(", ", modCounts.Select(m => $"{m.Key}={m.Value}"))}]");

        if (modCounts.Count == 0)
        {
            W("assert: no mod contributes grab-bag-rarity relics — nothing this filter can act on in the installed set.");
            return false;
        }
        string target = modCounts.OrderByDescending(m => m.Value).First().Key;
        W($"target mod: {target} ({modCounts[target]} grab-bag relic(s))");

        bool wasBlocked = !RelicGuardService.GetModAllowed(charTitle, target);
        RelicGuardService.SetModAllowed(charTitle, target, true); // baseline: allowed

        Step("baseline populate (nothing blocked)");
        var (baseBaseline, targetBaseline) = PopulateAndCount(universe, target);
        W($"baseline bag: {targetBaseline} '{target}' relic(s), {baseBaseline} base relic(s)");
        if (targetBaseline == 0)
        { W("assert: baseline bag has 0 target relics (unexpected) — aborting."); RestoreBlock(charTitle, target, wasBlocked); return false; }

        Step($"block target mod for {charTitle}, re-populate");
        RelicGuardService.SetModAllowed(charTitle, target, false);
        var (baseFiltered, targetFiltered) = PopulateAndCount(universe, target);
        W($"filtered bag: {targetFiltered} '{target}' relic(s), {baseFiltered} base relic(s)");

        Step("grab-bag verdict");
        bool targetGone = targetFiltered == 0;
        bool baseKept = baseFiltered > 0 && baseFiltered == baseBaseline;
        W($"assert: blocked mod relics removed = {targetGone} ({targetBaseline} -> {targetFiltered})");
        W($"assert: base relics untouched = {baseKept} ({baseBaseline} -> {baseFiltered})");

        // Ancient case: relics that never enter the grab bag (granted directly via RelicCmd.Obtain).
        bool ancientOk = await RunAncientObtainTest(player, target, charTitle);

        // Ancient BEING: the mod's ancient event itself should be swapped to a vanilla one.
        bool ancientBeingOk = RunAncientBeingFilterTest(charTitle, target);

        RestoreBlock(charTitle, target, wasBlocked);
        return targetGone && baseKept && ancientOk && ancientBeingOk;
    }

    /// <summary>
    /// Verify the Ancient-being substitution. Using one fixed seed, regenerate an act's rooms twice:
    /// once with the mod ALLOWED (baseline — the act should place the mod's custom ancient) and once
    /// BLOCKED (the act's ancient must become a vanilla one, i.e. Darv). Same seed → the target mod's
    /// own patch picks the same ancient both times, so the only difference is our filter.
    /// </summary>
    private static bool RunAncientBeingFilterTest(string charTitle, string target)
    {
        Step("ancient-being: find an act the mod places a custom ancient in");
        var run = RunManager.Instance;
        var state = run?.State;
        if (state == null) { W("no run state — skipping ancient-being test."); return true; }
        var us = state.UnlockState;

        ActModel? testAct = null;
        string baselineId = "";
        RelicGuardService.SetModAllowed(charTitle, target, true); // allowed → baseline
        foreach (var act in state.Acts)
        {
            try
            {
                act.GenerateRooms(new Rng(4242u), us, isMultiplayer: false);
                var anc = SafeAncient(act);
                if (anc != null && string.Equals(anc.GetType().Assembly.GetName().Name, target, StringComparison.OrdinalIgnoreCase))
                { testAct = act; baselineId = anc.Id.ToString() ?? ""; break; }
            }
            catch (Exception e) { W($"  regen(baseline) failed: {e.Message}"); }
        }

        if (testAct == null) { W($"assert: no act places a '{target}' ancient in this seed — skipping (nothing to substitute)."); return true; }
        W($"baseline: act places '{baselineId}' (from {target})");

        Step("ancient-being: block, regenerate same act");
        RelicGuardService.SetModAllowed(charTitle, target, false);
        try { testAct.GenerateRooms(new Rng(4242u), us, isMultiplayer: false); }
        catch (Exception e) { W($"regen(filtered) failed: {e.Message}"); return false; }
        var filtered = SafeAncient(testAct);
        string filteredId = filtered?.Id.ToString() ?? "(null)";
        bool isVanilla = filtered != null && filtered.GetType().Assembly == typeof(AncientEventModel).Assembly;
        W($"after block: act ancient = '{filteredId}', vanilla = {isVanilla}");
        Step("ancient-being verdict");
        W($"assert: mod ancient substituted with vanilla = {isVanilla}");
        return isVanilla;
    }

    private static AncientEventModel? SafeAncient(ActModel act)
    {
        try { var r = act._rooms; return (r != null && r.HasAncient) ? r.Ancient : null; }
        catch { return null; }
    }

    /// <summary>
    /// Verify the direct-grant path: obtain an ANCIENT-rarity relic of the blocked mod (the kind an
    /// ancient event hands out via RelicCmd.Obtain, which the grab-bag filter can't reach) and confirm
    /// it is swapped out — the player must NOT end up holding the blocked mod relic.
    /// </summary>
    private static async Task<bool> RunAncientObtainTest(Player player, string target, string charTitle)
    {
        Step("find an ancient relic of the blocked mod");
        RelicModel? ancient = null;
        try
        {
            foreach (var pool in ModelDb.AllRelicPools)
            {
                foreach (var r in pool.AllRelics)
                {
                    if (r == null || !RelicGuardService.IsModRelic(r)) continue;
                    if (!string.Equals(RelicGuardService.ModNameOf(r), target, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(r.Rarity.ToString(), "Ancient", StringComparison.OrdinalIgnoreCase)) continue;
                    ancient = r; break;
                }
                if (ancient != null) break;
            }
        }
        catch (Exception e) { W($"ancient scan failed: {e.Message}"); }

        if (ancient == null) { W($"assert: '{target}' has no Ancient-rarity relic to test — skipping (grab-bag result stands)."); return true; }

        string ancId = ancient.Id.ToString() ?? "";
        W($"ancient relic under test: {ancId}");

        RelicGuardService.SetModAllowed(charTitle, target, false); // ensure blocked
        int before = player.Relics.Count;
        try { await RelicCmd.Obtain(ancient.ToMutable(), player); }
        catch (Exception e) { W($"obtain failed: {e.Message}"); return false; }
        await Task.Delay(400);

        bool hasBlockedAncient = player.Relics.Any(r => { try { return string.Equals(r.Id.ToString(), ancId, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
        bool gainedSomething = player.Relics.Count > before;
        var lastId = player.Relics.LastOrDefault()?.Id.ToString() ?? "(none)";
        W($"ancient obtain: player relics {before} -> {player.Relics.Count} (last='{lastId}')");
        Step("ancient verdict");
        W($"assert: blocked ancient relic NOT held = {!hasBlockedAncient}; a substitute WAS granted = {gainedSomething}");
        return gainedSomething && !hasBlockedAncient;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Individual (per-item) blocking. The pack checkbox stays ALLOWED throughout — otherwise these
    // would just be re-testing the pack gate the tests above already cover. The discriminating
    // assertion is therefore "victim gone AND its pack-mates still there".
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Relic side: block ONE relic of an allowed mod pack and re-populate the grab bag.</summary>
    private static bool RunIndividualRelicTest(Player player)
    {
        Step("individual relic: find a mod with >=2 grab-bag relics");
        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";

        List<RelicModel> universe;
        try
        {
            universe = ModelDb.AllRelicPools
                .SelectMany(pool => { try { return pool.GetUnlockedRelics(UnlockState.all); } catch { return Enumerable.Empty<RelicModel>(); } })
                .Where(r => r != null && GrabBagRarities.Contains(r.Rarity.ToString()))
                .Distinct().ToList();
        }
        catch (Exception e) { W($"universe build failed: {e.Message}"); return false; }

        var group = universe.Where(RelicGuardService.IsModRelic)
            .GroupBy(RelicGuardService.ModNameOf, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (group == null)
        {
            W("assert: no mod contributes >=2 grab-bag relics — cannot tell individual from pack blocking. SKIP.");
            return true;
        }

        string mod = group.Key;
        string victimId = RelicGuardService.RelicIdOf(group.First());
        W($"target: mod '{mod}' ({group.Count()} grab-bag relic(s)), victim '{victimId}'");

        RelicGuardService.SetModAllowed(charTitle, mod, true);       // pack ALLOWED for the whole test
        RelicGuardService.SetRelicAllowed(charTitle, victimId, true);

        Step("individual relic: baseline populate");
        var b = PopulateAndSplit(universe, mod, victimId);
        W($"baseline: victim={b.victim}, pack-mates={b.mates}, base={b.baseCount}");
        if (b.victim == 0) { W("assert: victim absent from baseline bag — aborting."); return false; }

        Step("individual relic: block ONE relic, re-populate");
        RelicGuardService.SetRelicAllowed(charTitle, victimId, false);
        var f = PopulateAndSplit(universe, mod, victimId);
        W($"filtered: victim={f.victim}, pack-mates={f.mates}, base={f.baseCount}");

        Step("individual relic verdict");
        bool victimGone = f.victim == 0;
        bool matesKept = b.mates > 0 && f.mates == b.mates;
        bool baseKept = f.baseCount == b.baseCount;
        W($"assert: blocked relic removed = {victimGone} ({b.victim} -> {f.victim})");
        W($"assert: same-pack relics KEPT = {matesKept} ({b.mates} -> {f.mates})");
        W($"assert: base relics untouched = {baseKept} ({b.baseCount} -> {f.baseCount})");

        RelicGuardService.SetRelicAllowed(charTitle, victimId, true);
        return victimGone && matesKept && baseKept;
    }

    /// <summary>Card side: block ONE card of an allowed mod pack and re-run the real Filter path.</summary>
    private static bool RunIndividualCardTest(Player player)
    {
        Step("individual card: find a mod card pack with >=2 cards visible to this character");
        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";

        var packs = CardGuardService.GetModCardPacks();
        W($"mod card packs discovered: {packs.Count}");
        foreach (var p in packs)
            W($"  {p.Key}: {p.Value.Total} card(s), {p.Value.Colorless} colorless, listed {p.Value.Cards.Count}");

        string mod = "";
        List<CardModel> visible = new();
        foreach (var kv in packs.OrderByDescending(k => k.Value.Cards.Count))
        {
            CardGuardService.SetModAllowed(charTitle, kv.Key, true);
            var v = kv.Value.Cards.Where(c => CardGuardService.WouldAppear(c, player)).ToList();
            if (v.Count >= 2) { mod = kv.Key; visible = v; break; }
        }
        if (visible.Count < 2)
        {
            W("assert: no mod card pack has >=2 cards reaching this character — SKIP.");
            return true;
        }

        var victim = visible[0];
        string victimId = CardGuardService.CardIdOf(victim);
        W($"target: pack '{mod}', {visible.Count} visible card(s), victim '{victimId}'");

        CardGuardService.SetCardAllowed(charTitle, victimId, true);

        Step("individual card: baseline Filter");
        var baseKept = CardGuardService.Filter(visible, player).ToList();
        W($"baseline: {baseKept.Count}/{visible.Count} kept");

        Step("individual card: block ONE card, re-Filter");
        CardGuardService.SetCardAllowed(charTitle, victimId, false);
        var kept = CardGuardService.Filter(visible, player).ToList();
        W($"filtered: {kept.Count}/{visible.Count} kept");

        Step("individual card verdict");
        bool victimGone = !kept.Any(c => string.Equals(CardGuardService.CardIdOf(c), victimId, StringComparison.OrdinalIgnoreCase));
        bool exactlyOne = kept.Count == baseKept.Count - 1;
        bool reportAgrees = !CardGuardService.WouldAppear(victim, player);
        W($"assert: blocked card removed = {victimGone}");
        W($"assert: exactly one card removed = {exactlyOne} ({baseKept.Count} -> {kept.Count})");
        W($"assert: WouldAppear(victim) == false = {reportAgrees}  (what the 'cardguard' console reports)");

        CardGuardService.SetCardAllowed(charTitle, victimId, true);
        return victimGone && exactlyOne && reportAgrees;
    }

    /// <summary>
    /// v0.6.0: BASE-GAME cards are individually blockable, and a block is absolute — where the game
    /// would want more cards than survive the filter, the demand is clamped rather than a blocked
    /// card being handed back. Drives the real <c>CardFactory.CreateForReward</c>, so the clamp
    /// prefix, our filter postfix and the game's own rarity roll are all exercised together; before
    /// v0.6.0 the same calls could not be starved at all, because base cards were unblockable.
    ///
    /// Only ever blocks cards it first observed as allowed, restores exactly those in the finally,
    /// and never calls <c>CardGuardConfig.Save</c> — the player's settings file is not touched.
    /// </summary>
    private static bool RunBaseCardClampTest(Player player)
    {
        Step("base-card clamp: enumerate own pool");
        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";
        var pool = player.Character?.CardPool;
        if (pool == null) { W("no character pool — SKIP."); return true; }

        List<CardModel> all;
        try
        {
            all = pool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
                      .Where(c => c != null).Distinct().ToList();
        }
        catch (Exception e) { W($"pool enumeration failed: {e.Message} — SKIP."); return true; }

        // Rewards roll a rarity from what survives, so the survivors must share a rarity the reward
        // odds can actually select. Common is the one every character pool has plenty of.
        var commons = all.Where(c => { try { return c.Rarity == CardRarity.Common; } catch { return false; } }).ToList();
        W($"own pool '{charTitle}': {all.Count} unlocked, {commons.Count} common");
        if (commons.Count < 2) { W("fewer than 2 commons — SKIP."); return true; }

        var survivors = commons.Take(2).ToList();
        var survivorIds = survivors.Select(CardGuardService.CardIdOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toBlock = all.Where(c => !survivorIds.Contains(CardGuardService.CardIdOf(c)))
                         .Where(c => CardGuardService.GetCardAllowed(charTitle, CardGuardService.CardIdOf(c)))
                         .ToList();

        bool baseBlockOk = false, clamp2Ok = false, clamp1Ok = false, prefsOk = false;
        try
        {
            Step("base-card clamp: block every own-pool card but 2 commons");
            foreach (var c in toBlock) CardGuardService.SetCardAllowed(charTitle, CardGuardService.CardIdOf(c), false);
            W($"blocked {toBlock.Count} card(s); survivors: {string.Join(", ", survivors.Select(c => c.Id.Entry))}");

            // (2) base-game blocking actually bites — this is the v0.6.0 feature itself.
            var kept = CardGuardService.Filter(all, player).ToList();
            baseBlockOk = kept.Count == survivors.Count
                          && kept.All(c => survivorIds.Contains(CardGuardService.CardIdOf(c)));
            W($"assert: base cards filtered to survivors only = {baseBlockOk} ({all.Count} -> {kept.Count})");

            // (3) a 3-card reward must come back as 2 allowed cards, not an exception.
            Step("base-card clamp: 3-card reward with 2 allowed");
            clamp2Ok = RewardYields(player, 3, 2, survivorIds);

            // (4) ...and down to a single card, the floor the UI rule guarantees.
            Step("base-card clamp: 3-card reward with 1 allowed");
            CardGuardService.SetCardAllowed(charTitle, CardGuardService.CardIdOf(survivors[1]), false);
            var oneId = new HashSet<string>(new[] { CardGuardService.CardIdOf(survivors[0]) }, StringComparer.OrdinalIgnoreCase);
            clamp1Ok = RewardYields(player, 3, 1, oneId);

            // (5) the selection-demand clamp, which is what keeps Sealed Deck from soft-locking.
            Step("base-card clamp: selector demand fit");
            var probe = new CardSelectorPrefs(new LocString("modifiers", "SEALED_DECK.selectionPrompt"), 10)
            { RequireManualConfirmation = true, Cancelable = false };
            var fitted = Patches.SelectorClamp.Fit(probe, 1);
            prefsOk = fitted.MinSelect == 1 && fitted.MaxSelect == 1
                      && fitted.RequireManualConfirmation && !fitted.Cancelable;
            W($"assert: prefs 10..10 fitted to 1 card = {prefsOk} "
              + $"({fitted.MinSelect}..{fitted.MaxSelect}, manualConfirm={fitted.RequireManualConfirmation}, cancelable={fitted.Cancelable})");
        }
        catch (Exception e) { W($"base-card clamp exception: {e}"); }
        finally
        {
            foreach (var c in toBlock) CardGuardService.SetCardAllowed(charTitle, CardGuardService.CardIdOf(c), true);
            foreach (var c in survivors) CardGuardService.SetCardAllowed(charTitle, CardGuardService.CardIdOf(c), true);
            W("restored own-pool card blocks");
        }

        return baseBlockOk && clamp2Ok && clamp1Ok && prefsOk;
    }

    /// <summary>
    /// The all-characters scope (v0.8.0). A block stored under <see cref="CardGuardService.AllScope"/>
    /// must bite for whoever is playing, WITHOUT any entry under that character's own key — the exact
    /// thing the per-character-only design could not express, and the reason a user could configure
    /// one scope and see the cards anyway. Also checks the scopes stay independent (clearing the
    /// character's own key does not clear the global one) and that the reserved key survives the
    /// config round trip. Restores every setting it touches and never calls Save.
    /// </summary>
    private static bool RunAllScopeTest(Player player)
    {
        Step("all-characters scope: pick a probe card");
        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";
        var pool = player.Character?.CardPool;
        if (pool == null) { W("no character pool — SKIP."); return true; }

        CardModel? probe;
        try
        {
            probe = pool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
                        .FirstOrDefault(c => c != null
                            && CardGuardService.GetCardAllowed(charTitle, CardGuardService.CardIdOf(c)));
        }
        catch (Exception e) { W($"pool enumeration failed: {e.Message} — SKIP."); return true; }
        if (probe == null) { W("no allowed card in the own pool — SKIP."); return true; }

        string probeId = CardGuardService.CardIdOf(probe);
        const string All = CardGuardService.AllScope;

        // A second character to exercise the pack-level (cross) block through the global scope.
        string? otherTitle = CardGuardService.GetAllCharacters()
            .Select(c => c.Title)
            .FirstOrDefault(t => !string.Equals(t, charTitle, StringComparison.OrdinalIgnoreCase));

        bool cardOk = false, isolationOk = false, crossOk = true, relicOk = true, tripOk = false;
        var relicPack = RelicGuardService.GetModRelicPacks().Keys.FirstOrDefault();
        try
        {
            // (1) global block only — nothing under the character's own key — still hides the card.
            W($"probe card {probe.Id} (character key '{charTitle}' left untouched)");
            CardGuardService.SetCardAllowed(All, probeId, false);
            bool hidden = !CardGuardService.WouldAppear(probe, player);
            bool ownKeyClean = CardGuardService.GetCardAllowed(charTitle, probeId);
            cardOk = hidden && ownKeyClean;
            W($"assert: global block hides the card = {hidden}, own key untouched = {ownKeyClean}");

            // (2) the two scopes are independent: allowing it for this character must NOT win over
            // the global block (a per-character tick cannot undo an all-characters one).
            CardGuardService.SetCardAllowed(charTitle, probeId, true);
            isolationOk = !CardGuardService.WouldAppear(probe, player);
            W($"assert: per-character allow does not override the global block = {isolationOk}");

            // (3) pack level: a character blocked globally is blocked for the run's character too.
            if (otherTitle != null)
            {
                CardGuardService.SetCrossAllowed(All, otherTitle, false);
                crossOk = CardGuardService.IsCrossCharacterBlocked(player, otherTitle)
                          && !CardGuardService.GetCrossAllowedEffective(charTitle, otherTitle);
                W($"assert: '{otherTitle}' blocked globally is blocked for '{charTitle}' = {crossOk}");
            }
            else W("only one character installed — cross-block sub-check SKIPPED.");

            // (4) relics honour the same scope.
            if (relicPack != null)
            {
                RelicGuardService.SetModAllowed(All, relicPack, false);
                var r = RelicGuardService.GetModRelicPacks()[relicPack].Relics.FirstOrDefault();
                relicOk = r != null && RelicGuardService.IsRelicBlockedForCharacter(r, charTitle);
                W($"assert: relic mod '{relicPack}' blocked globally is blocked for '{charTitle}' = {relicOk}");
            }
            else W("no relic-adding mod — relic sub-check SKIPPED.");

            // (5) the reserved key is carried by the save/co-op snapshot like any other scope.
            var snap = CardGuardService.SnapshotLocalBlocks();
            tripOk = CardGuardService.GetConfiguredTitles().Contains(All, StringComparer.Ordinal)
                     && snap.card.TryGetValue(All, out var ids) && ids.Contains(probeId);
            W($"assert: '{All}' scope present in the persisted/broadcast snapshot = {tripOk}");
        }
        catch (Exception e) { W($"all-scope exception: {e}"); }
        finally
        {
            CardGuardService.SetCardAllowed(All, probeId, true);
            CardGuardService.SetCardAllowed(charTitle, probeId, true);
            if (otherTitle != null) CardGuardService.SetCrossAllowed(All, otherTitle, true);
            if (relicPack != null) RelicGuardService.SetModAllowed(All, relicPack, true);
            W("restored all-characters scope blocks");
        }

        return cardOk && isolationOk && crossOk && relicOk && tripOk;
    }

    /// <summary>Asks the real reward factory for <paramref name="ask"/> cards and checks it came back
    /// with exactly <paramref name="expect"/>, every one of them allowed. A throw here is the failure
    /// the clamp exists to prevent ("couldn't generate a valid rarity").</summary>
    private static bool RewardYields(Player player, int ask, int expect, HashSet<string> allowedIds)
    {
        try
        {
            var options = CardCreationOptions.ForRoom(player, RoomType.Monster);
            var got = CardFactory.CreateForReward(player, ask, options).ToList();
            bool countOk = got.Count == expect;
            bool allAllowed = got.All(r => allowedIds.Contains(CardGuardService.CardIdOf(r.Card)));
            W($"assert: asked {ask}, got {got.Count} (expected {expect}) = {countOk}; "
              + $"all allowed = {allAllowed} [{string.Join(", ", got.Select(r => r.Card?.Id.Entry))}]");
            return countOk && allAllowed;
        }
        catch (Exception e)
        {
            W($"assert: reward generation THREW (this is the clamp failing): {e.Message}");
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Potions. Two assertions on the real generation path:
    //   1) a blocked potion disappears from PotionFactory.GetPotionOptions — the set every random
    //      potion (rewards, shops, relics, cards, potion events) is drawn from — while its pool-mates
    //      stay;
    //   2) with only ONE potion left allowed, drawing still yields that potion and never a null. The
    //      factory rolls a RARITY first, so a thinned pool can leave the rolled rarity empty; this is
    //      what proves the game clamps to what is available (it does) instead of handing back a null
    //      that every caller dereferences (which is what a decompile of CreateRandomPotion suggests);
    //   3) and with EVERY potion blocked it throws — the reason the panel keeps a "one must remain"
    //      floor. Asserting the throw stops that floor being removed later as apparently pointless.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every potion id the panel can block, with its current allowed state — so a test can start from a
    /// known-permissive baseline and put the user's real configuration back afterwards.
    ///
    /// ★Needed because the settings file is the PLAYER's: this repo has already lost two test runs to
    /// ambient blocks (70 ironclad cards once, 48 ironclad potions later) making a test's premise false
    /// and the failure look like a regression.
    /// </summary>
    private static List<(string Id, bool Allowed)> SnapshotPotionState(string charTitle)
    {
        var snapshot = new List<(string, bool)>();
        foreach (var id in PotionGuardService.GetPotionGroups()
                     .SelectMany(g => g.Potions)
                     .Select(PotionGuardService.PotionIdOf)
                     .Where(i => i.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            snapshot.Add((id, PotionGuardService.GetPotionAllowed(charTitle, id)));
        return snapshot;
    }

    private static void RestorePotionState(string charTitle, List<(string Id, bool Allowed)> snapshot)
    {
        foreach (var (id, allowed) in snapshot) PotionGuardService.SetPotionAllowed(charTitle, id, allowed);
    }

    private static bool RunPotionFilterTest(Player player)
    {
        Step("potion: baseline options");
        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";
        var none = Array.Empty<PotionModel>();

        // Start permissive whatever the player's own config says; restored in the finally.
        var ambient = SnapshotPotionState(charTitle);
        int ambientBlocked = ambient.Count(a => !a.Allowed);
        if (ambientBlocked > 0) W($"(clearing {ambientBlocked} potion block(s) from the live config for this test)");
        foreach (var (id, _) in ambient) PotionGuardService.SetPotionAllowed(charTitle, id, true);

        List<PotionModel> baseline;
        try { baseline = PotionFactory.GetPotionOptions(player, none).Where(p => p != null).Distinct().ToList(); }
        catch (Exception e) { W($"assert: GetPotionOptions THREW: {e.Message}"); return false; }
        W($"baseline potion options: {baseline.Count}");
        if (baseline.Count < 2) { W("assert: fewer than 2 potions available — cannot tell blocking from emptiness. SKIP."); return true; }

        var victim = baseline[0];
        string victimId = PotionGuardService.PotionIdOf(victim);
        W($"victim: '{victimId}' ({victim.Rarity}), pool groups={PotionGuardService.GetPotionGroups().Count}");

        Step("potion: block ONE potion");
        PotionGuardService.SetPotionAllowed(charTitle, victimId, false);
        List<PotionModel> filtered;
        try { filtered = PotionFactory.GetPotionOptions(player, none).Where(p => p != null).Distinct().ToList(); }
        catch (Exception e) { W($"assert: GetPotionOptions THREW after block: {e.Message}"); PotionGuardService.SetPotionAllowed(charTitle, victimId, true); return false; }

        bool victimGone = !filtered.Any(p => PotionGuardService.PotionIdOf(p) == victimId);
        bool matesKept = filtered.Count == baseline.Count - 1;
        W($"assert: blocked potion removed = {victimGone}");
        W($"assert: every other potion kept = {matesKept} ({baseline.Count} -> {filtered.Count})");

        Step("potion: rarity starvation — leave exactly one potion allowed");
        var survivor = filtered.Count > 0 ? filtered[0] : victim;
        string survivorId = PotionGuardService.PotionIdOf(survivor);
        foreach (var p in baseline)
        {
            string id = PotionGuardService.PotionIdOf(p);
            if (id != survivorId) PotionGuardService.SetPotionAllowed(charTitle, id, false);
        }

        bool drawOk;
        try
        {
            // 20 draws because the rarity roll is random: if the game did honour an empty rarity, a
            // survivor of a rare-only pool would come back null most of the time, so 20 clean draws
            // cannot be luck.
            var singles = new List<PotionModel?>();
            for (int i = 0; i < 20; i++)
                singles.Add(PotionFactory.CreateRandomPotionOutOfCombat(player, player.PlayerRng.Shops));
            int nulls = singles.Count(p => p == null);
            int wrong = singles.Count(p => p != null && PotionGuardService.PotionIdOf(p) != survivorId);
            drawOk = nulls == 0 && wrong == 0;
            W($"assert: 20 single draws, 1 allowed potion ({survivor.Rarity}) -> {nulls} null, {wrong} not the survivor = {drawOk}");

            // The merchant's 3-in-one-call. How MANY it returns when the pool is that thin is the
            // game's business (measured: it clamps to 1) — what must hold is no nulls and nothing
            // blocked, so that is what is asserted and the count is only reported.
            var batch = PotionFactory.CreateRandomPotionsOutOfCombat(player, 3, player.PlayerRng.Shops);
            int batchNulls = batch.Count(p => p == null);
            int batchWrong = batch.Count(p => p != null && PotionGuardService.PotionIdOf(p) != survivorId);
            W($"assert: 3-draw -> {batch.Count} entr(ies), {batchNulls} null, {batchWrong} not the survivor "
              + $"[{string.Join(", ", batch.Select(p => p == null ? "<null>" : p.Id.Entry))}]");
            drawOk = drawOk && batchNulls == 0 && batchWrong == 0;
        }
        catch (Exception e)
        {
            W($"assert: starved draw THREW: {e.Message}");
            drawOk = false;
        }

        Step("potion: empty pool must throw (this is what the panel's floor prevents)");
        bool floorNeeded;
        PotionGuardService.SetPotionAllowed(charTitle, survivorId, false);
        try
        {
            var doomed = PotionFactory.CreateRandomPotionOutOfCombat(player, player.PlayerRng.Shops);
            floorNeeded = false;
            W($"assert: draw from an EMPTY pool threw = False (returned '{(doomed == null ? "<null>" : doomed.Id.Entry)}') "
              + "— re-check whether the one-potion floor is still load-bearing");
        }
        catch (Exception e)
        {
            floorNeeded = true;
            W($"assert: draw from an EMPTY pool threw = True ({e.GetType().Name}) — floor is load-bearing");
        }
        bool floorRefuses = !PotionGuardService.AnyPotionAllowedFor(charTitle);
        W($"assert: AnyPotionAllowedFor reports the empty pool = {floorRefuses} (this is what the panel refuses on)");

        // Put the player's own configuration back, not merely "everything allowed".
        RestorePotionState(charTitle, ambient);

        return victimGone && matesKept && drawOk && floorNeeded && floorRefuses;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Map events. Drives the real gate — RoomSet.EnsureNextEventIsValid, the sole thing
    // ActModel.PullNextEvent consults — and asserts a blocked event is stepped over for an event that
    // is itself allowed and unvisited. The act's event cursor is restored afterwards, so the live run
    // is left exactly as it was found.
    // ─────────────────────────────────────────────────────────────────────────

    private static bool RunEventBlockTest(Player player)
    {
        Step("event: locate the act's event list");
        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";
        var state = RunManager.Instance?.State;
        var act = state?.Act;
        var rooms = act?._rooms;
        if (state == null || rooms == null || rooms.events.Count < 2)
        {
            W($"assert: no usable act event list (events={rooms?.events.Count ?? -1}). SKIP.");
            return true;
        }

        int cursor = rooms.eventsVisited;
        W($"event list: {rooms.events.Count} entries, cursor {cursor}, groups={EventGuardService.GetEventGroups().Count}");
        try
        {
            rooms.EnsureNextEventIsValid(state);
            var before = rooms.NextEvent;
            string beforeId = EventGuardService.EventIdOf(before);
            W($"baseline next event: '{beforeId}'");

            Step("event: block that event, re-run the gate");
            EventGuardService.SetEventAllowed(charTitle, beforeId, false);
            rooms.eventsVisited = cursor;
            rooms.EnsureNextEventIsValid(state);
            var after = rooms.NextEvent;
            string afterId = EventGuardService.EventIdOf(after);

            Step("event verdict");
            var titles = new[] { charTitle };
            bool moved = afterId != beforeId;
            bool afterAllowed = !EventGuardService.IsEventBlockedForAny(after, titles);
            bool afterValid = after.IsAllowed(state) && !state.VisitedEventIds.Contains(after.Id);
            W($"assert: gate moved off the blocked event = {moved} ('{beforeId}' -> '{afterId}')");
            W($"assert: replacement is not blocked = {afterAllowed}");
            W($"assert: replacement still satisfies the game's own conditions = {afterValid}");

            EventGuardService.SetEventAllowed(charTitle, beforeId, true);
            return moved && afterAllowed && afterValid;
        }
        catch (Exception e)
        {
            W($"assert: event gate THREW: {e.Message}");
            return false;
        }
        finally
        {
            rooms.eventsVisited = cursor; // leave the live run's cursor where we found it
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The shop under a PARTIAL potion ban — reported from real play as "I filtered potions for a
    // character and the shop is still selling potions".
    //
    // Nothing covered this combination before: the potion test asserts on GetPotionOptions, and the
    // full-ban test asserts an EMPTY shelf. Neither answers "does a shop that still HAS potions only
    // stock allowed ones". Twenty inventories, because each one draws three potions and a single shop
    // could miss the blocked ones by luck.
    // ─────────────────────────────────────────────────────────────────────────

    private static bool RunShopPartialBanTest(Player player)
    {
        Step("shop partial ban: block half the potions this character can be offered");
        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";

        var ambient = SnapshotPotionState(charTitle);
        int ambientBlocked = ambient.Count(a => !a.Allowed);
        if (ambientBlocked > 0) W($"(clearing {ambientBlocked} potion block(s) from the live config for this test)");
        foreach (var (id, _) in ambient) PotionGuardService.SetPotionAllowed(charTitle, id, true);

        var offered = new List<PotionModel>();
        try
        {
            offered = PotionFactory.GetPotionOptions(player, Array.Empty<PotionModel>())
                .Where(p => p != null).Distinct().ToList();
        }
        catch (Exception e) { W($"GetPotionOptions failed: {e.Message} — SKIP."); RestorePotionState(charTitle, ambient); return true; }
        if (offered.Count < 4)
        {
            W($"only {offered.Count} potion(s) offered — SKIP.");
            RestorePotionState(charTitle, ambient);
            return true;
        }

        // Block every OTHER potion by id order, so both rarities and pools are mixed on each side and
        // the shop still has plenty to stock from.
        var ordered = offered.OrderBy(PotionGuardService.PotionIdOf, StringComparer.Ordinal).ToList();
        var blocked = new HashSet<string>(
            ordered.Where((_, i) => i % 2 == 0).Select(PotionGuardService.PotionIdOf),
            StringComparer.OrdinalIgnoreCase);

        bool optionsOk = false, shopOk = false, notEmptyOk = false;
        try
        {
            foreach (var id in blocked) PotionGuardService.SetPotionAllowed(charTitle, id, false);
            W($"blocked {blocked.Count} of {ordered.Count} potion(s) for '{charTitle}' (partial, not a full ban); "
              + $"fullyBanned={PotionGuardService.IsFullyBannedFor(player)}");

            var stillOffered = PotionFactory.GetPotionOptions(player, Array.Empty<PotionModel>())
                .Where(p => p != null).Select(PotionGuardService.PotionIdOf).ToList();
            int leaked = stillOffered.Count(id => blocked.Contains(id));
            optionsOk = leaked == 0 && stillOffered.Count > 0;
            W($"assert: draw pool holds no blocked potion = {optionsOk} "
              + $"({stillOffered.Count} offered, {leaked} blocked leaked)");

            // The reported path: real merchant inventories, built the way a shop room builds them.
            int stocked = 0, shopLeaks = 0;
            for (int i = 0; i < 20; i++)
            {
                var inv = MerchantInventory.CreateForNormalMerchant(player);
                foreach (var entry in inv.PotionEntries)
                {
                    var model = entry?.Model;
                    if (model == null) continue;
                    stocked++;
                    if (blocked.Contains(PotionGuardService.PotionIdOf(model))) shopLeaks++;
                }
            }
            shopOk = shopLeaks == 0;
            notEmptyOk = stocked > 0;
            W($"assert: 20 merchant shelves stock no blocked potion = {shopOk} "
              + $"({stocked} potion(s) stocked, {shopLeaks} blocked)");
            W($"assert: a partial ban still leaves the shop selling potions = {notEmptyOk} "
              + "(a partial block is a filter, not a ban — an empty shelf here would be the bug)");
        }
        catch (Exception e) { W($"shop partial ban exception: {e}"); }
        finally
        {
            RestorePotionState(charTitle, ambient);
            W("restored the player's own potion configuration");
        }

        return optionsOk && shopOk && notEmptyOk;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Full ban. Blocking EVERY potion / event is a supported state, and the point of it is that the
    // sources stop existing rather than thinning out. Both halves are asserted:
    //
    //   potions — the batch draw returns nothing instead of throwing, a potion reward populates to
    //             nothing and is dropped from its set, the merchant stocks no potions, and a
    //             potion-granting relic no-ops. Every one of these throws without the guards (the
    //             empty-pool InvalidOperationException measured in the potion test above).
    //   events  — the game's own candidate-type hook no longer offers RoomType.Event, so a '?' cannot
    //             resolve to an event room. Rolled repeatedly, since one roll proves little.
    //
    // Ends with a real shop room + screenshot: removing the potion shelf also has to remove its slot
    // NODES, or DefaultFocusedControl dereferences a null Entry once the other slots sell out. That is
    // a layout/crash question no logic assert can answer.
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<bool> RunFullBanTest(Player player)
    {
        Step("full ban: block every potion for this character");
        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";

        var ambientPotions = SnapshotPotionState(charTitle);
        var everyPotion = ambientPotions.Select(a => a.Id).ToList();
        var everyEvent = EventGuardService.GetEventGroups()
            .SelectMany(g => g.Events)
            .Select(EventGuardService.EventIdOf)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        bool batchOk = false, rewardOk = false, stripOk = false, shelfOk = false, relicOk = false;
        bool hookOk = false, rollOk = false, bannedOk = false, eventBannedOk = false, slotNodesOk = false;
        try
        {
            foreach (var id in everyPotion) PotionGuardService.SetPotionAllowed(charTitle, id, false);
            bannedOk = PotionGuardService.IsFullyBannedFor(player);
            W($"assert: potions read as fully banned = {bannedOk} ({everyPotion.Count} blocked)");

            Step("full ban: batch draw returns nothing rather than throwing");
            try
            {
                var batch = PotionFactory.CreateRandomPotionsOutOfCombat(player, 3, player.PlayerRng.Shops);
                batchOk = batch.Count == 0;
                W($"assert: 3-potion batch draw is empty = {batchOk} ({batch.Count} returned)");
            }
            catch (Exception e) { W($"assert: batch draw THREW: {e.Message}"); }

            Step("full ban: potion reward populates to nothing and is dropped from its set");
            try
            {
                var reward = new PotionReward(player);
                reward.Populate();
                rewardOk = !reward.IsPopulated;
                W($"assert: potion reward left unpopulated (no throw) = {rewardOk}");

                // The game's own inspect-without-offering entry point, so this is the real strip path.
                var set = new RewardsSet(player).WithCustomRewards(new List<Reward>
                {
                    new PotionReward(player),
                    new GoldReward(10, 20, player),
                });
                await set.GenerateWithoutOffering();
                int potions = set.Rewards.Count(r => r is PotionReward);
                int others = set.Rewards.Count(r => r is not PotionReward);
                stripOk = potions == 0 && others > 0;
                W($"assert: potion reward dropped from the set, others kept = {stripOk} "
                  + $"({potions} potion, {others} other)");
            }
            catch (Exception e) { W($"assert: reward path THREW: {e.Message}"); }

            Step("full ban: merchant stocks no potions");
            try
            {
                var inv = MerchantInventory.CreateForNormalMerchant(player);
                shelfOk = inv.PotionEntries.Count == 0;
                W($"assert: merchant potion shelf empty = {shelfOk} ({inv.PotionEntries.Count} entr(ies), "
                  + $"cards={inv.CharacterCardEntries.Count}, relics={inv.RelicEntries.Count})");
            }
            catch (Exception e) { W($"assert: merchant build THREW: {e.Message}"); }

            Step("full ban: a potion-granting relic no-ops instead of throwing");
            try
            {
                var frond = ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.DelicateFrond>().ToMutable();
                await RelicCmd.Obtain(frond, player);
                await Task.Delay(300);
                int before = player.Potions.Count(p => p != null);
                await frond.BeforeCombatStart();
                int after = player.Potions.Count(p => p != null);
                relicOk = after == before;
                W($"assert: Delicate Frond granted nothing and did not throw = {relicOk} "
                  + $"(potions held {before} -> {after})");
            }
            catch (Exception e) { W($"assert: potion relic THREW: {e.Message}"); }

            Step("full ban: block every event, then ask the game's own candidate hook");
            foreach (var id in everyEvent) EventGuardService.SetEventAllowed(charTitle, id, false);
            var titles = new[] { charTitle };
            var state = RunManager.Instance?.State;
            // Judged against the ACT's own event list, which is what the suppression consults — see
            // AreAllEventsBannedForAct on why the discovered list is the wrong thing to ask.
            eventBannedOk = EventGuardService.AreAllEventsBannedForAct(state, titles);
            int actEvents = 0;
            try { actEvents = state?.Act?._rooms?.events?.Count ?? 0; } catch { }
            W($"assert: the act's whole event list reads as blocked = {eventBannedOk} "
              + $"({everyEvent.Count} ids blocked, act list holds {actEvents})");

            if (state == null) W("no run state — event half SKIPPED");
            else
            {
                var candidates = new HashSet<RoomType>
                {
                    RoomType.Monster, RoomType.Elite, RoomType.Treasure, RoomType.Shop, RoomType.Event,
                };
                var after = Hook.ModifyUnknownMapPointRoomTypes(state, candidates);
                hookOk = !after.Contains(RoomType.Event) && after.Count > 0;
                W($"assert: Event removed from '?' candidates = {hookOk} "
                  + $"[{string.Join(", ", after.OrderBy(r => r))}]");

                // A fresh odds table on its own Rng, so the run's stream is untouched. 30 rolls: the
                // fallback is deterministic, but the odds loop is not, and one roll would prove little.
                W($"(runs completed on this profile = {state.UnlockState.NumberOfRuns} — a first-ever run "
                  + "forces two scripted '?' events before this hook, by design)");
                var odds = new UnknownMapPointOdds(new Rng(9173u));
                var rolled = new List<RoomType>();
                for (int i = 0; i < 30; i++) rolled.Add(odds.Roll(Array.Empty<RoomType>(), state));
                int events = rolled.Count(r => r == RoomType.Event);
                int elites = rolled.Count(r => r == RoomType.Elite);
                rollOk = events == 0 && elites == 0;
                W($"assert: 30 '?' rolls produced no event and no elite = {rollOk} "
                  + $"(event={events}, elite={elites}, "
                  + $"[{string.Join(", ", rolled.GroupBy(r => r).Select(g => $"{g.Key}={g.Count()}"))}])");
            }

            // Is the shop-side patch even attached? "the guard didn't fire" and "the guard isn't there"
            // look identical from the assertion, and only one of them is a code bug.
            try
            {
                var patched = HarmonyLib.Harmony.GetAllPatchedMethods()
                    .Where(m => m?.DeclaringType != null
                                && (m.DeclaringType.Name.Contains("Merchant") || m.Name == "Initialize"))
                    .Select(m => m.DeclaringType!.Name + "." + m.Name)
                    .Distinct().OrderBy(x => x).ToList();
                W($"harmony: merchant/Initialize patches attached = [{string.Join(", ", patched)}]");

                // Process-wide list above proves nothing about OUR patch — dump the owners.
                var target = HarmonyLib.AccessTools.Method(
                    typeof(MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory), "Initialize");
                if (target == null) W("harmony: NMerchantInventory.Initialize did not resolve");
                else
                {
                    var info = HarmonyLib.Harmony.GetPatchInfo(target);
                    var owners = info?.Postfixes?.Select(pp => pp.owner) ?? Enumerable.Empty<string>();
                    W($"harmony: resolved '{target.DeclaringType?.Name}.{target.Name}("
                      + $"{string.Join(",", target.GetParameters().Select(pa => pa.ParameterType.Name))})', "
                      + $"postfix owners = [{string.Join(", ", owners)}]");
                }
            }
            catch (Exception e) { W("harmony patch dump failed: " + e.Message); }

            Step("full ban: open a real shop, assert the potion slot NODES are gone");
            try
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Shop);
                await Task.Delay(2500);

                var shop = Engine.GetMainLoop() is SceneTree t
                    ? FindNode<MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory>(t.Root)
                    : null;
                if (shop == null) { W("shop inventory node not found — node assert SKIPPED"); }
                else
                {
                    // The shelves only render once the merchant screen is open. Open() throws at its
                    // LAST statement in a debug-entered room — NMerchantDialogue._dialogueSet is null
                    // because MerchantRoom never built a dialogue set on this path — but the open
                    // animation is kicked off several lines earlier, so the inventory still renders.
                    // Harness-only: nothing here is on a real player's path.
                    try { shop.Open(); }
                    catch (Exception oe) { W($"(Open() threw past the render step: {oe.GetType().Name} — debug-entered room has no merchant dialogue; harmless here)"); }
                    await Task.Delay(1500);
                    await Shot("14_shop_no_potions");

                    var slots = shop.GetAllSlots().ToList();
                    int potionSlots = slots.Count(sl => sl is MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantPotion);
                    // ★The real assertion. An unfilled slot left in the tree has Entry == null, and
                    // DefaultFocusedControl walks every slot doing s.Entry.IsStocked with no null check
                    // — so "no slot without an entry" is exactly the crash this guard prevents.
                    int nullEntry = slots.Count(sl => sl.Entry == null);
                    slotNodesOk = potionSlots == 0 && nullEntry == 0;
                    W($"assert: no potion slot nodes left and no slot without an entry = {slotNodesOk} "
                      + $"({slots.Count} slot(s), {potionSlots} potion, {nullEntry} entry-less)");
                }
            }
            catch (Exception e) { W($"assert: shop open/inspect THREW: {e}"); }
        }
        catch (Exception e) { W("full ban exception: " + e); }
        finally
        {
            RestorePotionState(charTitle, ambientPotions);
            foreach (var id in everyEvent) EventGuardService.SetEventAllowed(charTitle, id, true);
            W("restored the player's own potion configuration and cleared the event blocks");
        }

        return bannedOk && batchOk && rewardOk && stripOk && shelfOk && relicOk
               && eventBannedOk && hookOk && rollOk && slotNodesOk;
    }

    /// <summary>
    /// Persistence round-trip for the new individual blocks: write, wipe at runtime, re-Load. The
    /// real settings file is snapshotted first and restored in the finally, so a test run never
    /// rewrites the player's own configuration.
    /// </summary>
    private static bool RunConfigRoundTripTest(Player player)
    {
        Step("config round-trip: snapshot settings file");
        string path = Path.Combine(ModDir(), "Settings", "card_guard_config.txt");
        string? backup = null;
        bool existed = false;
        try { existed = File.Exists(path); if (existed) backup = File.ReadAllText(path); }
        catch (Exception e) { W($"backup read failed: {e.Message}"); }
        W($"existing config: {(existed ? backup?.Length + " bytes" : "(none)")}");

        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";
        const string probeCard = "CARD.CG_PROBE_CARD";
        const string probeRelic = "RELIC.CG_PROBE_RELIC";
        const string probePotion = "POTION.CG_PROBE_POTION";
        const string probeEvent = "EVENT.CG_PROBE_EVENT";
        bool ok = false;
        try
        {
            CardGuardService.SetCardAllowed(charTitle, probeCard, false);
            RelicGuardService.SetRelicAllowed(charTitle, probeRelic, false);
            PotionGuardService.SetPotionAllowed(charTitle, probePotion, false);
            EventGuardService.SetEventAllowed(charTitle, probeEvent, false);
            CardGuardConfig.Save();

            string written = File.Exists(path) ? File.ReadAllText(path) : "";
            bool onDisk = written.Contains(probeCard, StringComparison.Ordinal)
                          && written.Contains(probeRelic, StringComparison.Ordinal)
                          && written.Contains(probePotion, StringComparison.Ordinal)
                          && written.Contains(probeEvent, StringComparison.Ordinal)
                          && written.Contains("\"cardBlock\"", StringComparison.Ordinal)
                          && written.Contains("\"relicBlock\"", StringComparison.Ordinal)
                          && written.Contains("\"potionBlock\"", StringComparison.Ordinal)
                          && written.Contains("\"eventBlock\"", StringComparison.Ordinal);
            W($"assert: probes written under card/relic/potion/event blocks = {onDisk} ({written.Length} bytes)");

            // Wipe at runtime, then re-Load — the file must put them back.
            CardGuardService.SetCardAllowed(charTitle, probeCard, true);
            RelicGuardService.SetRelicAllowed(charTitle, probeRelic, true);
            PotionGuardService.SetPotionAllowed(charTitle, probePotion, true);
            EventGuardService.SetEventAllowed(charTitle, probeEvent, true);
            bool cleared = CardGuardService.GetCardAllowed(charTitle, probeCard)
                           && RelicGuardService.GetRelicAllowed(charTitle, probeRelic)
                           && PotionGuardService.GetPotionAllowed(charTitle, probePotion)
                           && EventGuardService.GetEventAllowed(charTitle, probeEvent);

            CardGuardConfig.Load();
            bool reCard = !CardGuardService.GetCardAllowed(charTitle, probeCard);
            bool reRelic = !RelicGuardService.GetRelicAllowed(charTitle, probeRelic);
            bool rePotion = !PotionGuardService.GetPotionAllowed(charTitle, probePotion);
            bool reEvent = !EventGuardService.GetEventAllowed(charTitle, probeEvent);
            W($"assert: cleared at runtime = {cleared}");
            W($"assert: restored by Load = card {reCard}, relic {reRelic}, potion {rePotion}, event {reEvent}");
            ok = onDisk && cleared && reCard && reRelic && rePotion && reEvent;
        }
        catch (Exception e) { W($"config round-trip failed: {e.Message}"); ok = false; }
        finally
        {
            try { CardGuardService.SetCardAllowed(charTitle, probeCard, true); } catch { }
            try { RelicGuardService.SetRelicAllowed(charTitle, probeRelic, true); } catch { }
            try { PotionGuardService.SetPotionAllowed(charTitle, probePotion, true); } catch { }
            try { EventGuardService.SetEventAllowed(charTitle, probeEvent, true); } catch { }
            try
            {
                if (backup != null) File.WriteAllText(path, backup);
                else if (File.Exists(path)) File.Delete(path);
                W("settings file restored to its original state");
            }
            catch (Exception e) { W($"settings restore FAILED: {e.Message}"); }
        }
        Step("config round-trip verdict");
        return ok;
    }

    /// <summary>
    /// Visual evidence for the new drill-down UI: the pack list, then a pack's per-item detail list.
    /// Logic asserts can't see a broken layout, so these two shots are the only proof the detail
    /// view actually renders.
    /// </summary>
    private static async Task ShotPanel(Player player)
    {
        Step("panel screenshots");
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            string charTitle = player.Character?.CardPool?.Title ?? "ironclad";
            Ui.CardGuardPanel.TestSelect(charTitle);

            // Prefer a relic pack (that tab is mod-only, so every row is drillable).
            var relicMod = RelicGuardService.GetModRelicPacks()
                .OrderByDescending(k => k.Value.Total).Select(k => k.Key).FirstOrDefault();
            var cardMod = CardGuardService.GetModCardPacks()
                .OrderByDescending(k => k.Value.Cards.Count).Select(k => k.Key).FirstOrDefault();

            // Block the two alphabetically-first relics of the pack so the shots show real partial
            // blocking — the pack row's "(2개 차단)" suffix and the detail header's "2 / N 차단".
            // An all-allowed screenshot can't distinguish "renders correctly" from "state ignored".
            var staged = new List<string>();
            var stagedCards = new List<string>();
            var stagedGlobal = new List<string>();
            var stagedPotions = new List<string>();
            var stagedEvents = new List<string>();
            try
            {
                if (relicMod != null && RelicGuardService.GetModRelicPacks().TryGetValue(relicMod, out var rp))
                    foreach (var r in rp.Relics
                                 .OrderBy(x => { try { return x.Title?.GetFormattedText() ?? ""; } catch { return ""; } },
                                          StringComparer.CurrentCultureIgnoreCase)
                                 .Take(2))
                    {
                        string id = RelicGuardService.RelicIdOf(r);
                        if (id.Length == 0) continue;
                        RelicGuardService.SetRelicAllowed(charTitle, id, false);
                        staged.Add(id);
                    }
                W($"staged {staged.Count} individually-blocked relic(s) for the screenshots");

                Ui.CardGuardPanel.TestOpen(tree.Root, relicMod != null ? 1 : 0, null, relicMod != null);
                await Task.Delay(900);
                await Shot("3_panel_packs");

                string? drill = relicMod ?? cardMod;
                if (drill != null)
                {
                    Ui.CardGuardPanel.TestOpen(tree.Root, relicMod != null ? 1 : 0, drill, relicMod != null);
                    await Task.Delay(900);
                    await Shot("4_panel_detail");
                    W($"detail shot taken for pack '{drill}' ({(relicMod != null ? "relics" : "cards")} tab)");
                }
                else W("no mod packs to drill into — detail shot skipped");

                // Cards tab too: it is the only place mod_colorless_fmt renders, and that string hits
                // the same loc-format path as the blocked-count suffix.
                if (cardMod != null && CardGuardService.GetModCardPacks().TryGetValue(cardMod, out var mp))
                {
                    foreach (var c in mp.Cards.Take(3))
                    {
                        string id = CardGuardService.CardIdOf(c);
                        if (id.Length == 0) continue;
                        CardGuardService.SetCardAllowed(charTitle, id, false);
                        stagedCards.Add(id);
                    }
                    W($"staged {stagedCards.Count} individually-blocked card(s) for the cards-tab shot");
                    Ui.CardGuardPanel.TestOpen(tree.Root, 0, null, false);
                    await Task.Delay(900);
                    await Shot("5_panel_cards");

                    _hoverOk = await RunHoverPreviewTest(tree, cardMod);

                    // The all-characters scope, both ways round: once with it selected (no "own"
                    // row — every character pack is a normal blockable row), and once from the
                    // character scope, where the same blocks must show as locked rows. Two shots
                    // because "the scope stores it" and "the other scope shows it" are separate
                    // failures, and only the second one is what the bug report was about.
                    foreach (var c in mp.Cards.Skip(3).Take(2))
                    {
                        string id = CardGuardService.CardIdOf(c);
                        if (id.Length == 0) continue;
                        CardGuardService.SetCardAllowed(CardGuardService.AllScope, id, false);
                        stagedGlobal.Add(id);
                    }
                    W($"staged {stagedGlobal.Count} all-characters card block(s) for the scope shots");

                    Ui.CardGuardPanel.TestSelect(CardGuardService.AllScope);
                    Ui.CardGuardPanel.TestOpen(tree.Root, 0, null, false);
                    await Task.Delay(900);
                    await Shot("8_panel_allscope");

                    Ui.CardGuardPanel.TestSelect(charTitle);
                    Ui.CardGuardPanel.TestOpen(tree.Root, 0, cardMod, false);
                    await Task.Delay(900);
                    await Shot("9_panel_allscope_locked");
                    W($"all-characters scope shots taken (scope view + locked rows under '{charTitle}')");
                }

                // Potions and events. Both tabs introduce a pack row shape the older ones don't — a
                // pool / act row with no flag of its own — so the tri-state glyph and the count suffix
                // take a different path there and need their own shots. A couple of items are blocked
                // first so a partial pack is actually visible.
                var potionGroup = PotionGuardService.GetPotionGroups()
                    .OrderByDescending(g => g.Potions.Count).FirstOrDefault();
                if (potionGroup != null)
                {
                    foreach (var p in potionGroup.Potions.Take(2))
                    {
                        string id = PotionGuardService.PotionIdOf(p);
                        if (id.Length == 0) continue;
                        PotionGuardService.SetPotionAllowed(charTitle, id, false);
                        stagedPotions.Add(id);
                    }
                    W($"staged {stagedPotions.Count} blocked potion(s) in pool '{potionGroup.Key}'");
                    Ui.CardGuardPanel.TestSelect(charTitle);
                    Ui.CardGuardPanel.TestOpen(tree.Root, 2, null, false);
                    await Task.Delay(900);
                    await Shot("10_panel_potions");
                    Ui.CardGuardPanel.TestOpenPotionPool(tree.Root, potionGroup.Key);
                    await Task.Delay(900);
                    await Shot("11_panel_potion_detail");
                    // Hover the first row: a potion name alone says nothing, so the panel shows the
                    // game's own description. Reported as text too — a screenshot cannot tell an empty
                    // description apart from one that failed to resolve.
                    W("hover (potion): " + Ui.CardGuardPanel.TestHoverFirstRow());
                    await Task.Delay(900);
                    await Shot("15_potion_hover");
                    Ui.CardGuardPanel.TestUnhover();
                }
                else W("no potion pools discovered — potion shots skipped");

                var eventGroup = EventGuardService.GetEventGroups()
                    .OrderByDescending(g => g.Events.Count).FirstOrDefault();
                if (eventGroup != null)
                {
                    foreach (var e in eventGroup.Events.Take(2))
                    {
                        string id = EventGuardService.EventIdOf(e);
                        if (id.Length == 0) continue;
                        EventGuardService.SetEventAllowed(charTitle, id, false);
                        stagedEvents.Add(id);
                    }
                    W($"staged {stagedEvents.Count} blocked event(s) in group '{eventGroup.Key}'");
                    Ui.CardGuardPanel.TestOpen(tree.Root, 3, null, false);
                    await Task.Delay(900);
                    await Shot("12_panel_events");
                    Ui.CardGuardPanel.TestOpenEventGroup(tree.Root, eventGroup.Key);
                    await Task.Delay(900);
                    await Shot("13_panel_event_detail");
                    W("hover (event): " + Ui.CardGuardPanel.TestHoverFirstRow());
                    await Task.Delay(900);
                    await Shot("16_event_hover");
                    Ui.CardGuardPanel.TestUnhover();
                }
                else W("no event groups discovered — event shots skipped");
            }
            finally
            {
                foreach (var id in staged)
                    try { RelicGuardService.SetRelicAllowed(charTitle, id, true); } catch { }
                foreach (var id in stagedCards)
                    try { CardGuardService.SetCardAllowed(charTitle, id, true); } catch { }
                foreach (var id in stagedGlobal)
                    try { CardGuardService.SetCardAllowed(CardGuardService.AllScope, id, true); } catch { }
                foreach (var id in stagedPotions)
                    try { PotionGuardService.SetPotionAllowed(charTitle, id, true); } catch { }
                foreach (var id in stagedEvents)
                    try { EventGuardService.SetEventAllowed(charTitle, id, true); } catch { }
                Ui.CardGuardPanel.TestSelect(charTitle);
                Ui.CardGuardPanel.TestClose();
            }
            await Task.Delay(400);
        }
        catch (Exception e) { W($"panel screenshots failed: {e.Message}"); }
    }

    /// <summary>
    /// Colorful Philosophers picks a FOREIGN character pool before any card exists, so its options
    /// bypass the card filter and need their own hook. Regression for the reported "a blocked MOD
    /// character is still offered, and picking it hands back a mix of the classes you did NOT block"
    /// and its follow-up "the blocked option now just disappears, shrinking the event":
    ///   1. run the event with nothing blocked → record which pools it offers (baseline),
    ///   2. block one of them → the same event must not offer that pool any more, and — as long as
    ///      another allowed character exists to stand in — must offer the SAME NUMBER of options,
    ///      each labelled with the color of the pool it actually gives (retarget, not drop),
    ///   3. filtering that pool's cards must substitute from exactly ONE other pool, never a mix.
    /// The event Rng is seeded from the run seed + event id, so both runs consider the same candidate
    /// set — the only difference is our filter. A MOD pool is preferred as the target, since that is
    /// the case that regressed (RitsuLib appends mod pools to the event's candidate order).
    /// </summary>
    private static async Task<bool> RunPhilosophersOptionTest(Player player)
    {
        Step("philosophers: baseline options (nothing blocked)");
        string charTitle = player.Character?.CardPool?.Title ?? "";
        if (charTitle.Length == 0) { W("no current card pool — skipping."); return true; }

        var baseline = await OfferedPools(player);
        if (baseline == null) { W("assert: could not run the event — FAIL"); return false; }
        W($"baseline options: {baseline.Count} [{string.Join(", ", baseline.Select(o => o.Pool.Title))}]");
        if (baseline.Count < 2)
        { W("assert: fewer than 2 foreign pools offered — cannot exercise the option filter, SKIP."); return true; }

        var baseAsm = typeof(EventModel).Assembly;
        var target = (baseline.FirstOrDefault(o => o.Pool.GetType().Assembly != baseAsm) ?? baseline[0]).Pool;
        string targetTitle = target.Title ?? "";
        bool isMod = target.GetType().Assembly != baseAsm;
        W($"target pool: {targetTitle} (mod-added={isMod})");

        bool ok = true;
        void Check(string what, bool cond)
        {
            W($"assert: {what} = {cond}");
            if (!cond) ok = false;
        }

        bool wasBlocked = !CardGuardService.GetCrossAllowed(charTitle, targetTitle);
        try
        {
            Step($"philosophers: block '{targetTitle}' for {charTitle}, re-run the event");
            CardGuardService.SetCrossAllowed(charTitle, targetTitle, false);
            var filtered = await OfferedPools(player);
            if (filtered == null) { W("assert: could not re-run the event — FAIL"); return false; }
            W($"filtered options: {filtered.Count} [{string.Join(", ", filtered.Select(o => o.Pool.Title))}]");

            Check($"blocked pool '{targetTitle}' is no longer offered",
                  !filtered.Any(o => string.Equals(o.Pool.Title, targetTitle, StringComparison.OrdinalIgnoreCase)));

            // A stand-in exists when some unlocked, non-own, non-colorless pool with a color name is
            // neither blocked (only the target is, in this test) nor already on the baseline list.
            var baselineTitles = new HashSet<string>(baseline.Select(o => o.Pool.Title ?? ""), StringComparer.OrdinalIgnoreCase);
            bool spareExists = player.UnlockState.CharacterCardPools.Any(p =>
                p != null && !p.IsColorless && !string.IsNullOrEmpty(p.EnergyColorName)
                && !string.Equals(p.Title ?? "", charTitle, StringComparison.OrdinalIgnoreCase)
                && !baselineTitles.Contains(p.Title ?? "")
                && CardGuardService.GetCrossAllowed(charTitle, p.Title ?? "")
                && CardGuardService.HasAnyAllowedCard(player, p));
            if (spareExists)
                Check("blocked option is retargeted, not dropped (count preserved)",
                      filtered.Count == baseline.Count);
            else
                Check("no stand-in available — blocked option dropped, the others survive",
                      filtered.Count == baseline.Count - baseline.Count(o => string.Equals(o.Pool.Title, targetTitle, StringComparison.OrdinalIgnoreCase)));

            Check("no character is offered twice",
                  filtered.Select(o => o.Pool.Title ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() == filtered.Count);
            Check("every option TextKey matches the pool it gives",
                  filtered.All(o =>
                  {
                      string key = o.Key ?? "";
                      int dot = key.LastIndexOf('.');
                      string suffix = dot >= 0 ? key.Substring(dot + 1) : key;
                      return string.Equals(suffix, o.Pool.EnergyColorName ?? "", StringComparison.OrdinalIgnoreCase);
                  }));

            // ★The TextKey check above only proves we wrote the field WE write. The player never
            // sees TextKey — NEventOptionButton renders Option.Title.GetFormattedText(), and the
            // ctor baked Title from the ORIGINAL key. Assert the rendered label too, or a retarget
            // that silently keeps the blocked character's name passes every other assertion.
            foreach (var o in filtered)
                W($"  option pool={o.Pool.Title} key={o.Key} label='{o.Label}' keyResolves='{o.KeyLabel}'");
            Check("displayed label matches the option's own TextKey",
                  filtered.All(o => string.Equals(o.Label, o.KeyLabel, StringComparison.Ordinal)));
            var baselineLabelByTitle = baseline.ToDictionary(o => o.Pool.Title ?? "", o => o.Label, StringComparer.OrdinalIgnoreCase);
            Check("no surviving option still shows the BLOCKED character's label",
                  !filtered.Any(o => baselineLabelByTitle.TryGetValue(targetTitle, out var blockedLabel)
                                     && blockedLabel.Length > 0
                                     && string.Equals(o.Label, blockedLabel, StringComparison.Ordinal)));

            Step("philosophers: substitute must come from ONE pool, not a mix");
            var blockedCards = target
                .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
                .Where(c => c != null).ToList();
            if (blockedCards.Count == 0)
            {
                W("assert: blocked pool has no unlocked cards — SKIP substitute check.");
            }
            else
            {
                var sub = CardGuardService.Filter(blockedCards, player).ToList();
                var titles = sub.Select(c => { try { return c.Pool?.Title ?? ""; } catch { return ""; } })
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                W($"substitute: {sub.Count} card(s) from [{string.Join(", ", titles)}]");
                Check("substitute is not the blocked pool",
                      !titles.Any(t => string.Equals(t, targetTitle, StringComparison.OrdinalIgnoreCase)));
                Check("substitute draws from a single character pool", titles.Count == 1);
            }
        }
        finally
        {
            CardGuardService.SetCrossAllowed(charTitle, targetTitle, !wasBlocked);
        }

        Step("philosophers verdict");
        return ok;
    }

    /// <summary><c>Label</c> = what the player actually reads on the button
    /// (<c>NEventOptionButton</c> renders <c>Option.Title.GetFormattedText()</c>, NOT the TextKey).
    /// <c>KeyLabel</c> = what the option's own <c>Key</c> resolves to. They diverge if something
    /// rewrites TextKey after construction, because the ctor bakes
    /// <c>Title = eventModel.GetOptionTitle(textKey)</c> once.</summary>
    private sealed record OfferedOption(CardPoolModel Pool, string Key, string Label, string KeyLabel);

    /// <summary>One run of the event: the options it ended up showing, and whether it closed itself
    /// (a zero-option event must finish, not sit there with nothing to click).</summary>
    private sealed record EventProbe(List<OfferedOption> Options, bool IsFinished);

    /// <summary>
    /// The two paths the follow-up report actually hit, which the single-block test never reaches:
    /// EVERY offered character blocked (the "只能跳过 / only Skip is left" case), and a character
    /// blocked not by its checkbox but by having all of its cards blocked one by one — the
    /// <c>HasAnyAllowedCard</c> route added in v0.5.2, which sends an option down the same retarget
    /// path. Restores every block it sets, so the run's config is left exactly as found.
    /// </summary>
    private static async Task<bool> RunPhilosophersEdgeTest(Player player)
    {
        Step("philosophers-edge: baseline");
        string charTitle = player.Character?.CardPool?.Title ?? "";
        if (charTitle.Length == 0) { W("no current card pool — SKIP."); return true; }

        var baseProbe = await ProbeEvent(player);
        if (baseProbe == null || baseProbe.Options.Count == 0) { W("no options offered — SKIP."); return true; }
        var baseline = baseProbe.Options;
        W($"baseline: {baseline.Count} option(s) [{string.Join(", ", baseline.Select(o => o.Pool.Title))}], isFinished={baseProbe.IsFinished}");

        // The placement gate must be a one-way door: with nothing blocked it has to leave the
        // vanilla answer alone, or we would be deleting an event the game wanted to place.
        var stateNow = RunManager.Instance?.State;
        W($"assert: event still placeable with nothing blocked = {stateNow != null && ModelDb.Event<ColorfulPhilosophers>().IsAllowed(stateNow)}");
        if (stateNow != null && !ModelDb.Event<ColorfulPhilosophers>().IsAllowed(stateNow))
        { W("  ^ FAIL — the gate is suppressing the event when it should not."); return false; }

        bool ok = true;
        void Check(string what, bool cond) { W($"assert: {what} = {cond}"); if (!cond) ok = false; }

        var others = player.UnlockState.CharacterCardPools
            .Where(p => p != null && !p.IsColorless
                        && !string.Equals(p.Title ?? "", charTitle, StringComparison.OrdinalIgnoreCase)
                        && (p.Title ?? "").Length > 0)
            .ToList();
        var savedCross = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in others) savedCross[p.Title!] = CardGuardService.GetCrossAllowed(charTitle, p.Title!);
        var savedCards = new List<string>();
        string cardVictimTitle = "";

        try
        {
            // ── A) Every other character blocked: no stand-in can exist anywhere.
            Step("philosophers-edge: block EVERY other character");
            foreach (var p in others) CardGuardService.SetCrossAllowed(charTitle, p.Title!, false);
            var none = await ProbeEvent(player);
            if (none == null) { W("assert: the event threw with everything blocked — FAIL"); return false; }
            W($"all-blocked: {none.Options.Count} option(s), isFinished={none.IsFinished}");
            Check("no blocked character survives an all-blocked roll", none.Options.Count == 0);
            Check("a zero-option event closes itself (no softlock)", none.IsFinished);

            // …and it should not be PLACED at all in that state: vanilla gates on the unlock state,
            // which blocks never touch, so without this the act still spends a room on an event that
            // ends the moment you walk in.
            var runState = RunManager.Instance?.State;
            bool allowedWhenAllBlocked = runState != null && ModelDb.Event<ColorfulPhilosophers>().IsAllowed(runState);
            Check("the event is not placed when every character is blocked", !allowedWhenAllBlocked);

            // Same config, different surface. An EVENT can end, so it offers nothing. A relic that
            // forces another character's cards on you (Kaleidoscope / Prismatic Gem) cannot — the
            // pool must not be left empty or the RNG pick throws — so with no other character
            // allowed the substitute has to fall back to the player's OWN class. Assert that split
            // explicitly: "everything blocked" means no event, but own-class cards from a relic.
            var foreignCards = others
                .SelectMany(p => { try { return p.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint); } catch { return Enumerable.Empty<CardModel>(); } })
                .Where(c => c != null).ToList();
            if (foreignCards.Count == 0)
            {
                W("no foreign cards to feed the substitute — SKIP.");
            }
            else
            {
                var sub = CardGuardService.Filter(foreignCards, player).ToList();
                var subTitles = sub.Select(c => { try { return c.Pool?.Title ?? ""; } catch { return ""; } })
                                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                W($"relic-path substitute with EVERYTHING blocked: {sub.Count} card(s) from [{string.Join(", ", subTitles)}]");
                Check("relic substitute falls back to the player's OWN class when nothing else is allowed",
                      sub.Count > 0 && subTitles.Count == 1
                      && string.Equals(subTitles[0], charTitle, StringComparison.OrdinalIgnoreCase));
            }

            // ── B) Every ROLLED character blocked, but characters the roll missed stay allowed:
            //       the offered slots should be handed to those spares rather than vanishing.
            Step("philosophers-edge: block every rolled character, leave the rest allowed");
            foreach (var p in others) CardGuardService.SetCrossAllowed(charTitle, p.Title!, true);
            var rolled = new HashSet<string>(baseline.Select(o => o.Pool.Title ?? ""), StringComparer.OrdinalIgnoreCase);
            foreach (var t in rolled) CardGuardService.SetCrossAllowed(charTitle, t, false);
            int spares = others.Count(p => !rolled.Contains(p.Title!)
                                           && !string.IsNullOrEmpty(p.EnergyColorName)
                                           && CardGuardService.HasAnyAllowedCard(player, p));
            var swapped = await ProbeEvent(player);
            if (swapped == null) { W("assert: the event threw — FAIL"); return false; }
            W($"rolled-blocked: {swapped.Options.Count} option(s) [{string.Join(", ", swapped.Options.Select(o => o.Pool.Title))}], spares available={spares}");
            Check("no blocked character is offered", !swapped.Options.Any(o => rolled.Contains(o.Pool.Title ?? "")));
            Check("as many slots kept as there were spares to fill them",
                  swapped.Options.Count == Math.Min(baseline.Count, spares));
            Check("no character is offered twice",
                  swapped.Options.Select(o => o.Pool.Title ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() == swapped.Options.Count);
            Check("every displayed label still matches its own option key",
                  swapped.Options.All(o => string.Equals(o.Label, o.KeyLabel, StringComparison.Ordinal)));

            // ── C) Not cross-blocked, but emptied card-by-card (v0.5.2's HasAnyAllowedCard route).
            foreach (var t in rolled) CardGuardService.SetCrossAllowed(charTitle, t, true);
            var victim = baseline.Select(o => o.Pool)
                .FirstOrDefault(p => p.GetType().Assembly != typeof(EventModel).Assembly);
            if (victim == null)
            {
                W("no mod-added character in the roll — SKIP the per-card route (base cards are never listed one by one).");
            }
            else
            {
                cardVictimTitle = victim.Title ?? "";
                Step($"philosophers-edge: block every card of '{cardVictimTitle}' individually");
                foreach (var c in victim.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint))
                {
                    if (c == null) continue;
                    string id = CardGuardService.CardIdOf(c);
                    if (id.Length == 0 || !CardGuardService.GetCardAllowed(charTitle, id)) continue;
                    CardGuardService.SetCardAllowed(charTitle, id, false);
                    savedCards.Add(id);
                }
                W($"blocked {savedCards.Count} card(s); pack checkbox left ALLOWED (cross={CardGuardService.GetCrossAllowed(charTitle, cardVictimTitle)})");
                var emptied = await ProbeEvent(player);
                if (emptied == null) { W("assert: the event threw — FAIL"); return false; }
                W($"card-emptied: {emptied.Options.Count} option(s) [{string.Join(", ", emptied.Options.Select(o => o.Pool.Title))}]");
                Check("a character with no allowed cards left is not offered",
                      !emptied.Options.Any(o => string.Equals(o.Pool.Title, cardVictimTitle, StringComparison.OrdinalIgnoreCase)));
                Check("its slot is filled rather than lost", emptied.Options.Count == baseline.Count);
                Check("every displayed label still matches its own option key",
                      emptied.Options.All(o => string.Equals(o.Label, o.KeyLabel, StringComparison.Ordinal)));
            }
        }
        finally
        {
            foreach (var (t, allowed) in savedCross) CardGuardService.SetCrossAllowed(charTitle, t, allowed);
            foreach (var id in savedCards) CardGuardService.SetCardAllowed(charTitle, id, true);
            W("philosophers-edge: config restored");
        }

        Step("philosophers-edge verdict");
        return ok;
    }

    /// <summary>
    /// Run a fresh Colorful Philosophers instance for <paramref name="player"/> and report the
    /// character pool and TextKey behind each surviving option. A mutable clone is used because
    /// <c>BeginEvent</c> refuses an instance that already has an owner. Null on failure.
    /// </summary>
    private static async Task<List<OfferedOption>?> OfferedPools(Player player)
        => (await ProbeEvent(player))?.Options;

    /// <summary>
    /// Call <c>EventModel.BeginEvent</c> without baking its parameter list into this assembly: the
    /// game branches disagree on it (`public` takes (Player, bool), a beta build also took a combat
    /// synchronizer), and a direct call stops compiling the moment the local game updates. The probe
    /// only reads the generated options, so the extra parameters get their harmless defaults —
    /// the player, false for the flags, null for the rest.
    /// </summary>
    private static async Task BeginEventCompat(EventModel ev, Player player)
    {
        var m = ev.GetType().GetMethod("BeginEvent", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException("EventModel.BeginEvent not found on this game build");
        var ps = m.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var t = ps[i].ParameterType;
            args[i] = t.IsAssignableFrom(typeof(Player)) ? player
                    : t == typeof(bool) ? false
                    : t.IsValueType ? Activator.CreateInstance(t)
                    : null;
        }
        await (Task)m.Invoke(ev, args)!;
    }

    private static async Task<EventProbe?> ProbeEvent(Player player)
    {
        try
        {
            var ev = (ColorfulPhilosophers)ModelDb.Event<ColorfulPhilosophers>().MutableClone();
            await BeginEventCompat(ev, player);
            var options = new List<OfferedOption>();
            foreach (var opt in ev.CurrentOptions)
            {
                var p = Patches.ColorfulPhilosophers_Patch.ResolveTargetPool(opt);
                if (p != null)
                {
                    string key = opt?.TextKey ?? "";
                    string label = "", keyLabel = "";
                    try { label = opt?.Title?.GetFormattedText() ?? ""; } catch (Exception e) { label = "<throw:" + e.GetType().Name + ">"; }
                    try { keyLabel = ev.GetOptionTitle(key)?.GetFormattedText() ?? ""; } catch (Exception e) { keyLabel = "<throw:" + e.GetType().Name + ">"; }
                    options.Add(new OfferedOption(p, key, label, keyLabel));
                }
                else W($"  (option '{opt?.TextKey}' has no resolvable pool)");
            }
            return new EventProbe(options, ev.IsFinished);
        }
        catch (Exception e) { W($"philosophers: event run failed: {e.Message}"); return null; }
    }

    /// <summary>Set by <see cref="RunHoverPreviewTest"/>; folded into the overall verdict.</summary>
    private static bool _hoverOk = true;

    /// <summary>
    /// The pack checkbox and its detail list must behave as one setting:
    ///   pack off      → every item off
    ///   pack on       → every item on
    ///   some items on → pack reads "partial"
    ///   last item off → pack blocks itself (so the pack-only content it gates goes too)
    ///   an item back on → pack un-blocks itself
    /// Runtime only — none of these hooks call Save(), so the player's config file is untouched.
    /// </summary>
    private static bool RunPackLinkageTest(Player player)
    {
        Step("pack linkage: pick a relic pack with >=3 items");
        const int RELICS = 3; // kind: mod relics
        string charTitle = player.Character?.CardPool?.Title ?? "ironclad";
        Ui.CardGuardPanel.TestSelect(charTitle);

        string key = "";
        List<string> ids = new();
        foreach (var kv in RelicGuardService.GetModRelicPacks().OrderByDescending(k => k.Value.Total))
        {
            var candidate = Ui.CardGuardPanel.TestPackItemIds(RELICS, kv.Key);
            if (candidate.Count >= 3) { key = kv.Key; ids = candidate; break; }
        }
        if (ids.Count < 3) { W("assert: no relic pack with >=3 items — SKIP linkage test."); return true; }
        W($"target pack '{key}' with {ids.Count} item(s)");

        bool ok = true;
        void Check(string what, bool cond)
        {
            W($"assert: {what} = {cond}");
            if (!cond) ok = false;
        }

        // 1) Pack ON → everything on, nothing partial.
        Ui.CardGuardPanel.TestPackToggle(RELICS, key, true);
        var s = Ui.CardGuardPanel.TestPackState(RELICS, key);
        W($"  after pack ON  : total={s.total} allowed={s.allowed} blocked={s.blocked} partial={s.partial}");
        Check("pack ON allows every item", s.allowed == s.total && !s.blocked && !s.partial);

        // 2) Pack OFF → every item off too (the linkage the user asked for).
        Ui.CardGuardPanel.TestPackToggle(RELICS, key, false);
        s = Ui.CardGuardPanel.TestPackState(RELICS, key);
        int stillAllowed = ids.Count(id => RelicGuardService.GetRelicAllowed(charTitle, id));
        W($"  after pack OFF : total={s.total} allowed={s.allowed} blocked={s.blocked} partial={s.partial}, rawAllowedIds={stillAllowed}");
        Check("pack OFF blocks the pack", s.blocked);
        Check("pack OFF switches every item off", stillAllowed == 0 && s.allowed == 0);
        Check("pack OFF is not shown as partial", !s.partial);

        // 3) Back ON, then switch ONE item off → partial, pack still allowed.
        Ui.CardGuardPanel.TestPackToggle(RELICS, key, true);
        Ui.CardGuardPanel.TestItemToggle(RELICS, key, ids[0], false);
        s = Ui.CardGuardPanel.TestPackState(RELICS, key);
        W($"  one item off   : total={s.total} allowed={s.allowed} blocked={s.blocked} partial={s.partial}");
        Check("one item off reads as partial", s.partial);
        Check("one item off leaves the pack allowed", !s.blocked && s.allowed == s.total - 1);

        // 4) Switch the REST off → the pack blocks itself.
        for (int i = 1; i < ids.Count; i++) Ui.CardGuardPanel.TestItemToggle(RELICS, key, ids[i], false);
        s = Ui.CardGuardPanel.TestPackState(RELICS, key);
        W($"  all items off  : total={s.total} allowed={s.allowed} blocked={s.blocked} partial={s.partial}");
        Check("last item off blocks the pack", s.blocked && s.allowed == 0 && !s.partial);

        // 5) One back on → the pack un-blocks itself, else the flag would keep suppressing it.
        Ui.CardGuardPanel.TestItemToggle(RELICS, key, ids[0], true);
        s = Ui.CardGuardPanel.TestPackState(RELICS, key);
        W($"  one back on    : total={s.total} allowed={s.allowed} blocked={s.blocked} partial={s.partial}");
        Check("an item returning un-blocks the pack", !s.blocked && s.allowed == 1);
        Check("that state reads as partial", s.partial || s.total == 1);

        Step("pack linkage verdict");
        Ui.CardGuardPanel.TestPackToggle(RELICS, key, true); // leave it clean
        return ok;
    }

    /// <summary>
    /// The card hover preview borrows a node from the game's shared 30-node NCard pool. Screenshots
    /// prove it renders; only the pool free-count proves it is RETURNED — a preview leaked per hover
    /// would quietly starve the pool the real combat cards come from.
    /// </summary>
    private static async Task<bool> RunHoverPreviewTest(SceneTree tree, string cardMod)
    {
        Step("hover preview: open a card pack detail list");
        try
        {
            Ui.CardGuardPanel.TestOpen(tree.Root, 0, cardMod, false);
            await Task.Delay(900);

            // The real mouse cursor sits wherever the user left it; if that lands on a card row the
            // game fires MouseEntered for real and a preview is already borrowed. Clear first so the
            // baseline is well-defined instead of depending on cursor position.
            Ui.CardGuardPanel.TestUnhover();
            await Task.Delay(300);
            int freeBefore = Ui.CardGuardPanel.TestCardPoolFree();
            if (freeBefore < 0) { W("assert: NCard pool not reachable — SKIP hover test."); return true; }

            Ui.CardGuardPanel.TestHoverFirstCard();
            await Task.Delay(900);
            var (shown, isRealCard) = Ui.CardGuardPanel.TestPreviewState();
            int freeDuring = Ui.CardGuardPanel.TestCardPoolFree();
            await Shot("6_panel_card_hover");

            // Control case: a base-game card from the live deck. If this renders but the mod card
            // above does not, the fault is the mod's card data, not our preview plumbing.
            try
            {
                var deckCard = RunManager.Instance?.State?.Players?.FirstOrDefault()?.Deck?.Cards?.FirstOrDefault();
                if (deckCard != null)
                {
                    Ui.CardGuardPanel.TestUnhover();
                    await Task.Delay(300);
                    Ui.CardGuardPanel.TestHoverCard(deckCard);
                    await Task.Delay(700);
                    await Shot("7_panel_hover_basecard");
                    W($"control shot: base-game deck card '{deckCard.Id.Entry}' (title='{deckCard.Title}')");
                }
                else W("control shot skipped — no deck card available");
            }
            catch (Exception e) { W($"control shot failed: {e.Message}"); }

            Step("hover preview: unhover");
            Ui.CardGuardPanel.TestUnhover();
            await Task.Delay(700);
            var (shownAfter, _) = Ui.CardGuardPanel.TestPreviewState();
            int freeAfter = Ui.CardGuardPanel.TestCardPoolFree();

            Step("hover preview verdict");
            W($"NCard pool free: before={freeBefore}, during={freeDuring}, after={freeAfter}");
            bool borrowed = !isRealCard || freeDuring == freeBefore - 1;
            bool returned = freeAfter == freeBefore;
            W($"assert: preview mounted on hover = {shown} (real pooled NCard = {isRealCard})");
            W($"assert: pooled node borrowed while shown = {borrowed}");
            W($"assert: preview torn down on unhover = {!shownAfter}");
            W($"assert: pooled node RETURNED (no leak) = {returned}");
            return shown && borrowed && !shownAfter && returned;
        }
        catch (Exception e) { W($"hover preview test failed: {e.Message}"); return false; }
    }

    /// <summary>Populate a fresh bag and split the count into (base, same-pack mates, the victim).</summary>
    private static (int baseCount, int mates, int victim) PopulateAndSplit(List<RelicModel> relics, string mod, string victimId)
    {
        try
        {
            var bag = new RelicGrabBag(refreshAllowed: false);
            bag.Populate(relics, new Rng(12345u));
            int baseCount = 0, mates = 0, victim = 0;
            var deques = bag._deques;
            if (deques == null) return (0, 0, 0);
            foreach (var list in deques.Values)
            {
                if (list == null) continue;
                foreach (var r in list)
                {
                    if (r == null) continue;
                    if (!RelicGuardService.IsModRelic(r)) { baseCount++; continue; }
                    if (!string.Equals(RelicGuardService.ModNameOf(r), mod, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(RelicGuardService.RelicIdOf(r), victimId, StringComparison.OrdinalIgnoreCase)) victim++;
                    else mates++;
                }
            }
            return (baseCount, mates, victim);
        }
        catch (Exception e) { W($"populate failed: {e.Message}"); return (0, 0, 0); }
    }

    /// <summary>Populate a fresh bag from <paramref name="relics"/> (Postfix filters it) and count.</summary>
    private static (int baseCount, int targetCount) PopulateAndCount(List<RelicModel> relics, string targetMod)
    {
        try
        {
            var bag = new RelicGrabBag(refreshAllowed: false);
            bag.Populate(relics, new Rng(12345u));
            return CountBag(bag, targetMod);
        }
        catch (Exception e) { W($"populate failed: {e.Message}"); return (0, 0); }
    }

    /// <summary>(baseRelicCount, targetModRelicCount) across every rarity deque in the bag.</summary>
    private static (int baseCount, int targetCount) CountBag(RelicGrabBag bag, string targetMod)
    {
        int baseCount = 0, targetCount = 0;
        var deques = bag._deques;
        if (deques == null) return (0, 0);
        foreach (var list in deques.Values)
        {
            if (list == null) continue;
            foreach (var r in list)
            {
                if (r == null) continue;
                if (!RelicGuardService.IsModRelic(r)) baseCount++;
                else if (string.Equals(RelicGuardService.ModNameOf(r), targetMod, StringComparison.OrdinalIgnoreCase)) targetCount++;
            }
        }
        return (baseCount, targetCount);
    }

    private static void RestoreBlock(string charTitle, string mod, bool wasBlocked)
    {
        try { RelicGuardService.SetModAllowed(charTitle, mod, !wasBlocked); } catch { }
    }

    #region selection automation (unused by this logic-only test, kept for the watchdog/pump safety net)
    private static readonly HashSet<string> _pumpIgnore = new() { };
    private const int PumpGraceMs = 4000;
    private static IDisposable? _selectorScope;
    private static bool _pumpRunning;

    private static void StartAutomation()
    {
        EnsureSelector();
        if (_pumpRunning) return;
        _pumpRunning = true;
        int handlers = ScreenHandlers().Count;
        TaskHelper.RunSafely(PumpLoop());
        W($"selection automation on (selector + {handlers} screen handler(s), grace {PumpGraceMs}ms)");
    }

    private static void EnsureSelector()
    {
        try
        {
            if (CardSelectCmd.Selector != null) return;
            _selectorScope = CardSelectCmd.PushSelector(new AutoSelector());
        }
        catch (Exception e) { W("selector push failed: " + e.Message); }
    }

    private sealed class AutoSelector : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var list = options.ToList();
            int n = Math.Min(maxSelect, list.Count);
            if (n < minSelect) n = Math.Min(minSelect, list.Count);
            W($"  [selector] auto-picked {n}/{list.Count}");
            return Task.FromResult<IEnumerable<CardModel>>(list.Take(n).ToList());
        }

        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        {
            var pick = options.FirstOrDefault()?.Card;
            W($"  [selector] auto-picked card reward: {pick?.Id.Entry ?? "(none)"}");
            return new CardRewardSelection { card = pick, alternative = null };
        }
    }

    private static async Task PumpLoop()
    {
        var rng = new Rng(1u);
        object? seen = null;
        var seenAt = DateTime.UtcNow;
        int attempts = 0;
        while (!_done)
        {
            await Task.Delay(500);
            try
            {
                EnsureSelector();
                object? top = NOverlayStack.Instance?.Peek();
                if (top == null) { seen = null; attempts = 0; continue; }
                if (!ReferenceEquals(top, seen)) { seen = top; seenAt = DateTime.UtcNow; attempts = 0; continue; }
                if ((DateTime.UtcNow - seenAt).TotalMilliseconds < PumpGraceMs) continue;

                string name = top.GetType().Name;
                if (_pumpIgnore.Contains(name)) continue;
                if (attempts >= 3)
                {
                    if (attempts == 3) { attempts++; W($"  [pump] {name} will not close after 3 attempts — leaving it"); }
                    continue;
                }
                attempts++;
                W($"  [pump] auto-handling unattended screen: {name} (attempt {attempts})");
                await HandleScreen(top, rng);
                seenAt = DateTime.UtcNow;
            }
            catch (Exception e) { W("  [pump] " + e.Message); }
        }
    }

    private static async Task HandleScreen(object screen, Rng rng)
    {
        if (!ScreenHandlers().TryGetValue(screen.GetType(), out var handler))
        {
            W($"  [pump] no AutoSlay handler for {screen.GetType().Name}");
            return;
        }
        var ht = handler.GetType();
        var timeout = ht.GetProperty("Timeout")?.GetValue(handler) as TimeSpan? ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(timeout);
        var task = ht.GetMethod("HandleAsync")?.Invoke(handler, new object[] { rng, cts.Token }) as Task;
        if (task == null) { W($"  [pump] {ht.Name}.HandleAsync not invokable"); return; }
        await task;
        W($"  [pump] handled {screen.GetType().Name}");
    }

    private static Dictionary<Type, object>? _screenHandlers;

    private static Dictionary<Type, object> ScreenHandlers()
    {
        if (_screenHandlers != null) return _screenHandlers;
        var map = new Dictionary<Type, object>();
        try
        {
            var asm = typeof(CardSelectCmd).Assembly;
            var iface = asm.GetType("MegaCrit.Sts2.Core.AutoSlay.Handlers.IScreenHandler");
            if (iface == null) { W("  [pump] AutoSlay handlers not found"); return _screenHandlers = map; }
            Type?[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
            foreach (var t in types)
            {
                if (t == null || t.IsAbstract || t.IsInterface || !iface.IsAssignableFrom(t)) continue;
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                var h = Activator.CreateInstance(t);
                if (h != null && t.GetProperty("ScreenType")?.GetValue(h) is Type st) map[st] = h;
            }
        }
        catch (Exception e) { W("  [pump] handler discovery failed: " + e.Message); }
        return _screenHandlers = map;
    }

    private static string TopScreenName()
    {
        try { return NOverlayStack.Instance?.Peek()?.GetType().Name ?? "(none)"; } catch { return "(unavailable)"; }
    }
    #endregion

    private static async Task Shot(string name)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            await Task.Delay(120);
            var img = tree.Root.GetTexture()?.GetImage();
            if (img == null) { W($"shot {name}: null image"); return; }
            string p = Path.Combine(ModDir(), $"selftest.sp.{name}.png");
            var err = img.SavePng(p);
            W($"shot {name}: {(err == Error.Ok ? $"saved {img.GetWidth()}x{img.GetHeight()}" : "err " + err)}");
        }
        catch (Exception e) { W($"shot {name} failed: {e.Message}"); }
    }

    private static void W(string line) { _out.AppendLine(line); Log($"SOLO | {line}"); }
    private static void Log(string s) { try { MainFile.Logger.Info($"[{Tag}] {s}"); } catch { } }

    private static void Flush(bool ok)
    {
        if (_done) return;
        _done = true;
        _selectorScope?.Dispose();
        _selectorScope = null;
        _out.Insert(0, (ok ? "RESULT: OK\n" : "RESULT: FAIL\n"));
        try { File.WriteAllText(Path.Combine(ModDir(), "selftest.sp.txt"), _out.ToString()); } catch { }
    }
}
