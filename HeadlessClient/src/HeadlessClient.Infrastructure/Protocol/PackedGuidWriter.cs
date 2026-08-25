using System.Buffers.Binary;

namespace HeadlessClient.Infrastructure.Protocol;

public static class PackedGuidWriter
{
    public static byte[] Write(ulong guid)
    {
        var mask = (byte)0;
        Span<byte> parts = stackalloc byte[8];
        var n = 0;
        for (var i = 0; i < 8; i++)
        {
            var b = (byte)((guid >> (8 * i)) & 0xFF);
            if (b == 0) continue;
            mask |= (byte)(1 << i);
            parts[n++] = b;
        }

        var result = new byte[1 + n];
        result[0] = mask;
        if (n > 0)
            parts[..n].CopyTo(result.AsSpan(1));
        return result;
    }

    public static byte[] WriteUnpacked(ulong guid)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, guid);
        return b;
    }
}
