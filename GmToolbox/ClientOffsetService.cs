using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AscensionNetTool;

/// <summary>
/// Portable Ascension version fingerprint + masked AOB rediscovery.
/// Runs on startup / after path save. No Python required on the user machine.
/// ExtProxy still validates/remaps at inject time; this keeps Config/offsets.resolved.json fresh
/// and logs whether the client PE moved.
/// </summary>
static class ClientOffsetService
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    static readonly object Gate = new();
    static string _lastSummary = "not scanned";

    public static string LastSummary => _lastSummary;

    public static string FingerprintPath =>
        Path.Combine(Paths.AppRoot, "Config", "client-fingerprint.json");

    public static string ResolvedPath =>
        Path.Combine(Paths.AppRoot, "Config", "offsets.resolved.json");

    public static string PatternsPath =>
        Path.Combine(Paths.AppRoot, "Config", "offset-patterns.json");

    public sealed class ScanResult
    {
        public bool Ok { get; set; }
        public bool Skipped { get; set; }
        public bool ClientChanged { get; set; }
        public string Summary { get; set; } = "";
        public string? AscensionHash { get; set; }
        public string? ExtensionsHash { get; set; }
        public long AscensionSize { get; set; }
        public long ExtensionsSize { get; set; }
        public int Pass { get; set; }
        public int Fail { get; set; }
        public List<object> Sites { get; set; } = new();
        public string? Error { get; set; }
    }

    public static ScanResult EnsureFresh(Action<string>? log = null)
    {
        lock (Gate)
        {
            var r = ScanCore(force: false, log);
            _lastSummary = r.Summary;
            return r;
        }
    }

    public static ScanResult ForceRescan(Action<string>? log = null)
    {
        lock (Gate)
        {
            var r = ScanCore(force: true, log);
            _lastSummary = r.Summary;
            return r;
        }
    }

    static ScanResult ScanCore(bool force, Action<string>? log)
    {
        var result = new ScanResult();
        try
        {
            if (!SettingsStore.IsAscensionConfigured())
            {
                result.Skipped = true;
                result.Summary = "paths not configured — skip offset scan";
                log?.Invoke(result.Summary);
                return result;
            }

            string exe = SettingsStore.Current.AscensionExe;
            string ext = Path.Combine(SettingsStore.Current.LiveDir, "Extensions.dll");
            if (!File.Exists(ext))
            {
                result.Error = "Extensions.dll missing beside Ascension.exe";
                result.Summary = result.Error;
                log?.Invoke(result.Summary);
                return result;
            }

            string exeHash = Sha256File(exe);
            string extHash = Sha256File(ext);
            var exeInfo = new FileInfo(exe);
            var extInfo = new FileInfo(ext);
            result.AscensionHash = exeHash;
            result.ExtensionsHash = extHash;
            result.AscensionSize = exeInfo.Length;
            result.ExtensionsSize = extInfo.Length;

            bool changed = force || FingerprintChanged(exeHash, extHash, exeInfo.Length, extInfo.Length);
            result.ClientChanged = changed;

            if (!changed && File.Exists(ResolvedPath))
            {
                result.Ok = true;
                result.Skipped = true;
                result.Summary = $"client unchanged (sha asc={exeHash[..8]}… ext={extHash[..8]}…)";
                log?.Invoke("Offset scan: " + result.Summary);
                return result;
            }

            log?.Invoke(changed
                ? "Ascension/Extensions fingerprint changed — rediscovering offsets…"
                : "Force offset rediscovery…");

            var patterns = LoadPatterns();
            byte[] ascImg = FlattenPe(exe, out uint ascBase);
            byte[] extImg = FlattenPe(ext, out uint extBase);

            int pass = 0, fail = 0;
            var sites = new List<object>();
            foreach (var p in patterns)
            {
                byte[] img = p.Module.Equals("Extensions", StringComparison.OrdinalIgnoreCase) ? extImg : ascImg;
                uint stock = ParseHexU32(p.StockRva);
                byte[] pat = Convert.FromHexString(p.Pattern);
                byte[] mask = Convert.FromHexString(p.Mask);
                if (pat.Length != mask.Length || pat.Length == 0)
                {
                    fail++;
                    sites.Add(new { p.Name, status = "FAIL", error = "bad pattern/mask length" });
                    continue;
                }

                bool stockOk = MatchAt(img, (int)stock, pat, mask);
                var (found, hits) = ScanMasked(img, pat, mask, (int)stock);
                bool ok = stockOk || (found is int f && (hits == 1 || Math.Abs(f - (int)stock) <= 0x10000));
                if (ok) pass++; else fail++;
                uint resolved = found is int rf ? (uint)rf : stock;
                sites.Add(new
                {
                    name = p.Name,
                    module = p.Module,
                    stockRva = $"0x{stock:X}",
                    resolvedRva = $"0x{resolved:X}",
                    stockOk,
                    hitCount = hits,
                    status = ok ? "PASS" : "FAIL",
                });
            }

            result.Pass = pass;
            result.Fail = fail;
            result.Sites = sites;
            result.Ok = fail == 0;

            Directory.CreateDirectory(Path.Combine(Paths.AppRoot, "Config"));
            var doc = new
            {
                generatedUtc = DateTime.UtcNow.ToString("o"),
                ascension = exe,
                extensions = ext,
                ascensionImageBase = $"0x{ascBase:X}",
                extensionsImageBase = $"0x{extBase:X}",
                ascensionSha256 = exeHash,
                extensionsSha256 = extHash,
                summary = new { pass, fail },
                sites,
            };
            File.WriteAllText(ResolvedPath, JsonSerializer.Serialize(doc, JsonOpts));

            var fp = new
            {
                ascensionSha256 = exeHash,
                extensionsSha256 = extHash,
                ascensionSize = exeInfo.Length,
                extensionsSize = extInfo.Length,
                scannedUtc = DateTime.UtcNow.ToString("o"),
                pass,
                fail,
            };
            File.WriteAllText(FingerprintPath, JsonSerializer.Serialize(fp, JsonOpts));

            result.Summary = fail == 0
                ? $"offset scan OK {pass}/{pass + fail} (client {(changed ? "updated" : "forced")})"
                : $"offset scan WARN {pass} pass / {fail} fail — ExtProxy will still validate at inject";
            log?.Invoke(result.Summary);
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.Summary = "offset scan error: " + ex.Message;
            log?.Invoke(result.Summary);
            return result;
        }
    }

    static bool FingerprintChanged(string exeHash, string extHash, long exeSize, long extSize)
    {
        try
        {
            if (!File.Exists(FingerprintPath))
                return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(FingerprintPath));
            var r = doc.RootElement;
            string a = r.TryGetProperty("ascensionSha256", out var ah) ? ah.GetString() ?? "" : "";
            string e = r.TryGetProperty("extensionsSha256", out var eh) ? eh.GetString() ?? "" : "";
            long asz = r.TryGetProperty("ascensionSize", out var aszEl) ? aszEl.GetInt64() : -1;
            long esz = r.TryGetProperty("extensionsSize", out var eszEl) ? eszEl.GetInt64() : -1;
            return !string.Equals(a, exeHash, StringComparison.OrdinalIgnoreCase)
                   || !string.Equals(e, extHash, StringComparison.OrdinalIgnoreCase)
                   || asz != exeSize || esz != extSize;
        }
        catch
        {
            return true;
        }
    }

    sealed class PatternRow
    {
        public string Name { get; set; } = "";
        public string Module { get; set; } = "Ascension";
        public string StockRva { get; set; } = "0x0";
        public string Pattern { get; set; } = "";
        public string Mask { get; set; } = "";
    }

    static List<PatternRow> LoadPatterns()
    {
        try
        {
            if (File.Exists(PatternsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(PatternsPath));
                if (doc.RootElement.TryGetProperty("sites", out var sites))
                {
                    var list = new List<PatternRow>();
                    foreach (var s in sites.EnumerateArray())
                    {
                        list.Add(new PatternRow
                        {
                            Name = s.GetProperty("name").GetString() ?? "",
                            Module = s.TryGetProperty("module", out var m) ? (m.GetString() ?? "Ascension") : "Ascension",
                            StockRva = s.TryGetProperty("stockRva", out var r) ? (r.GetString() ?? "0")
                                : (s.TryGetProperty("stock_rva", out var r2) ? (r2.GetString() ?? "0") : "0"),
                            Pattern = s.GetProperty("pattern").GetString() ?? "",
                            Mask = s.GetProperty("mask").GetString() ?? "",
                        });
                    }
                    if (list.Count > 0)
                        return list;
                }
            }
        }
        catch { }

        // Embedded defaults — must match ExtProxy/OffsetResolve.c + Config/offset-patterns.json
        return new List<PatternRow>
        {
            new() { Name = "NetClientSend", Module = "Ascension", StockRva = "0x232B50", Pattern = "558bec568bf183be34050000", Mask = "ffffffffffffffffffffffff" },
            new() { Name = "PacketQueue", Module = "Ascension", StockRva = "0x6F40", Pattern = "558beca10000000085c07534", Mask = "ffffffff00000000ffffffff" },
            new() { Name = "FrameScriptExecute", Module = "Ascension", StockRva = "0x419210", Pattern = "558bec5183050000000001a1", Mask = "ffffffffffff00000000ffff" },
            new() { Name = "RegisterFunction", Module = "Ascension", StockRva = "0x417F90", Pattern = "558bec8b450c568b3500000000", Mask = "ffffffffffffffffff00000000" },
            new() { Name = "LuaToNumber", Module = "Ascension", StockRva = "0x44E030", Pattern = "558bec8b450c8b4d0883ec10e800000000", Mask = "ffffffffffffffffffffffffff00000000" },
            new() { Name = "LuaPushNumber", Module = "Ascension", StockRva = "0x44E2A0", Pattern = "558bec8b4d08dd450c8b410c", Mask = "ffffffffffffffffffffffff" },
            new() { Name = "LuaPushString", Module = "Ascension", StockRva = "0x44E350", Pattern = "558bec8b550c85d2751c8b45", Mask = "ffffffffffffffffffffffff" },
            new() { Name = "LuaToLString", Module = "Ascension", StockRva = "0x44E0E0", Pattern = "558bec568b7508578b7d0c8bc78bcee8", Mask = "ffffffffffffffffffffffffffffffff" },
            new() { Name = "GameUiSetTarget", Module = "Ascension", StockRva = "0x124BF0", Pattern = "558bec81ecac020000538b5d08568b75", Mask = "ffffffffffffffffffffffffffffffff" },
            new() { Name = "ExtProcessIncoming", Module = "Extensions", StockRva = "0x2C66C0", Pattern = "558bec6aff680000000064a1000000005081ec84000000a10000000033c58945f0535657508d45f464a3000000008955", Mask = "ffffffffffff00000000ffffffffffffffffffffffffffff00000000ffffffffffffffffffffffffffffffffffffffff" },
            new() { Name = "ExtOpcodeToName", Module = "Extensions", StockRva = "0x2C76A0", Pattern = "8b4424043dd40900000f8749", Mask = "ffffffffffffffffffffffff" },
            new() { Name = "ExtSend", Module = "Extensions", StockRva = "0x312990", Pattern = "a100000000566a01ff74240cffd083c4", Mask = "ff00000000ffffffffffffffffffffff" },
            new() { Name = "ExtCreatePacket", Module = "Extensions", StockRva = "0x312220", Pattern = "558bec6aff680000000064a1000000005083ec08535657a10000000033c5508d45f464a3000000008b7d08b8b0fa8400", Mask = "ffffffffffff00000000ffffffffffffffffffffffffffff00000000ffffffffffffffffffffffffffffffffffffffff" },
        };
    }

    static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    static uint ParseHexU32(string s)
    {
        s = (s ?? "0").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return Convert.ToUInt32(s, 16);
    }

    static bool MatchAt(byte[] hay, int off, byte[] pat, byte[] mask)
    {
        if (off < 0 || off + pat.Length > hay.Length)
            return false;
        for (int i = 0; i < pat.Length; i++)
        {
            if ((hay[off + i] & mask[i]) != (pat[i] & mask[i]))
                return false;
        }
        return true;
    }

    static (int? found, int hits) ScanMasked(byte[] hay, byte[] pat, byte[] mask, int prefer)
    {
        // Near-stock window first so generic prologues cannot flood a global hit cap
        // before the real site (Extensions SEH frames used to hit this).
        const int window = 0x20000;
        int lo = Math.Max(0, prefer - window);
        int hi = Math.Min(hay.Length - pat.Length, prefer + window);
        var near = new List<int>();
        for (int i = lo; i <= hi; i++)
        {
            if (MatchAt(hay, i, pat, mask))
                near.Add(i);
        }
        if (near.Count == 1)
            return (near[0], 1);
        if (near.Count > 1)
        {
            int best = -1, bestD = int.MaxValue;
            foreach (int h in near)
            {
                int d = Math.Abs(h - prefer);
                if (d < bestD)
                {
                    bestD = d;
                    best = h;
                }
            }
            if (best >= 0 && bestD <= 0x10000)
                return (best, near.Count);
        }

        var hits = new List<int>();
        int lim = hay.Length - pat.Length;
        for (int i = 0; i <= lim && hits.Count < 64; i++)
        {
            if (MatchAt(hay, i, pat, mask))
                hits.Add(i);
        }
        if (hits.Count == 1)
            return (hits[0], 1);
        if (hits.Count > 1)
        {
            int best = -1, bestD = int.MaxValue;
            foreach (int h in hits)
            {
                int d = Math.Abs(h - prefer);
                if (d < bestD && d <= 0x10000)
                {
                    bestD = d;
                    best = h;
                }
            }
            if (best >= 0)
                return (best, hits.Count);
        }
        return (null, hits.Count);
    }

    /// <summary>Map PE file into SizeOfImage buffer (section RVA layout).</summary>
    public static byte[] FlattenPe(string path, out uint imageBase)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < 0x40 || raw[0] != (byte)'M' || raw[1] != (byte)'Z')
            throw new InvalidDataException("Not a PE: " + path);
        int eLfanew = BitConverter.ToInt32(raw, 0x3C);
        if (eLfanew <= 0 || eLfanew + 0x18 >= raw.Length)
            throw new InvalidDataException("Bad PE header: " + path);
        if (BitConverter.ToUInt32(raw, eLfanew) != 0x00004550)
            throw new InvalidDataException("Missing PE signature: " + path);
        ushort magic = BitConverter.ToUInt16(raw, eLfanew + 0x18);
        if (magic != 0x10B) // PE32 only (Ascension is 32-bit)
            throw new InvalidDataException("Expected PE32: " + path);
        imageBase = BitConverter.ToUInt32(raw, eLfanew + 0x34);
        uint sizeOfImage = BitConverter.ToUInt32(raw, eLfanew + 0x50);
        uint sizeOfHeaders = BitConverter.ToUInt32(raw, eLfanew + 0x54);
        ushort numSec = BitConverter.ToUInt16(raw, eLfanew + 6);
        ushort optSize = BitConverter.ToUInt16(raw, eLfanew + 0x14);
        int secTable = eLfanew + 0x18 + optSize;

        var img = new byte[sizeOfImage];
        int hdrCopy = (int)Math.Min(sizeOfHeaders, (uint)raw.Length);
        Buffer.BlockCopy(raw, 0, img, 0, hdrCopy);
        for (int i = 0; i < numSec; i++)
        {
            int o = secTable + i * 40;
            if (o + 40 > raw.Length) break;
            uint va = BitConverter.ToUInt32(raw, o + 12);
            uint rawSize = BitConverter.ToUInt32(raw, o + 16);
            uint rawPtr = BitConverter.ToUInt32(raw, o + 20);
            if (rawSize == 0 || rawPtr == 0) continue;
            int copy = (int)Math.Min(rawSize, (uint)raw.Length - rawPtr);
            if (copy <= 0) continue;
            if (va + copy > img.Length)
                copy = (int)(img.Length - va);
            if (copy > 0)
                Buffer.BlockCopy(raw, (int)rawPtr, img, (int)va, copy);
        }
        return img;
    }
}
