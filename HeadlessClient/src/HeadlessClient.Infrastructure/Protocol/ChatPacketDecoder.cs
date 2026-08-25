using System.Buffers.Binary;
using System.Text;
using HeadlessClient.Domain.World;

namespace HeadlessClient.Infrastructure.Protocol;

/// <summary>
/// Ascension / WotLK <c>SMSG_MESSAGECHAT</c> decoder (verified against live Wooz captures + GmToolbox).
/// Layout: type u8 | lang u32 | senderGuid u64 | unk u32 | [channel] | [npc name] | targetGuid u64 | msgLen u32 | msg | tag.
/// </summary>
public static class ChatPacketDecoder
{
    public static bool TryDecode(ReadOnlySpan<byte> payload, out ChatLine line)
    {
        line = default!;
        try
        {
            if (payload.Length < 13)
            {
                return false;
            }

            var pos = 0;
            var type = payload[pos++];
            var language = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos, 4));
            pos += 4;
            var senderGuid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(pos, 8));
            pos += 8;

            if (payload.Length >= pos + 4)
            {
                pos += 4; // unk / flags (2.1.0+)
            }

            var channel = string.Empty;
            if (type == ChatTypes.Channel)
            {
                channel = ReadCString(payload, ref pos);
            }

            var senderName = string.Empty;
            if (IsNamedNpc(type) && payload.Length > pos + 8)
            {
                if (!TryReadNpcName(payload, ref pos, out senderName))
                {
                    senderName = string.Empty;
                }
            }

            ulong targetGuid = 0;
            if (payload.Length >= pos + 8)
            {
                targetGuid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(pos, 8));
                pos += 8;
            }

            var message = string.Empty;
            if (payload.Length >= pos + 4)
            {
                var len = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(pos, 4));
                pos += 4;
                if (len > 0 && len < 0x4000 && payload.Length >= pos + len)
                {
                    var take = len;
                    if (take > 0 && payload[pos + take - 1] == 0)
                    {
                        take--;
                    }

                    message = Encoding.UTF8.GetString(payload.Slice(pos, Math.Max(0, take))).Trim();
                    pos += len;
                }
                else if (payload.Length > pos)
                {
                    message = ReadCString(payload, ref pos).Trim();
                }
            }
            else if (payload.Length > pos)
            {
                message = ReadCString(payload, ref pos).Trim();
            }

            if (payload.Length > pos)
            {
                pos++; // chat tag
            }

            if (!IsReadableChat(message))
            {
                message = RescueMessage(payload, Math.Max(0, pos - 8)) ?? message;
                if (!IsReadableChat(message))
                {
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(channel))
            {
                channel = type switch
                {
                    ChatTypes.Whisper => "WHISPER",
                    ChatTypes.WhisperInform => "WHISPER_INFORM",
                    ChatTypes.WhisperForeign => "WHISPER",
                    _ => ChatTypes.Name(type)
                };
            }

            var senderGuidHex = senderGuid == 0 ? "" : senderGuid.ToString("X16");
            var readable = string.IsNullOrWhiteSpace(senderName)
                ? message
                : $"{senderName}: {message}";

            var direction = type switch
            {
                ChatTypes.WhisperInform => "out",
                ChatTypes.Whisper or ChatTypes.WhisperForeign => "in",
                _ => ""
            };

            line = new ChatLine(
                DateTimeOffset.UtcNow,
                type,
                language.ToString(),
                senderName,
                channel,
                message,
                SenderGuid: senderGuidHex,
                ReadableText: readable,
                TargetGuid: targetGuid == 0 ? "" : targetGuid.ToString("X16"),
                Direction: direction);

            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool IsNamedNpc(byte type) => type is
        ChatTypes.MonsterSay or ChatTypes.MonsterParty or ChatTypes.MonsterYell
        or ChatTypes.MonsterWhisper or ChatTypes.MonsterEmote
        or ChatTypes.RaidBossEmote or ChatTypes.RaidBossWhisper
        or ChatTypes.Battleground or ChatTypes.BattlegroundLeader
        or ChatTypes.Achievement or ChatTypes.GuildAchievement
        or ChatTypes.ChannelNotice or ChatTypes.ChannelNoticeUser;

    private static bool TryReadNpcName(ReadOnlySpan<byte> payload, ref int pos, out string name)
    {
        name = string.Empty;
        var save = pos;

        // Ascension often uses u32 length-prefixed NPC names (includes trailing NUL).
        if (payload.Length >= pos + 4)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(pos, 4));
            if (len is > 1 and < 64 && payload.Length >= pos + 4 + len)
            {
                var take = len;
                if (payload[pos + 4 + take - 1] == 0)
                {
                    take--;
                }

                var cand = Encoding.UTF8.GetString(payload.Slice(pos + 4, Math.Max(0, take))).Trim();
                if (cand.Length is > 0 and < 48 && IsMostlyPrintable(cand))
                {
                    name = cand;
                    pos += 4 + len;
                    return true;
                }
            }
        }

        pos = save;
        var maybe = PeekCString(payload, pos, 64);
        if (maybe.Length is > 0 and < 48 && IsMostlyPrintable(maybe) && !LooksLikeGuidBlob(maybe))
        {
            name = ReadCString(payload, ref pos);
            return true;
        }

        pos = save;
        return false;
    }

    private static string? RescueMessage(ReadOnlySpan<byte> full, int from)
    {
        if (from < 0)
        {
            from = 0;
        }

        if (from >= full.Length)
        {
            return null;
        }

        var tail = full.Slice(from);
        for (var i = 0; i + 4 < tail.Length; i++)
        {
            var len = BinaryPrimitives.ReadUInt32LittleEndian(tail.Slice(i, 4));
            if (len is < 2 or > 400)
            {
                continue;
            }

            if (i + 4 + (int)len > tail.Length)
            {
                continue;
            }

            var slice = tail.Slice(i + 4, (int)len);
            var n = slice.Length;
            if (n > 0 && slice[n - 1] == 0)
            {
                n--;
            }

            var cand = Encoding.UTF8.GetString(slice[..Math.Max(0, n)]).TrimEnd('\0').Trim();
            if (IsReadableChat(cand))
            {
                return cand;
            }
        }

        return null;
    }

    private static bool IsReadableChat(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        var good = 0;
        var bad = 0;
        foreach (var c in s)
        {
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            {
                bad++;
            }
            else if (c < 32)
            {
                bad++;
            }
            else
            {
                good++;
            }
        }

        return good >= 2 && good * 5 >= (good + bad) * 4;
    }

    private static bool IsMostlyPrintable(string s)
    {
        var ok = 0;
        foreach (var c in s)
        {
            if (c >= 32 && c < 127)
            {
                ok++;
            }
        }

        return s.Length > 0 && ok * 10 >= s.Length * 8;
    }

    private static bool LooksLikeGuidBlob(string s) =>
        s.Length >= 8 && s.Count(c =>
            (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')) * 2 >= s.Length;

    private static string PeekCString(ReadOnlySpan<byte> data, int offset, int max)
    {
        var start = offset;
        var end = Math.Min(data.Length, offset + max);
        while (offset < end && data[offset] != 0)
        {
            offset++;
        }

        if (offset >= end)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(data.Slice(start, offset - start));
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
            throw new InvalidDataException("Unterminated CString in chat payload.");
        }

        var value = Encoding.UTF8.GetString(data.Slice(start, offset - start));
        offset++;
        return value;
    }
}
