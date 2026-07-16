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

    /// <summary>Wire-format version. Bump on any serialization change; mismatches are ignored.
    /// v2 added the global relic-mod block list.</summary>
    public const int Protocol = 2;

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
    }

    public void Deserialize(PacketReader reader)
    {
        magic = reader.ReadInt();
        protocol = reader.ReadInt();
        crossBlock = CardGuardNet.ReadMap(reader);
        modBlock = CardGuardNet.ReadMap(reader);
        relicModBlock = CardGuardNet.ReadMap(reader);
    }
}

/// <summary>
/// Peer → host. Acknowledges receipt of a matching-protocol <see cref="CardGuardConfigMessage"/>.
/// The host only activates filtering for the run once every connected peer has acked, so a peer
/// that lacks the mod (or the matching version) leaves filtering OFF for everyone — safe, never a
/// desync.
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
