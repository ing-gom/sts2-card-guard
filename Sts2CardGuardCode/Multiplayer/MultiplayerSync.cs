using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace Sts2CardGuard.Multiplayer;

/// <summary>
/// Makes Card Guard safe in networked co-op by unifying every peer on the HOST's configuration.
///
/// Why this is necessary: STS2 co-op is host-authoritative lockstep. At each sync point the host
/// pushes its RNG to all peers (<c>PlayerDataSyncer.StartSync</c>), then every peer re-computes card
/// generation deterministically and the <c>ChecksumTracker</c> compares against the host — a peer
/// whose state diverges abandons the run. Card Guard filters the candidate pool BEFORE the RNG
/// pick, so if two peers filter differently (e.g. each configured only their own character), their
/// picks diverge and the client is kicked. The only consistent choice is: everyone filters with the
/// host's config.
///
/// How it stays race-free: the host's config is exchanged during the LOBBY phase (a long, reliable
/// window) and the "filtering on/off + which config" decision is locked ONCE, at run start, before
/// any lockstep action executes. There is no in-run flag flip, so no message-timing race can cause
/// a mid-run divergence. If anything is uncertain at lock-in (peer hasn't acked, host config never
/// arrived, protocol mismatch), the side falls back to pass-through (no filtering) — the mod does
/// nothing in that run, but never desyncs.
/// </summary>
internal static class MultiplayerSync
{
    private static readonly object _gate = new();

    // The lobby's net service we currently have handlers registered on (for clean re-registration).
    private static INetGameService? _net;

    // ---- Host bookkeeping: which connected peers have acknowledged our config ----
    private static readonly HashSet<ulong> _ackedPeers = new();

    // ---- Client bookkeeping: the host config we received during the lobby ----
    private static bool _hostConfigReceived;
    private static Dictionary<string, List<string>>? _hostCross;
    private static Dictionary<string, List<string>>? _hostMod;

    /// <summary>
    /// Called when a <see cref="StartRunLobby"/> is constructed (host or client). Resets per-lobby
    /// state, registers our message handlers on the lobby's net service, and — as host — pushes our
    /// config to every peer now and whenever another peer connects.
    /// </summary>
    public static void OnLobbyCreated(StartRunLobby lobby)
    {
        try
        {
            var net = lobby.NetService;
            lock (_gate)
            {
                // Fresh lobby → forget everything from a previous session.
                _ackedPeers.Clear();
                _hostConfigReceived = false;
                _hostCross = null;
                _hostMod = null;

                if (!ReferenceEquals(_net, net))
                {
                    UnregisterFrom(_net);
                    _net = net;
                    net.RegisterMessageHandler<CardGuardConfigMessage>(OnConfigReceived);
                    net.RegisterMessageHandler<CardGuardAckMessage>(OnAckReceived);
                }
            }

            // Fail-safe baseline for the upcoming run: filtering OFF until LockInForRun explicitly
            // turns it on. If any run-start path ever bypasses the lock-in, we stay pass-through
            // (mod does nothing) rather than filtering with un-synced local config (which desyncs).
            CardGuardService.DisableMpFiltering();

            if (net.Type == NetGameType.Host)
            {
                // Cover peers already connected at ctor time, then keep new joiners covered.
                SendConfigToAll(net);
                lobby.PlayerConnected += _ => SendConfigToAll(net);
            }
            // Client: nothing to do proactively — the host pushes config on our PlayerConnected.

            Log.Info($"multiplayer lobby detected (type={net.Type}); host-config sync armed.");
        }
        catch (Exception ex) { Log.Warn($"OnLobbyCreated failed: {ex.Message}"); }
    }

    private static void UnregisterFrom(INetGameService? net)
    {
        if (net == null) return;
        try { net.UnregisterMessageHandler<CardGuardConfigMessage>(OnConfigReceived); } catch { }
        try { net.UnregisterMessageHandler<CardGuardAckMessage>(OnAckReceived); } catch { }
    }

    /// <summary>Host: broadcast our local blocked-pack config to all peers (reliable, idempotent).</summary>
    private static void SendConfigToAll(INetGameService net)
    {
        try
        {
            var (cross, mod) = CardGuardService.SnapshotLocalBlocks();
            var msg = new CardGuardConfigMessage
            {
                magic = CardGuardNet.Magic,
                protocol = CardGuardNet.Protocol,
                crossBlock = cross,
                modBlock = mod,
            };
            net.SendMessage(msg);
            Log.Info($"host config broadcast ({cross.Count} char-block set(s), {mod.Count} mod-block set(s)).");
        }
        catch (Exception ex) { Log.Warn($"SendConfigToAll failed: {ex.Message}"); }
    }

    // ---- Handlers ----

    private static void OnConfigReceived(CardGuardConfigMessage msg, ulong senderId)
    {
        try
        {
            if (msg.magic != CardGuardNet.Magic || msg.protocol != CardGuardNet.Protocol)
            {
                Log.Warn($"ignoring host config (magic/protocol mismatch: magic=0x{msg.magic:X}, proto={msg.protocol}).");
                return; // leave _hostConfigReceived false → this peer will pass-through
            }
            lock (_gate)
            {
                _hostCross = msg.crossBlock ?? new Dictionary<string, List<string>>();
                _hostMod = msg.modBlock ?? new Dictionary<string, List<string>>();
                _hostConfigReceived = true;
            }
            // Acknowledge so the host knows it may safely enable filtering for the run.
            var net = _net;
            if (net != null)
            {
                net.SendMessage(new CardGuardAckMessage { magic = CardGuardNet.Magic, protocol = CardGuardNet.Protocol });
            }
            Log.Info("received + acked host config; will filter with host settings this run.");
        }
        catch (Exception ex) { Log.Warn($"OnConfigReceived failed: {ex.Message}"); }
    }

    private static void OnAckReceived(CardGuardAckMessage msg, ulong senderId)
    {
        if (msg.magic != CardGuardNet.Magic || msg.protocol != CardGuardNet.Protocol) return;
        lock (_gate) { _ackedPeers.Add(senderId); }
        Log.Info($"peer {senderId} acked config.");
    }

    /// <summary>
    /// Locks the filtering decision for a run that is about to start. Called before the lockstep
    /// simulation runs, so the choice is fixed for the whole run (no in-run race). Falls back to
    /// pass-through whenever consistency across peers can't be guaranteed.
    /// </summary>
    public static void LockInForRun(StartRunLobby lobby)
    {
        try
        {
            var net = lobby.NetService;
            switch (net.Type)
            {
                case NetGameType.Host:
                {
                    bool allAcked = AllConnectedPeersAcked(net);
                    if (allAcked)
                    {
                        CardGuardService.ActivateMpLocal();
                        Log.Info("MP lock-in (host): all peers acked → filtering with host config.");
                    }
                    else
                    {
                        CardGuardService.DisableMpFiltering();
                        Log.Warn("MP lock-in (host): a peer did not ack (missing/old mod) → filtering OFF this run.");
                    }
                    break;
                }
                case NetGameType.Client:
                {
                    bool have;
                    Dictionary<string, List<string>>? cross, mod;
                    lock (_gate) { have = _hostConfigReceived; cross = _hostCross; mod = _hostMod; }
                    if (have)
                    {
                        CardGuardService.ActivateMpOverride(cross!, mod!);
                        Log.Info("MP lock-in (client): applying host config (own settings ignored this run).");
                    }
                    else
                    {
                        CardGuardService.DisableMpFiltering();
                        Log.Warn("MP lock-in (client): host config not received (host missing mod?) → filtering OFF this run.");
                    }
                    break;
                }
                default:
                    // Singleplayer / fake-multiplayer: use local config normally.
                    CardGuardService.ClearMpState();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"LockInForRun failed ({ex.Message}) → disabling filtering to stay safe.");
            try { CardGuardService.DisableMpFiltering(); } catch { }
        }
    }

    private static bool AllConnectedPeersAcked(INetGameService net)
    {
        if (net is INetHostGameService host)
        {
            lock (_gate)
            {
                foreach (var peer in host.ConnectedPeers)
                    if (!_ackedPeers.Contains(peer.peerId)) return false;
            }
        }
        return true; // no peers (host alone) → nothing to desync
    }
}
