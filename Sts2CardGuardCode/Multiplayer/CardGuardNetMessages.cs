using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace Sts2CardGuard.Multiplayer;

/// <summary>
/// Custom networked messages, auto-registered by the game: <c>MessageTypes</c>'s static ctor
/// concats <c>ReflectionHelper.GetSubtypesInMods&lt;INetMessage&gt;()</c>, and <c>NetTypeCache</c>
/// assigns byte IDs by ordinal type-name sort — so both peers agree on IDs as long as they load the
/// same set of net-message-adding mods (Card Guard being one). See <see cref="MultiplayerSync"/>.
///
/// Both carry a <see cref="Magic"/> guard + <see cref="Protocol"/> version so that, if a mod-set
/// mismatch ever shifts the byte IDs and a foreign packet is misrouted here, the receiver discards
/// it (→ no host config applied → filtering stays off) instead of acting on garbage.
/// </summary>
internal static class CardGuardNet
{
    /// <summary>"CGSH" — sanity marker written as the first field of every Card Guard message.</summary>
    public const int Magic = 0x43475348;

    /// <summary>
    /// "CGHI" — marker for the client's initial request (see <see cref="CardGuardAckMessage"/>).
    ///
    /// Carried in the <c>magic</c> field of the ack message rather than as a new message type:
    /// <c>NetTypeCache</c> assigns wire IDs by sorting the game's AND every mod's
    /// <c>INetMessage</c> types together by type name, so adding a type shifts the IDs of the
    /// game's own messages for anyone whose mod set differs. In practice the game already forbids
    /// that lobby — <c>ModManager.GetGameplayRelevantModNameList</c> keys on <c>id + "-" + version</c>
    /// and a mismatch is refused with ModMismatch before the join completes — so this is belt and
    /// braces, not a live hazard. It also keeps the change to one constant.
    /// </summary>
    public const int HelloMagic = 0x43474849;

    /// <summary>Wire-format version. Bump on any serialization change; mismatches are ignored.
    /// v2 added the global relic-mod block list. v3 added the individual card / relic block lists —
    /// a v2 peer's config is discarded on receipt, so a mixed-version lobby simply runs unfiltered
    /// rather than filtering differently on each side.
    ///
    /// NOT bumped for the v0.7.0 client-request handshake: that change adds no field to either
    /// message, only a second magic value. Keeping the version means a mod-updated client can still
    /// use an older host's unsolicited broadcast when it happens to arrive in time.
    ///
    /// v4 (v0.8.0) adds the all-characters scope. No field changed — the block maps already carry
    /// arbitrary keys — but their MEANING did: a v3 peer would read the "*" key as a character named
    /// "*" and match it against nothing, so it would filter differently from the host instead of
    /// identically. Bumping makes such a lobby run unfiltered rather than out of step.</summary>
    public const int Protocol = 4;

    /// <summary>Defensive cap on decoded collection sizes (guards a misaligned/foreign packet).</summary>
    private const int MaxEntries = 100_000;

    public static void WriteMap(PacketWriter w, Dictionary<string, List<string>>? map)
    {
        map ??= new Dictionary<string, List<string>>();
        w.WriteInt(map.Count);
        foreach (var kv in map)
        {
            w.WriteString(kv.Key ?? string.Empty);
            var vals = kv.Value ?? new List<string>();
            w.WriteInt(vals.Count);
            foreach (var v in vals) w.WriteString(v ?? string.Empty);
        }
    }

    public static Dictionary<string, List<string>> ReadMap(PacketReader r)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int n = r.ReadInt();
        if (n < 0 || n > MaxEntries) throw new InvalidOperationException($"CardGuard map count out of range: {n}");
        for (int i = 0; i < n; i++)
        {
            var key = r.ReadString();
            int m = r.ReadInt();
            if (m < 0 || m > MaxEntries) throw new InvalidOperationException($"CardGuard list count out of range: {m}");
            var list = new List<string>(Math.Min(m, 64));
            for (int j = 0; j < m; j++) list.Add(r.ReadString());
            map[key] = list;
        }
        return map;
    }
}

/// <summary>
/// Host → peers. Carries the HOST's blocked-pack configuration so every peer filters the shared
/// (host-seeded) card RNG identically. Sent during the lobby phase, before the run's lockstep
/// simulation begins, so there is no in-run flag flip to race.
/// </summary>
internal struct CardGuardConfigMessage : INetMessage, IPacketSerializable
{
    public int magic;
    public int protocol;
    public Dictionary<string, List<string>>? crossBlock;
    public Dictionary<string, List<string>>? modBlock;
    public Dictionary<string, List<string>>? relicModBlock;
    public Dictionary<string, List<string>>? cardBlock;
    public Dictionary<string, List<string>>? relicBlock;

    // Host sends directly to each peer; no onward relay needed.
    public readonly bool ShouldBroadcast => false;
    public readonly bool ShouldBuffer => false;
    public readonly NetTransferMode Mode => NetTransferMode.Reliable;
    public readonly LogLevel LogLevel => LogLevel.VeryDebug;

    public readonly void Serialize(PacketWriter writer)
    {
        writer.WriteInt(magic);
        writer.WriteInt(protocol);
        CardGuardNet.WriteMap(writer, crossBlock);
        CardGuardNet.WriteMap(writer, modBlock);
        CardGuardNet.WriteMap(writer, relicModBlock);
        CardGuardNet.WriteMap(writer, cardBlock);
        CardGuardNet.WriteMap(writer, relicBlock);
    }

    public void Deserialize(PacketReader reader)
    {
        magic = reader.ReadInt();
        protocol = reader.ReadInt();
        crossBlock = CardGuardNet.ReadMap(reader);
        modBlock = CardGuardNet.ReadMap(reader);
        relicModBlock = CardGuardNet.ReadMap(reader);
        cardBlock = CardGuardNet.ReadMap(reader);
        relicBlock = CardGuardNet.ReadMap(reader);
    }
}

/// <summary>
/// Peer → host, in two flavours distinguished by <see cref="magic"/>:
///
/// <list type="bullet">
/// <item><see cref="CardGuardNet.HelloMagic"/> — "my lobby exists and my handlers are registered,
/// send me your config". The client sends this itself instead of relying on the host's unsolicited
/// broadcast, which the host fires from <c>StartRunLobby.PlayerConnected</c> while the joining
/// client is still several frames away from constructing its own lobby: the config packet then
/// reaches a client with no handler registered for it and <c>NetMessageBus</c> drops it outright
/// (it is not buffered). In a two-player lobby no further <c>PlayerConnected</c> ever fires, so
/// there was no second chance and filtering stayed off for the whole run.</item>
/// <item><see cref="CardGuardNet.Magic"/> — acknowledges receipt of a matching-protocol
/// <see cref="CardGuardConfigMessage"/>. The host only activates filtering for the run once every
/// connected peer has acked, so a peer that lacks the mod (or the matching version) leaves
/// filtering OFF for everyone — safe, never a desync.</item>
/// </list>
/// </summary>
internal struct CardGuardAckMessage : INetMessage, IPacketSerializable
{
    public int magic;
    public int protocol;

    public readonly bool ShouldBroadcast => false;
    public readonly bool ShouldBuffer => false;
    public readonly NetTransferMode Mode => NetTransferMode.Reliable;
    public readonly LogLevel LogLevel => LogLevel.VeryDebug;

    public readonly void Serialize(PacketWriter writer)
    {
        writer.WriteInt(magic);
        writer.WriteInt(protocol);
    }

    public void Deserialize(PacketReader reader)
    {
        magic = reader.ReadInt();
        protocol = reader.ReadInt();
    }
}
