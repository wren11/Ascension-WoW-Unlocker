using System.Buffers.Binary;
using System.Text;

namespace AscensionNetTool;

/// <summary>Parse SMSG_CHAR_ENUM (stock WotLK + best-effort Ascension extras) for guid→lowercase name.</summary>
public static class CharEnumParser
{
    public readonly record struct Entry(ulong Guid, string Name);

    public static IReadOnlyList<Entry> Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 11) return Array.Empty<Entry>();
        int body = OpcodeWidth(packet);
        if (body >= packet.Length) return Array.Empty<Entry>();
        var payload = packet[body..];
        var list = new List<Entry>();
        if (payload.Length < 1) return list;
        int count = payload[0];
        int off = 1;
        if (count is >= 1 and <= 16)
        {
            for (int i = 0; i < count && off + 9 <= payload.Length; i++)
            {
                ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(off, 8));
                off += 8;
                if (!TryReadCString(payload, ref off, out var name)) break;
                name = Norm(name);
                if (guid != 0 && IsPlayable(name))
                    list.Add(new Entry(guid, name));
                int skip = 1 + 1 + 1 + 5 + 1 + 4 + 4 + 12 + 4 + 4 + 4 + 1 + 12 + (23 * 9);
                if (off + skip <= payload.Length) off += skip;
                else break;
            }
        }
        if (list.Count == 0)
            Scan(payload, list);
        return list;
    }

    static int OpcodeWidth(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) return 0;
        if (p.Length >= 4)
        {
            uint op32 = BinaryPrimitives.ReadUInt32LittleEndian(p);
            if (op32 <= 0x9D4) return 4;
        }
        return 2;
    }

    static void Scan(ReadOnlySpan<byte> payload, List<Entry> list)
    {
        int off = 0;
        while (off + 10 <= payload.Length && list.Count < 16)
        {
            ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(off, 8));
            int n = off + 8;
            if (!TryReadCString(payload, ref n, out var name))
            {
                off++;
                continue;
            }
            name = Norm(name);
            if (guid != 0 && IsPlayable(name))
            {
                list.Add(new Entry(guid, name));
                off = n;
            }
            else off++;
        }
    }

    static bool TryReadCString(ReadOnlySpan<byte> p, ref int off, out string name)
    {
        name = "";
        int start = off;
        while (off < p.Length && p[off] != 0 && off - start < 24) off++;
        if (off >= p.Length) return false;
        name = Encoding.UTF8.GetString(p.Slice(start, off - start));
        if (off < p.Length && p[off] == 0) off++;
        return true;
    }

    public static string Norm(string name) => (name ?? "").Trim().ToLowerInvariant();

    public static bool IsPlayable(string? name)
    {
        var n = Norm(name ?? "");
        if (n.Length is < 2 or > 24) return false;
        foreach (var ch in n)
        {
            if (!char.IsLetter(ch)) return false;
        }
        return true;
    }
}
