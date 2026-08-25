using System.Buffers.Binary;
using HeadlessClient.Domain.Protocol;

namespace HeadlessClient.Infrastructure.World;

public static class WorldPacketFramer
{
    public const int MaxServerPacketSize = 2 * 1024 * 1024; // wire cap; Ascension send-side uses 0x186A0

    public static byte[] BuildClientHeader(uint opcode, int payloadLength, bool largeHeader = false)
    {
        var size = checked((ushort)(4 + payloadLength));
        if (largeHeader)
        {
            throw new NotSupportedException("Large client headers are not supported.");
        }

        var header = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, 4), opcode);
        return header;
    }

    public static byte[] BuildClientPacket(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var payload = packet.Payload.ToArray();
        var header = BuildClientHeader(packet.Opcode, payload.Length);
        var frame = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        Buffer.BlockCopy(payload, 0, frame, header.Length, payload.Length);
        return frame;
    }

    /// <summary>
    /// Parse a decrypted Ascension/WotLK server header.
    /// Normal: 4 bytes (BE size u16 + LE opcode u16).
    /// Large (size &gt; 0x7FFF): 5 bytes — 3-byte BE size with high bit on first byte + LE opcode u16.
    /// Verified against Ascension FrameUnencrypted @ RVA 0x675F0.
    /// </summary>
    public static (int Size, ushort Opcode, int HeaderLength) ParseServerHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            throw new ArgumentException("Server header must be at least 4 bytes.", nameof(header));
        }

        if ((header[0] & 0x80) != 0)
        {
            if (header.Length < 5)
            {
                throw new ArgumentException("Large server header must be 5 bytes.", nameof(header));
            }

            var size = ((header[0] & 0x7F) << 16) | (header[1] << 8) | header[2];
            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(3, 2));
            return (size, opcode, 5);
        }

        var smallSize = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(0, 2));
        var smallOpcode = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(2, 2));
        return (smallSize, smallOpcode, 4);
    }

    public static bool IsLargeServerHeaderPrefix(ReadOnlySpan<byte> firstFourBytes)
    {
        if (firstFourBytes.Length < 1)
        {
            return false;
        }

        return (firstFourBytes[0] & 0x80) != 0;
    }

    public static Packet ParseServerPacket(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        var (_, opcode, _) = ParseServerHeader(header);
        return new Packet(opcode, payload.ToArray());
    }

    public static int PayloadLengthFromServerSize(int sizeIncludingOpcode)
    {
        if (sizeIncludingOpcode < 2)
        {
            throw new InvalidDataException($"Invalid server packet size {sizeIncludingOpcode}.");
        }

        return sizeIncludingOpcode - 2;
    }
}
