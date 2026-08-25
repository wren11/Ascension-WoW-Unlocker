using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Protocol;

namespace HeadlessClient.Infrastructure.Monitoring;

/// <summary>
/// Single process-wide Object Manager: merges live UPDATE_OBJECT sightings from every
/// fleet session with static query templates and identity (name/entry) overlays.
/// Persists identity + last position so reconnects do not wipe the aggregate.
/// </summary>
public sealed class InMemoryObjectDirectory : IObjectDirectory
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<ulong, WorldObject> _objects = new();
    private readonly object _io = new();
    private readonly string _path;
    private int _saveScheduled;
    private string? _observerTag;

    public InMemoryObjectDirectory(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeadlessClient",
                "object-manager.json")
            : Path.GetFullPath(path);
        Load();
    }

    public string PathUsed => _path;

    /// <summary>Set by AccountSessionRunner so Upsert/Observe tags the active character.</summary>
    public void SetObserver(string? tag) => _observerTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();

    public IReadOnlyCollection<WorldObject> Snapshot() => _objects.Values.ToArray();

    public void Upsert(WorldObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        Observe(obj, _observerTag);
    }

    public void Observe(WorldObject patch, string? seenBy = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Guid == 0)
        {
            return;
        }

        var tag = string.IsNullOrWhiteSpace(seenBy) ? _observerTag : seenBy!.Trim();
        var now = DateTimeOffset.UtcNow;

        _objects.AddOrUpdate(
            patch.Guid,
            _ => Merge(null, patch, tag, now),
            (_, cur) => Merge(cur, patch, tag, now));

        ScheduleSave();
    }

    public void ApplyIdentity(ulong guid, string? name = null, uint entry = 0, string? staticName = null)
    {
        if (guid == 0)
        {
            return;
        }

        _objects.AddOrUpdate(
            guid,
            _ => new WorldObject(
                guid,
                string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                entry,
                0, 0, 0, 0,
                0, 0,
                InferTypeId(guid),
                DateTimeOffset.UtcNow,
                null,
                string.IsNullOrWhiteSpace(staticName) ? null : staticName.Trim(),
                Alive: true,
                Source: "cache",
                FirstSeenUtc: DateTimeOffset.UtcNow),
            (_, cur) =>
            {
                var n = !string.IsNullOrWhiteSpace(name) ? name.Trim() : cur.Name;
                var e = entry != 0 ? entry : cur.Entry;
                var sn = !string.IsNullOrWhiteSpace(staticName) ? staticName.Trim() : cur.StaticName;
                if (string.IsNullOrWhiteSpace(sn) && e != 0
                    && TryGetStaticName(e, out var fromStatic))
                {
                    sn = fromStatic;
                }

                if (n == cur.Name && e == cur.Entry && sn == cur.StaticName)
                {
                    return cur;
                }

                return cur with
                {
                    Name = n,
                    Entry = e,
                    StaticName = sn,
                    LastSeenUtc = DateTimeOffset.UtcNow
                };
            });

        ScheduleSave();
    }

    public void UpsertStatic(string kind, uint entry, string name, byte typeId = 0)
    {
        if (entry == 0 || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        kind = (kind ?? "creature").Trim().ToLowerInvariant();
        var guid = StaticGuid(kind, entry);
        if (typeId == 0)
        {
            typeId = kind switch
            {
                "gameobject" or "go" => (byte)5,
                "item" => (byte)1,
                _ => (byte)3
            };
        }

        var now = DateTimeOffset.UtcNow;
        _objects.AddOrUpdate(
            guid,
            _ => new WorldObject(
                guid,
                name.Trim(),
                entry,
                0, 0, 0, 0,
                0, 0,
                typeId,
                now,
                null,
                name.Trim(),
                Alive: true,
                Source: "static",
                FirstSeenUtc: now),
            (_, cur) => cur with
            {
                Name = name.Trim(),
                StaticName = name.Trim(),
                Entry = entry,
                TypeId = typeId != 0 ? typeId : cur.TypeId,
                Source = cur.Source == "live" ? "live" : "static",
                LastSeenUtc = now
            });

        // Join onto any live instances that already carry this entry.
        foreach (var kv in _objects)
        {
            if (kv.Value.Source == "static")
            {
                continue;
            }

            if (kv.Value.Entry == entry
                && (string.IsNullOrWhiteSpace(kv.Value.StaticName)
                    || string.IsNullOrWhiteSpace(kv.Value.Name)))
            {
                ApplyIdentity(kv.Key, kv.Value.Name, entry, name.Trim());
            }
        }

        ScheduleSave();
    }

    public void Remove(ulong guid)
    {
        // Soft-destroy: keep identity/position in the global aggregate, mark not alive.
        if (_objects.TryGetValue(guid, out var cur))
        {
            _objects[guid] = cur with { Alive = false, LastSeenUtc = DateTimeOffset.UtcNow };
            ScheduleSave();
        }
    }

    /// <summary>Global OM — Clear must not wipe the shared aggregate.</summary>
    public void Clear()
    {
        SoftClearSession(_observerTag);
    }

    public void SoftClearSession(string? sessionTag)
    {
        if (string.IsNullOrWhiteSpace(sessionTag))
        {
            return;
        }

        var tag = sessionTag.Trim();
        foreach (var kv in _objects)
        {
            var cur = kv.Value;
            if (cur.SeenBy is null || cur.SeenBy.Count == 0)
            {
                continue;
            }

            if (!cur.SeenBy.Any(s => s.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var next = cur.SeenBy
                .Where(s => !s.Equals(tag, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _objects[kv.Key] = cur with
            {
                SeenBy = next.Length == 0 ? Array.Empty<string>() : next,
                Alive = next.Length > 0 && cur.Alive,
                LastSeenUtc = DateTimeOffset.UtcNow
            };
        }

        ScheduleSave();
    }

    public object GetAggregateSummary()
    {
        var snap = _objects.Values.ToArray();
        return new
        {
            ok = true,
            total = snap.Length,
            alive = snap.Count(o => o.Alive && o.Source != "static"),
            staticTemplates = snap.Count(o => o.Source == "static"),
            named = snap.Count(o => !string.IsNullOrWhiteSpace(o.Name) || !string.IsNullOrWhiteSpace(o.StaticName)),
            withEntry = snap.Count(o => o.Entry != 0),
            path = _path,
            observers = snap
                .SelectMany(o => o.SeenBy ?? Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public WorldObject? TryGet(ulong guid) =>
        _objects.TryGetValue(guid, out var o) ? o : null;

    private static WorldObject Merge(WorldObject? cur, WorldObject patch, string? tag, DateTimeOffset now)
    {
        if (cur is null)
        {
            var initialSeen = string.IsNullOrWhiteSpace(tag)
                ? Array.Empty<string>()
                : new[] { tag! };
            var ix = patch.X; var iy = patch.Y; var iz = patch.Z; var io = patch.Orientation;
            if (!UpdateObjectProjector.IsSaneWorldPosition(ix, iy, iz))
            {
                ix = iy = iz = io = 0;
            }

            return patch with
            {
                Name = string.IsNullOrWhiteSpace(patch.Name) ? null : patch.Name.Trim(),
                X = ix,
                Y = iy,
                Z = iz,
                Orientation = io,
                LastSeenUtc = now,
                FirstSeenUtc = now,
                SeenBy = initialSeen,
                Alive = true,
                Source = string.IsNullOrWhiteSpace(patch.Source) ? "live" : patch.Source
            };
        }

        var name = !string.IsNullOrWhiteSpace(patch.Name) ? patch.Name.Trim() : cur.Name;
        var entry = patch.Entry != 0 ? patch.Entry : cur.Entry;
        var staticName = !string.IsNullOrWhiteSpace(patch.StaticName) ? patch.StaticName : cur.StaticName;
        var health = patch.Health != 0 ? patch.Health : cur.Health;
        var maxHealth = patch.MaxHealth != 0 ? patch.MaxHealth : cur.MaxHealth;
        var typeId = patch.TypeId != 0 ? patch.TypeId : cur.TypeId;
        // Prefer fresh live coordinates only when the patch carries a sane movement position.
        // Reject live-nopos / garbage floats so a misaligned parse cannot poison the row.
        var patchHasSanePos = patch.Source is not ("static" or "live-nopos")
                              && UpdateObjectProjector.IsSaneWorldPosition(patch.X, patch.Y, patch.Z)
                              && (Math.Abs(patch.X) > 0.01f || Math.Abs(patch.Y) > 0.01f || Math.Abs(patch.Z) > 0.01f
                                  || patch.Source == "live");
        var curHasSanePos = UpdateObjectProjector.IsSaneWorldPosition(cur.X, cur.Y, cur.Z);
        var usePos = patchHasSanePos
                     && (Math.Abs(patch.X) > 0.01f || Math.Abs(patch.Y) > 0.01f || Math.Abs(patch.Z) > 0.01f
                         || !curHasSanePos
                         || (Math.Abs(cur.X) < 0.01f && Math.Abs(cur.Y) < 0.01f && Math.Abs(cur.Z) < 0.01f));
        // If current coords are garbage, clear them even when patch has no position.
        var x = usePos ? patch.X : (curHasSanePos ? cur.X : 0);
        var y = usePos ? patch.Y : (curHasSanePos ? cur.Y : 0);
        var z = usePos ? patch.Z : (curHasSanePos ? cur.Z : 0);
        var o = usePos ? patch.Orientation : (curHasSanePos ? cur.Orientation : 0);
        var map = patch.MapId != 0 ? patch.MapId : cur.MapId;

        var seen = MergeSeen(cur.SeenBy, tag);
        var source = cur.Source == "static" && patch.Source == "live" ? "live" : cur.Source;
        if (patch.Source == "live")
        {
            source = "live";
        }

        return cur with
        {
            Name = name,
            Entry = entry,
            StaticName = staticName,
            X = x,
            Y = y,
            Z = z,
            Orientation = o,
            Health = health,
            MaxHealth = maxHealth,
            TypeId = typeId,
            LastSeenUtc = now,
            SeenBy = seen,
            Alive = true,
            MapId = map,
            Source = source,
            FirstSeenUtc = cur.FirstSeenUtc == default ? now : cur.FirstSeenUtc
        };
    }

    private static IReadOnlyList<string> MergeSeen(IReadOnlyList<string>? existing, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return existing ?? Array.Empty<string>();
        }

        var list = existing?.ToList() ?? new List<string>();
        if (!list.Any(s => s.Equals(tag, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(tag!);
        }

        return list;
    }

    private bool TryGetStaticName(uint entry, out string name)
    {
        name = "";
        foreach (var kind in new[] { "creature", "gameobject", "item" })
        {
            if (_objects.TryGetValue(StaticGuid(kind, entry), out var s)
                && !string.IsNullOrWhiteSpace(s.Name))
            {
                name = s.Name!;
                return true;
            }
        }

        return false;
    }

    /// <summary>Synthetic GUID space for static templates (never collides with packed WoW GUIDs).</summary>
    public static ulong StaticGuid(string kind, uint entry)
    {
        var hi = kind.ToLowerInvariant() switch
        {
            "gameobject" or "go" => 0x8100UL,
            "item" => 0x8200UL,
            "quest" => 0x8300UL,
            _ => 0x8000UL // creature
        };
        return (hi << 48) | entry;
    }

    public static bool IsStaticGuid(ulong guid) => (guid >> 48) is 0x8000 or 0x8100 or 0x8200 or 0x8300;

    private static byte InferTypeId(ulong guid)
    {
        if (IsStaticGuid(guid))
        {
            return (guid >> 48) switch
            {
                0x8100 => (byte)5,
                0x8200 => (byte)1,
                _ => (byte)3
            };
        }

        return WorldIntelService.InferTypeId(guid);
    }

    private void ScheduleSave()
    {
        // SoftRealm persists chat + roster + portal members only — OM stays in-memory.
        return;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var store = JsonSerializer.Deserialize<OmStore>(json, JsonOpts);
            if (store?.Objects is null)
            {
                return;
            }

            foreach (var row in store.Objects)
            {
                if (!ulong.TryParse(row.Guid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var guid)
                    || guid == 0)
                {
                    continue;
                }

                var x = row.X; var y = row.Y; var z = row.Z; var o = row.O;
                if (!UpdateObjectProjector.IsSaneWorldPosition(x, y, z))
                {
                    x = y = z = o = 0;
                }

                _objects[guid] = new WorldObject(
                    guid,
                    row.Name,
                    row.Entry,
                    x, y, z, o,
                    row.Health, row.MaxHealth,
                    row.TypeId,
                    row.LastSeenUtc == default ? DateTimeOffset.UtcNow : row.LastSeenUtc,
                    row.SeenBy,
                    row.StaticName,
                    Alive: false, // restored cold — becomes alive when re-observed
                    MapId: row.MapId,
                    Source: string.IsNullOrWhiteSpace(row.Source) ? "cache" : row.Source!,
                    FirstSeenUtc: row.FirstSeenUtc == default ? DateTimeOffset.UtcNow : row.FirstSeenUtc);
            }
        }
        catch
        {
            // corrupt cache — start empty
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Cap persistence: prefer named / with-entry / recently seen; always keep static templates.
            var rows = _objects.Values
                .OrderByDescending(o => o.Source == "static")
                .ThenByDescending(o => !string.IsNullOrWhiteSpace(o.Name) || !string.IsNullOrWhiteSpace(o.StaticName))
                .ThenByDescending(o => o.LastSeenUtc)
                .Take(8_000)
                .Select(o => new OmRow
                {
                    Guid = o.Guid.ToString("X16", CultureInfo.InvariantCulture),
                    Name = o.Name,
                    Entry = o.Entry,
                    X = o.X,
                    Y = o.Y,
                    Z = o.Z,
                    O = o.Orientation,
                    Health = o.Health,
                    MaxHealth = o.MaxHealth,
                    TypeId = o.TypeId,
                    LastSeenUtc = o.LastSeenUtc,
                    FirstSeenUtc = o.FirstSeenUtc,
                    SeenBy = o.SeenBy?.ToArray(),
                    StaticName = o.StaticName,
                    MapId = o.MapId,
                    Source = o.Source
                })
                .ToList();

            var store = new OmStore
            {
                SavedAtUtc = DateTimeOffset.UtcNow,
                Objects = rows
            };

            lock (_io)
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(store, JsonOpts));
            }
        }
        catch
        {
            // ignore IO
        }
    }

    private sealed class OmStore
    {
        public DateTimeOffset SavedAtUtc { get; set; }
        public List<OmRow> Objects { get; set; } = new();
    }

    private sealed class OmRow
    {
        public string Guid { get; set; } = "";
        public string? Name { get; set; }
        public uint Entry { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float O { get; set; }
        public uint Health { get; set; }
        public uint MaxHealth { get; set; }
        public byte TypeId { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public DateTimeOffset FirstSeenUtc { get; set; }
        public string[]? SeenBy { get; set; }
        public string? StaticName { get; set; }
        public uint MapId { get; set; }
        public string? Source { get; set; }
    }
}
