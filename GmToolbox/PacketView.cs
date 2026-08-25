using System.Text;

namespace AscensionNetTool;

static class PacketView
{
    const int MoveSize = 38;
    const uint OpNewWorld = 0x3E;
    const uint OpLoginVerifyWorld = 0x236;

    public static string Describe(CapturedPacket p, string opName)
    {
        var sb = new StringBuilder(512);
        string name = string.IsNullOrEmpty(opName) ? "?" : opName;
        sb.AppendLine($"seq {p.Seq}   tick {p.Tick}   {DirName(p.Dir)}   {p.Data.Length} bytes");
        sb.AppendLine($"opcode 0x{p.Opcode:X4}  {name}");
        sb.AppendLine($"framing {Framing(p)}");
        sb.AppendLine();

        string? fields = Decode(p);
        if (fields is not null)
        {
            sb.AppendLine(fields);
            sb.AppendLine();
        }

        sb.AppendLine("fields:");
        sb.Append(Fields(p));
        sb.AppendLine();

        sb.Append(HexDump(p.Data));
        return sb.ToString();
    }

    public static string DirName(PktDir d) => d switch
    {
        PktDir.In => "recv",
        PktDir.Out => "send",
        _ => "replay",
    };

    static string Framing(CapturedPacket p)
    {
        var d = p.Data;
        if (d.Length >= 4 && BitConverter.ToUInt32(d, 0) == p.Opcode)
            return "u32 opcode at +0 (body from +4)";
        if (d.Length >= 2 && BitConverter.ToUInt16(d, 0) == (ushort)p.Opcode)
            return "u16 opcode at +0 (body from +2)";
        return "opcode not in payload (body from +0)";
    }

    static bool U32Framed(CapturedPacket p) =>
        p.Data.Length >= 4 && BitConverter.ToUInt32(p.Data, 0) == p.Opcode;

    static string? Decode(CapturedPacket p)
    {
        if (!U32Framed(p))
            return null;
        if (p.Opcode is OpNewWorld or OpLoginVerifyWorld)
            return DecodeWorld(p.Data);
        if (p.Opcode >= 0xB5 && p.Opcode <= 0xFF)
            return DecodeMove(p.Data);
        if (ChatDecoder.TryDecode(p, out var chat) && chat is not null)
        {
            return $"chat/{chat.Kind} type={chat.ChatTypeName} ({chat.ChatType})\n"
                + $"channel={chat.Channel}\n"
                + $"sender={chat.SenderName} guid={chat.SenderGuid}\n"
                + $"target={chat.TargetGuid}\n"
                + $"lang={chat.Language} tag={chat.ChatTag}\n"
                + $"message={chat.Message}";
        }
        return null;
    }

    static string? DecodeMove(byte[] d)
    {
        if (d.Length < MoveSize)
            return null;

        uint guid = BitConverter.ToUInt32(d, 4);
        uint flags = BitConverter.ToUInt32(d, 8);
        ushort flags2 = BitConverter.ToUInt16(d, 12);
        uint time = BitConverter.ToUInt32(d, 14);
        float x = BitConverter.ToSingle(d, 18);
        float y = BitConverter.ToSingle(d, 22);
        float z = BitConverter.ToSingle(d, 26);
        float o = BitConverter.ToSingle(d, 30);
        uint counter = BitConverter.ToUInt32(d, 34);

        var sb = new StringBuilder(256);
        sb.AppendLine("movement:");
        sb.AppendLine($"  mover guid low : {guid} (0x{guid:X8})");
        sb.AppendLine($"  flags          : 0x{flags:X8}{FlagNames(flags)}");
        sb.AppendLine($"  flags2         : 0x{flags2:X4}");
        sb.AppendLine($"  time           : {time}");
        sb.AppendLine($"  position       : {x:F2}, {y:F2}, {z:F2}");
        sb.AppendLine($"  facing         : {o:F3} rad");
        sb.Append($"  move counter   : {counter}");
        if (d.Length > MoveSize)
            sb.Append($"{Environment.NewLine}  trailing       : {d.Length - MoveSize} bytes");
        return sb.ToString();
    }

    static string? DecodeWorld(byte[] d)
    {
        if (d.Length < 20)
            return null;
        uint map = BitConverter.ToUInt32(d, 4);
        float x = BitConverter.ToSingle(d, 8);
        float y = BitConverter.ToSingle(d, 12);
        float z = BitConverter.ToSingle(d, 16);

        var sb = new StringBuilder(160);
        sb.AppendLine("world transfer:");
        sb.AppendLine($"  map            : {map}");
        sb.Append($"  position       : {x:F2}, {y:F2}, {z:F2}");
        if (d.Length >= 24)
            sb.Append($"{Environment.NewLine}  facing         : {BitConverter.ToSingle(d, 20):F3} rad");
        return sb.ToString();
    }

    static readonly (uint Bit, string Name)[] MoveFlagBits =
    {
        (0x00000001, "FORWARD"),
        (0x00000002, "BACKWARD"),
        (0x00000004, "STRAFE_LEFT"),
        (0x00000008, "STRAFE_RIGHT"),
        (0x00000400, "DISABLE_GRAVITY"),
        (0x00000800, "ROOT"),
        (0x00001000, "FALLING"),
        (0x00002000, "FALLING_FAR"),
        (0x00200000, "ASCENDING"),
        (0x00400000, "DESCENDING"),
        (0x01000000, "CAN_FLY"),
        (0x02000000, "FLYING"),
    };

    static string FlagNames(uint flags)
    {
        var hits = MoveFlagBits.Where(f => (flags & f.Bit) != 0).Select(f => f.Name);
        string joined = string.Join(" ", hits);
        return joined.Length == 0 ? "" : "  [" + joined + "]";
    }

    public static string FormatHexDump(byte[] data) => HexDump(data);

    static string HexDump(byte[] data)
    {
        if (data.Length == 0)
            return "(no payload)";
        var sb = new StringBuilder(data.Length * 4);
        for (int off = 0; off < data.Length; off += 16)
        {
            int n = Math.Min(16, data.Length - off);
            sb.Append($"{off:X4}  ");
            for (int i = 0; i < 16; i++)
                sb.Append(i < n ? data[off + i].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (int i = 0; i < n; i++)
            {
                byte b = data[off + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.Append(Environment.NewLine);
        }
        return sb.ToString();
    }

    public static byte[] ParseHex(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();

        // Hex-dump paste (offset + hex columns + ASCII) — never feed ASCII into the parser.
        byte[]? dump = TryParseHexDump(text);
        if (dump is { Length: > 0 })
            return dump;

        // Pure spaced / continuous hex (editor FormatHex output, typed bytes).
        var tokens = new List<string>();
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#') || t.StartsWith("//"))
                continue;
            // Drop trailing ASCII-looking runs after two+ spaces if present
            int cut = t.IndexOf("  ", StringComparison.Ordinal);
            if (cut > 8)
                t = t[..cut];
            t = t.Replace("0x", " ", StringComparison.OrdinalIgnoreCase).Replace(",", " ");
            foreach (string part in t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                // Skip dump offsets like 0000/0010 when alone
                if (p.Length >= 4 && p.Length <= 8 && IsAllHex(p) && tokens.Count == 0
                    && p.Length != 2 && LooksLikeOffsetToken(p))
                    continue;
                if (p.Length == 1 && IsAllHex(p))
                    tokens.Add("0" + p);
                else if (p.Length == 2 && IsAllHex(p))
                    tokens.Add(p);
                else if (p.Length > 2 && (p.Length % 2) == 0 && IsAllHex(p))
                {
                    for (int i = 0; i < p.Length; i += 2)
                        tokens.Add(p.Substring(i, 2));
                }
            }
        }

        if (tokens.Count == 0)
            throw new InvalidOperationException(
                "Could not find any recognizable hex digits. Paste spaced bytes or a hex dump (offset columns OK).");

        var bytes = new byte[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
            bytes[i] = Convert.ToByte(tokens[i], 16);
        return bytes;
    }

    static bool IsAllHex(string s)
    {
        foreach (char c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return s.Length > 0;
    }

    static bool LooksLikeOffsetToken(string p)
    {
        // 4–8 hex digits that look like a dump line offset (0000, 0010, …)
        if (p.Length is < 4 or > 8) return false;
        return p.EndsWith('0') || p.All(c => c == '0' || (c >= '0' && c <= '9'));
    }

    /// <summary>Parse classic hex-dump lines produced by <see cref="Describe"/> / HexDump.</summary>
    static byte[]? TryParseHexDump(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var bytes = new List<byte>(512);
        int matchedLines = 0;
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length < 8) continue;
            int i = 0;
            while (i < line.Length && Uri.IsHexDigit(line[i])) i++;
            if (i is < 4 or > 8) continue;
            if (i >= line.Length || line[i] != ' ') continue;
            // Require at least two spaces (or space+hex) after offset — dump style
            int sp = 0;
            while (i < line.Length && line[i] == ' ') { i++; sp++; }
            if (sp < 1) continue;
            if (i + 1 >= line.Length || !Uri.IsHexDigit(line[i]) || !Uri.IsHexDigit(line[i + 1]))
                continue;

            int taken = 0;
            int before = bytes.Count;
            while (i + 1 < line.Length && taken < 16)
            {
                if (line[i] == ' ' && i + 1 < line.Length && line[i + 1] == ' ')
                    break; // ASCII column
                if (line[i] == ' ') { i++; continue; }
                if (!Uri.IsHexDigit(line[i]) || i + 1 >= line.Length || !Uri.IsHexDigit(line[i + 1]))
                    break;
                bytes.Add(Convert.ToByte(string.Concat(line[i], line[i + 1]), 16));
                i += 2;
                taken++;
            }
            if (taken > 0)
                matchedLines++;
            else if (bytes.Count > before)
                bytes.RemoveRange(before, bytes.Count - before);
        }
        if (matchedLines == 0 || bytes.Count == 0)
            return null;
        return bytes.ToArray();
    }

    static int BodyStart(CapturedPacket p)
    {
        var d = p.Data;
        if (d.Length >= 4 && BitConverter.ToUInt32(d, 0) == p.Opcode)
            return 4;
        if (d.Length >= 2 && BitConverter.ToUInt16(d, 0) == (ushort)p.Opcode)
            return 2;
        return 0;
    }

    static bool LooksLikeGuid(byte[] d, int o)
    {
        if (o + 8 > d.Length)
            return false;
        uint hi = BitConverter.ToUInt32(d, o + 4);
        uint lo = BitConverter.ToUInt32(d, o);
        if (lo == 0 && hi == 0)
            return false;
        ushort tag = (ushort)(hi >> 16);
        return tag == 0xF130 || tag == 0xF131 || tag == 0xF150 || tag == 0xF151
            || tag == 0xF140 || tag == 0xF110 || tag == 0xF120
            || (hi == 0 && lo > 0 && lo < 0x0FFFFFFF);
    }

    static bool LooksLikeFloat(byte[] d, int o)
    {
        if (o + 4 > d.Length)
            return false;
        float f = BitConverter.ToSingle(d, o);
        if (float.IsNaN(f) || float.IsInfinity(f))
            return false;
        if (f == 0f)
            return false;
        float a = Math.Abs(f);
        return a > 0.001f && a < 1e6f;
    }

    static int StringLen(byte[] d, int o)
    {
        int n = 0;
        while (o + n < d.Length && d[o + n] >= 0x20 && d[o + n] < 0x7F)
            n++;
        if (n < 3)
            return 0;
        return (o + n < d.Length && d[o + n] == 0) ? n : 0;
    }

    public static string Fields(CapturedPacket p)
    {
        var d = p.Data;
        int o = BodyStart(p);
        if (o == 0 && d.Length == 0)
            return "  (empty)" + Environment.NewLine;

        var sb = new StringBuilder(256);
        if (o > 0)
            sb.AppendLine($"  +{0:X4}  opcode  0x{p.Opcode:X4}  {Opcodes.Name(p.Opcode)}");

        int guard = 0;
        while (o < d.Length && guard++ < 512)
        {
            int slen = StringLen(d, o);
            if (slen > 0)
            {
                string str = Encoding.ASCII.GetString(d, o, slen);
                if (str.Length > 60)
                    str = str[..60] + "...";
                sb.AppendLine($"  +{o:X4}  string  \"{str}\" ({slen} bytes)");
                o += slen + 1;
                continue;
            }
            if (LooksLikeGuid(d, o))
            {
                sb.AppendLine($"  +{o:X4}  guid    0x{BitConverter.ToUInt64(d, o):X16}");
                o += 8;
                continue;
            }
            if (LooksLikeFloat(d, o))
            {
                sb.AppendLine($"  +{o:X4}  float   {BitConverter.ToSingle(d, o):G7}");
                o += 4;
                continue;
            }
            int align = 0;
            for (int k = 1; k <= 3 && o + k < d.Length; k++)
            {
                if (StringLen(d, o + k) > 0) { align = k; break; }
            }
            if (align > 0)
            {
                for (int k = 0; k < align; k++)
                    sb.AppendLine($"  +{o + k:X4}  u8      {d[o + k]}  (0x{d[o + k]:X2})");
                o += align;
                continue;
            }
            if (o + 4 <= d.Length)
            {
                uint v = BitConverter.ToUInt32(d, o);
                sb.AppendLine($"  +{o:X4}  u32     {v}  (0x{v:X8})");
                o += 4;
                continue;
            }
            if (o + 2 <= d.Length)
            {
                ushort v = BitConverter.ToUInt16(d, o);
                sb.AppendLine($"  +{o:X4}  u16     {v}  (0x{v:X4})");
                o += 2;
                continue;
            }
            sb.AppendLine($"  +{o:X4}  u8      {d[o]}  (0x{d[o]:X2})");
            o += 1;
        }
        return sb.ToString();
    }
}
