using System.IO.MemoryMappedFiles;
using System.Text;
using HeadlessClient.Domain.World;

namespace HeadlessClient.Infrastructure.Auth;

/// <summary>Drains ExtProxy packet ring for inbound chat opcodes.</summary>
public sealed class ExtProxyPacketRingReader : IDisposable
{
    public const uint PktMagic = 0x504B5431;
    public const int RingSlots = 2048;
    public const int RingMax = 2048;
    public const int HeaderSize = 56; // 14 x uint32 (matches ExtProxy PktRingHeader)
    public const int SlotSize = 16 + RingMax;

    public const uint OpMessageChat = 0x0096;
    public const uint OpGmMessageChat = 0x03B3;
    public const uint OpNotification = 0x01CB;
    public const uint OpServerMessage = 0x0291;
    public const uint OpMotd = 0x033D;
    public const uint OpNameQueryResponse = 0x0051;

    public sealed class NameQueryHit
    {
        public string Guid { get; init; } = "";
        public string Name { get; init; } = "";
        public string Realm { get; init; } = "";
        public int Race { get; init; } = -1;
        public int Gender { get; init; } = -1;
        public int ClassId { get; init; } = -1;
    }

    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    uint _lastSeq;

    public bool IsOpen => _view is not null;

    public bool TryOpen(int pid)
    {
        Dispose();
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            var name = $"Local\\AscensionExtProxyRingV5_{pid}";
            _mmf = MemoryMappedFile.OpenExisting(name);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (_view.ReadUInt32(0) != PktMagic)
            {
                Dispose();
                return false;
            }

            _lastSeq = _view.ReadUInt32(12);
            return true;
        }
        catch
        {
            Dispose();
            return false;
        }
    }

    public List<ChatLine> DrainNew()
    {
        DrainAll(out var chats, out _);
        return chats;
    }

    public void DrainAll(out List<ChatLine> chats, out List<NameQueryHit> names)
    {
        chats = new List<ChatLine>();
        names = new List<NameQueryHit>();
        if (_view is null)
        {
            return;
        }

        if (_view.ReadUInt32(0) != PktMagic)
        {
            return;
        }

        var cur = _view.ReadUInt32(12);
        if (cur == _lastSeq)
        {
            return;
        }

        var start = _lastSeq;
        if (cur > start && cur - start > RingSlots)
        {
            start = cur - RingSlots;
        }

        var maxTake = Math.Min(cur - start, 256u);
        start = cur - maxTake;

        for (var seq = start + 1; seq <= cur; seq++)
        {
            var idx = (seq - 1) % RingSlots;
            long slotOff = HeaderSize + (long)idx * SlotSize;
            var slotSeq = _view.ReadUInt32(slotOff);
            if (slotSeq != seq)
            {
                continue;
            }

            var dir = _view.ReadByte(slotOff + 8);
            if (dir != 1)
            {
                continue;
            }

            var size = _view.ReadUInt16(slotOff + 10);
            var opcode = _view.ReadUInt32(slotOff + 12);
            if (!IsChatOpcode(opcode) && opcode != OpNameQueryResponse)
            {
                continue;
            }

            if (size > RingMax)
            {
                size = RingMax;
            }

            var data = new byte[size];
            if (size > 0)
            {
                _view.ReadArray(slotOff + 16, data, 0, size);
            }

            if (opcode == OpNameQueryResponse)
            {
                if (TryDecodeNameQuery(data, out var hit))
                {
                    names.Add(hit);
                }

                continue;
            }

            if (TryDecode(opcode, data, out var line))
            {
                chats.Add(line);
            }
        }

        _lastSeq = cur;
    }

    static bool IsChatOpcode(uint opcode) =>
        opcode is OpMessageChat or OpGmMessageChat or OpNotification or OpServerMessage or OpMotd;

    static bool TryDecode(uint opcode, byte[] data, out ChatLine line)
    {
        line = default!;
        var body = BodyStart(data, opcode);
        if (body < 0 || body >= data.Length)
        {
            return false;
        }

        var span = data.AsSpan(body);
        try
        {
            if (opcode is OpMessageChat or OpGmMessageChat)
            {
                return TryDecodeMessageChat(span, out line);
            }

            if (opcode is OpNotification or OpMotd)
            {
                var msg = ReadCString(span, 0);
                if (string.IsNullOrWhiteSpace(msg))
                {
                    return false;
                }

                line = new ChatLine(DateTimeOffset.UtcNow, 0, "0", "System",
                    opcode == OpMotd ? "MOTD" : "NOTIFICATION", msg);
                return true;
            }

            if (opcode == OpServerMessage && span.Length >= 5)
            {
                var msg = ReadCString(span, 4);
                if (string.IsNullOrWhiteSpace(msg))
                {
                    return false;
                }

                line = new ChatLine(DateTimeOffset.UtcNow, 0, "0", "System", "SERVER", msg);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    static bool TryDecodeMessageChat(ReadOnlySpan<byte> s, out ChatLine line)
    {
        line = default!;
        if (s.Length < 13)
        {
            return false;
        }

        var pos = 0;
        var type = s[pos++];
        var language = BitConverter.ToUInt32(s.Slice(pos, 4));
        pos += 4;
        var senderGuid = BitConverter.ToUInt64(s.Slice(pos, 8));
        pos += 8;
        if (s.Length >= pos + 4)
        {
            pos += 4; // unk
        }

        var channel = string.Empty;
        if (type == 0x11)
        {
            channel = ReadCString(s, ref pos);
        }

        var senderName = string.Empty;
        if (IsNamedNpc(type) && s.Length > pos + 8)
        {
            var save = pos;
            var maybe = PeekCString(s, pos, 64);
            if (maybe.Length is > 0 and < 48 && IsMostlyPrintable(maybe))
            {
                senderName = ReadCString(s, ref pos);
            }
            else
            {
                pos = save;
            }
        }

        if (s.Length >= pos + 8)
        {
            pos += 8; // target guid
        }

        var message = string.Empty;
        if (s.Length >= pos + 4)
        {
            var len = BitConverter.ToInt32(s.Slice(pos, 4));
            pos += 4;
            if (len > 0 && len < 0x4000 && s.Length >= pos + len)
            {
                var take = len;
                if (take > 0 && s[pos + take - 1] == 0)
                {
                    take--;
                }

                message = Encoding.UTF8.GetString(s.Slice(pos, Math.Max(0, take))).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(channel))
        {
            channel = TypeName(type);
        }

        line = new ChatLine(
            DateTimeOffset.UtcNow,
            type,
            language.ToString(),
            senderName,
            channel,
            message,
            senderGuid == 0 ? "" : senderGuid.ToString("X16"));
        return true;
    }

    static bool TryDecodeNameQuery(byte[] data, out NameQueryHit hit)
    {
        hit = null!;
        var body = BodyStart(data, OpNameQueryResponse);
        if (body < 0 || body >= data.Length)
        {
            return false;
        }

        var s = data.AsSpan(body);
        if (s.Length < 10)
        {
            return false;
        }

        try
        {
            var pos = 0;
            ulong guid = ReadPackedGuid(s, ref pos);
            if (guid == 0 && s.Length >= 8)
            {
                pos = 0;
                guid = BitConverter.ToUInt64(s.Slice(0, 8));
                pos = 8;
            }

            if (pos >= s.Length)
            {
                return false;
            }

            var early = s[pos++];
            if (early == 1)
            {
                return false;
            }

            var name = ReadCString(s, ref pos);
            var realm = pos < s.Length ? ReadCString(s, ref pos) : "";
            var race = -1;
            var gender = -1;
            var classId = -1;
            if (s.Length >= pos + 3)
            {
                race = s[pos++];
                gender = s[pos++];
                classId = s[pos++];
            }

            if (guid == 0 || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            hit = new NameQueryHit
            {
                Guid = guid.ToString("X16"),
                Name = name,
                Realm = realm,
                Race = race,
                Gender = gender,
                ClassId = classId
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    static ulong ReadPackedGuid(ReadOnlySpan<byte> s, ref int pos)
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
                break;
            }

            guid |= (ulong)s[pos++] << (8 * i);
        }

        return guid;
    }

    static int BodyStart(byte[] d, uint opcode)
    {
        if (d.Length >= 4 && BitConverter.ToUInt32(d, 0) == opcode)
        {
            return 4;
        }

        if (d.Length >= 2 && BitConverter.ToUInt16(d, 0) == (ushort)opcode)
        {
            return 2;
        }

        return 0;
    }

    static bool IsNamedNpc(byte type) =>
        type is 0x0C or 0x0D or 0x0E or 0x0F or 0x10 or 0x33 or 0x34 or 0x2C or 0x2D or 0x2E or 0x2F
            or 0x15 or 0x16;

    static string TypeName(byte type) => type switch
    {
        0x00 => "SYSTEM",
        0x01 => "SAY",
        0x02 => "PARTY",
        0x03 => "RAID",
        0x04 => "GUILD",
        0x05 => "OFFICER",
        0x06 => "YELL",
        0x07 => "WHISPER",
        0x09 => "WHISPER_INFORM",
        0x0A => "EMOTE",
        0x11 => "CHANNEL",
        0x2C => "BATTLEGROUND",
        _ => $"TYPE_{type:X2}"
    };

    static string ReadCString(ReadOnlySpan<byte> data, int start)
    {
        var pos = start;
        return ReadCString(data, ref pos);
    }

    static string ReadCString(ReadOnlySpan<byte> data, ref int offset)
    {
        var start = offset;
        while (offset < data.Length && data[offset] != 0)
        {
            offset++;
        }

        if (offset >= data.Length)
        {
            return Encoding.UTF8.GetString(data.Slice(start));
        }

        var value = Encoding.UTF8.GetString(data.Slice(start, offset - start));
        offset++;
        return value;
    }

    static string PeekCString(ReadOnlySpan<byte> data, int offset, int max)
    {
        var end = Math.Min(data.Length, offset + max);
        var i = offset;
        while (i < end && data[i] != 0)
        {
            i++;
        }

        return i > offset ? Encoding.UTF8.GetString(data.Slice(offset, i - offset)) : string.Empty;
    }

    static bool IsMostlyPrintable(string s)
    {
        var good = 0;
        foreach (var c in s)
        {
            if (char.IsControl(c))
            {
                return false;
            }

            if (c >= 32)
            {
                good++;
            }
        }

        return good >= 1;
    }

    public void Dispose()
    {
        try
        {
            _view?.Dispose();
        }
        catch
        {
        }

        try
        {
            _mmf?.Dispose();
        }
        catch
        {
        }

        _view = null;
        _mmf = null;
    }
}
