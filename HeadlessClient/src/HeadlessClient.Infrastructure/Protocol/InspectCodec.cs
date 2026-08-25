using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using HeadlessClient.Domain.Protocol;

namespace HeadlessClient.Infrastructure.Protocol;

/// <summary>WotLK / Ascension inspect request + best-effort response parsing.</summary>
public static class InspectCodec
{
    public const uint CmsgInspect = 0x0114;
    public const uint SmsgInspectResultsUpdate = 0x0115;
    public const uint SmsgInspectTalent = 0x03F4;
    public const uint MsgInspectHonorStats = 0x02D6;
    public const uint MsgInspectArenaTeams = 0x0377;
    public const uint CmsgQueryInspectAchievements = 0x046B;
    public const uint SmsgRespondInspectAchievements = 0x046C;
    public const uint CmsgSetSelection = 0x013D;

    public static Packet BuildSetSelection(ulong guid) =>
        new(CmsgSetSelection, GuidBytes(guid));

    public static Packet BuildInspect(ulong guid) =>
        new(CmsgInspect, GuidBytes(guid));

    public static Packet BuildInspectHonor(ulong guid) =>
        new(MsgInspectHonorStats, GuidBytes(guid));

    public static Packet BuildInspectArena(ulong guid) =>
        new(MsgInspectArenaTeams, GuidBytes(guid));

    public static Packet BuildInspectAchievements(ulong guid) =>
        new(CmsgQueryInspectAchievements, GuidBytes(guid));

    public static bool TryParseInspectTalent(ReadOnlySpan<byte> data, out InspectTalentSnapshot snap)
    {
        snap = new InspectTalentSnapshot();
        var pos = 0;
        if (!TryReadPackedGuid(data, ref pos, out var guid) || guid == 0)
        {
            return false;
        }

        snap.Guid = guid;
        if (!TryReadU32(data, ref pos, out var unspent))
        {
            return false;
        }

        snap.UnspentTalentPoints = unspent;
        if (pos >= data.Length)
        {
            return true;
        }

        var specCount = data[pos++];
        snap.SpecCount = specCount;
        if (pos >= data.Length)
        {
            return true;
        }

        snap.ActiveSpec = data[pos++];
        var talents = new List<InspectTalentNode>();
        for (var s = 0; s < specCount && pos < data.Length; s++)
        {
            if (pos >= data.Length)
            {
                break;
            }

            var talentCount = data[pos++];
            for (var t = 0; t < talentCount && pos + 5 <= data.Length; t++)
            {
                var talentId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
                pos += 4;
                var rank = data[pos++];
                if (talentId != 0 && rank > 0)
                {
                    talents.Add(new InspectTalentNode(talentId, rank, s));
                }
            }
        }

        snap.Talents = talents;

        // Glyphs (optional)
        if (pos < data.Length)
        {
            var glyphCount = data[pos++];
            var glyphs = new List<ushort>();
            for (var g = 0; g < glyphCount && pos + 2 <= data.Length; g++)
            {
                glyphs.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2)));
                pos += 2;
            }

            snap.Glyphs = glyphs;
        }

        // Best-effort gear item ids from remaining u32 stream (enchant/item slots).
        var items = new List<uint>();
        while (pos + 4 <= data.Length && items.Count < 40)
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
            pos += 4;
            if (v is > 0 and < 200_000)
            {
                items.Add(v);
            }
        }

        snap.ItemIds = items.Distinct().Take(30).ToList();
        snap.RawLen = data.Length;
        return true;
    }

    public static bool TryParseHonor(ReadOnlySpan<byte> data, out InspectHonorSnapshot honor)
    {
        honor = new InspectHonorSnapshot();
        var pos = 0;
        if (!TryReadGuidLoose(data, ref pos, out var guid))
        {
            return false;
        }

        honor.Guid = guid;
        if (pos < data.Length)
        {
            honor.HonorLevel = data[pos++];
        }

        if (TryReadU32(data, ref pos, out var kills))
        {
            honor.Kills = kills;
        }

        if (TryReadU32(data, ref pos, out var today))
        {
            honor.HonorToday = today;
        }

        if (TryReadU32(data, ref pos, out var yesterday))
        {
            honor.HonorYesterday = yesterday;
        }

        if (TryReadU32(data, ref pos, out var lifetime))
        {
            honor.LifetimeHonorableKills = lifetime;
        }

        return true;
    }

    public static bool TryParseArenaTeam(ReadOnlySpan<byte> data, out InspectArenaSnapshot arena)
    {
        arena = new InspectArenaSnapshot();
        var pos = 0;
        if (!TryReadGuidLoose(data, ref pos, out var guid))
        {
            return false;
        }

        arena.Guid = guid;
        if (pos >= data.Length)
        {
            return false;
        }

        arena.Slot = data[pos++];
        TryReadU32(data, ref pos, out var team);
        arena.TeamId = team;
        TryReadU32(data, ref pos, out var rating);
        arena.TeamRating = rating;
        TryReadU32(data, ref pos, out var games);
        arena.GamesSeason = games;
        TryReadU32(data, ref pos, out var wins);
        arena.WinsSeason = wins;
        TryReadU32(data, ref pos, out var total);
        arena.TotalGames = total;
        TryReadU32(data, ref pos, out var personal);
        arena.PersonalRating = personal;
        arena.Bracket = arena.Slot switch
        {
            0 => "2v2",
            1 => "3v3",
            2 => "5v5",
            _ => $"slot{arena.Slot}"
        };
        return arena.TeamId != 0 || arena.PersonalRating != 0 || arena.TeamRating != 0;
    }

    public static bool TryParseAchievements(ReadOnlySpan<byte> data, out InspectAchievementSnapshot ach)
    {
        ach = new InspectAchievementSnapshot();
        var pos = 0;
        ulong guid;
        if (!TryReadPackedGuid(data, ref pos, out guid))
        {
            pos = 0;
            if (!TryReadGuidLoose(data, ref pos, out guid))
            {
                return false;
            }
        }

        ach.Guid = guid;
        var ids = new List<uint>();
        while (pos + 4 <= data.Length && ids.Count < 200)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
            pos += 4;
            // timestamps often follow; keep plausible achievement ids
            if (id is > 0 and < 50_000)
            {
                ids.Add(id);
            }
        }

        ach.AchievementIds = ids.Distinct().Take(100).ToList();
        ach.RawLen = data.Length;
        return true;
    }

    private static byte[] GuidBytes(ulong g)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, g);
        return b;
    }

    private static bool TryReadU32(ReadOnlySpan<byte> data, ref int pos, out uint value)
    {
        if (pos + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
        pos += 4;
        return true;
    }

    private static bool TryReadGuidLoose(ReadOnlySpan<byte> data, ref int pos, out ulong guid)
    {
        if (pos + 8 <= data.Length)
        {
            guid = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos, 8));
            pos += 8;
            return guid != 0;
        }

        return TryReadPackedGuid(data, ref pos, out guid);
    }

    private static bool TryReadPackedGuid(ReadOnlySpan<byte> data, ref int pos, out ulong guid)
    {
        guid = 0;
        if (pos >= data.Length)
        {
            return false;
        }

        var mask = data[pos++];
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) == 0)
            {
                continue;
            }

            if (pos >= data.Length)
            {
                return false;
            }

            value |= (ulong)data[pos++] << (8 * i);
        }

        guid = value;
        return guid != 0;
    }
}

public sealed class InspectTalentSnapshot
{
    public ulong Guid { get; set; }
    public uint UnspentTalentPoints { get; set; }
    public byte SpecCount { get; set; }
    public byte ActiveSpec { get; set; }
    public IReadOnlyList<InspectTalentNode> Talents { get; set; } = Array.Empty<InspectTalentNode>();
    public IReadOnlyList<ushort> Glyphs { get; set; } = Array.Empty<ushort>();
    public IReadOnlyList<uint> ItemIds { get; set; } = Array.Empty<uint>();
    public int RawLen { get; set; }
}

public sealed record InspectTalentNode(uint TalentId, byte Rank, int SpecIndex);

public sealed class InspectHonorSnapshot
{
    public ulong Guid { get; set; }
    public byte HonorLevel { get; set; }
    public uint Kills { get; set; }
    public uint HonorToday { get; set; }
    public uint HonorYesterday { get; set; }
    public uint LifetimeHonorableKills { get; set; }
}

public sealed class InspectArenaSnapshot
{
    public ulong Guid { get; set; }
    public byte Slot { get; set; }
    public string Bracket { get; set; } = "";
    public uint TeamId { get; set; }
    public uint TeamRating { get; set; }
    public uint GamesSeason { get; set; }
    public uint WinsSeason { get; set; }
    public uint TotalGames { get; set; }
    public uint PersonalRating { get; set; }
}

public sealed class InspectAchievementSnapshot
{
    public ulong Guid { get; set; }
    public IReadOnlyList<uint> AchievementIds { get; set; } = Array.Empty<uint>();
    public int RawLen { get; set; }
}
