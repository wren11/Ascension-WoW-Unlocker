using System.Buffers.Binary;
using System.Text;
using HeadlessClient.Domain.Auth;

namespace HeadlessClient.Infrastructure.Auth;

public static class AuthPacketCodec
{
    public const byte CmdAuthLogonChallenge = 0x00;
    public const byte CmdAuthLogonProof = 0x01;
    public const byte CmdRealmList = 0x10;

    public static byte[] EncodeLogonChallenge(string account, int build, uint ipAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var accountBytes = Encoding.ASCII.GetBytes(account.ToUpperInvariant());
        if (accountBytes.Length > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(account), "Account name exceeds 255 bytes.");
        }

        var size = (ushort)(30 + accountBytes.Length);
        using var ms = new MemoryStream(4 + size);
        using var bw = new BinaryWriter(ms);
        bw.Write(CmdAuthLogonChallenge);
        bw.Write((byte)0x08);
        bw.Write(size);
        bw.Write(Encoding.ASCII.GetBytes("WoW\0"));
        bw.Write((byte)3);
        bw.Write((byte)3);
        bw.Write((byte)5);
        bw.Write((ushort)build);
        bw.Write(Encoding.ASCII.GetBytes("68x\0"));
        bw.Write(Encoding.ASCII.GetBytes("niW\0"));
        bw.Write(Encoding.ASCII.GetBytes("SUne"));
        bw.Write(0);
        bw.Write(ipAddress);
        bw.Write((byte)accountBytes.Length);
        bw.Write(accountBytes);
        return ms.ToArray();
    }

    public static LogonChallengeResponse DecodeLogonChallenge(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 3)
        {
            throw new InvalidDataException("CMD_AUTH_LOGON_CHALLENGE response too short.");
        }

        var cmd = packet[0];
        if (cmd != CmdAuthLogonChallenge)
        {
            throw new InvalidDataException($"Expected CMD_AUTH_LOGON_CHALLENGE (0x00), got 0x{cmd:X2}.");
        }

        var error = packet[2];
        if (error != 0)
        {
            return new LogonChallengeResponse(error, Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>());
        }

        if (packet.Length < 3 + 32 + 1)
        {
            throw new InvalidDataException("CMD_AUTH_LOGON_CHALLENGE success payload incomplete.");
        }

        var offset = 3;
        var B = packet.Slice(offset, 32).ToArray();
        offset += 32;
        var gLen = packet[offset++];
        if (packet.Length < offset + gLen + 1)
        {
            throw new InvalidDataException("CMD_AUTH_LOGON_CHALLENGE missing generator.");
        }

        var g = packet.Slice(offset, gLen).ToArray();
        offset += gLen;
        var nLen = packet[offset++];
        if (packet.Length < offset + nLen + 32 + 16)
        {
            throw new InvalidDataException("CMD_AUTH_LOGON_CHALLENGE missing N or salt.");
        }

        var N = packet.Slice(offset, nLen).ToArray();
        offset += nLen;
        var salt = packet.Slice(offset, 32).ToArray();
        offset += 32;
        var crcSalt = packet.Slice(offset, 16).ToArray();
        return new LogonChallengeResponse(error, B, g, N, salt, crcSalt);
    }

    public static byte[] EncodeLogonProof(byte[] A, byte[] M1, byte[] crcHash)
    {
        ArgumentNullException.ThrowIfNull(A);
        ArgumentNullException.ThrowIfNull(M1);
        ArgumentNullException.ThrowIfNull(crcHash);
        if (A.Length != 32 || M1.Length != 20 || crcHash.Length != 20)
        {
            throw new ArgumentException("Logon proof fields must be A=32, M1=20, crc=20 bytes.");
        }

        using var ms = new MemoryStream(1 + 32 + 20 + 20 + 2);
        using var bw = new BinaryWriter(ms);
        bw.Write(CmdAuthLogonProof);
        bw.Write(A);
        bw.Write(M1);
        bw.Write(crcHash);
        bw.Write((byte)0);
        bw.Write((byte)0);
        return ms.ToArray();
    }

    public static LogonProofResponse DecodeLogonProof(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2)
        {
            throw new InvalidDataException("CMD_AUTH_LOGON_PROOF response too short.");
        }

        var cmd = packet[0];
        if (cmd != CmdAuthLogonProof)
        {
            throw new InvalidDataException($"Expected CMD_AUTH_LOGON_PROOF (0x01), got 0x{cmd:X2}.");
        }

        var error = packet[1];
        if (error != 0)
        {
            return new LogonProofResponse(error, Array.Empty<byte>(), Array.Empty<byte>());
        }

        if (packet.Length < 2 + 20)
        {
            throw new InvalidDataException("CMD_AUTH_LOGON_PROOF missing M2.");
        }

        var M2 = packet.Slice(2, 20).ToArray();
        var tail = packet.Length > 22 ? packet.Slice(22).ToArray() : Array.Empty<byte>();
        return new LogonProofResponse(error, M2, tail);
    }

    public static byte[] EncodeRealmListRequest()
    {
        return [CmdRealmList, 0, 0, 0, 0];
    }

    public static IReadOnlyList<RealmInfo> DecodeRealmList(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 1 + 2 + 4 + 2)
        {
            throw new InvalidDataException("CMD_REALM_LIST response too short.");
        }

        if (packet[0] != CmdRealmList)
        {
            throw new InvalidDataException($"Expected CMD_REALM_LIST (0x10), got 0x{packet[0]:X2}.");
        }

        var size = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(1, 2));
        if (packet.Length < 3 + size)
        {
            throw new InvalidDataException("CMD_REALM_LIST size exceeds buffer.");
        }

        var body = packet.Slice(3, size);
        var offset = 4;
        if (body.Length < offset + 2)
        {
            throw new InvalidDataException("CMD_REALM_LIST missing realm count.");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        var realms = new List<RealmInfo>(count);
        for (var i = 0; i < count; i++)
        {
            if (body.Length < offset + 3)
            {
                throw new InvalidDataException("CMD_REALM_LIST truncated realm header.");
            }

            var type = body[offset++];
            var locked = body[offset++];
            var flags = body[offset++];
            var name = ReadCString(body, ref offset);
            var address = ReadCString(body, ref offset);
            if (body.Length < offset + 4 + 3)
            {
                throw new InvalidDataException("CMD_REALM_LIST truncated realm trailer.");
            }

            var population = BitConverter.ToSingle(body.Slice(offset, 4));
            offset += 4;
            var characterCount = body[offset++];
            var timezone = body[offset++];
            var id = body[offset++];
            _ = locked;
            realms.Add(new RealmInfo(type, flags, name, address, population, characterCount, timezone, id));
        }

        return realms;
    }

    private static string ReadCString(ReadOnlySpan<byte> data, ref int offset)
    {
        var start = offset;
        while (offset < data.Length && data[offset] != 0)
        {
            offset++;
        }

        if (offset >= data.Length)
        {
            throw new InvalidDataException("Unterminated CString in auth packet.");
        }

        var value = Encoding.UTF8.GetString(data.Slice(start, offset - start));
        offset++;
        return value;
    }

    public sealed record LogonChallengeResponse(
        byte Error,
        byte[] B,
        byte[] Generator,
        byte[] LargeSafePrime,
        byte[] Salt,
        byte[] CrcSalt);

    public sealed record LogonProofResponse(byte Error, byte[] M2, byte[] Tail);
}
