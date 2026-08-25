using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AscensionNetTool;

sealed class SharedObject
{
    public ulong Guid { get; set; }
    public uint Entry { get; set; }
    public string? Name { get; set; }
    public uint TypeMask { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Facing { get; set; }
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int Level { get; set; }
    public int Faction { get; set; }
    public int SrcInstance { get; set; }
    public DateTime Seen { get; set; } = DateTime.UtcNow;
    /// <summary>Host-only sort hint from publishing instance distance.</summary>
    public float DistHint { get; set; }
}

/// <summary>Unified world model across all connected game instances (last-seen wins per GUID).</summary>
sealed class SharedStateManager
{
    readonly ConcurrentDictionary<ulong, SharedObject> _byGuid = new();

    public event Action? Changed;

    public IReadOnlyList<SharedObject> All =>
        _byGuid.Values.OrderBy(o => o.SrcInstance).ThenBy(o => o.DistHint).ToList();

    public IReadOnlyList<SharedObject> ForInstance(int id) =>
        _byGuid.Values.Where(o => o.SrcInstance == id).ToList();

    public IReadOnlyList<SharedObject> Players() =>
        _byGuid.Values.Where(o => (o.TypeMask & ObjTypeMask.Player) != 0).ToList();

    public IReadOnlyList<SharedObject> Near(float x, float y, float r)
    {
        float r2 = r * r;
        return _byGuid.Values
            .Where(o =>
            {
                float dx = o.X - x, dy = o.Y - y;
                return dx * dx + dy * dy <= r2;
            })
            .ToList();
    }

    public void Publish(int srcInstance, ObjSnapshotHeader hdr, ObjUnit[] units)
    {
        var now = DateTime.UtcNow;
        bool changed = false;
        foreach (var u in units)
        {
            if (u.Guid == 0) continue;
            Upsert(srcInstance, u.Guid, u.Entry, u.TypeMask, u.X, u.Y, u.Z, u.Facing,
                (int)u.Health, (int)u.MaxHealth, (int)u.Level, (int)u.Faction, u.Dist, now);
            changed = true;
            EventBus.Publish(new ObjectDiscoveredEvent(srcInstance, u.Guid, u.Entry));
        }

        // Always publish this instance's local player from the snapshot header so peers
        // can see each other even when the player isn't present (or isn't typed) in Units[].
        if (hdr.PlayerGuid != 0)
        {
            Upsert(srcInstance, hdr.PlayerGuid, 0, ObjTypeMask.Player,
                hdr.PlayerX, hdr.PlayerY, hdr.PlayerZ, 0f,
                0, 0, 0, 0, 0f, now);
            changed = true;
        }

        // Drop stale entries from this instance (>8s without refresh)
        foreach (var kv in _byGuid)
        {
            if (kv.Value.SrcInstance == srcInstance
                && (now - kv.Value.Seen).TotalSeconds > 8)
            {
                _byGuid.TryRemove(kv.Key, out _);
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
            EventBus.Publish(new SharedUpdatedEvent(_byGuid.Count, 0));
        }
    }

    void Upsert(int srcInstance, ulong guid, uint entry, uint typeMask,
        float x, float y, float z, float facing,
        int health, int maxHealth, int level, int faction, float distHint, DateTime now)
    {
        var obj = new SharedObject
        {
            Guid = guid,
            Entry = entry,
            TypeMask = typeMask,
            X = x,
            Y = y,
            Z = z,
            Facing = facing,
            Health = health,
            MaxHealth = maxHealth,
            Level = level,
            Faction = faction,
            SrcInstance = srcInstance,
            Seen = now,
            DistHint = distHint,
        };
        _byGuid.AddOrUpdate(guid, obj, (_, prev) =>
        {
            if (prev.Seen > now) return prev;
            if ((typeMask & ObjTypeMask.Player) == 0 && (prev.TypeMask & ObjTypeMask.Player) != 0
                && prev.SrcInstance == srcInstance && prev.Guid == guid)
                obj.TypeMask |= ObjTypeMask.Player;
            if (obj.MaxHealth == 0 && prev.MaxHealth > 0)
            {
                obj.Health = prev.Health;
                obj.MaxHealth = prev.MaxHealth;
                obj.Level = prev.Level;
                obj.Faction = prev.Faction;
                obj.Entry = prev.Entry != 0 ? prev.Entry : obj.Entry;
            }
            return obj;
        });
    }

    public void ClearInstance(int id)
    {
        foreach (var kv in _byGuid)
        {
            if (kv.Value.SrcInstance == id)
                _byGuid.TryRemove(kv.Key, out _);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Prioritized object body (no per-client header): players first, then other-instance
    /// units, then everything else — capped at 256 so peer players never get dropped.
    /// </summary>
    public byte[] SerializeBody()
    {
        var prioritized = All
            .OrderByDescending(o => (o.TypeMask & ObjTypeMask.Player) != 0)
            .ThenBy(o => o.DistHint)
            .Take(256)
            .ToList();
        int objSize = Marshal.SizeOf<SharedViewObjectCs>();
        var body = new byte[prioritized.Count * objSize];
        var handle = GCHandle.Alloc(body, GCHandleType.Pinned);
        try
        {
            nint p = handle.AddrOfPinnedObject();
            for (int i = 0; i < prioritized.Count; i++)
            {
                var o = prioritized[i];
                var rec = new SharedViewObjectCs
                {
                    Guid = o.Guid,
                    Entry = o.Entry,
                    TypeMask = o.TypeMask,
                    Health = o.Health,
                    MaxHealth = o.MaxHealth,
                    Level = (uint)o.Level,
                    Faction = o.Faction,
                    X = o.X,
                    Y = o.Y,
                    Z = o.Z,
                    Facing = o.Facing,
                    SrcInstance = (uint)o.SrcInstance,
                };
                Marshal.StructureToPtr(rec, p + i * objSize, false);
            }
        }
        finally { handle.Free(); }
        return body;
    }

    /// <summary>Prefix SharedViewHeader onto a shared body for one client.</summary>
    public byte[] WrapForClient(byte[] body, int thisInstanceId, int totalInstances, uint ownerPid)
    {
        int hdrSize = Marshal.SizeOf<SharedViewHeaderCs>();
        int objSize = Marshal.SizeOf<SharedViewObjectCs>();
        uint count = objSize > 0 ? (uint)(body.Length / objSize) : 0;
        var buf = new byte[hdrSize + body.Length];
        var hdr = new SharedViewHeaderCs
        {
            Magic = 0x53485631, // SHARED_VIEW_MAGIC
            ThisInstance = (uint)thisInstanceId,
            TotalInstances = (uint)Math.Max(1, totalInstances),
            OwnerPid = ownerPid,
            Count = count,
        };
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(hdr, handle.AddrOfPinnedObject(), false);
        }
        finally { handle.Free(); }
        Buffer.BlockCopy(body, 0, buf, hdrSize, body.Length);
        return buf;
    }

    /// <summary>Wire blob for kCmdSubscribeShared (SharedViewHeader + SharedViewObject[]).</summary>
    public byte[] SerializeForClient(int thisInstanceId, int totalInstances, uint ownerPid) =>
        WrapForClient(SerializeBody(), thisInstanceId, totalInstances, ownerPid);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct SharedViewHeaderCs
{
    public uint Magic;
    public uint ThisInstance;
    public uint TotalInstances;
    public uint OwnerPid;
    public uint Count;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct SharedViewObjectCs
{
    public ulong Guid;
    public uint Entry;
    public uint TypeMask;
    public int Health;
    public int MaxHealth;
    public uint Level;
    public int Faction;
    public float X, Y, Z, Facing;
    public uint SrcInstance;
}
