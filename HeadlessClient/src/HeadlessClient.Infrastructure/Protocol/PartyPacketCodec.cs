using System.Text;
using HeadlessClient.Domain.Protocol;

namespace HeadlessClient.Infrastructure.Protocol;

/// <summary>WotLK party invite / accept helpers (CMSG_GROUP_*).</summary>
public static class PartyPacketCodec
{
    public const uint CmsgGroupInvite = 0x006E;
    public const uint CmsgGroupAccept = 0x0072;
    public const uint CmsgGroupDecline = 0x0073;
    public const uint CmsgGroupDisband = 0x007B;

    public static Packet BuildInvite(string characterName)
    {
        characterName = (characterName ?? "").Trim();
        if (characterName.Length == 0)
        {
            throw new ArgumentException("Character name required.");
        }

        var nameBytes = Encoding.UTF8.GetBytes(characterName);
        var payload = new byte[nameBytes.Length + 1];
        Buffer.BlockCopy(nameBytes, 0, payload, 0, nameBytes.Length);
        payload[^1] = 0;
        return new Packet(CmsgGroupInvite, payload);
    }

    public static Packet BuildAccept() => new(CmsgGroupAccept, Array.Empty<byte>());

    public static Packet BuildDecline() => new(CmsgGroupDecline, Array.Empty<byte>());

    public static Packet BuildDisband() => new(CmsgGroupDisband, Array.Empty<byte>());
}
