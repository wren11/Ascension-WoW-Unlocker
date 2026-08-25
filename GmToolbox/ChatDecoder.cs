using System.Text;

namespace AscensionNetTool;

/// <summary>WotLK 3.3.5 / Ascension SMSG chat + system-log decoders.</summary>
static class ChatDecoder
{
    public const uint OpMessageChat = 0x0096;
    public const uint OpGmMessageChat = 0x03B3;
    public const uint OpNotification = 0x01CB;
    public const uint OpServerMessage = 0x0291;
    public const uint OpMotd = 0x033D;
    public const uint OpNameQueryResponse = 0x0051;

    public static readonly HashSet<uint> AlwaysCaptureOpcodes = new()
    {
        OpMessageChat, OpGmMessageChat, OpNotification, OpServerMessage, OpMotd,
        OpNameQueryResponse,
    };

    public sealed class DecodedNameQuery
    {
        public string Guid { get; init; } = "";
        public string Name { get; init; } = "";
        public string Realm { get; init; } = "";
        public int Race { get; init; } = -1;
        public int Gender { get; init; } = -1;
        public int Class { get; init; } = -1;
    }

    public static bool TryDecodeNameQuery(CapturedPacket pkt, out DecodedNameQuery? info)
    {
        info = null;
        if (pkt.Dir != PktDir.In) return false;
        if (Opcodes.Normalize(pkt.Opcode) != OpNameQueryResponse) return false;
        int body = BodyStart(pkt.Data, OpNameQueryResponse);
        if (body < 0 || body >= pkt.Data.Length) return false;
        try
        {
            var r = new Reader(pkt.Data.AsSpan(body));
            // 3.3.5: PackedGuid; older/some cores: u64
            ulong guid = r.TryReadPackedGuid();
            if (guid == 0 && r.Remaining >= 8)
            {
                r.Pos = 0;
                guid = r.U64();
            }
            if (r.Remaining < 1) return false;
            byte early = r.U8(); // 3.1+; if 1, empty
            if (early == 1) return false;
            string name = r.CString();
            string realm = r.Remaining > 0 ? r.CString() : "";
            // Ascension/TC 3.3.5 often use u8 race/gender/class after realm;
            // some builds use u32. Prefer u8 then fall back.
            int race = -1, gender = -1, classId = -1;
            if (r.Remaining >= 3)
            {
                int save = r.Pos;
                race = r.U8();
                gender = r.U8();
                classId = r.U8();
                // Heuristic: race 1-22, gender 0-1, class 1-11
                if (race < 1 || race > 30 || gender > 1 || classId < 1 || classId > 11)
                {
                    r.Pos = save;
                    if (r.Remaining >= 12)
                    {
                        race = (int)r.U32();
                        gender = (int)r.U32();
                        classId = (int)r.U32();
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(name) || guid == 0) return false;
            info = new DecodedNameQuery
            {
                Guid = PlayerDirectory.NormGuid(guid),
                Name = name,
                Realm = realm,
                Race = race,
                Gender = gender,
                Class = classId,
            };
            return true;
        }
        catch { return false; }
    }

    public enum ChatMsgType : byte
    {
        System = 0x00,
        Say = 0x01,
        Party = 0x02,
        Raid = 0x03,
        Guild = 0x04,
        Officer = 0x05,
        Yell = 0x06,
        Whisper = 0x07,
        WhisperForeign = 0x08,
        WhisperInform = 0x09,
        Emote = 0x0A,
        TextEmote = 0x0B,
        MonsterSay = 0x0C,
        MonsterParty = 0x0D,
        MonsterYell = 0x0E,
        MonsterWhisper = 0x0F,
        MonsterEmote = 0x10,
        Channel = 0x11,
        ChannelJoin = 0x12,
        ChannelLeave = 0x13,
        ChannelList = 0x14,
        ChannelNotice = 0x15,
        ChannelNoticeUser = 0x16,
        Afk = 0x17,
        Dnd = 0x18,
        Ignored = 0x19,
        Skill = 0x1A,
        Loot = 0x1B,
        Money = 0x1C,
        Opening = 0x1D,
        Tradeskills = 0x1E,
        PetInfo = 0x1F,
        CombatMiscInfo = 0x20,
        CombatXpGain = 0x21,
        CombatHonorGain = 0x22,
        CombatFactionChange = 0x23,
        BgSystemNeutral = 0x24,
        BgSystemAlliance = 0x25,
        BgSystemHorde = 0x26,
        RaidLeader = 0x27,
        RaidWarning = 0x28,
        RaidBossEmote = 0x29,
        RaidBossWhisper = 0x2A,
        Filtered = 0x2B,
        Battleground = 0x2C,
        BattlegroundLeader = 0x2D,
        Restricted = 0x2E,
        Bnet = 0x2F,
        Achievement = 0x30,
        GuildAchievement = 0x31,
        ArenaPoints = 0x32,
        PartyLeader = 0x33,
    }

    public sealed class DecodedChat
    {
        public uint Opcode { get; init; }
        public string Kind { get; init; } = "chat"; // chat | notification | server | motd
        public int ChatType { get; init; } = -1;
        public string ChatTypeName { get; init; } = "";
        public uint Language { get; init; }
        public string SenderGuid { get; init; } = "";
        public string SenderName { get; init; } = "";
        public string TargetGuid { get; init; } = "";
        public string Channel { get; init; } = "";
        public string Message { get; init; } = "";
        public byte ChatTag { get; init; }
        public string RawHex { get; init; } = "";
        public int InstanceId { get; init; }
    }

    public static bool TryDecode(CapturedPacket pkt, out DecodedChat? chat)
    {
        chat = null;
        if (pkt.Dir != PktDir.In) return false;
        var op = Opcodes.Normalize(pkt.Opcode);
        if (!AlwaysCaptureOpcodes.Contains(op)) return false;

        int body = BodyStart(pkt.Data, op);
        if (body < 0 || body >= pkt.Data.Length) return false;
        var span = pkt.Data.AsSpan(body);
        string hex = Convert.ToHexString(pkt.Data);

        try
        {
            chat = op switch
            {
                OpMessageChat or OpGmMessageChat => DecodeMessageChat(op, span, hex),
                OpNotification => DecodeCStringPacket(op, "notification", span, hex),
                OpServerMessage => DecodeServerMessage(span, hex),
                OpMotd => DecodeMotd(span, hex),
                _ => null,
            };
            return chat is not null;
        }
        catch
        {
            return false;
        }
    }

    static int BodyStart(byte[] d, uint opcode)
    {
        if (d.Length >= 4 && BitConverter.ToUInt32(d, 0) == opcode) return 4;
        if (d.Length >= 2 && BitConverter.ToUInt16(d, 0) == (ushort)opcode) return 2;
        return 0;
    }

    static DecodedChat? DecodeMessageChat(uint opcode, ReadOnlySpan<byte> s, string hex)
    {
        var r = new Reader(s);
        byte type = r.U8();
        uint lang = r.U32();
        ulong sender = r.U64();
        if (r.Remaining >= 4)
            _ = r.U32(); // flags / unk (2.1.0+)

        string channel = "";
        string senderName = "";

        // Channel name precedes target GUID for CHAT_MSG_CHANNEL.
        if (type == (byte)ChatMsgType.Channel)
            channel = r.CString();

        bool namedNpc = type is
            (byte)ChatMsgType.MonsterSay or (byte)ChatMsgType.MonsterParty
            or (byte)ChatMsgType.MonsterYell or (byte)ChatMsgType.MonsterWhisper
            or (byte)ChatMsgType.MonsterEmote
            or (byte)ChatMsgType.RaidBossEmote or (byte)ChatMsgType.RaidBossWhisper
            or (byte)ChatMsgType.Battleground or (byte)ChatMsgType.BattlegroundLeader
            or (byte)ChatMsgType.Achievement or (byte)ChatMsgType.GuildAchievement
            or (byte)ChatMsgType.ChannelNotice or (byte)ChatMsgType.ChannelNoticeUser;

        if (namedNpc && r.Remaining > 8)
        {
            int save = r.Pos;
            string maybe = r.TryPeekCString(64);
            if (maybe.Length is > 0 and < 48 && IsMostlyPrintable(maybe) && !LooksLikeGuidBlob(maybe))
                senderName = r.CString();
            else
                r.Pos = save;
        }

        ulong target = 0;
        if (r.Remaining >= 8)
            target = r.U64();

        string message = "";
        if (r.Remaining >= 4)
        {
            uint len = r.U32();
            if (len > 0 && len < 0x4000 && r.Remaining >= (int)len)
                message = r.FixedString((int)len);
            else if (r.Remaining > 0)
                message = r.CString();
        }
        else if (r.Remaining > 0)
        {
            message = r.CString();
        }

        byte tag = r.Remaining > 0 ? r.U8() : (byte)0;
        message = message.TrimEnd('\0').Trim();
        // Reject binary garbage — keep real chat words only.
        if (!IsReadableChat(message))
        {
            // Fallback: scan remaining tail for a length-prefixed or cstring run.
            message = RescueMessage(s, r.Pos) ?? message;
            if (!IsReadableChat(message))
                message = "";
        }

        // Resolve name from directory when packet has GUID only (normal player chat).
        if (string.IsNullOrWhiteSpace(senderName) && sender != 0)
            senderName = PlayerDirectory.ResolveName(PlayerDirectory.NormGuid(sender));

        // Channel display name from chat type when not a named channel.
        if (string.IsNullOrWhiteSpace(channel))
            channel = TypeName(type);

        return new DecodedChat
        {
            Opcode = opcode,
            Kind = "chat",
            ChatType = type,
            ChatTypeName = TypeName(type),
            Language = lang,
            SenderGuid = PlayerDirectory.NormGuid(sender),
            SenderName = senderName,
            TargetGuid = PlayerDirectory.NormGuid(target),
            Channel = channel,
            Message = message,
            ChatTag = tag,
            RawHex = hex,
        };
    }

    static bool IsReadableChat(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        int good = 0, bad = 0;
        foreach (char c in s)
        {
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') bad++;
            else if (c < 32) bad++;
            else good++;
        }
        return good >= 2 && good * 5 >= (good + bad) * 4;
    }

    static string? RescueMessage(ReadOnlySpan<byte> full, int from)
    {
        if (from < 0) from = 0;
        if (from >= full.Length) return null;
        var tail = full.Slice(from);
        // Prefer u32-length string
        for (int i = 0; i + 4 < tail.Length; i++)
        {
            uint len = BitConverter.ToUInt32(tail.Slice(i, 4));
            if (len is < 2 or > 400) continue;
            if (i + 4 + (int)len > tail.Length) continue;
            var slice = tail.Slice(i + 4, (int)len);
            int n = slice.Length;
            if (n > 0 && slice[n - 1] == 0) n--;
            string cand = Encoding.UTF8.GetString(slice[..Math.Max(0, n)]).TrimEnd('\0').Trim();
            if (IsReadableChat(cand)) return cand;
        }
        // CString scan
        for (int i = 0; i < tail.Length; i++)
        {
            if (tail[i] < 0x20 || tail[i] >= 0x7F) continue;
            int j = i;
            while (j < tail.Length && tail[j] != 0 && j - i < 400) j++;
            if (j >= tail.Length || tail[j] != 0) continue;
            string cand = Encoding.UTF8.GetString(tail.Slice(i, j - i)).Trim();
            if (cand.Length >= 2 && IsReadableChat(cand)) return cand;
        }
        return null;
    }

    static bool LooksLikeGuidBlob(string s)
        => s.Length >= 12 && s.All(c => Uri.IsHexDigit(c));

    static DecodedChat DecodeCStringPacket(uint op, string kind, ReadOnlySpan<byte> s, string hex)
    {
        var r = new Reader(s);
        string msg = r.CString();
        return new DecodedChat
        {
            Opcode = op,
            Kind = kind,
            ChatTypeName = kind,
            Message = msg,
            Channel = kind,
            RawHex = hex,
        };
    }

    static DecodedChat DecodeServerMessage(ReadOnlySpan<byte> s, string hex)
    {
        var r = new Reader(s);
        uint typ = r.Remaining >= 4 ? r.U32() : 0;
        string msg = r.CString();
        return new DecodedChat
        {
            Opcode = OpServerMessage,
            Kind = "server",
            ChatType = (int)typ,
            ChatTypeName = "server_message",
            Message = msg,
            Channel = "server",
            RawHex = hex,
        };
    }

    static DecodedChat DecodeMotd(ReadOnlySpan<byte> s, string hex)
    {
        var r = new Reader(s);
        uint lineCount = r.Remaining >= 4 ? r.U32() : 0;
        var sb = new StringBuilder();
        for (uint i = 0; i < lineCount && i < 64; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(r.CString());
        }
        if (sb.Length == 0 && r.Remaining > 0)
            sb.Append(r.CString());
        return new DecodedChat
        {
            Opcode = OpMotd,
            Kind = "motd",
            ChatTypeName = "motd",
            Message = sb.ToString(),
            Channel = "motd",
            RawHex = hex,
        };
    }

    public static string TypeName(int type)
    {
        if (Enum.IsDefined(typeof(ChatMsgType), (byte)type))
            return ((ChatMsgType)type).ToString();
        return $"Type_{type:X2}";
    }

    static bool IsMostlyPrintable(string s)
    {
        int ok = 0;
        foreach (char c in s)
        {
            if (c >= 32 && c < 127) ok++;
        }
        return s.Length > 0 && ok * 10 >= s.Length * 8;
    }

    ref struct Reader
    {
        readonly ReadOnlySpan<byte> _s;
        public int Pos;
        public Reader(ReadOnlySpan<byte> s) { _s = s; Pos = 0; }
        public int Remaining => _s.Length - Pos;

        public byte U8()
        {
            if (Remaining < 1) throw new InvalidOperationException();
            return _s[Pos++];
        }
        public uint U32()
        {
            if (Remaining < 4) throw new InvalidOperationException();
            uint v = BitConverter.ToUInt32(_s.Slice(Pos, 4));
            Pos += 4;
            return v;
        }
        public ulong U64()
        {
            if (Remaining < 8) throw new InvalidOperationException();
            ulong v = BitConverter.ToUInt64(_s.Slice(Pos, 8));
            Pos += 8;
            return v;
        }
        public ulong TryReadPackedGuid()
        {
            if (Remaining < 1) return 0;
            byte mask = U8();
            ulong guid = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                if (Remaining < 1) return 0;
                guid |= ((ulong)U8()) << (8 * i);
            }
            return guid;
        }
        public string CString()
        {
            int start = Pos;
            while (Pos < _s.Length && _s[Pos] != 0) Pos++;
            string s = Encoding.UTF8.GetString(_s.Slice(start, Pos - start));
            if (Pos < _s.Length) Pos++; // skip NUL
            return s;
        }
        public string TryPeekCString(int max)
        {
            int end = Math.Min(_s.Length, Pos + max);
            int i = Pos;
            while (i < end && _s[i] != 0) i++;
            if (i >= end) return "";
            return Encoding.UTF8.GetString(_s.Slice(Pos, i - Pos));
        }
        public string FixedString(int len)
        {
            if (len <= 0) return "";
            if (Remaining < len) len = Remaining;
            int n = len;
            if (n > 0 && _s[Pos + n - 1] == 0) n--;
            string s = Encoding.UTF8.GetString(_s.Slice(Pos, Math.Max(0, n)));
            Pos += len;
            return s;
        }
    }
}
