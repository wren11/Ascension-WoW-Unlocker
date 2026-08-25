using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AscensionNetTool;

enum FuzzFieldKind : byte
{
    Opcode = 0,
    U8 = 1,
    U16 = 2,
    U32 = 3,
    I32 = 4,
    Float = 5,
    Guid = 6,
    Bytes = 7,
}

sealed class DetectedField
{
    public int Offset { get; init; }
    public int Size { get; init; }
    public FuzzFieldKind Kind { get; init; }
    public string Label { get; init; } = "";
    public ulong Sample { get; init; }
}

static class PacketStructure
{
    public static int BodyStart(byte[] data, uint opcode)
    {
        if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == opcode)
            return 4;
        if (data.Length >= 2 && BitConverter.ToUInt16(data, 0) == (ushort)opcode)
            return 2;
        return 0;
    }

    public static List<DetectedField> Detect(byte[] data, uint opcode)
    {
        var fields = new List<DetectedField>(64);
        int o = BodyStart(data, opcode);
        if (o > 0)
        {
            fields.Add(new DetectedField
            {
                Offset = 0,
                Size = o,
                Kind = FuzzFieldKind.Opcode,
                Label = $"opcode 0x{opcode:X4}",
                Sample = opcode,
            });
        }

        int guard = 0;
        while (o < data.Length && guard++ < 512)
        {
            int slen = AsciiLen(data, o);
            if (slen >= 3)
            {
                fields.Add(new DetectedField
                {
                    Offset = o,
                    Size = slen + (o + slen < data.Length && data[o + slen] == 0 ? 1 : 0),
                    Kind = FuzzFieldKind.Bytes,
                    Label = "string/bytes",
                });
                o += fields[^1].Size;
                continue;
            }
            if (LooksGuid(data, o))
            {
                fields.Add(new DetectedField
                {
                    Offset = o,
                    Size = 8,
                    Kind = FuzzFieldKind.Guid,
                    Label = "guid",
                    Sample = BitConverter.ToUInt64(data, o),
                });
                o += 8;
                continue;
            }
            if (LooksFloat(data, o))
            {
                float f = BitConverter.ToSingle(data, o);
                fields.Add(new DetectedField
                {
                    Offset = o,
                    Size = 4,
                    Kind = FuzzFieldKind.Float,
                    Label = $"float {f:G6}",
                    Sample = BitConverter.ToUInt32(data, o),
                });
                o += 4;
                continue;
            }
            if (o + 4 <= data.Length)
            {
                uint v = BitConverter.ToUInt32(data, o);
                fields.Add(new DetectedField
                {
                    Offset = o,
                    Size = 4,
                    Kind = FuzzFieldKind.U32,
                    Label = $"u32 0x{v:X8}",
                    Sample = v,
                });
                o += 4;
                continue;
            }
            if (o + 2 <= data.Length)
            {
                ushort v = BitConverter.ToUInt16(data, o);
                fields.Add(new DetectedField
                {
                    Offset = o,
                    Size = 2,
                    Kind = FuzzFieldKind.U16,
                    Label = $"u16 0x{v:X4}",
                    Sample = v,
                });
                o += 2;
                continue;
            }
            fields.Add(new DetectedField
            {
                Offset = o,
                Size = 1,
                Kind = FuzzFieldKind.U8,
                Label = $"u8 0x{data[o]:X2}",
                Sample = data[o],
            });
            o += 1;
        }
        return fields;
    }

    static int AsciiLen(byte[] d, int o)
    {
        int n = 0;
        while (o + n < d.Length && d[o + n] >= 0x20 && d[o + n] < 0x7F)
            n++;
        return n >= 3 ? n : 0;
    }

    static bool LooksGuid(byte[] d, int o)
    {
        if (o + 8 > d.Length) return false;
        uint hi = BitConverter.ToUInt32(d, o + 4);
        uint lo = BitConverter.ToUInt32(d, o);
        if (lo == 0 && hi == 0) return false;
        ushort tag = (ushort)(hi >> 16);
        return tag is 0xF130 or 0xF131 or 0xF150 or 0xF151 or 0xF140 or 0xF110 or 0xF120
            || (hi == 0 && lo > 0 && lo < 0x0FFFFFFF);
    }

    static bool LooksFloat(byte[] d, int o)
    {
        if (o + 4 > d.Length) return false;
        float f = BitConverter.ToSingle(d, o);
        if (float.IsNaN(f) || float.IsInfinity(f) || f == 0f) return false;
        float a = Math.Abs(f);
        return a > 0.001f && a < 1e6f;
    }

    public static string Fingerprint(ReadOnlySpan<byte> pkt)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(pkt, hash);
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}

sealed class FuzzVariant
{
    public required byte[] Packet { get; init; }
    public required string Strategy { get; init; }
    public required string Description { get; init; }
    public int FieldOffset { get; init; } = -1;
    public long Index { get; init; }
    public string Fingerprint => PacketStructure.Fingerprint(Packet);
}

sealed class FuzzHit
{
    public required long VariantIndex { get; init; }
    public required string Strategy { get; init; }
    public required string Description { get; init; }
    public required string SentHex { get; init; }
    public required List<string> Responses { get; init; }
    public DateTime Utc { get; init; } = DateTime.UtcNow;
}

sealed class PacketFuzzSettings
{
    public bool MutateBytes { get; set; } = true;
    public bool MutateU16 { get; set; } = true;
    public bool MutateU32 { get; set; } = true;
    public bool MutateInts { get; set; } = true;
    public bool MutateFloats { get; set; } = true;
    public bool MutateGuids { get; set; } = true;
    public bool MutatePairs { get; set; } = true;
    public bool BitFlips { get; set; } = true;
    public bool FuzzyRandom { get; set; } = true;
    public bool RetryNoResponse { get; set; } = true;
    public bool AutoRecover { get; set; } = true;
    public bool BruteLayouts { get; set; } = true;
    public bool OmInjectIntoSeed { get; set; } = true;
    public bool ScanOtherInstances { get; set; } = true;
    public bool TryPadding { get; set; } = true;
    public bool RefreshDynamicFields { get; set; } = true;
    public int PacketsPerSec { get; set; } = 40;
    public int Parallel { get; set; } = 2;
    public int CorrelateMs { get; set; } = 40;
    public int MaxVariants { get; set; } = 50000;
    public int RandomBudget { get; set; } = 2000;
    public int NoResponseRetries { get; set; } = 2;
    public int OmGuidBudget { get; set; } = 16;
    public int OmEntryBudget { get; set; } = 12;
    public int OmCoordBudget { get; set; } = 10;
    public int OmStringBudget { get; set; } = 12;
    public int OmU32Budget { get; set; } = 16;
    public int MaxLayoutPerms { get; set; } = 6;
    /// <summary>When true, also synthesize packets from opcode+OM without requiring a seed body.</summary>
    public bool OpcodeBruteMode { get; set; } = true;
}

sealed class PacketFuzzPersist
{
    public string SeedHex { get; set; } = "";
    public uint Opcode { get; set; }
    public long NextIndex { get; set; }
    public long Sent { get; set; }
    public long Interesting { get; set; }
    public long Crashes { get; set; }
    public long Blacklisted { get; set; }
    public string? LoginPacketHex { get; set; }
    public List<string> Blacklist { get; set; } = new();
    public PacketFuzzSettings Settings { get; set; } = new();
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
}

static class PacketFuzzStore
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Dir => Path.Combine(Paths.AppRoot, "Config", "packet-fuzz");
    public static string StatePath => Path.Combine(Dir, "state.json");
    public static string HitsPath => Path.Combine(Dir, "hits.jsonl");
    public static string BlacklistPath => Path.Combine(Dir, "blacklist.txt");

    public static void EnsureDir() => Directory.CreateDirectory(Dir);

    public static void Save(PacketFuzzPersist p)
    {
        EnsureDir();
        p.SavedUtc = DateTime.UtcNow;
        File.WriteAllText(StatePath, JsonSerializer.Serialize(p, JsonOpts));
        File.WriteAllLines(BlacklistPath, p.Blacklist);
    }

    public static PacketFuzzPersist? Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var p = JsonSerializer.Deserialize<PacketFuzzPersist>(File.ReadAllText(StatePath));
            if (p is null) return null;
            if (File.Exists(BlacklistPath))
            {
                foreach (string line in File.ReadAllLines(BlacklistPath))
                {
                    string t = line.Trim();
                    if (t.Length > 0 && !p.Blacklist.Contains(t))
                        p.Blacklist.Add(t);
                }
            }
            return p;
        }
        catch { return null; }
    }

    public static void AppendHit(FuzzHit hit)
    {
        EnsureDir();
        string line = JsonSerializer.Serialize(hit);
        File.AppendAllText(HitsPath, line + Environment.NewLine);
    }
}
