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

            bool ok = await RunRelicFilterTest(player);

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
