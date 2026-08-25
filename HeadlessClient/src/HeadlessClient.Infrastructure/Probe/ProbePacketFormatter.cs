using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using HeadlessClient.Infrastructure.Logging;

namespace HeadlessClient.Infrastructure.Probe;

/// <summary>Full hex|ASCII dump + string scrape + best-effort structured decode for probe SMSG bodies.</summary>
public static class ProbePacketFormatter
{
    public const int MaxStoreBytes = 8192;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ProbeSmsg Build(uint opcode, string name, ReadOnlySpan<byte> payload, long offsetMs)
    {
        var truncated = payload.Length > MaxStoreBytes;
        var slice = truncated ? payload[..MaxStoreBytes] : payload;
        var hex = Convert.ToHexString(slice);
        var wpe = PacketWireLogger.FormatWpe(slice);
        if (truncated)
        {
            wpe += $"\n… truncated {payload.Length - MaxStoreBytes} bytes (stored {MaxStoreBytes}/{payload.Length})";
        }

        var strings = ExtractStrings(slice);
        var decoded = TryDecode(opcode, name, slice);
        var decodedJson = decoded is null
            ? ""
            : JsonSerializer.Serialize(decoded, JsonOpts);

        return new ProbeSmsg(
            opcode,
            name,
            payload.Length,
            hex.Length > 96 ? hex[..96] + "…" : hex,
            offsetMs,
            HexFull: hex,
            WpeDump: wpe,
            AsciiStrings: strings,
            DecodedJson: decodedJson,
            Truncated: truncated);
    }

    public static IReadOnlyList<string> ExtractStrings(ReadOnlySpan<byte> data)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Null-terminated C strings
        var i = 0;
        while (i < data.Length)
        {
            if (data[i] is >= 0x20 and <= 0x7E)
            {
                var start = i;
                while (i < data.Length && data[i] is >= 0x20 and <= 0x7E)
                {
                    i++;
                }

                var len = i - start;
                if (len >= 3)
                {
                    var s = Encoding.UTF8.GetString(data.Slice(start, len));
                    if (seen.Add(s))
                    {
                        list.Add(s);
                    }
                }

                if (i < data.Length && data[i] == 0)
                {
                    i++;
                }

                continue;
            }

            i++;
        }

        // Length-prefixed (u32 LE) UTF-8 blobs
        for (var p = 0; p + 4 < data.Length; p++)
        {
            var n = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(p, 4));
            if (n is < 3 or > 512)
            {
                continue;
            }

            if (p + 4 + n > data.Length)
            {
                continue;
            }

            var slice = data.Slice(p + 4, n);
            var take = n;
            if (take > 0 && slice[take - 1] == 0)
            {
                take--;
            }

            if (take < 3)
            {
                continue;
            }

            var ok = true;
            for (var k = 0; k < take; k++)
            {
                var b = slice[k];
                if (b is < 0x20 or > 0x7E)
                {
                    if (b is not (byte)'\n' and not (byte)'\r' and not (byte)'\t')
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (!ok)
            {
                continue;
            }

            var s = Encoding.UTF8.GetString(slice[..take]).Trim();
            if (s.Length >= 3 && seen.Add(s))
            {
                list.Add(s);
            }
        }

        return list.Take(40).ToList();
    }

    public static object? TryDecode(uint opcode, string name, ReadOnlySpan<byte> data)
    {
        try
        {
            return opcode switch
            {
                0x0051 => DecodeNameQueryResponse(data),
                0x0058 => DecodeItemQueryResponse(data),
                0x005B => DecodePageText(data),
                0x005D => DecodeQuestQueryResponse(data),
                0x005F => DecodeGameObjectQueryResponse(data),
                0x0061 => DecodeCreatureQueryResponse(data),
                0x0063 => DecodeWho(data),
                0x0180 => DecodeNpcText(data),
                0x01CF => DecodeQueryTime(data),
                0x01DD => DecodePong(data),
                0x020C => DecodeUpdateAccountData(data),
                0x0284 => DecodeMailTime(data),
                0x0932 => DecodePatchItem(data),
                0x00B3 => DecodeGameObjectAnim(data),
                0x0161 => DecodeLootRelease(data),
                _ => DecodeGeneric(name, data)
            };
        }
        catch
        {
            return DecodeGeneric(name, data);
        }
    }

    private static object DecodeGeneric(string name, ReadOnlySpan<byte> data)
    {
        var fields = new Dictionary<string, object?>
        {
            ["kind"] = "generic",
            ["name"] = name,
            ["len"] = data.Length
        };
        if (data.Length >= 4)
        {
            fields["u32_0"] = BinaryPrimitives.ReadUInt32LittleEndian(data);
        }

        if (data.Length >= 8)
        {
            fields["u64_0"] = BinaryPrimitives.ReadUInt64LittleEndian(data).ToString("X16");
            fields["u32_1"] = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        }

        fields["strings"] = ExtractStrings(data);
        return fields;
    }

    private static object DecodeNameQueryResponse(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var guid = ReadPackedGuid(data, ref pos);
        if (guid == 0 && data.Length >= 8)
        {
            pos = 0;
            guid = BinaryPrimitives.ReadUInt64LittleEndian(data);
            pos = 8;
        }

        byte flag = 0;
        if (pos < data.Length)
        {
            flag = data[pos++];
        }

        string name = "", realm = "";
        int race = -1, gender = -1, classId = -1;
        if (flag != 1 && pos < data.Length)
        {
            name = ReadCString(data, ref pos);
            if (pos < data.Length)
            {
                realm = ReadCString(data, ref pos);
            }

            if (data.Length >= pos + 3)
            {
                race = data[pos++];
                gender = data[pos++];
                classId = data[pos++];
            }
        }

        return new
        {
            kind = "SMSG_NAME_QUERY_RESPONSE",
            guid = guid.ToString("X16"),
            unknownFlag = flag,
            name,
            realm,
            race,
            gender,
            classId,
            strings = ExtractStrings(data)
        };
    }

    private static object DecodeItemQueryResponse(ReadOnlySpan<byte> data)
    {
        // WotLK 3.3.5 / Ascension: itemId; if not found often length<=4 or high bit set.
        if (data.Length < 4)
        {
            return new
            {
                kind = "SMSG_ITEM_QUERY_SINGLE_RESPONSE",
                itemId = 0u,
                notFound = true,
                found = false,
                name = "",
                strings = Array.Empty<string>(),
                len = data.Length
            };
        }

        var itemIdRaw = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var notFound = (itemIdRaw & 0x80000000u) != 0 || data.Length <= 4;
        var itemId = itemIdRaw & 0x7FFFFFFFu;
        if (notFound)
        {
            return new
            {
                kind = "SMSG_ITEM_QUERY_SINGLE_RESPONSE",
                itemId,
                notFound = true,
                found = false,
                name = "",
                strings = Array.Empty<string>(),
                len = data.Length
            };
        }

        try
        {
            var tip = TryParseWotlkItemQuery(data);
            if (tip is not null)
            {
                return tip;
            }
        }
        catch
        {
            // fall through to string scrape
        }

        var strings = ExtractStrings(data);
        return new
        {
            kind = "SMSG_ITEM_QUERY_SINGLE_RESPONSE",
            itemId,
            notFound = false,
            found = true,
            name = strings.FirstOrDefault() ?? "",
            strings,
            len = data.Length
        };
    }

    /// <summary>Best-effort WotLK item query body → Wowhead-style tooltip fields.</summary>
    private static object? TryParseWotlkItemQuery(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        if (!TryReadU32(data, ref pos, out var itemIdRaw))
        {
            return null;
        }

        var itemId = itemIdRaw & 0x7FFFFFFFu;
        if (!TryReadU32(data, ref pos, out var itemClass)
            || !TryReadU32(data, ref pos, out var itemSubClass)
            || !TryReadI32(data, ref pos, out _) // sound override
            || pos >= data.Length)
        {
            return null;
        }

        var name1 = ReadCString(data, ref pos);
        var name2 = ReadCString(data, ref pos);
        var name3 = ReadCString(data, ref pos);
        var name4 = ReadCString(data, ref pos);
        if (!TryReadU32(data, ref pos, out var displayId)
            || !TryReadU32(data, ref pos, out var quality)
            || !TryReadU32(data, ref pos, out var flags)
            || !TryReadU32(data, ref pos, out var flags2)
            || !TryReadU32(data, ref pos, out var buyPrice)
            || !TryReadU32(data, ref pos, out var sellPrice)
            || !TryReadU32(data, ref pos, out var inventoryType)
            || !TryReadU32(data, ref pos, out _)
            || !TryReadU32(data, ref pos, out _)
            || !TryReadU32(data, ref pos, out var itemLevel)
            || !TryReadU32(data, ref pos, out var requiredLevel))
        {
            return null;
        }

        // required skill … container_slots = 10 u32
        for (var i = 0; i < 10; i++)
        {
            if (!TryReadU32(data, ref pos, out _))
            {
                return null;
            }
        }

        if (!TryReadU32(data, ref pos, out var amountOfStats) || amountOfStats > 32)
        {
            return null;
        }

        var stats = new List<object>((int)amountOfStats);
        for (var i = 0; i < amountOfStats; i++)
        {
            if (!TryReadI32(data, ref pos, out var statType) || !TryReadI32(data, ref pos, out var statValue))
            {
                return null;
            }

            if (statType != 0 && statValue != 0)
            {
                stats.Add(new { type = statType, value = statValue, name = ItemStatName(statType) });
            }
        }

        if (!TryReadU32(data, ref pos, out _) || !TryReadU32(data, ref pos, out _)) // scaling
        {
            return null;
        }

        var damages = new List<object>(2);
        for (var i = 0; i < 2; i++)
        {
            if (!TryReadF32(data, ref pos, out var dmin)
                || !TryReadF32(data, ref pos, out var dmax)
                || !TryReadU32(data, ref pos, out var school))
            {
                return null;
            }

            if (dmax > 0)
            {
                damages.Add(new { min = dmin, max = dmax, school });
            }
        }

        if (!TryReadI32(data, ref pos, out var armor))
        {
            return null;
        }

        // 6 resistances
        for (var i = 0; i < 6; i++)
        {
            if (!TryReadI32(data, ref pos, out _))
            {
                return null;
            }
        }

        if (!TryReadU32(data, ref pos, out var delay)
            || !TryReadU32(data, ref pos, out _)
            || !TryReadF32(data, ref pos, out _))
        {
            return null;
        }

        // 5 item spells × 6 u32
        for (var i = 0; i < 5 * 6; i++)
        {
            if (!TryReadU32(data, ref pos, out _))
            {
                return null;
            }
        }

        if (!TryReadU32(data, ref pos, out var bonding))
        {
            return null;
        }

        var description = ReadCString(data, ref pos);
        var name = !string.IsNullOrWhiteSpace(name1) ? name1
            : !string.IsNullOrWhiteSpace(name2) ? name2
            : !string.IsNullOrWhiteSpace(name3) ? name3
            : name4;
        var strings = new List<string>();
        foreach (var s in new[] { name1, name2, name3, name4, description })
        {
            if (!string.IsNullOrWhiteSpace(s))
            {
                strings.Add(s);
            }
        }

        return new
        {
            kind = "SMSG_ITEM_QUERY_SINGLE_RESPONSE",
            itemId,
            notFound = false,
            found = true,
            name,
            description,
            quality = (int)quality,
            qualityName = ItemQualityName((int)quality),
            qualityColor = ItemQualityColor((int)quality),
            itemClass = (int)itemClass,
            itemSubClass = (int)itemSubClass,
            displayId,
            flags,
            flags2,
            buyPrice,
            sellPrice,
            inventoryType = (int)inventoryType,
            inventoryTypeName = InventoryTypeName((int)inventoryType),
            itemLevel = (int)itemLevel,
            requiredLevel = (int)requiredLevel,
            bonding = (int)bonding,
            bondingName = BondingName((int)bonding),
            armor,
            delay = delay > 0 ? delay / 1000.0 : 0,
            stats,
            damages,
            strings,
            len = data.Length
        };
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

    private static bool TryReadI32(ReadOnlySpan<byte> data, ref int pos, out int value)
    {
        if (pos + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4));
        pos += 4;
        return true;
    }

    private static bool TryReadF32(ReadOnlySpan<byte> data, ref int pos, out float value)
    {
        if (pos + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(pos, 4));
        pos += 4;
        return true;
    }

    private static string ItemQualityName(int q) => q switch
    {
        0 => "Poor",
        1 => "Common",
        2 => "Uncommon",
        3 => "Rare",
        4 => "Epic",
        5 => "Legendary",
        6 => "Artifact",
        7 => "Heirloom",
        _ => "Unknown"
    };

    private static string ItemQualityColor(int q) => q switch
    {
        0 => "#9d9d9d",
        1 => "#ffffff",
        2 => "#1eff00",
        3 => "#0070dd",
        4 => "#a335ee",
        5 => "#ff8000",
        6 => "#e6cc80",
        7 => "#00ccff",
        _ => "#ffffff"
    };

    private static string BondingName(int b) => b switch
    {
        1 => "Binds when picked up",
        2 => "Binds when equipped",
        3 => "Binds when used",
        4 => "Quest Item",
        _ => ""
    };

    private static string InventoryTypeName(int t) => t switch
    {
        1 => "Head",
        2 => "Neck",
        3 => "Shoulder",
        4 => "Shirt",
        5 => "Chest",
        6 => "Waist",
        7 => "Legs",
        8 => "Feet",
        9 => "Wrist",
        10 => "Hands",
        11 => "Finger",
        12 => "Trinket",
        13 => "One-Hand",
        14 => "Shield",
        15 => "Ranged",
        16 => "Back",
        17 => "Two-Hand",
        18 => "Bag",
        19 => "Tabard",
        20 => "Robe",
        21 => "Main Hand",
        22 => "Off Hand",
        23 => "Held In Off-hand",
        24 => "Ammo",
        25 => "Thrown",
        26 => "Ranged",
        28 => "Relic",
        _ => ""
    };

    private static string ItemStatName(int type) => type switch
    {
        0 => "Mana",
        1 => "Health",
        3 => "Agility",
        4 => "Strength",
        5 => "Intellect",
        6 => "Spirit",
        7 => "Stamina",
        12 => "Defense Rating",
        13 => "Dodge Rating",
        14 => "Parry Rating",
        15 => "Block Rating",
        16 => "Hit Melee Rating",
        17 => "Hit Ranged Rating",
        18 => "Hit Spell Rating",
        19 => "Crit Melee Rating",
        20 => "Crit Ranged Rating",
        21 => "Crit Spell Rating",
        28 => "Haste Melee Rating",
        29 => "Haste Ranged Rating",
        30 => "Haste Spell Rating",
        31 => "Hit Rating",
        32 => "Crit Rating",
        35 => "Resilience Rating",
        36 => "Haste Rating",
        37 => "Expertise Rating",
        38 => "Attack Power",
        39 => "Ranged Attack Power",
        41 => "Spell Healing Done",
        42 => "Spell Damage Done",
        43 => "Mana Regeneration",
        44 => "Armor Penetration Rating",
        45 => "Spell Power",
        46 => "Health Regen",
        47 => "Spell Penetration",
        48 => "Block Value",
        _ => "Stat " + type
    };

    private static object DecodeCreatureQueryResponse(ReadOnlySpan<byte> data)
    {
        uint entry = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
        // entry with 0x80000000 = not found
        var found = (entry & 0x80000000) == 0 && data.Length > 4;
        var strings = ExtractStrings(data.Length > 4 ? data[4..] : data);
        return new
        {
            kind = "SMSG_CREATURE_QUERY_RESPONSE",
            entry = entry & 0x7FFFFFFF,
            found,
            names = strings.Take(4).ToList(),
            strings,
            len = data.Length
        };
    }

    private static object DecodeGameObjectQueryResponse(ReadOnlySpan<byte> data)
    {
        uint entry = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
        var found = (entry & 0x80000000) == 0 && data.Length > 4;
        var strings = ExtractStrings(data.Length > 4 ? data[4..] : data);
        return new
        {
            kind = "SMSG_GAMEOBJECT_QUERY_RESPONSE",
            entry = entry & 0x7FFFFFFF,
            found,
            name = strings.FirstOrDefault() ?? "",
            strings,
            len = data.Length
        };
    }

    private static object DecodeQuestQueryResponse(ReadOnlySpan<byte> data)
    {
        uint questId = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
        var strings = ExtractStrings(data);
        return new
        {
            kind = "SMSG_QUEST_QUERY_RESPONSE",
            questId,
            title = strings.FirstOrDefault() ?? "",
            strings,
            len = data.Length
        };
    }

    private static object DecodePageText(ReadOnlySpan<byte> data)
    {
        uint pageId = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
        var strings = ExtractStrings(data);
        return new { kind = "SMSG_PAGE_TEXT_QUERY_RESPONSE", pageId, text = strings.FirstOrDefault() ?? "", strings };
    }

    private static object DecodeNpcText(ReadOnlySpan<byte> data)
    {
        uint textId = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
        return new
        {
            kind = "SMSG_NPC_TEXT_UPDATE",
            textId,
            strings = ExtractStrings(data),
            len = data.Length
        };
    }

    private static object DecodeWho(ReadOnlySpan<byte> data)
    {
        uint total = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
        uint count = data.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) : 0;
        return new
        {
            kind = "SMSG_WHO",
            total,
            count,
            strings = ExtractStrings(data),
            len = data.Length
        };
    }

    private static object DecodeQueryTime(ReadOnlySpan<byte> data) => new
    {
        kind = "SMSG_QUERY_TIME_RESPONSE",
        time = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0u,
        time2 = data.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) : 0u
    };

    private static object DecodePong(ReadOnlySpan<byte> data) => new
    {
        kind = "SMSG_PONG",
        sequence = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0u
    };

    private static object DecodeUpdateAccountData(ReadOnlySpan<byte> data)
    {
        uint type = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
        uint time = data.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) : 0;
        uint size = data.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)) : 0;
        return new
        {
            kind = "SMSG_UPDATE_ACCOUNT_DATA",
            type,
            time,
            decompressedSize = size,
            strings = ExtractStrings(data),
            len = data.Length
        };
    }

    private static object DecodeMailTime(ReadOnlySpan<byte> data) => new
    {
        kind = "MSG_QUERY_NEXT_MAIL_TIME",
        u32_0 = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0u,
        u32_1 = data.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) : 0u,
        hex = Convert.ToHexString(data)
    };

    private static object DecodePatchItem(ReadOnlySpan<byte> data) => new
    {
        kind = "SMSG_PATCH_ITEM",
        note = "Ascension-specific item patch blob",
        u32_0 = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0u,
        strings = ExtractStrings(data),
        len = data.Length,
        hex = Convert.ToHexString(data)
    };

    private static object DecodeGameObjectAnim(ReadOnlySpan<byte> data) => new
    {
        kind = "SMSG_GAMEOBJECT_CUSTOM_ANIM",
        guid = data.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(data).ToString("X16") : "",
        anim = data.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)) : 0u
    };

    private static object DecodeLootRelease(ReadOnlySpan<byte> data) => new
    {
        kind = "SMSG_LOOT_RELEASE_RESPONSE",
        guid = data.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(data).ToString("X16") : "",
        unk = data.Length >= 9 ? data[8] : (byte)0
    };

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

        var value = Encoding.UTF8.GetString(data.Slice(start, offset - start));
        if (offset < data.Length)
        {
            offset++;
        }

        return value;
    }
}
