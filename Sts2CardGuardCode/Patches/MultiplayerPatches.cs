using System;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using Sts2CardGuard.Multiplayer;

namespace Sts2CardGuard.Patches;

/// <summary>
/// Hook the multiplayer lobby's construction so Card Guard can exchange the host's configuration
/// with every peer DURING the lobby (a long, reliable window), before the run's lockstep simulation
/// starts. Registers our message handlers and, as host, pushes config to all peers.
/// </summary>
[HarmonyPatch]
internal static class StartRunLobbyCtor_Patch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Constructor(typeof(StartRunLobby), new[]
        {
            typeof(MegaCrit.Sts2.Core.Runs.GameMode),
            typeof(MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService),
            typeof(IStartRunLobbyListener),
            typeof(int),
        });

    // The daily-run ctor overload chains to this 4-arg ctor, so patching here covers both paths.
    private static void Postfix(StartRunLobby __instance)
    {
        try { MultiplayerSync.OnLobbyCreated(__instance); }
        catch (Exception ex) { Log.Warn($"StartRunLobby ctor hook failed: {ex.Message}"); }
    }
}

/// <summary>
/// Lock the filtering decision the instant a networked run begins — before any lockstep action (and
/// therefore any card generation) executes. Both host and client reach this via
/// <c>NGame.StartNewMultiplayerRun(StartRunLobby, ...)</c>.
/// </summary>
[HarmonyPatch]
internal static class StartNewMultiplayerRun_Patch
{
    private static MethodBase TargetMethod() =>
        typeof(NGame).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == nameof(NGame.StartNewMultiplayerRun)
                        && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(StartRunLobby));

    private static void Prefix(StartRunLobby lobby)
    {
        try { MultiplayerSync.LockInForRun(lobby); }
        catch (Exception ex) { Log.Warn($"StartNewMultiplayerRun hook failed: {ex.Message}"); }
    }
}

/// <summary>
/// Starting a singleplayer run clears the multiplayer state so the local config takes over again.
///
/// ★This is not only a "after a co-op run" tidy-up — it is what makes filtering work in singleplayer at
/// all. Character select constructs a <c>StartRunLobby</c> even for a solo run (type=Singleplayer), and
/// the ctor hook above sets every guard to pass-through as its fail-safe baseline. Without this reset the
/// guards stay off for the whole solo run.
///
/// ★Goes through <see cref="MultiplayerSync.ClearAllMpState"/> rather than naming the services here.
/// Naming them is precisely how potion and event filtering shipped broken in singleplayer: this line
/// listed cards and relics only, so both new guards were left in pass-through and silently did nothing
/// while every other test passed.
/// </summary>
[HarmonyPatch]
internal static class StartNewSingleplayerRun_Patch
{
    private static MethodBase TargetMethod() =>
        typeof(NGame).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == nameof(NGame.StartNewSingleplayerRun));

    private static void Prefix()
    {
        try { MultiplayerSync.ClearAllMpState(); }
        catch (Exception ex) { Log.Warn($"StartNewSingleplayerRun hook failed: {ex.Message}"); }
    }
}

