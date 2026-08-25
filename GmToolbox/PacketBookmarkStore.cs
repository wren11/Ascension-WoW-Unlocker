using System.Text.Json;

namespace AscensionNetTool;

sealed class PacketBookmark
{
    public int Slot { get; set; }
    public string Label { get; set; } = "";
    /// <summary>0 = to server, 1 = into client.</summary>
    public int Dir { get; set; }
    public string Hex { get; set; } = "";
    public uint Opcode { get; set; }
    public string OpcodeName { get; set; } = "";
}

/// <summary>Persistent packet bookmarks (slots 1–16) under Config/packet-bookmarks.json.</summary>
static class PacketBookmarkStore
{
    public const int SlotCount = 16;

    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    static Dictionary<int, PacketBookmark>? _cache;

    public static string PathFile =>
        System.IO.Path.Combine(Paths.AppRoot, "Config", "packet-bookmarks.json");

    public static PacketBookmark? Get(int slot)
    {
        if (slot < 1 || slot > SlotCount) return null;
        lock (Gate)
        {
            EnsureLoaded();
            return _cache!.TryGetValue(slot, out var b) ? Clone(b) : null;
        }
    }

    public static IReadOnlyList<PacketBookmark> All()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _cache!.Values.OrderBy(b => b.Slot).Select(Clone).ToList();
        }
    }

    public static void Set(int slot, byte[] data, int dir, string? label = null, uint opcode = 0)
    {
        if (slot < 1 || slot > SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (data.Length == 0 || data.Length > IpcConstants.ReplayMax)
            throw new InvalidOperationException("bookmark payload size invalid");

        if (opcode == 0 && data.Length >= 2)
            opcode = data.Length >= 4 && BitConverter.ToUInt32(data, 0) <= 0xFFFFu
                ? BitConverter.ToUInt32(data, 0)
                : BitConverter.ToUInt16(data, 0);

        var bm = new PacketBookmark
        {
            Slot = slot,
            Dir = dir == 0 ? 0 : 1,
            Hex = string.Join(" ", data.Select(b => b.ToString("X2"))),
            Opcode = Opcodes.Normalize(opcode),
            OpcodeName = Opcodes.Name(opcode),
            Label = string.IsNullOrWhiteSpace(label)
                ? $"0x{Opcodes.Normalize(opcode):X4}"
                : label.Trim(),
        };

        lock (Gate)
        {
            EnsureLoaded();
            _cache![slot] = bm;
            PersistUnlocked();
        }
    }

    public static void Clear(int slot)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (slot <= 0)
                _cache!.Clear();
            else
                _cache!.Remove(slot);
            PersistUnlocked();
        }
    }

    public static byte[]? ParseBytes(PacketBookmark bm)
    {
        try { return PacketView.ParseHex(bm.Hex); }
        catch { return null; }
    }

    /// <summary>Push all bookmarks into the live ExtProxy (best-effort).</summary>
    public static void SyncToProxy(ProxyClient proxy)
    {
        if (!proxy.Connected) return;
        foreach (var bm in All())
        {
            var bytes = ParseBytes(bm);
            if (bytes is null || bytes.Length == 0) continue;
            try { proxy.BookmarkSet(bm.Slot, bm.Dir, bytes); }
            catch { }
        }
    }

    static void EnsureLoaded()
    {
        if (_cache is not null) return;
        _cache = new Dictionary<int, PacketBookmark>();
        try
        {
            string path = PathFile;
            if (!File.Exists(path)) return;
            var list = JsonSerializer.Deserialize<List<PacketBookmark>>(File.ReadAllText(path));
            if (list is null) return;
            foreach (var b in list)
            {
                if (b.Slot < 1 || b.Slot > SlotCount) continue;
                if (string.IsNullOrWhiteSpace(b.Hex)) continue;
                _cache[b.Slot] = b;
            }
        }
        catch { }
    }

    static void PersistUnlocked()
    {
        string dir = System.IO.Path.GetDirectoryName(PathFile)!;
        Directory.CreateDirectory(dir);
        var list = _cache!.Values.OrderBy(b => b.Slot).ToList();
        File.WriteAllText(PathFile, JsonSerializer.Serialize(list, JsonOpts));
    }

    static PacketBookmark Clone(PacketBookmark b) => new()
    {
        Slot = b.Slot,
        Label = b.Label,
        Dir = b.Dir,
        Hex = b.Hex,
        Opcode = b.Opcode,
        OpcodeName = b.OpcodeName,
    };
}
