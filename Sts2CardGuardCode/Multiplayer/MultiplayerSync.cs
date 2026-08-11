using System;
using System.Collections.Generic;
using System.Reflection;
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
/// window). The client PULLS it — it announces itself once its lobby exists, because the host's
/// unsolicited broadcast fires from <c>PlayerConnected</c> while the joining client is still
/// constructing that lobby, and a message with no registered handler is dropped, not buffered.
/// The "filtering on/off + which config" decision is then locked ONCE, at run start, before
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
    private static Dictionary<string, List<string>>? _hostRelicMod;
    private static Dictionary<string, List<string>>? _hostCard;
    private static Dictionary<string, List<string>>? _hostRelic;

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
                _hostRelicMod = null;
                _hostCard = null;
                _hostRelic = null;

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
            RelicGuardService.DisableMpFiltering();

            if (net.Type == NetGameType.Host)
            {
                // Cover peers already connected at ctor time, then keep new joiners covered.
                SendConfigToAll(net);
                SubscribePlayerConnected(lobby);
            }
            else if (net.Type == NetGameType.Client)
            {
                // Ask for the config now that our handler is registered. The host's own broadcast
                // fires the instant it accepts our join request — before this lobby exists — so it
                // lands on a client with no handler and is discarded (NetMessageBus drops, never
                // buffers, messages with no registered handler). Pulling closes that window; see
                // CardGuardAckMessage.
                SendHello(net);
            }

            Log.Info($"multiplayer lobby detected (type={net.Type}); host-config sync armed.");
        }
        catch (Exception ex) { Log.Warn($"OnLobbyCreated failed: {ex.Message}"); }
    }

    /// <summary>
    /// Subscribe to <c>StartRunLobby.PlayerConnected</c> without naming its parameter type.
    ///
    /// v0.110 renamed <c>LobbyPlayer</c> to <c>StartRunLobbyPlayer</c>, and the event is
    /// <c>Action&lt;TPlayer&gt;</c> on both. A lambda here would bake whichever name this DLL was
    /// compiled against into <see cref="OnLobbyCreated" />, so the JIT threw "Could not load type
    /// … LobbyPlayer" on the other branch — taking the entire ctor hook down with it, which is
    /// exactly how host-config sync died on the 110 beta. Binding through the event's own delegate
    /// type keeps one published DLL working on both `public` and `public-beta`: relaxed delegate
    /// binding lets a handler declared with <c>object</c> satisfy <c>Action&lt;TPlayer&gt;</c> for
    /// any reference TPlayer.
    ///
    /// If this fails we degrade safely rather than desync: a late joiner simply never receives the
    /// host config, so it never acks, and <see cref="LockInForRun" /> turns filtering OFF for the
    /// run (pass-through) instead of filtering with un-synced config.
    /// </summary>
    private static void SubscribePlayerConnected(StartRunLobby lobby)
    {
        try
        {
            var evt = lobby.GetType().GetEvent("PlayerConnected", BindingFlags.Public | BindingFlags.Instance);
            var handlerType = evt?.EventHandlerType;
            var handler = typeof(MultiplayerSync).GetMethod(nameof(OnPeerConnected), BindingFlags.NonPublic | BindingFlags.Static);
            if (evt == null || handlerType == null || handler == null)
            {
                Log.Warn("StartRunLobby.PlayerConnected not found on this game build — peers joining after lobby creation will not receive host config (filtering stays off for the run).");
                return;
            }
            evt.AddEventHandler(lobby, Delegate.CreateDelegate(handlerType, null, handler));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not subscribe to PlayerConnected ({ex.Message}) — peers joining after lobby creation will not receive host config (filtering stays off for the run).");
        }
    }

    /// <summary>Late joiner appeared — re-broadcast. Reads the net service from the field rather
    /// than a closure, so the handler signature stays version-independent.</summary>
    private static void OnPeerConnected(object? _)
    {
        INetGameService? net;
        lock (_gate) net = _net;
        if (net != null) SendConfigToAll(net);
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
            var (cross, mod, card) = CardGuardService.SnapshotLocalBlocks();
            var (relicMod, relic) = RelicGuardService.SnapshotLocalBlocks();
            var msg = new CardGuardConfigMessage
            {
                magic = CardGuardNet.Magic,
                protocol = CardGuardNet.Protocol,
                crossBlock = cross,
                modBlock = mod,
                relicModBlock = relicMod,
                cardBlock = card,
                relicBlock = relic,
            };
            net.SendMessage(msg);
            Log.Info($"host config broadcast ({cross.Count} char-block set(s), {mod.Count} mod-block set(s), {relicMod.Count} relic-mod set(s), {card.Count} card-block set(s), {relic.Count} relic-block set(s)).");
        }
        catch (Exception ex) { Log.Warn($"SendConfigToAll failed: {ex.Message}"); }
    }

    /// <summary>Client: request the host's config now that we can receive it.</summary>
    private static void SendHello(INetGameService net)
    {
        try
        {
            net.SendMessage(new CardGuardAckMessage { magic = CardGuardNet.HelloMagic, protocol = CardGuardNet.Protocol });
            Log.Info("requested host config (client hello).");
        }
        catch (Exception ex) { Log.Warn($"SendHello failed: {ex.Message}"); }
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
                _hostRelicMod = msg.relicModBlock ?? new Dictionary<string, List<string>>();
                _hostCard = msg.cardBlock ?? new Dictionary<string, List<string>>();
                _hostRelic = msg.relicBlock ?? new Dictionary<string, List<string>>();
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
        if (msg.protocol != CardGuardNet.Protocol) return;

        // A joining client announcing it can now receive: re-broadcast. Idempotent — a peer that
        // already holds the config just re-stores identical values and re-acks, and nothing reads
        // those values until lock-in at run start.
        if (msg.magic == CardGuardNet.HelloMagic)
        {
            INetGameService? net;
            lock (_gate) net = _net;
            if (net != null && net.Type == NetGameType.Host)
            {
                Log.Info($"peer {senderId} requested config → re-broadcasting.");
                SendConfigToAll(net);
            }
            return;
        }

        if (msg.magic != CardGuardNet.Magic) return;
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
                        RelicGuardService.ActivateMpLocal();
                        Log.Info("MP lock-in (host): all peers acked → filtering with host config.");
                    }
                    else
                    {
                        CardGuardService.DisableMpFiltering();
                        RelicGuardService.DisableMpFiltering();
                        Log.Warn("MP lock-in (host): a peer did not ack (missing/old mod) → filtering OFF this run.");
                    }
                    break;
                }
                case NetGameType.Client:
                {
                    bool have;
                    Dictionary<string, List<string>>? cross, mod, relicMod, card, relic;
                    lock (_gate)
                    {
                        have = _hostConfigReceived;
                        cross = _hostCross; mod = _hostMod; relicMod = _hostRelicMod;
                        card = _hostCard; relic = _hostRelic;
                    }
                    if (have)
                    {
                        CardGuardService.ActivateMpOverride(cross!, mod!, card ?? new Dictionary<string, List<string>>());
                        RelicGuardService.ActivateMpOverride(
                            relicMod ?? new Dictionary<string, List<string>>(),
                            relic ?? new Dictionary<string, List<string>>());
                        Log.Info("MP lock-in (client): applying host config (own settings ignored this run).");
                    }
                    else
                    {
                        CardGuardService.DisableMpFiltering();
                        RelicGuardService.DisableMpFiltering();
                        Log.Warn("MP lock-in (client): host config not received (host missing mod?) → filtering OFF this run.");
                    }
                    break;
                }
                default:
                    // Singleplayer / fake-multiplayer: use local config normally.
                    CardGuardService.ClearMpState();
                    RelicGuardService.ClearMpState();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"LockInForRun failed ({ex.Message}) → disabling filtering to stay safe.");
            try { CardGuardService.DisableMpFiltering(); } catch { }
            try { RelicGuardService.DisableMpFiltering(); } catch { }
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
