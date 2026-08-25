using System.Collections.Concurrent;
using System.Globalization;
using HeadlessClient.Domain.World;

namespace HeadlessClient.Infrastructure.Monitoring;

/// <summary>GUID/name player dossier used for chat enrichment + Who/name-query gating.</summary>
public sealed class PlayerDirectory
{
    readonly ConcurrentDictionary<string, PlayerInfo> _byGuid = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string> _guidByName = new(StringComparer.OrdinalIgnoreCase);

    public static string NormGuid(ulong g) => g.ToString("X16", CultureInfo.InvariantCulture);

    public static string NormGuid(string? g)
    {
        if (string.IsNullOrWhiteSpace(g))
        {
            return "";
        }

        g = g.Trim();
        if (g.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            g = g[2..];
        }

        return ulong.TryParse(g, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u)
            ? u.ToString("X16", CultureInfo.InvariantCulture)
            : g.ToUpperInvariant();
    }

    public bool TryGetByGuid(string? guid, out PlayerInfo info) =>
        _byGuid.TryGetValue(NormGuid(guid), out info!);

    public bool TryGetByName(string? name, out PlayerInfo info)
    {
        info = null!;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (_guidByName.TryGetValue(name.Trim(), out var guid) && _byGuid.TryGetValue(guid, out info!))
        {
            return true;
        }

        return false;
    }

    public string ResolveName(string? guid, string? fallback = null)
    {
        guid = NormGuid(guid);
        if (guid.Length == 0)
        {
            return fallback ?? "";
        }

        return _byGuid.TryGetValue(guid, out var p) && p.HasName ? p.Name : fallback ?? "";
    }

    public PlayerInfo Observe(
        string? guid,
        string? name,
        int level = -1,
        int classId = -1,
        int race = -1,
        int gender = -1,
        string? guild = null,
        string? zone = null,
        string? realm = null)
    {
        guid = NormGuid(guid);
        name = (name ?? "").Trim();
        if (guid.Length == 0 && name.Length > 0 && _guidByName.TryGetValue(name, out var known))
        {
            guid = known;
        }

        if (guid.Length == 0)
        {
            guid = name.Length > 0 ? $"NAME:{name.ToUpperInvariant()}" : $"ANON:{Guid.NewGuid():N}";
        }

        var p = _byGuid.AddOrUpdate(guid,
            _ => new PlayerInfo
            {
                Guid = guid.StartsWith("NAME:", StringComparison.Ordinal) ? "" : guid,
                Name = name,
                Realm = realm ?? "",
                Level = level,
                ClassId = classId,
                Race = race,
                Gender = gender,
                Guild = guild ?? "",
                Zone = zone ?? "",
                LastSeenUtc = DateTimeOffset.UtcNow
            },
            (_, cur) =>
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    cur.Name = name;
                }

                if (!string.IsNullOrWhiteSpace(realm))
                {
                    cur.Realm = realm!;
                }

                if (level > 0)
                {
                    cur.Level = level;
                }

                if (classId > 0)
                {
                    cur.ClassId = classId;
                }

                if (race > 0)
                {
                    cur.Race = race;
                }

                if (gender >= 0)
                {
                    cur.Gender = gender;
                }

                if (!string.IsNullOrWhiteSpace(guild))
                {
                    cur.Guild = guild!;
                }

                if (!string.IsNullOrWhiteSpace(zone))
                {
                    cur.Zone = zone!;
                }

                if (cur.Guid.Length == 0 && !guid.StartsWith("NAME:", StringComparison.Ordinal)
                    && !guid.StartsWith("ANON:", StringComparison.Ordinal))
                {
                    cur.Guid = guid;
                }

                cur.LastSeenUtc = DateTimeOffset.UtcNow;
                return cur;
            });

        if (p.HasName)
        {
            _guidByName[p.Name] = guid;
        }

        return p;
    }

    public void NoteChat(string? guid, string? name)
    {
        var p = Observe(guid, name);
        p.LastChatUtc = DateTimeOffset.UtcNow;
        p.MessageCount++;
    }

    public IReadOnlyList<PlayerInfo> Snapshot() => _byGuid.Values.ToArray();
}
