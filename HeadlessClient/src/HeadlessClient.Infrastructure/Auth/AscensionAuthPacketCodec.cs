using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HeadlessClient.Infrastructure.Auth;

/// <summary>
/// Ascension wraps stock WotLK CMD_AUTH_LOGON_CHALLENGE in a 638-byte attestation packet.
/// Layout verified from ExtProxy AuthWire captures (Extensions.dll .vm_sec produces the seal at runtime).
/// </summary>
public static class AscensionAuthPacketCodec
{
    public const int WrappedPacketLength = 638;
    public const ushort WrappedBodySize = 0x027A;
    public const int NonceLength = 16;
    public const int SealedFieldLength = 64;
    public const int StableTailLength = 546;

    public static readonly byte[] Magic = [0xFC, 0xF4, 0xF4, 0xE6];
    public static readonly byte[] Marker = [0xAD, 0x7D, 0x35, 0x70];

    public const int OffsetMagic = 4;
    public const int OffsetNonce = 8;
    public const int OffsetMarker = 0x18;
    public const int OffsetSealedField = 0x1C;
    public const int OffsetStableTail = 0x5C;

    public static AscensionChallengePacket Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != WrappedPacketLength)
        {
            throw new InvalidDataException($"Ascension challenge must be {WrappedPacketLength} bytes, got {packet.Length}.");
        }

        if (packet[0] != AuthPacketCodec.CmdAuthLogonChallenge || packet[1] != 0x08)
        {
            throw new InvalidDataException("Ascension challenge header is not CMD_AUTH_LOGON_CHALLENGE/0x08.");
        }

        var size = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2, 2));
        if (size != WrappedBodySize)
        {
            throw new InvalidDataException($"Ascension challenge size field 0x{size:X4}, expected 0x{WrappedBodySize:X4}.");
        }

        if (!packet.Slice(OffsetMagic, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Ascension challenge magic mismatch.");
        }

        if (!packet.Slice(OffsetMarker, 4).SequenceEqual(Marker))
        {
            throw new InvalidDataException("Ascension challenge marker mismatch.");
        }

        return new AscensionChallengePacket(
            packet.Slice(OffsetNonce, NonceLength).ToArray(),
            packet.Slice(OffsetSealedField, SealedFieldLength).ToArray(),
            packet.Slice(OffsetStableTail, StableTailLength).ToArray());
    }

    public static byte[] Build(
        ReadOnlySpan<byte> nonce16,
        ReadOnlySpan<byte> sealedField64,
        ReadOnlySpan<byte> stableTail546)
    {
        if (nonce16.Length != NonceLength)
        {
            throw new ArgumentException("Nonce must be 16 bytes.", nameof(nonce16));
        }

        if (sealedField64.Length != SealedFieldLength)
        {
            throw new ArgumentException("Sealed field must be 64 bytes.", nameof(sealedField64));
        }

        if (stableTail546.Length != StableTailLength)
        {
            throw new ArgumentException("Stable tail must be 546 bytes.", nameof(stableTail546));
        }

        var packet = new byte[WrappedPacketLength];
        packet[0] = AuthPacketCodec.CmdAuthLogonChallenge;
        packet[1] = 0x08;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), WrappedBodySize);
        Magic.CopyTo(packet.AsSpan(OffsetMagic));
        nonce16.CopyTo(packet.AsSpan(OffsetNonce));
        Marker.CopyTo(packet.AsSpan(OffsetMarker));
        sealedField64.CopyTo(packet.AsSpan(OffsetSealedField));
        stableTail546.CopyTo(packet.AsSpan(OffsetStableTail));
        return packet;
    }

    public static byte[] NewNonce()
    {
        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    public static byte[] LoadStableTail(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == StableTailLength)
        {
            return bytes;
        }

        if (bytes.Length == WrappedPacketLength)
        {
            return Parse(bytes).StableTail;
        }

        throw new InvalidDataException(
            $"Stable tail file must be {StableTailLength} or {WrappedPacketLength} bytes, got {bytes.Length}.");
    }

    public sealed record AscensionChallengePacket(byte[] Nonce, byte[] SealedField, byte[] StableTail);
}
