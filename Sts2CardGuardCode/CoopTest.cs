// coop-verify — 2-instance co-op self-test for the relic-pack filter (v0.4.0).
//
// Drop `selftest.coop.flag` next to the mod DLL and launch two instances (coop-selftest.ps1). The
// HOST blocks a relic-adding mod FOR its character BEFORE the lobby, so that block rides the normal
// host-config broadcast to the client (client's own config stays empty — it must rely on the host
// override). Both peers enter a 2-player run, the host jumps to a new room (networked `room`, which
// fires the ChecksumTracker at the boundary — the moment a relic-filter divergence would surface),
// and both peers write their observed relic-bag state to selftest.coop.<role>.txt.
//
// PASS = the two roles converge (same bag signature) AND the blocked mod's relics are absent on BOTH
// (proves the host config actually synced and filtered) AND neither peer dropped (StateDivergence).

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
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace Sts2CardGuard;

internal static class CoopTest
{
    private const string Tag = "CardGuard";
    private const double StepTimeoutSec = 120;

    // The host blocks this mod's relics for this character before the lobby; the client must learn it
    // only through the host-config broadcast. SlayTheUniverse contributes grab-bag-rarity relics.
    private const string TargetMod = "SlayTheUniverse";
    private const string TargetChar = "ironclad"; // fastmp default character on both peers

    private static readonly StringBuilder _out = new();
    private static bool _isHost;
    private static string _role = "?";
    private static bool _readySent, _launched, _done;
    private static string _step = "(not started)";
    private static DateTime _stepAt = DateTime.UtcNow;

    private static string ModDir() => Path.GetDirectoryName(typeof(CoopTest).Assembly.Location) ?? ".";

    public static void ArmIfRequested()
    {
        try
        {
            if (!File.Exists(Path.Combine(ModDir(), "selftest.coop.flag"))) return;
            var fm = System.Environment.GetCommandLineArgs().FirstOrDefault(a => a.Contains("fastmp"));
            _isHost = fm != null && fm.Contains("host");
            _role = fm == null ? "nofastmp" : (_isHost ? "host" : "join");
            W($"coop selftest armed (role={_role}, arg='{fm}')");

            // HOST ONLY: set the block pre-lobby so it rides the host-config broadcast. The client
            // leaves its local config empty on purpose — it must filter via the host override, which is
            // exactly the sync path under test. (Just a string in a dict; no ModelDb needed yet.)
            if (_isHost)
            {
                RelicGuardService.SetModAllowed(TargetChar, TargetMod, false);
                W($"HOST pre-lobby: blocked '{TargetMod}' for '{TargetChar}' (will broadcast to client)");
            }

            Poll();
        }
        catch (Exception e) { Log($"coop arm failed: {e.Message}"); }
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

        if (!_launched && run != null && run.IsInProgress && (run.State?.Players?.Count ?? 0) >= 2)
        {
            _launched = true;
            W($"COOP RUN IN PROGRESS — players={run.State!.Players.Count}");
            StartAutomation();
            Step(_isHost ? "host phase" : "join phase");
            TaskHelper.RunSafely(_isHost ? HostPhase(run) : JoinPhase(run));
            return;
        }

        if (!_done && (DateTime.UtcNow - _stepAt).TotalSeconds > StepTimeoutSec)
        {
            W($"WATCHDOG: no progress for {StepTimeoutSec:F0}s at step '{_step}' — flushing partial result.");
            W($"WATCHDOG: overlay on top = {TopScreenName()}");
            W($"WATCHDOG: run={(RunManager.Instance?.IsInProgress == true ? "in progress" : "none")}, readySent={_readySent}");
            Flush(false);
            return;
        }

        if (!_readySent)
        {
            var screen = FindScreen(tree.Root);
            if (screen == null) { W("waiting for character-select lobby…"); return; }
            var lobby = LobbyOf(screen);
            if (lobby == null) { W("screen found but lobby null"); return; }
            try { lobby.SetReady(true); _readySent = true; Step($"SetReady(true) sent (players={lobby.Players.Count})"); }
            catch (Exception e) { W("SetReady failed: " + e.Message); }
        }
    }

    private static void Step(string name) { _step = name; _stepAt = DateTime.UtcNow; W($"— {name}"); }

    private static async Task HostPhase(RunManager run)
    {
        try
        {
            await Task.Delay(2000);
            await Shot("01_run");
            var me = LocalPlayerOf(run);
            if (me == null) { W("HOST: local player not found"); Flush(false); return; }
            W($"HOST: local player = {me.NetId}; filter passThrough={RelicGuardService.IsPassThrough}, networked={RelicGuardService.IsNetworkedRun}");

            Step("HOST baseline snapshot");
            W($"HOST: baseline = {Snapshot(run)}");

            // Ancient/direct-grant path: hand the host player an ANCIENT-rarity relic of the blocked mod
            // via the built-in networked `relic` command (grants through RelicCmd.Obtain on BOTH peers).
            // Our Obtain swap must fire identically on each → both end with the substitute, not the
            // blocked relic, and stay converged.
            string? ancientEntry = FindAncientEntry();
            if (ancientEntry != null)
            {
                Step($"HOST grant blocked ancient relic '{ancientEntry}' (networked)");
                run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "relic " + ancientEntry, inCombat: false));
                await Task.Delay(5000);
                W($"HOST: after ancient grant = {Snapshot(run)}");
            }
            else W("HOST: no ancient relic of the blocked mod found — skipping ancient-grant step.");

            // Room jump = networked → both peers execute → ChecksumTracker fires at the boundary, which
            // is where a relic-filter divergence (if any) would drop the client.
            Step("HOST room transition (Shop)");
            run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "room Shop", inCombat: false));
            await Task.Delay(7000);

            Step("HOST final snapshot");
            W($"HOST: FINAL = {Snapshot(run)}");
            await Shot("02_final");
            W("=== coop host done ===");
            Flush(true);
        }
        catch (Exception e) { W("HOST exception: " + e); Flush(false); }
    }

    private static async Task JoinPhase(RunManager run)
    {
        try
        {
            await Task.Delay(2000);
            await Shot("01_run");
            W($"JOIN: filter passThrough={RelicGuardService.IsPassThrough}, networked={RelicGuardService.IsNetworkedRun}");
            Step("JOIN baseline snapshot");
            W($"JOIN: baseline = {Snapshot(run)}");
            Step("JOIN waiting for host ancient grant + room transition to replicate");
            await Task.Delay(20000);
            Step("JOIN final snapshot");
            W($"JOIN: FINAL = {Snapshot(run)}");
            await Shot("02_final");
            W("=== coop join done ===");
            Flush(true);
        }
        catch (Exception e) { W("JOIN exception: " + e); Flush(false); }
    }

    /// <summary>
    /// The replicated relic-bag state both peers must agree on: player count, in-progress, total relics
    /// across every player bag + the shared bag, how many are from the blocked mod (must be 0 if the
    /// host block synced and filtered), and a stable signature of the sorted relic ids. Guards a dropped
    /// session (run.State null after StateDivergence) so a desync is recorded, not thrown away.
    /// </summary>
    private static string Snapshot(RunManager run)
    {
        try
        {
            var state = run.State;
            if (state == null || !run.IsInProgress) return "SESSION DROPPED (state null / not in progress)";
            var players = state.Players;

            // Bag contents (grab-bag filter) + obtained relics (ancient/direct-grant swap) both go into
            // the signature, so convergence covers both filter paths.
            var ids = new List<string>();
            int obtainedTarget = 0;
            foreach (var p in players.OrderBy(p => p.NetId))
            {
                CollectBag(p.RelicGrabBag, ids);
                foreach (var r in p.Relics)
                {
                    string id; try { id = r.Id.ToString() ?? ""; } catch { continue; }
                    ids.Add("OWN:" + id);
                    if (RelicGuardService.IsModRelic(r) && string.Equals(RelicGuardService.ModNameOf(r), TargetMod, StringComparison.OrdinalIgnoreCase))
                        obtainedTarget++;
                }
            }
            CollectBag(state.SharedRelicGrabBag, ids);

            // Ancient beings placed on each act's map — the mod's ancients should be substituted to Darv.
            int modAncients = 0;
            foreach (var act in state.Acts)
            {
                try
                {
                    var r = act._rooms;
                    if (r == null || !r.HasAncient) continue;
                    var a = r.Ancient;
                    string aid; try { aid = a.Id.ToString() ?? ""; } catch { continue; }
                    ids.Add("ANC:" + aid);
                    if (a.GetType().Assembly != typeof(AncientEventModel).Assembly &&
                        string.Equals(a.GetType().Assembly.GetName().Name, TargetMod, StringComparison.OrdinalIgnoreCase))
                        modAncients++;
                }
                catch { }
            }

            int bagTarget = 0;
            foreach (var id in ids) if (!id.StartsWith("OWN:") && !id.StartsWith("ANC:") && id.IndexOf(TargetMod, StringComparison.OrdinalIgnoreCase) >= 0) bagTarget++;

            return $"players={players.Count} inProgress=True items={ids.Count} bagTarget={bagTarget} obtainedTarget={obtainedTarget} modAncients={modAncients} sig={StableHash(ids):X8}";
        }
        catch (Exception e) { return "SNAPSHOT ERROR: " + e.Message; }
    }

    /// <summary>Id.Entry of an Ancient-rarity relic from the blocked mod, for the `relic` console command.</summary>
    private static string? FindAncientEntry()
    {
        try
        {
            foreach (var pool in ModelDb.AllRelicPools)
                foreach (var r in pool.AllRelics)
                {
                    if (r == null || !RelicGuardService.IsModRelic(r)) continue;
                    if (!string.Equals(RelicGuardService.ModNameOf(r), TargetMod, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(r.Rarity.ToString(), "Ancient", StringComparison.OrdinalIgnoreCase)) continue;
                    try { return r.Id.Entry; } catch { }
                }
        }
        catch (Exception e) { W("FindAncientEntry failed: " + e.Message); }
        return null;
    }

    private static void CollectBag(RelicGrabBag? bag, List<string> ids)
    {
        try
        {
            var deques = bag?._deques;
            if (deques == null) return;
            foreach (var list in deques.Values)
                if (list != null)
                    foreach (var r in list)
                        if (r != null) { try { ids.Add(r.Id.ToString() ?? ""); } catch { } }
        }
        catch { }
    }

    private static uint StableHash(IEnumerable<string> ids)
    {
        uint h = 2166136261u;
        foreach (var s in ids.OrderBy(x => x, StringComparer.Ordinal))
        {
            foreach (char c in s) { h ^= c; h *= 16777619u; }
            h ^= 0x2C; h *= 16777619u; // separator so [ab][c] != [a][bc]
        }
        return h;
    }

    #region selection automation (P1 selector + P2 screen pump)
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
        try { if (CardSelectCmd.Selector == null) _selectorScope = CardSelectCmd.PushSelector(new AutoSelector()); }
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
                if (attempts >= 3) { if (attempts == 3) { attempts++; W($"  [pump] {name} won't close after 3 — leaving it"); } continue; }
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
        { W($"  [pump] no AutoSlay handler for {screen.GetType().Name}"); return; }
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

    private static async Task Shot(string name, int tries = 6)
    {
        try
        {
            for (int i = 0; i < tries; i++)
            {
                if (Engine.GetMainLoop() is not SceneTree tree) return;
                var img = tree.Root.GetTexture()?.GetImage();
                if (img != null && !IsBlank(img))
                {
                    var err = img.SavePng(Path.Combine(ModDir(), $"selftest.coop.{_role}.{name}.png"));
                    W($"shot {name}: {err} (try {i + 1})");
                    return;
                }
                await Task.Delay(2000);
            }
            if (Engine.GetMainLoop() is SceneTree t2)
                t2.Root.GetTexture()?.GetImage()?.SavePng(Path.Combine(ModDir(), $"selftest.coop.{_role}.{name}.png"));
            W($"shot {name}: still black after {tries} tries (saved anyway)");
        }
        catch (Exception e) { W($"shot {name} failed: {e.Message}"); }
    }

    private static bool IsBlank(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        if (w == 0 || h == 0) return true;
        for (int x = w / 10; x < w; x += Math.Max(1, w / 10))
            for (int y = h / 10; y < h; y += Math.Max(1, h / 10))
            { var c = img.GetPixel(x, y); if (c.R + c.G + c.B > 0.05f) return false; }
        return true;
    }

    // --- lobby / player plumbing ---
    private static NCharacterSelectScreen? FindScreen(Node n)
    {
        if (n is NCharacterSelectScreen s) return s;
        foreach (var c in n.GetChildren()) { var r = FindScreen(c); if (r != null) return r; }
        return null;
    }

    private static StartRunLobby? LobbyOf(NCharacterSelectScreen screen)
    {
        try { return typeof(NCharacterSelectScreen).GetField("_lobby", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(screen) as StartRunLobby; }
        catch { return null; }
    }

    private static ulong LocalNetId(RunManager run) { try { return run.NetService.NetId; } catch { return 1uL; } }

    private static Player? LocalPlayerOf(RunManager run)
    {
        var players = run.State!.Players;
        try { var me = LocalContext.GetMe(players); if (me != null) return me; } catch { }
        ulong id = LocalNetId(run);
        return players.FirstOrDefault(p => p.NetId == id) ?? players.FirstOrDefault();
    }

    private static void W(string line) { _out.AppendLine(line); Log($"COOP[{_role}] | {line}"); }
    private static void Log(string s) { try { MainFile.Logger.Info($"[{Tag}] {s}"); } catch { } }

    private static void Flush(bool ok)
    {
        if (_done) return;
        _done = true;
        _selectorScope?.Dispose();
        _selectorScope = null;
        _out.Insert(0, (ok ? "RESULT: OK\n" : "RESULT: FAIL\n") + "role=" + _role + "\n");
        try { File.WriteAllText(Path.Combine(ModDir(), $"selftest.coop.{_role}.txt"), _out.ToString()); } catch { }
    }
}
