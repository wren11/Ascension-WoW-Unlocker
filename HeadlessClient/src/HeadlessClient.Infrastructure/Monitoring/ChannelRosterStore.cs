using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace HeadlessClient.Infrastructure.Monitoring;

/// <summary>
/// Persists joined / known chat channels across Host restarts and world reconnects.
/// Chat message bodies live in SQLite; this only remembers channel names to rejoin and show in the UI.
/// </summary>
public sealed class ChannelRosterStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly ILogger<ChannelRosterStore>? _log;
    private readonly object _io = new();
    private int _dirty;
    private DateTimeOffset _lastSaveUtc = DateTimeOffset.MinValue;

    public ChannelRosterStore(string? path = null, ILogger<ChannelRosterStore>? log = null)
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
        return Path.Combine(dir, "channels.json");
    }

    public ChannelRosterSnapshot Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return ChannelRosterSnapshot.Empty;
            }

            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<ChannelFile>(json, JsonOpts);
            if (doc is null)
            {
                return ChannelRosterSnapshot.Empty;
            }

            return new ChannelRosterSnapshot(
                NormalizeList(doc.Joined),
                NormalizeList(doc.Known),
                NormalizeList(doc.Invalid));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to load channel roster from {Path}", _path);
            return ChannelRosterSnapshot.Empty;
        }
    }

    public void Save(
        IEnumerable<string> joined,
        IEnumerable<string> known,
        IEnumerable<string>? invalid = null,
        bool force = false)
    {
        Interlocked.Exchange(ref _dirty, 1);
        if (!force && DateTimeOffset.UtcNow - _lastSaveUtc < TimeSpan.FromSeconds(1.5))
        {
            return;
        }

        Flush(joined, known, invalid);
    }

    public void Flush(
        IEnumerable<string> joined,
        IEnumerable<string> known,
        IEnumerable<string>? invalid = null)
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

                var file = new ChannelFile
                {
                    Joined = NormalizeList(joined),
                    Known = NormalizeList(known),
                    Invalid = NormalizeList(invalid ?? Array.Empty<string>()),
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };

                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
                File.Copy(tmp, _path, overwrite: true);
                File.Delete(tmp);
                _lastSaveUtc = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref _dirty, 0);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Failed to save channel roster to {Path}", _path);
            }
        }
    }

    private static List<string> NormalizeList(IEnumerable<string>? items) =>
        (items ?? Array.Empty<string>())
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => s.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private sealed class ChannelFile
    {
        public List<string> Joined { get; set; } = new();
        public List<string> Known { get; set; } = new();
        public List<string> Invalid { get; set; } = new();
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}

public sealed record ChannelRosterSnapshot(
    IReadOnlyList<string> Joined,
    IReadOnlyList<string> Known,
    IReadOnlyList<string> Invalid)
{
    public static ChannelRosterSnapshot Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}
