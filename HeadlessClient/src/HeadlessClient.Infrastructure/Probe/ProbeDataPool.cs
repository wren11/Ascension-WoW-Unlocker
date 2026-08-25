using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using HeadlessClient.Domain.World;

namespace HeadlessClient.Infrastructure.Probe;

/// <summary>Live values harvested from object manager + recent inbound packets.</summary>
public sealed class ProbeDataPool
{
    private readonly ConcurrentQueue<ulong> _guids = new();
    private readonly ConcurrentQueue<uint> _entries = new();
    private readonly ConcurrentQueue<(float X, float Y, float Z, float O)> _positions = new();
    private readonly object _gate = new();
    private ulong _selfGuid;
    private float _x, _y, _z, _o;
    private uint _mapId, _zoneId;

    public ulong SelfGuid
    {
        get { lock (_gate) return _selfGuid; }
    }

    public (float X, float Y, float Z, float O) SelfPosition
    {
        get { lock (_gate) return (_x, _y, _z, _o); }
    }

    public void SetSelf(ulong guid, float x, float y, float z, float o, uint mapId = 0, uint zoneId = 0)
    {
        lock (_gate)
        {
            if (guid != 0) _selfGuid = guid;
            _x = x; _y = y; _z = z; _o = o;
            if (mapId != 0) _mapId = mapId;
            if (zoneId != 0) _zoneId = zoneId;
        }

        NoteGuid(guid);
        NotePosition(x, y, z, o);
    }

    public void IngestObjects(IEnumerable<WorldObject> objects)
    {
        foreach (var o in objects)
        {
            NoteGuid(o.Guid);
            if (o.Entry != 0) NoteEntry(o.Entry);
            if (o.X != 0 || o.Y != 0 || o.Z != 0)
            {
                NotePosition(o.X, o.Y, o.Z, o.Orientation);
            }

            // TypeId 4 = player in stock WoW; Ascension projector may leave 0.
            if (o.TypeId == 4 || (o.Guid != 0 && _selfGuid == 0 && o.Health > 0 && o.MaxHealth > 0))
            {
                // Prefer first healthy unit as self if unset.
                if (_selfGuid == 0 && o.Guid != 0)
                {
                    SetSelf(o.Guid, o.X, o.Y, o.Z, o.Orientation);
                }
            }
        }
    }

    public void NoteGuid(ulong guid)
    {
        if (guid == 0) return;
        _guids.Enqueue(guid);
        Trim(_guids, 64);
    }

    public void NoteEntry(uint entry)
    {
        if (entry == 0) return;
        _entries.Enqueue(entry);
        Trim(_entries, 64);
    }

    public void NotePosition(float x, float y, float z, float o)
    {
        _positions.Enqueue((x, y, z, o));
        Trim(_positions, 32);
    }

    public void NoteInboundPacket(uint opcode, ReadOnlySpan<byte> payload)
    {
        // Scrape GUIs / entries from inbound bodies for later CMSG fills.
        if (payload.Length >= 8)
        {
            NoteGuid(BinaryPrimitives.ReadUInt64LittleEndian(payload));
        }

        if (payload.Length >= 4)
        {
            var u = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            if (u is > 0 and < 500_000) NoteEntry(u);
        }

        // Packed-ish: scan for plausible low GUIDs (player counter in low bytes).
        for (var i = 0; i + 8 <= payload.Length; i += 4)
        {
            var g = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(i, 8));
            var high = g >> 48;
            // Type masks commonly 0x0000 / 0x0008 player-ish on Ascension private.
            if (g != 0 && high <= 0xF000 && (g & 0xFFFFFFFF) != 0)
            {
                NoteGuid(g);
            }
        }

        _ = opcode;
    }

    public IReadOnlyList<ulong> Guids()
    {
        var list = _guids.ToArray().Distinct().Take(24).ToList();
        var self = SelfGuid;
        if (self != 0 && !list.Contains(self)) list.Insert(0, self);
        return list;
    }

    public IReadOnlyList<uint> Entries() =>
        _entries.ToArray().Distinct().Take(24).ToList();

    public uint MapId { get { lock (_gate) return _mapId; } }
    public uint ZoneId { get { lock (_gate) return _zoneId; } }

    public uint NextEntryOr(uint fallback)
    {
        var e = Entries();
        return e.Count > 0 ? e[0] : fallback;
    }

    public ulong NextGuidOr(ulong fallback)
    {
        var g = Guids();
        return g.Count > 0 ? g[0] : fallback;
    }

    private static void Trim<T>(ConcurrentQueue<T> q, int max)
    {
        while (q.Count > max && q.TryDequeue(out _))
        {
        }
    }
}

public enum ProbeTemplateKind : byte
{
    Empty = 0,
    Guid = 1,
    EntryGuid = 2,
    Entry = 3,
    U32 = 4,
    TwoU32 = 5,
    GuidGuid = 6,
    Xyz = 7,
    GuidXyz = 8,
    Known = 9,
    String = 10,
    Zone = 11,
}

public sealed record ProbeVariant(
    string Label,
    ProbeTemplateKind Kind,
    byte[] Payload,
    string Notes);

/// <summary>Builds CMSG bodies using live OM/packet atoms + Trinity/gtker layouts.</summary>
public static class ProbePayloadFactory
{
    public static IReadOnlyList<ProbeVariant> BuildVariants(
        uint opcode,
        string name,
        ProbeDataPool pool,
        bool includeGeneric)
    {
        var list = new List<ProbeVariant>();
        var self = pool.SelfGuid;
        var target = pool.NextGuidOr(self);
        var entry = pool.NextEntryOr(1);
        var (x, y, z, o) = pool.SelfPosition;

        // Documented / high-value QUERY + interaction layouts (3.3.5 / gtker + Trinity).
        switch (name)
        {
            case "CMSG_NAME_QUERY":
            case "CMSG_PET_NAME_QUERY":
                foreach (var g in pool.Guids().Take(3))
                    list.Add(Known(name, GuidBytes(g), $"guid={g:X16}"));
                break;
            case "CMSG_CREATURE_QUERY":
                foreach (var e in pool.Entries().Take(3))
                    list.Add(Known(name, EntryGuid(e, target != 0 ? target : self), $"entry={e}"));
                if (list.Count == 0)
                    list.Add(Known(name, EntryGuid(entry, self), $"entry={entry}"));
                break;
            case "CMSG_GAMEOBJECT_QUERY":
                foreach (var e in pool.Entries().Take(3))
                    list.Add(Known(name, EntryGuid(e, target), $"entry={e}"));
                break;
            case "CMSG_ITEM_QUERY_SINGLE":
            case "CMSG_ITEM_NAME_QUERY":
                foreach (var e in pool.Entries().Take(5))
                    list.Add(Known(name, U32(e), $"item={e}"));
                list.Add(Known(name, U32(25), "item=25(Worn Shortsword)"));
                break;
            case "CMSG_QUEST_QUERY":
                foreach (var id in new uint[] { 1, 2, 5, 6, 7, 9, entry })
                    list.Add(Known(name, U32(id), $"quest={id}"));
                break;
            case "CMSG_NPC_TEXT_QUERY":
                list.Add(Known(name, Concat(U32(1), GuidBytes(target)), "text=1"));
                break;
            case "CMSG_PAGE_TEXT_QUERY":
                list.Add(Known(name, U32(1), "page=1"));
                break;
            case "CMSG_QUERY_TIME":
            case "CMSG_PLAYED_TIME":
            case "MSG_QUERY_NEXT_MAIL_TIME":
            case "CMSG_READY_FOR_ACCOUNT_DATA_TIMES":
            case "CMSG_BATTLEFIELD_STATUS":
            case "CMSG_CALENDAR_GET_NUM_PENDING":
            case "CMSG_KEEP_ALIVE":
            case "CMSG_WARDEN_DATA": // empty may just be ignored
                list.Add(Known(name, Array.Empty<byte>(), "empty"));
                break;
            case "CMSG_SET_SELECTION":
            case "CMSG_GOSSIP_HELLO":
            case "CMSG_LIST_INVENTORY":
            case "CMSG_TRAINER_LIST":
            case "CMSG_BANKER_ACTIVATE":
            case "CMSG_BINDER_ACTIVATE":
            case "CMSG_SPIRIT_HEALER_ACTIVATE":
            case "CMSG_PETITION_SHOWLIST":
            case "CMSG_AUCTION_HELLO":
            case "CMSG_ATTACKSWING":
            case "CMSG_LOOT":
            case "CMSG_GAMEOBJ_USE":
            case "CMSG_GAMEOBJ_REPORT_USE":
            case "CMSG_TALK_TO_GOSSIP":
                foreach (var g in pool.Guids().Take(4))
                    list.Add(Known(name, GuidBytes(g), $"guid={g:X16}"));
                break;
            case "CMSG_TAXINODE_STATUS_QUERY":
            case "CMSG_TAXIQUERYAVAILABLENODES":
                list.Add(Known(name, GuidBytes(target), $"guid={target:X16}"));
                break;
            case "CMSG_ZONEUPDATE":
                list.Add(Known(name, U32(pool.ZoneId != 0 ? pool.ZoneId : 267), "zone"));
                break;
            case "CMSG_AREATRIGGER":
                list.Add(Known(name, U32(1), "trigger=1"));
                break;
            case "CMSG_PING":
                list.Add(Known(name, Concat(U32(1), U32(0)), "seq=1 latency=0"));
                break;
            case "CMSG_QUERY_INSPECT_ACHIEVEMENTS":
                list.Add(Known(name, GuidBytes(target != 0 ? target : self), "inspect"));
                break;
            case "CMSG_REQUEST_ACCOUNT_DATA":
                for (uint i = 0; i < 8; i++)
                    list.Add(Known(name, U32(i), $"type={i}"));
                break;
            case "CMSG_WHO":
                list.Add(Known(name, BuildWho(), "who empty filter"));
                break;
            case "MSG_MOVE_FALL_LAND":
            case "MSG_MOVE_HEARTBEAT":
            case "MSG_MOVE_STOP":
                list.Add(Known(name, BuildMove(self, x, y, z, o), "self pos"));
                break;
        }

        if (list.Count > 0 && !includeGeneric)
        {
            return list;
        }

        if (includeGeneric)
        {
            list.Add(new ProbeVariant("empty", ProbeTemplateKind.Empty, Array.Empty<byte>(), "empty body"));
            if (target != 0)
            {
                list.Add(new ProbeVariant("guid", ProbeTemplateKind.Guid, GuidBytes(target), $"guid={target:X16}"));
                list.Add(new ProbeVariant("entry+guid", ProbeTemplateKind.EntryGuid, EntryGuid(entry, target), $"e={entry}"));
                list.Add(new ProbeVariant("guid+guid", ProbeTemplateKind.GuidGuid, Concat(GuidBytes(self), GuidBytes(target)), "self+target"));
            }

            list.Add(new ProbeVariant("u32", ProbeTemplateKind.U32, U32(entry != 0 ? entry : 1), $"u32={entry}"));
            list.Add(new ProbeVariant("2xu32", ProbeTemplateKind.TwoU32, Concat(U32(entry), U32(1)), "entry,1"));
            list.Add(new ProbeVariant("xyz", ProbeTemplateKind.Xyz, Xyz(x, y, z, o), "self xyz"));
            if (self != 0)
            {
                list.Add(new ProbeVariant("guid+xyz", ProbeTemplateKind.GuidXyz, Concat(GuidBytes(self), Xyz(x, y, z, o)), "self"));
            }

            list.Add(new ProbeVariant("cstring", ProbeTemplateKind.String, CString("test"), "cstring"));
            list.Add(new ProbeVariant("zone", ProbeTemplateKind.Zone, U32(pool.ZoneId != 0 ? pool.ZoneId : 1), "zoneId"));
        }

        // De-dupe identical payloads
        return list
            .GroupBy(v => Convert.ToHexString(v.Payload))
            .Select(g => g.First())
            .Take(12)
            .ToList();
    }

    private static ProbeVariant Known(string name, byte[] payload, string notes) =>
        new($"known:{name}", ProbeTemplateKind.Known, payload, notes);

    private static byte[] GuidBytes(ulong g)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, g);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] EntryGuid(uint entry, ulong guid) => Concat(U32(entry), GuidBytes(guid));

    private static byte[] Xyz(float x, float y, float z, float o)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), z);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), o);
        return b;
    }

    private static byte[] CString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        var b = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, b, 0, bytes.Length);
        return b;
    }

    private static byte[] BuildWho()
    {
        using var ms = new MemoryStream(32);
        using var bw = new BinaryWriter(ms);
        bw.Write(1u); bw.Write(80u); bw.Write(0u); bw.Write(0u); bw.Write(0u);
        bw.Write(1u); bw.Write((byte)0);
        return ms.ToArray();
    }

    private static byte[] BuildMove(ulong guid, float x, float y, float z, float o)
    {
        using var ms = new MemoryStream(64);
        using var bw = new BinaryWriter(ms);
        bw.Write(guid);
        bw.Write(0u);
        bw.Write((uint)Environment.TickCount);
        bw.Write(x); bw.Write(y); bw.Write(z); bw.Write(o);
        bw.Write(0u);
        return ms.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(p => p.Length);
        var b = new byte[len];
        var o = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, b, o, p.Length);
            o += p.Length;
        }

        return b;
    }
}
