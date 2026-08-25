using System.Buffers.Binary;
using System.Text;
using HeadlessClient.Domain.Protocol;

namespace HeadlessClient.Infrastructure.Protocol;

/// <summary>WotLK 3.3.5a guild CMSG builders (create / invite / ranks / MOTD).</summary>
public static class GuildPacketCodec
{
    public const uint CmsgGuildCreate = 0x0081;
    public const uint CmsgGuildInvite = 0x0082;
    public const uint CmsgGuildAccept = 0x0084;
    public const uint CmsgGuildRoster = 0x0089;
    public const uint CmsgGuildMotd = 0x0091;
    public const uint CmsgGuildRank = 0x0231;
    public const uint CmsgGuildAddRank = 0x0232;
    public const uint CmsgGuildInfoText = 0x02FC;

    public const uint SmsgGuildInvite = 0x0083;
    public const uint SmsgGuildEvent = 0x0092;
    public const uint SmsgGuildCommandResult = 0x0093;
    public const uint SmsgGuildRoster = 0x008A;

    public const int GuildBankTabs = 6;

    public static Packet BuildCreate(string guildName) => BuildCStringPacket(CmsgGuildCreate, guildName);

    public static Packet BuildInvite(string characterName) => BuildCStringPacket(CmsgGuildInvite, characterName);

    public static Packet BuildAccept() => new(CmsgGuildAccept, Array.Empty<byte>());

    public static Packet BuildRosterRequest() => new(CmsgGuildRoster, Array.Empty<byte>());

    public static Packet BuildMotd(string motd) => BuildCStringPacket(CmsgGuildMotd, motd ?? "", allowEmpty: true);

    public static Packet BuildInfoText(string text) => BuildCStringPacket(CmsgGuildInfoText, text ?? "", allowEmpty: true);

    public static Packet BuildAddRank(string rankName) => BuildCStringPacket(CmsgGuildAddRank, rankName);

    /// <summary>
    /// Rename + rights for an existing rank slot.
    /// Layout: rankId, rights, moneyPerDay, 6×(bankRights, bankSlots), cstring name.
    /// </summary>
    public static Packet BuildSetRank(
        uint rankId,
        string rankName,
        uint rights = 0x00000061,
        uint moneyPerDay = 0)
    {
        rankName = (rankName ?? "").Trim();
        if (rankName.Length == 0)
        {
            throw new ArgumentException("Rank name required.");
        }

        if (rankId == 0)
        {
            rights = 0xFFFFFFFFu;
            moneyPerDay = 0xFFFFFFFFu;
        }

        var nameBytes = Encoding.UTF8.GetBytes(rankName);
        var payload = new byte[4 + 4 + 4 + (GuildBankTabs * 8) + nameBytes.Length + 1];
        var o = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), rankId);
        o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), rights);
        o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), moneyPerDay);
        o += 4;
        for (var i = 0; i < GuildBankTabs; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), 0);
            o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), 0);
            o += 4;
        }

        Buffer.BlockCopy(nameBytes, 0, payload, o, nameBytes.Length);
        payload[^1] = 0;
        return new Packet(CmsgGuildRank, payload);
    }

    public static bool TryParseCommandResult(ReadOnlySpan<byte> data, out uint command, out uint result, out string name)
    {
        command = 0;
        result = 0;
        name = "";
        if (data.Length < 8)
        {
            return false;
        }

        command = BinaryPrimitives.ReadUInt32LittleEndian(data);
        result = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (data.Length > 8)
        {
            var end = data[8..].IndexOf((byte)0);
            var slice = end >= 0 ? data.Slice(8, end) : data[8..];
            name = Encoding.UTF8.GetString(slice);
        }

        return true;
    }

    private static Packet BuildCStringPacket(uint opcode, string value, bool allowEmpty = false)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0 && !allowEmpty)
        {
            throw new ArgumentException("Value required.");
        }

        var nameBytes = Encoding.UTF8.GetBytes(value);
        var payload = new byte[nameBytes.Length + 1];
        Buffer.BlockCopy(nameBytes, 0, payload, 0, nameBytes.Length);
        payload[^1] = 0;
        return new Packet(opcode, payload);
    }
}
