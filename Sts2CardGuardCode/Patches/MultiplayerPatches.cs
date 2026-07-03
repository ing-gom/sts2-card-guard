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
/// Starting a singleplayer run clears any leftover multiplayer override so the local config takes
/// over again (e.g. after finishing a co-op run and starting a solo one).
/// </summary>
[HarmonyPatch]
internal static class StartNewSingleplayerRun_Patch
{
    private static MethodBase TargetMethod() =>
        typeof(NGame).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == nameof(NGame.StartNewSingleplayerRun));

    private static void Prefix()
    {
        try { CardGuardService.ClearMpState(); }
        catch (Exception ex) { Log.Warn($"StartNewSingleplayerRun hook failed: {ex.Message}"); }
    }
}
