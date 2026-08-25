using System.Collections.Concurrent;

namespace AscensionNetTool;

/// <summary>In-memory GUID → player dossier, fed by name-query packets + OM snapshots.</summary>
static class PlayerDirectory
{
    public sealed class PlayerInfo
    {
        public string Guid { get; set; } = "";
        public string Name { get; set; } = "";
        public string Realm { get; set; } = "";
        public int Race { get; set; } = -1;
        public int Gender { get; set; } = -1;
        public int Class { get; set; } = -1;
        public int Level { get; set; } = -1;
        public int Faction { get; set; } = -1;
        public int Hp { get; set; } = -1;
        public int MaxHp { get; set; } = -1;
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public int MapId { get; set; } = -1;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastChatUtc { get; set; }
        public int MessageCount { get; set; }
    }

    const int MaxEntries = 4000;
    const int StaleMinutes = 120;

    static readonly ConcurrentDictionary<string, PlayerInfo> ByGuid =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, string> GuidByName =
        new(StringComparer.OrdinalIgnoreCase);

    public static string NormGuid(ulong g) => g.ToString("X16");
    public static string NormGuid(string? g)
    {
        if (string.IsNullOrWhiteSpace(g)) return "";
        g = g.Trim();
        if (g.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) g = g[2..];
        if (ulong.TryParse(g, System.Globalization.NumberStyles.HexNumber, null, out var u))
            return u.ToString("X16");
        return g.ToUpperInvariant();
    }

    public static bool TryGet(string guid, out PlayerInfo info)
        => ByGuid.TryGetValue(NormGuid(guid), out info!);

    public static string ResolveName(string guid, string? fallback = null)
    {
        guid = NormGuid(guid);
        if (guid.Length == 0) return fallback ?? "";
        if (ByGuid.TryGetValue(guid, out var p) && !string.IsNullOrWhiteSpace(p.Name))
            return p.Name;
        return fallback ?? "";
    }

    public static PlayerInfo ObserveName(string guid, string name, string? realm = null,
        int race = -1, int gender = -1, int classId = -1)
    {
        guid = NormGuid(guid);
        name = (name ?? "").Trim();
        var p = ByGuid.AddOrUpdate(guid,
            _ => new PlayerInfo
            {
                Guid = guid,
                Name = name,
                Realm = realm ?? "",
                Race = race,
                Gender = gender,
                Class = classId,
                LastSeenUtc = DateTime.UtcNow,
            },
            (_, cur) =>
            {
                if (!string.IsNullOrWhiteSpace(name)) cur.Name = name;
                if (!string.IsNullOrWhiteSpace(realm)) cur.Realm = realm!;
                if (race >= 0) cur.Race = race;
                if (gender >= 0) cur.Gender = gender;
                if (classId >= 0) cur.Class = classId;
                cur.LastSeenUtc = DateTime.UtcNow;
                return cur;
            });
        if (!string.IsNullOrWhiteSpace(p.Name))
            GuidByName[p.Name] = guid;
        MaybePrune();
        return p;
    }

    static void MaybePrune()
    {
        if (ByGuid.Count <= MaxEntries) return;
        var cutoff = DateTime.UtcNow.AddMinutes(-StaleMinutes);
        foreach (var kv in ByGuid)
        {
            if (kv.Value.LastSeenUtc < cutoff)
            {
                if (ByGuid.TryRemove(kv.Key, out var removed)
                    && !string.IsNullOrWhiteSpace(removed.Name))
                    GuidByName.TryRemove(removed.Name, out _);
            }
        }
        // Hard cap: drop oldest if still over
        if (ByGuid.Count <= MaxEntries) return;
        foreach (var kv in ByGuid.OrderBy(x => x.Value.LastSeenUtc).Take(ByGuid.Count - MaxEntries))
        {
            if (ByGuid.TryRemove(kv.Key, out var removed)
                && !string.IsNullOrWhiteSpace(removed.Name))
                GuidByName.TryRemove(removed.Name, out _);
        }
    }

    public static void ObserveUnit(ulong guid, int level, int faction, int hp, int maxHp,
        float x, float y, float z, int typeMask, string? nameHint = null)
    {
        // Players only (TYPE_PLAYER = 0x10)
        if ((typeMask & 0x10) == 0) return;
        string g = NormGuid(guid);
        ByGuid.AddOrUpdate(g,
            _ => new PlayerInfo
            {
                Guid = g,
                Name = nameHint ?? "",
                Level = level,
                Faction = faction,
                Hp = hp,
                MaxHp = maxHp,
                X = x, Y = y, Z = z,
                LastSeenUtc = DateTime.UtcNow,
            },
            (_, cur) =>
            {
                if (!string.IsNullOrWhiteSpace(nameHint)) cur.Name = nameHint!;
                if (level > 0) cur.Level = level;
                if (faction != 0) cur.Faction = faction;
                if (maxHp > 0) { cur.Hp = hp; cur.MaxHp = maxHp; }
                cur.X = x; cur.Y = y; cur.Z = z;
                cur.LastSeenUtc = DateTime.UtcNow;
                return cur;
            });
    }

    public static void NoteChat(string guid, string? name)
    {
        guid = NormGuid(guid);
        if (guid.Length == 0) return;
        var p = ObserveName(guid, name ?? "");
        p.MessageCount++;
        p.LastChatUtc = DateTime.UtcNow;
    }

    public static IReadOnlyList<PlayerInfo> Snapshot(int take = 200)
        => ByGuid.Values
            .OrderByDescending(p => p.LastSeenUtc)
            .Take(Math.Clamp(take, 1, 2000))
            .ToList();
}
