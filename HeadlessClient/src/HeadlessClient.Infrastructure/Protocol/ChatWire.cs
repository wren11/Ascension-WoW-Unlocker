using System.Buffers.Binary;
using System.Text;
using HeadlessClient.Domain.Protocol;

namespace HeadlessClient.Infrastructure.Protocol;

/// <summary>WotLK / Ascension chat type ids (CHAT_MSG_*) — matches GmToolbox ChatDecoder.</summary>
public static class ChatTypes
{
    public const byte System = 0x00;
    public const byte Say = 0x01;
    public const byte Party = 0x02;
    public const byte Raid = 0x03;
    public const byte Guild = 0x04;
    public const byte Officer = 0x05;
    public const byte Yell = 0x06;
    public const byte Whisper = 0x07;
    public const byte WhisperForeign = 0x08;
    public const byte WhisperInform = 0x09;
    public const byte Emote = 0x0A;
    public const byte TextEmote = 0x0B;
    public const byte MonsterSay = 0x0C;
    public const byte MonsterParty = 0x0D;
    public const byte MonsterYell = 0x0E;
    public const byte MonsterWhisper = 0x0F;
    public const byte MonsterEmote = 0x10;
    public const byte Channel = 0x11;
    public const byte ChannelNotice = 0x15;
    public const byte ChannelNoticeUser = 0x16;
    public const byte Afk = 0x17;
    public const byte Dnd = 0x18;
    public const byte RaidLeader = 0x27;
    public const byte RaidWarning = 0x28;
    public const byte RaidBossEmote = 0x29;
    public const byte RaidBossWhisper = 0x2A;
    public const byte Battleground = 0x2C;
    public const byte BattlegroundLeader = 0x2D;
    public const byte Achievement = 0x30;
    public const byte GuildAchievement = 0x31;
    public const byte PartyLeader = 0x33;

    public static string Name(byte type) => type switch
    {
        System => "SYSTEM",
        Say => "SAY",
        Party => "PARTY",
        Raid => "RAID",
        Guild => "GUILD",
        Officer => "OFFICER",
        Yell => "YELL",
        Whisper => "WHISPER",
        WhisperForeign => "WHISPER",
        WhisperInform => "WHISPER_INFORM",
        Emote => "EMOTE",
        TextEmote => "TEXT_EMOTE",
        MonsterSay => "MONSTER_SAY",
        MonsterParty => "MONSTER_PARTY",
        MonsterYell => "MONSTER_YELL",
        MonsterWhisper => "MONSTER_WHISPER",
        MonsterEmote => "MONSTER_EMOTE",
        Channel => "CHANNEL",
        RaidLeader => "RAID_LEADER",
        RaidWarning => "RAID_WARNING",
        RaidBossEmote => "RAID_BOSS_EMOTE",
        RaidBossWhisper => "RAID_BOSS_WHISPER",
        Battleground => "BATTLEGROUND",
        BattlegroundLeader => "BATTLEGROUND_LEADER",
        Achievement => "ACHIEVEMENT",
        GuildAchievement => "GUILD_ACHIEVEMENT",
        PartyLeader => "PARTY_LEADER",
        Afk => "AFK",
        Dnd => "DND",
        _ => $"TYPE_{type:X2}"
    };

    public static byte Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Say;
        }

        return name.Trim().ToUpperInvariant() switch
        {
            "SAY" => Say,
            "PARTY" => Party,
            "RAID" => Raid,
            "GUILD" => Guild,
            "OFFICER" => Officer,
            "YELL" => Yell,
            "WHISPER" or "W" => Whisper,
            "EMOTE" => Emote,
            "CHANNEL" or "CHAN" => Channel,
            "SYSTEM" => System,
            _ => Say
        };
    }
}

/// <summary>Builds CMSG_MESSAGECHAT (0x0095) payloads — speak as the headless character.</summary>
public static class ChatMessageBuilder
{
    public const uint Opcode = 0x0095;
    public const uint JoinChannelOpcode = 0x0097;
    public const uint LeaveChannelOpcode = 0x0098;

    public static Packet Build(
        byte type,
        string message,
        string? channelOrTarget = null,
        uint language = 0)
    {
        message ??= string.Empty;
        if (message.Length > 255)
        {
            message = message[..255];
        }

        using var ms = new MemoryStream(64 + message.Length);
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write((uint)type);
            bw.Write(language);

            if (type is ChatTypes.Whisper or ChatTypes.Channel)
            {
                WriteCString(bw, channelOrTarget ?? string.Empty);
            }

            WriteCString(bw, message);
        }

        return new Packet(Opcode, ms.ToArray());
    }

    public static Packet BuildJoinChannel(string channel, string password = "")
        => BuildJoinChannel(0, channel, password);

    /// <summary>
    /// WotLK CMSG_JOIN_CHANNEL: channelId u32 | unk u8 | unk u8 | name\0 | password\0.
    /// Zone channels (Trade/LFG/…) often need a non-zero <paramref name="channelId"/> from ChatChannels.dbc.
    /// </summary>
    public static Packet BuildJoinChannel(uint channelId, string channel, string password = "")
    {
        using var ms = new MemoryStream(64);
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(channelId);
            bw.Write((byte)0);
            bw.Write((byte)0);
            WriteCString(bw, channel ?? string.Empty);
            WriteCString(bw, password ?? string.Empty);
        }

        return new Packet(JoinChannelOpcode, ms.ToArray());
    }

    /// <summary>TBC-style join (name + password only) — Ascension fallback when WotLK layout gets INVALID_NAME.</summary>
    public static Packet BuildJoinChannelTbc(string channel, string password = "")
    {
        using var ms = new MemoryStream(64);
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            WriteCString(bw, channel ?? string.Empty);
            WriteCString(bw, password ?? string.Empty);
        }

        return new Packet(JoinChannelOpcode, ms.ToArray());
    }

    private static void WriteCString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        bw.Write(bytes);
        bw.Write((byte)0);
    }
}

/// <summary>SMSG_CHANNEL_NOTIFY (0x0099).</summary>
public static class ChannelNotifyCodec
{
    public const uint Opcode = 0x0099;

    // Trinity ChatNotify (3.3.5)
    public const byte YouJoined = 0x02;
    public const byte YouLeft = 0x03;
    public const byte WrongPassword = 0x04;
    public const byte NotMember = 0x05;
    public const byte AlreadyMember = 0x17;
    public const byte InvalidName = 0x1B;
    public const byte NotInArea = 0x20;
    public const byte NotInLfg = 0x21;

    public static bool TryParse(ReadOnlySpan<byte> payload, out byte type, out string channel)
    {
        type = 0;
        channel = string.Empty;
        if (payload.Length < 2)
        {
            return false;
        }

        type = payload[0];
        var pos = 1;
        var start = pos;
        while (pos < payload.Length && payload[pos] != 0)
        {
            pos++;
        }

        if (pos > start)
        {
            channel = Encoding.UTF8.GetString(payload.Slice(start, pos - start));
        }

        return true;
    }

    public static string Describe(byte type) => type switch
    {
        YouJoined => "joined",
        YouLeft => "left",
        WrongPassword => "wrong_password",
        NotMember => "not_member",
        AlreadyMember => "already_member",
        InvalidName => "invalid_name",
        NotInArea => "not_in_area",
        NotInLfg => "not_in_lfg",
        _ => $"notify_0x{type:X2}"
    };

    public static bool IsJoined(byte type) => type is YouJoined or AlreadyMember;
}

/// <summary>Built-in + Ascension channels to join after enter-world.</summary>
public static class DefaultChatChannels
{
    public const string Misc = "Misc";

    /// <summary>From live client chat-cache + WotLK ChatChannels.dbc + Ascension customs.</summary>
    public static IReadOnlyList<(uint ChannelId, string Name)> All { get; } =
    [
        // Zone / constant channels — id=0 and DBC ids
        (0, "General"),
        (1, "General"),
        (0, "Trade"),
        (2, "Trade"),
        (0, "LocalDefense"),
        (22, "LocalDefense"),
        (0, "WorldDefense"),
        (23, "WorldDefense"),
        (0, "GuildRecruitment"),
        (24, "GuildRecruitment"),
        (25, "GuildRecruitment"),
        (0, "LookingForGroup"),
        (26, "LookingForGroup"),
        // Ascension / private-realm globals (chat-cache + common customs)
        (0, "Ascension"),
        (0, "Newcomers"),
        (0, "World"),
        (0, "Global"),
        (0, "Hardcore"),
        (0, "LFG"),
        (0, "Recruitment"),
        (0, "Services"),
        (0, "General Discussion"),
        (0, "LookingForGuild"),
        (0, "LookingForMore"),
        (0, "Defense"),
        (0, "LocalDefense"),
        // Extra DBC-style id probes (some realms remap)
        (3, "LocalDefense"),
        (4, "General"),
        (5, "Trade"),
        (6, "LookingForGroup"),
    ];
}

/// <summary>CMSG_CHANNEL_LIST (0x009A).</summary>
public static class ChannelListCodec
{
    public const uint CmsgChannelList = 0x009A;
    public const uint SmsgChannelList = 0x009B;

    public static Packet BuildRequest(string channel)
    {
        channel ??= "";
        var nameBytes = Encoding.UTF8.GetBytes(channel.Trim());
        var payload = new byte[nameBytes.Length + 1];
        Buffer.BlockCopy(nameBytes, 0, payload, 0, nameBytes.Length);
        return new Packet(CmsgChannelList, payload);
    }
}

/// <summary>CMSG_WHO / SMSG_WHO helpers.</summary>
public static class WhoPacketCodec
{
    public const uint CmsgWho = 0x0062;
    public const uint SmsgWho = 0x0063;

    public static Packet BuildRequest(
        string? nameFilter = null,
        uint levelMin = 1,
        uint levelMax = 80)
    {
        using var ms = new MemoryStream(64);
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(levelMin);
            bw.Write(levelMax);
            bw.Write(0u); // racemask
            bw.Write(0u); // classmask
            bw.Write(0u); // zones count
            if (string.IsNullOrWhiteSpace(nameFilter))
            {
                // Empty name string improves hit rate on some Ascension builds vs zero strings.
                bw.Write(1u);
                bw.Write((byte)0);
            }
            else
            {
                bw.Write(1u);
                var bytes = Encoding.UTF8.GetBytes(nameFilter.Trim());
                bw.Write(bytes);
                bw.Write((byte)0);
            }
        }

        return new Packet(CmsgWho, ms.ToArray());
    }

    public static IReadOnlyList<WhoEntry> ParseResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            return Array.Empty<WhoEntry>();
        }

        var offset = 0;
        var total = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
        offset += 4;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
        offset += 4;
        _ = total;

        var list = new List<WhoEntry>((int)Math.Min(count, 100));
        for (var i = 0; i < count && offset < payload.Length; i++)
        {
            var name = ReadCString(payload, ref offset);
            var guild = ReadCString(payload, ref offset);
            if (payload.Length < offset + 4 + 4 + 4 + 4 + 1)
            {
                break;
            }

            var level = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var classId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var race = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var zone = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var gender = payload[offset++];
            list.Add(new WhoEntry(name, guild, (int)level, (int)classId, (int)race, (int)zone, gender));
        }

        return list;
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
            throw new InvalidDataException("Unterminated CString in SMSG_WHO.");
        }

        var value = Encoding.UTF8.GetString(data.Slice(start, offset - start));
        offset++;
        return value;
    }
}

public sealed record WhoEntry(
    string Name,
    string Guild,
    int Level,
    int ClassId,
    int Race,
    int ZoneId,
    byte Gender,
    string Guid = "",
    long MessageCount = 0,
    DateTimeOffset LastSeenUtc = default,
    DateTimeOffset? LastWhoUtc = null,
    /// <summary>online | recent | offline — computed at snapshot time.</summary>
    string Presence = "offline");

public static class PlayerPresence
{
    public const string Online = "online";
    public const string Recent = "recent";
    public const string Offline = "offline";

    /// <summary>Still confirmed by SMSG_WHO within this window.</summary>
    public static readonly TimeSpan OnlineTtl = TimeSpan.FromMinutes(3);

    /// <summary>Seen via chat/WHO/name-query within this window → amber.</summary>
    public static readonly TimeSpan RecentTtl = TimeSpan.FromMinutes(30);

    public static string Compute(WhoEntry entry, DateTimeOffset now, string? selfName, bool selfOnline)
    {
        if (selfOnline
            && !string.IsNullOrWhiteSpace(selfName)
            && entry.Name.Equals(selfName, StringComparison.OrdinalIgnoreCase))
        {
            return Online;
        }

        if (entry.LastWhoUtc is { } whoAt && now - whoAt <= OnlineTtl)
        {
            return Online;
        }

        var seen = entry.LastSeenUtc == default ? DateTimeOffset.MinValue : entry.LastSeenUtc;
        if (seen != DateTimeOffset.MinValue && now - seen <= RecentTtl)
        {
            return Recent;
        }

        return Offline;
    }

    /// <summary>Best last-activity timestamp for away text (WHO preferred, else chat seen).</summary>
    public static DateTimeOffset? BestLastSeen(WhoEntry entry)
    {
        if (entry.LastWhoUtc is { } whoAt && whoAt != default)
        {
            return whoAt;
        }

        if (entry.LastSeenUtc != default)
        {
            return entry.LastSeenUtc;
        }

        return null;
    }

    /// <summary>Human away string: "online", "3m ago", "offline 5h", "offline 2d".</summary>
    public static string FormatAway(WhoEntry entry, DateTimeOffset now, string? selfName, bool selfOnline)
    {
        var presence = Compute(entry, now, selfName, selfOnline);
        if (presence == Online)
        {
            return "online";
        }

        var last = BestLastSeen(entry);
        if (last is null)
        {
            return presence == Recent ? "seen recently" : "offline";
        }

        var delta = now - last.Value;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        var ago = FormatDuration(delta);
        if (presence == Recent)
        {
            return $"seen {ago}";
        }

        return $"offline {ago}";
    }

    public static string FormatDuration(TimeSpan delta)
    {
        if (delta.TotalSeconds < 45)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            var m = Math.Max(1, (int)delta.TotalMinutes);
            return $"{m}m";
        }

        if (delta.TotalHours < 48)
        {
            var h = Math.Max(1, (int)delta.TotalHours);
            return $"{h}h";
        }

        var d = Math.Max(1, (int)delta.TotalDays);
        if (d < 14)
        {
            return $"{d}d";
        }

        var w = Math.Max(1, d / 7);
        if (w < 10)
        {
            return $"{w}w";
        }

        var mo = Math.Max(1, d / 30);
        return $"{mo}mo";
    }
}

/// <summary>CMSG_NAME_QUERY / SMSG_NAME_QUERY_RESPONSE.</summary>
public static class NameQueryCodec
{
    public const uint CmsgNameQuery = 0x0050;
    public const uint SmsgNameQueryResponse = 0x0051;

    public static Packet BuildRequest(ulong guid)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, guid);
        return new Packet(CmsgNameQuery, payload);
    }

    public static bool TryParseResponse(ReadOnlySpan<byte> payload, out ulong guid, out string name, out int race, out int gender, out int classId)
    {
        guid = 0;
        name = string.Empty;
        race = -1;
        gender = -1;
        classId = -1;
        if (payload.Length < 10)
        {
            return false;
        }

        try
        {
            var pos = 0;
            guid = ReadPackedGuid(payload, ref pos);
            if (guid == 0 && payload.Length >= 8)
            {
                pos = 0;
                guid = BinaryPrimitives.ReadUInt64LittleEndian(payload);
                pos = 8;
            }

            if (pos >= payload.Length)
            {
                return false;
            }

            var early = payload[pos++];
            // WotLK: 1 means name unknown / offline stub.
            if (early == 1)
            {
                return false;
            }

            // Some builds put name immediately; early==0 means "has name".
            // If early looks like first letter of name, rewind.
            if (early is >= (byte)'A' and <= (byte)'z')
            {
                pos--;
            }

            name = ReadCString(payload, ref pos);
            if (pos < payload.Length)
            {
                _ = ReadCString(payload, ref pos); // realm
            }

            if (payload.Length >= pos + 3)
            {
                race = payload[pos++];
                gender = payload[pos++];
                classId = payload[pos++];
            }

            return guid != 0 && !string.IsNullOrWhiteSpace(name);
        }
        catch
        {
            return false;
        }
    }

    private static ulong ReadPackedGuid(ReadOnlySpan<byte> s, ref int pos)
    {
        if (pos >= s.Length)
        {
            return 0;
        }

        var mask = s[pos++];
        ulong guid = 0;
        for (var i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) == 0)
            {
                continue;
            }

            if (pos >= s.Length)
            {
                return 0;
            }

            guid |= (ulong)s[pos++] << (8 * i);
        }

        return guid;
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
            throw new InvalidDataException("Unterminated CString in name query.");
        }

        var value = Encoding.UTF8.GetString(data.Slice(start, offset - start));
        offset++;
        return value;
    }
}
