using System.Text.Json;
using System.Text.Json.Serialization;
using HeadlessClient.Infrastructure.Protocol;
using Microsoft.Extensions.Logging;

namespace HeadlessClient.Infrastructure.Monitoring;

/// <summary>
/// Persists every seen character name + last WHO/chat timestamps across Host restarts.
/// </summary>
public sealed class PlayerRosterStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly ILogger<PlayerRosterStore>? _log;
    private readonly object _io = new();
    private int _dirty;
    private DateTimeOffset _lastSaveUtc = DateTimeOffset.MinValue;

    public PlayerRosterStore(string? path = null, ILogger<PlayerRosterStore>? log = null)
    {
        _log = log;
        _path = string.IsNullOrWhiteSpace(path)
            ? DefaultPath()
            : Path.GetFullPath(path);
    }

    public string PathUsed => _path;

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeadlessClient");
        return Path.Combine(dir, "player-roster.json");
    }

    public IReadOnlyList<WhoEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Array.Empty<WhoEntry>();
            }

            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<RosterFile>(json, JsonOpts);
            if (doc?.Players is null || doc.Players.Count == 0)
            {
                return Array.Empty<WhoEntry>();
            }

            var now = DateTimeOffset.UtcNow;
            return doc.Players
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new WhoEntry(
                    p.Name.Trim(),
                    p.Guild ?? "",
                    p.Level,
                    p.ClassId,
                    p.Race,
                    p.ZoneId,
                    p.Gender,
                    p.Guid ?? "",
                    p.MessageCount,
                    p.LastSeenUtc == default ? now : p.LastSeenUtc,
                    p.LastWhoUtc,
                    Presence: "offline"))
                .ToList();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to load player roster from {Path}", _path);
            return Array.Empty<WhoEntry>();
        }
    }

    public void Save(IEnumerable<WhoEntry> players, bool force = false)
    {
        Interlocked.Exchange(ref _dirty, 1);
        if (!force && DateTimeOffset.UtcNow - _lastSaveUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        Flush(players);
    }

    public void Flush(IEnumerable<WhoEntry> players)
    {
        lock (_io)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var list = players
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.LastSeenUtc).First())
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new RosterPlayer
                    {
                        Name = p.Name,
                        Guild = p.Guild,
                        Level = p.Level,
                        ClassId = p.ClassId,
                        Race = p.Race,
                        ZoneId = p.ZoneId,
                        Gender = p.Gender,
                        Guid = p.Guid,
                        MessageCount = p.MessageCount,
                        LastSeenUtc = p.LastSeenUtc == default ? DateTimeOffset.UtcNow : p.LastSeenUtc,
                        LastWhoUtc = p.LastWhoUtc
                    })
                    .ToList();

                var file = new RosterFile
                {
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Players = list
                };
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
                File.Copy(tmp, _path, overwrite: true);
                try { File.Delete(tmp); } catch { /* ignore */ }
                _lastSaveUtc = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref _dirty, 0);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _dirty, 1);
                _log?.LogWarning(ex, "Failed to save player roster to {Path}", _path);
            }
        }
    }

    private sealed class RosterFile
    {
        public DateTimeOffset SavedAtUtc { get; set; }
        public List<RosterPlayer> Players { get; set; } = new();
    }

    private sealed class RosterPlayer
    {
        public string Name { get; set; } = "";
        public string? Guild { get; set; }
        public int Level { get; set; } = -1;
        public int ClassId { get; set; } = -1;
        public int Race { get; set; } = -1;
        public int ZoneId { get; set; }
        public byte Gender { get; set; }
        public string? Guid { get; set; }
        public long MessageCount { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public DateTimeOffset? LastWhoUtc { get; set; }
    }
}
