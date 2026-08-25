using System.Collections.Concurrent;
using System.Text.Json;

namespace AscensionNetTool;

public enum PacketFieldType
{
    U8, U16, U32, U64, I32, F32, Guid, CString, Bytes
}

public sealed class PacketFieldDef
{
    public int Offset { get; set; }
    public PacketFieldType Type { get; set; }
    public string Name { get; set; } = "";
    public int Size { get; set; } // for Bytes / CString max
}

public sealed class PacketSchema
{
    public uint Opcode { get; set; }
    public string Name { get; set; } = "";
    public string Dir { get; set; } = "either"; // cmsg/smsg/either
    public List<PacketFieldDef> Fields { get; set; } = new();
    public string Source { get; set; } = "seed"; // seed|capture|heuristic
}

/// <summary>
/// Verified + capture-driven CMSG/SMSG field layouts. Never invent Ascension remaps —
/// only layouts backed by PacketView decoders or explicit seed entries.
/// </summary>
static class PacketSchemaRegistry
{
    static readonly ConcurrentDictionary<uint, PacketSchema> ByOp = new();
    static int _seeded;

    public static void EnsureSeeded()
    {
        if (Interlocked.Exchange(ref _seeded, 1) == 1) return;
        SeedKnown();
        TryLoadDisk();
    }

    static void SeedKnown()
    {
        // Offsets are relative to body start AFTER framing detection by PacketView.
        // For u32-framed packets, body starts at +4.
        Add(0x003D, "CMSG_PLAYER_LOGIN", "cmsg",
            F(0, PacketFieldType.Guid, "guid"));
        Add(0x0037, "CMSG_CHAR_ENUM", "cmsg");
        Add(0x003E, "SMSG_NEW_WORLD", "smsg",
            F(0, PacketFieldType.U32, "mapId"),
            F(4, PacketFieldType.F32, "x"),
            F(8, PacketFieldType.F32, "y"),
            F(12, PacketFieldType.F32, "z"),
            F(16, PacketFieldType.F32, "o"));
        Add(0x0236, "SMSG_LOGIN_VERIFY_WORLD", "smsg",
            F(0, PacketFieldType.U32, "mapId"),
            F(4, PacketFieldType.F32, "x"),
            F(8, PacketFieldType.F32, "y"),
            F(12, PacketFieldType.F32, "z"),
            F(16, PacketFieldType.F32, "o"));
        Add(0x0096, "SMSG_MESSAGECHAT", "smsg",
            F(0, PacketFieldType.U8, "chatType"),
            F(1, PacketFieldType.U32, "language"));
        Add(0x0050, "CMSG_NAME_QUERY", "cmsg",
            F(0, PacketFieldType.Guid, "guid"));
        Add(0x0051, "SMSG_NAME_QUERY_RESPONSE", "smsg",
            F(0, PacketFieldType.Guid, "guid"));
        // Movement family (MSG_MOVE_* 0xB5-0xFF) — shared header after opcode
        for (uint op = 0xB5; op <= 0xFF; op++)
        {
            string n = Opcodes.Name(op);
            if (n.StartsWith("0x", StringComparison.Ordinal)) continue;
            Add(op, n, "either",
                F(0, PacketFieldType.U32, "guidLo"),
                F(4, PacketFieldType.U32, "moveFlags"),
                F(8, PacketFieldType.U16, "flags2"),
                F(10, PacketFieldType.U32, "time"),
                F(14, PacketFieldType.F32, "x"),
                F(18, PacketFieldType.F32, "y"),
                F(22, PacketFieldType.F32, "z"),
                F(26, PacketFieldType.F32, "o"));
        }
    }

    static PacketFieldDef F(int off, PacketFieldType t, string name, int size = 0) =>
        new() { Offset = off, Type = t, Name = name, Size = size };

    static void Add(uint op, string name, string dir, params PacketFieldDef[] fields)
    {
        ByOp[op] = new PacketSchema
        {
            Opcode = op,
            Name = name,
            Dir = dir,
            Fields = fields.ToList(),
            Source = "seed",
        };
    }

    static string DiskPath => Path.Combine(Paths.AppRoot, "Config", "packet-schemas.json");

    static void TryLoadDisk()
    {
        try
        {
            if (!File.Exists(DiskPath)) return;
            var list = JsonSerializer.Deserialize<List<PacketSchema>>(File.ReadAllText(DiskPath));
            if (list is null) return;
            foreach (var s in list)
                ByOp[s.Opcode] = s;
        }
        catch { }
    }

    public static void SaveDisk()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiskPath)!);
            File.WriteAllText(DiskPath, JsonSerializer.Serialize(ByOp.Values.OrderBy(s => s.Opcode),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static PacketSchema? Get(uint opcode) =>
        ByOp.TryGetValue(Opcodes.Normalize(opcode), out var s) ? s : null;

    public static IReadOnlyList<PacketSchema> All => ByOp.Values.OrderBy(s => s.Opcode).ToList();

    /// <summary>Infer simple fields from a captured body and merge into registry (capture-driven).</summary>
    public static PacketSchema InferAndStore(uint opcode, byte[] framed)
    {
        opcode = Opcodes.Normalize(opcode);
        int bodyOff = 0;
        if (framed.Length >= 4 && BitConverter.ToUInt32(framed, 0) == opcode) bodyOff = 4;
        else if (framed.Length >= 2 && BitConverter.ToUInt16(framed, 0) == (ushort)opcode) bodyOff = 2;
        var body = framed.AsSpan(bodyOff);
        var fields = new List<PacketFieldDef>();
        // Heuristic: walk aligned u32s that look like floats or small ints
        for (int i = 0; i + 4 <= body.Length && fields.Count < 24; i += 4)
        {
            float f = BitConverter.ToSingle(body.Slice(i, 4));
            uint u = BitConverter.ToUInt32(body.Slice(i, 4));
            if (f is > -20000 and < 20000 && Math.Abs(f) > 0.001f && float.IsFinite(f))
                fields.Add(F(i, PacketFieldType.F32, $"f{i:X2}"));
            else
                fields.Add(F(i, PacketFieldType.U32, $"u{i:X2}"));
        }
        var schema = new PacketSchema
        {
            Opcode = opcode,
            Name = Opcodes.Name(opcode),
            Dir = "either",
            Fields = fields,
            Source = "capture",
        };
        ByOp[opcode] = schema;
        return schema;
    }

    public static List<object> DecodeFields(uint opcode, byte[] framed)
    {
        EnsureSeeded();
        var schema = Get(opcode) ?? InferAndStore(opcode, framed);
        int bodyOff = 0;
        if (framed.Length >= 4 && BitConverter.ToUInt32(framed, 0) == opcode) bodyOff = 4;
        else if (framed.Length >= 2 && BitConverter.ToUInt16(framed, 0) == (ushort)opcode) bodyOff = 2;
        var body = framed.AsSpan(bodyOff);
        var rows = new List<object>();
        foreach (var f in schema.Fields)
        {
            object? val = null;
            try
            {
                val = f.Type switch
                {
                    PacketFieldType.U8 when f.Offset + 1 <= body.Length => body[f.Offset],
                    PacketFieldType.U16 when f.Offset + 2 <= body.Length => BitConverter.ToUInt16(body.Slice(f.Offset, 2)),
                    PacketFieldType.U32 when f.Offset + 4 <= body.Length => BitConverter.ToUInt32(body.Slice(f.Offset, 4)),
                    PacketFieldType.I32 when f.Offset + 4 <= body.Length => BitConverter.ToInt32(body.Slice(f.Offset, 4)),
                    PacketFieldType.F32 when f.Offset + 4 <= body.Length => BitConverter.ToSingle(body.Slice(f.Offset, 4)),
                    PacketFieldType.U64 when f.Offset + 8 <= body.Length => BitConverter.ToUInt64(body.Slice(f.Offset, 8)),
                    PacketFieldType.Guid when f.Offset + 8 <= body.Length => BitConverter.ToUInt64(body.Slice(f.Offset, 8)).ToString("X16"),
                    _ => null,
                };
            }
            catch { val = null; }
            rows.Add(new
            {
                offset = f.Offset,
                type = f.Type.ToString(),
                name = f.Name,
                value = val,
            });
        }
        return rows;
    }
}
