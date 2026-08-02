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
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
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
            await NGame.Instance.StartNewSingleplayerRun(character, shouldSave: false, acts,
                Array.Empty<ModifierModel>(), "SOLOTEST", GameMode.Standard, 0);
            await Task.Delay(3000);

            var run = RunManager.Instance;
            if (run?.IsInProgress != true || (run.State?.Players?.Count ?? 0) == 0)
            { W("run did not start"); Flush(false); return; }
            var player = run.State!.Players.First();
            W($"run started: {player.Character?.Id.Entry}, floor {run.State.TotalFloor}");

            StartAutomation();
            await Shot("1_run");

            bool okBag = await RunRelicFilterTest(player);
            bool okRelicOne = RunIndividualRelicTest(player);
            bool okCardOne = RunIndividualCardTest(player);
            bool okCfg = RunConfigRoundTripTest(player);
            bool okLink = RunPackLinkageTest(player);
            bool okPhil = await RunPhilosophersOptionTest(player);
            bool okPhilEdge = await RunPhilosophersEdgeTest(player);
            await ShotPanel(player);

            bool ok = okBag && okRelicOne && okCardOne && okCfg && okLink && okPhil && okPhilEdge && _hoverOk;
            W($"=== summary: grabbag/ancient={okBag}, individual-relic={okRelicOne}, "
              + $"individual-card={okCardOne}, config-roundtrip={okCfg}, pack-linkage={okLink}, "
              + $"philosophers={okPhil}, philosophers-edge={okPhilEdge}, hover-preview={_hoverOk} ===");

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

        RelicGuardService.ClearMpState(); // normal single-player mode (local config)
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
        bool ok = false;
        try
        {
            CardGuardService.SetCardAllowed(charTitle, probeCard, false);
            RelicGuardService.SetRelicAllowed(charTitle, probeRelic, false);
            CardGuardConfig.Save();

            string written = File.Exists(path) ? File.ReadAllText(path) : "";
            bool onDisk = written.Contains(probeCard, StringComparison.Ordinal)
                          && written.Contains(probeRelic, StringComparison.Ordinal)
                          && written.Contains("\"cardBlock\"", StringComparison.Ordinal)
                          && written.Contains("\"relicBlock\"", StringComparison.Ordinal);
            W($"assert: probes written under cardBlock/relicBlock = {onDisk} ({written.Length} bytes)");

            // Wipe at runtime, then re-Load — the file must put them back.
            CardGuardService.SetCardAllowed(charTitle, probeCard, true);
            RelicGuardService.SetRelicAllowed(charTitle, probeRelic, true);
            bool cleared = CardGuardService.GetCardAllowed(charTitle, probeCard)
                           && RelicGuardService.GetRelicAllowed(charTitle, probeRelic);

            CardGuardConfig.Load();
            bool reCard = !CardGuardService.GetCardAllowed(charTitle, probeCard);
            bool reRelic = !RelicGuardService.GetRelicAllowed(charTitle, probeRelic);
            W($"assert: cleared at runtime = {cleared}");
            W($"assert: restored by Load = card {reCard}, relic {reRelic}");
            ok = onDisk && cleared && reCard && reRelic;
        }
        catch (Exception e) { W($"config round-trip failed: {e.Message}"); ok = false; }
        finally
        {
            try { CardGuardService.SetCardAllowed(charTitle, probeCard, true); } catch { }
            try { RelicGuardService.SetRelicAllowed(charTitle, probeRelic, true); } catch { }
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
                }
            }
            finally
            {
                foreach (var id in staged)
                    try { RelicGuardService.SetRelicAllowed(charTitle, id, true); } catch { }
                foreach (var id in stagedCards)
                    try { CardGuardService.SetCardAllowed(charTitle, id, true); } catch { }
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

    private static async Task<EventProbe?> ProbeEvent(Player player)
    {
        try
        {
            var ev = (ColorfulPhilosophers)ModelDb.Event<ColorfulPhilosophers>().MutableClone();
            await ev.BeginEvent(player, isPreFinished: false);
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
