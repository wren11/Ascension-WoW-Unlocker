using System.Text;

namespace AscensionNetTool;

/// <summary>
/// Character-create lab: client unlocks, Lua CreateCharacter bot, login/enum packet experiments.
/// Owned Ascension client only — server may still reject.
/// </summary>
static class CharCreateLab
{
    public static readonly (int Race, int Class, string Label)[] Presets =
    {
        (1, 1, "Human Warrior"),
        (1, 8, "Human Mage"),
        (2, 1, "Orc Warrior"),
        (4, 11, "Night Elf Druid"),
        (5, 9, "Undead Warlock"),
        (8, 4, "Troll Rogue"),
        (10, 2, "Blood Elf Paladin"),
        (11, 7, "Draenei Shaman"),
        // Invalid / locked combos (need unlock) — client may allow, server may reject
        (1, 11, "Human Druid (invalid)"),
        (1, 7, "Human Shaman (invalid)"),
        (2, 2, "Orc Paladin (invalid)"),
        (4, 7, "NE Shaman (invalid)"),
        (10, 1, "BE Warrior (era-lock?)"),
        (11, 4, "Draenei Rogue (invalid)"),
        (5, 11, "Undead Druid (invalid)"),
        (3, 8, "Dwarf Mage (invalid)"),
    };

    /// <summary>Named experiment payloads for the Char Create lab runner.</summary>
    public static readonly (string Id, string Name, int Race, int Class, int Sex, string Note)[] Experiments =
    {
        ("ascii", "LabAscii", 1, 1, 0, "plain ASCII"),
        ("cyrillic", "ТестИмя", 1, 8, 0, "Cyrillic only"),
        ("mixed", "AbяCd", 1, 1, 0, "mixed scripts"),
        ("symbol", "A·B", 5, 9, 0, "middle-dot symbol"),
        ("quotespace", "Ab Cd", 1, 1, 0, "space (usually illegal)"),
        ("short", "Xy", 1, 1, 0, "too short?"),
        ("long", "Abcdefghijklmnop", 1, 1, 0, "long name"),
        ("digits", "Name9", 1, 1, 0, "trailing digit"),
        ("invalid_combo", "BadCombo", 1, 11, 0, "Human Druid"),
        ("undead_druid", "DeadTree", 5, 11, 1, "Undead Druid"),
        ("orc_paladin", "HolyOrc", 2, 2, 0, "Orc Paladin"),
        // OOB race/class via SetSelected* FATAL #134 — exercise unlock path with max valid ids only.
        ("high_ids", "HiIds", 11, 11, 0, "max stock race/class (OOB clamped)"),
    };

    static readonly char[] NameAlphabet = "abcdefghijklmnopqrstuvwxyz".ToCharArray();

    public static string RandomName(int minLen = 5, int maxLen = 10, bool weird = false)
    {
        var rng = Random.Shared;
        int len = rng.Next(minLen, maxLen + 1);
        var sb = new StringBuilder(len + 4);
        sb.Append(char.ToUpperInvariant(NameAlphabet[rng.Next(NameAlphabet.Length)]));
        for (int i = 1; i < len; i++)
            sb.Append(NameAlphabet[rng.Next(NameAlphabet.Length)]);
        if (weird)
        {
            // Requires ValidateName unlock — mixed script / symbols for lab tests.
            string[] extras = { "я", "ж", "・", "ß", "Æ", "之", "מ", "د" };
            if (rng.Next(2) == 0)
                sb.Append(extras[rng.Next(extras.Length)]);
            else
                sb.Insert(rng.Next(1, sb.Length), extras[rng.Next(extras.Length)]);
        }
        return sb.ToString();
    }

    public static string UnlockScript(bool on) =>
        on
            ? "local a,b,c=GmCharCreateUnlock and GmCharCreateUnlock(1); "
              + "print('|cff2ecc71[CharCreate]|r unlock ON',tostring(a),tostring(b),tostring(c))"
            : "local a=GmCharCreateUnlock and GmCharCreateUnlock('off'); "
              + "print('|cff2ecc71[CharCreate]|r unlock OFF',tostring(a))";

    public static string StatusScript() =>
        "local a,b,c=GmCharCreateUnlock and GmCharCreateUnlock(); "
        + "print('|cff2ecc71[CharCreate]|r unlock=',tostring(a),'vn=',tostring(b),'rc=',tostring(c)); "
        + "if GetNumCharacters then print('chars=',tostring(GetNumCharacters())) end; "
        + "if GetSelectedRace then print('race=',tostring(GetSelectedRace()),'class=',tostring(GetSelectedClass()),'sex=',tostring(GetSelectedSex())) end; "
        + "local f=CharacterCreateFrame or CharacterCreate; "
        + "print('createFrame=',tostring(f~=nil), f and f.IsShown and tostring(f:IsShown()) or '?')";

    public static string SetupAppearanceScript(int raceIndex, int classIndex, int sexIndex, bool randomize)
    {
        // Glue SetSelectedRace(>11) → FATAL #134 on CHARACTER_CREATE_PREVIEW_CHANGED.
        int raceUi = raceIndex <= 0 ? 0 : Math.Clamp(raceIndex, 1, 11);
        int classUi = classIndex <= 0 ? 0 : Math.Clamp(classIndex, 1, 31);
        int sexUi = Math.Clamp(sexIndex, 0, 1);
        var sb = new StringBuilder();
        sb.AppendLine("pcall(function()");
        // HARD GATE: SetSelectedClass with NULL CharCreate obj → AV at RVA 0xE20E5 ([ecx+0x1C]).
        // Only poke UI when Create frame is actually shown.
        sb.AppendLine("  local f=CharacterCreateFrame or CharacterCreate");
        sb.AppendLine("  if not f or (f.IsShown and not f:IsShown()) then");
        sb.AppendLine("    print('|cffff5555[CharCreate]|r SKIP setup — open Create New Character first')");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        if (raceUi > 0)
            sb.AppendLine($"  if SetSelectedRace then SetSelectedRace({raceUi}) end");
        if (classUi > 0)
            sb.AppendLine($"  if SetSelectedClass then SetSelectedClass({classUi}) end");
        sb.AppendLine($"  if SetSelectedSex then SetSelectedSex({sexUi}) end");
        if (randomize)
            sb.AppendLine("  if RandomizeCharCustomization then RandomizeCharCustomization() end");
        // Memory poke uses same UI-safe ids (ExtProxy also clamps live object).
        if (raceUi > 0 || classUi > 0)
            sb.AppendLine($"  if GmCharCreateForce then GmCharCreateForce({Math.Max(1, raceUi)},{Math.Max(1, classUi)},{sexUi}) end");
        sb.AppendLine("end)");
        return sb.ToString();
    }

    public static string CreateScript(string name, int raceIndex, int classIndex, int sexIndex, bool randomize, bool unlockFirst)
    {
        name = SanitizeLuaString(name);
        var sb = new StringBuilder();
        if (unlockFirst)
            sb.AppendLine(UnlockScript(true));
        sb.Append(SetupAppearanceScript(raceIndex, classIndex, sexIndex, randomize));
        sb.AppendLine("pcall(function()");
        sb.AppendLine("  if not CreateCharacter then print('|cffff5555[CharCreate]|r CreateCharacter missing') return end");
        sb.AppendLine("  local f=CharacterCreateFrame or CharacterCreate");
        sb.AppendLine("  if not f or (f.IsShown and not f:IsShown()) then");
        sb.AppendLine("    print('|cffff5555[CharCreate]|r BLOCKED — click Create New Character, then retry')");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine($"  print('|cff2ecc71[CharCreate]|r CreateCharacter(\"{name}\") race={raceIndex} class={classIndex}')");
        sb.AppendLine($"  CreateCharacter(\"{name}\")");
        sb.AppendLine("end)");
        return sb.ToString();
    }

    public static string ChaosScript() =>
        "pcall(function() "
        + "if not GmCharCreateChaos then print('|cffff5555[CharCreate]|r GmCharCreateChaos missing') return end; "
        + "local r,c,s,sk,f,h=GmCharCreateChaos(); "
        + "print(string.format('|cff2ecc71[CharCreate]|r CHAOS race=%s class=%s sex=%s skin=%s face=%s hair=%s', "
        + "tostring(r),tostring(c),tostring(s),tostring(sk),tostring(f),tostring(h))); "
        + "local fr=CharacterCreateFrame or CharacterCreate; "
        + "if not fr or (fr.IsShown and not fr:IsShown()) then "
        + "  print('|cffffaa00[CharCreate]|r open Create New Character then CreateCharacter') return end; "
        + "local n='X'..tostring(math.random(10000,99999)); "
        + "if CreateCharacter then CreateCharacter(n); print('|cff2ecc71[CharCreate]|r CreateCharacter',n) end "
        + "end)";

    /// <summary>Force a custom/out-of-range class id (Ascension custom classes often > 11).</summary>
    public static string ForceClassScript(int race, int classId, int sex, int skin, int face, int hairStyle, int hairColor, int facial) =>
        UnlockScript(true) + "\n" +
        $"pcall(function() "
        + $"local r=math.max(1,math.min(11,{race})); local c=math.max(1,math.min(31,{classId})); "
        + $"local s=math.max(0,math.min(1,{sex})); "
        + $"if SetSelectedRace then SetSelectedRace(r) end; "
        + $"if SetSelectedClass then SetSelectedClass(c) end; "
        + $"if SetSelectedSex then SetSelectedSex(s) end; "
        + $"if GmCharCreateForce then GmCharCreateForce(r,c,s,{Math.Clamp(skin, 0, 12)},{Math.Clamp(face, 0, 12)},{Math.Clamp(hairStyle, 0, 12)},{Math.Clamp(hairColor, 0, 12)},{Math.Clamp(facial, 0, 12)}) end; "
        + $"print('|cff2ecc71[CharCreate]|r Force r='..r..' c='..c..' (requested {race}/{classId})') end)";

    /// <summary>Run a batch of named experiments (invalid names + combos + chaos).</summary>
    public static string ExperimentBatchScript(bool unlockFirst)
    {
        var sb = new StringBuilder();
        if (unlockFirst)
            sb.AppendLine(UnlockScript(true));
        sb.AppendLine("print('|cff2ecc71[CharCreate]|r === experiment batch ===')");
        sb.AppendLine("if GmCharCreateChaos then pcall(GmCharCreateChaos) end");
        foreach (var e in Experiments)
        {
            if (e.Race <= 0 || e.Class <= 0)
                continue;
            string n = SanitizeLuaString(e.Name);
            sb.Append(SetupAppearanceScript(e.Race, e.Class, e.Sex, randomize: true));
            sb.AppendLine("pcall(function()");
            sb.AppendLine($"  print('|cff2ecc71[CharCreate]|r EXP {e.Id}: {e.Note}')");
            sb.AppendLine($"  if CreateCharacter then CreateCharacter(\"{n}\") end");
            sb.AppendLine("end)");
        }
        // Appearance matrix — ExtProxy clamps live UI; keep race/class preview-safe.
        int[][] looks = {
            new[] { 1, 1, 0, 12, 12, 12, 12, 12 },
            new[] { 1, 15, 0, 0, 0, 8, 8, 8 },
            new[] { 10, 11, 1, 7, 7, 7, 7, 7 },
            new[] { 11, 11, 0, 8, 4, 4, 4, 4 },
        };
        foreach (var a in looks)
        {
            sb.AppendLine($"pcall(function() if GmCharCreateForce then GmCharCreateForce({a[0]},{a[1]},{a[2]},{a[3]},{a[4]},{a[5]},{a[6]},{a[7]}) end end)");
            sb.AppendLine("pcall(function() local f=CharacterCreateFrame or CharacterCreate; "
                + "if f and f.IsShown and f:IsShown() and CreateCharacter then "
                + $"CreateCharacter(\"Z{a[0]}c{a[1]}\") end end)");
        }
        sb.AppendLine("print('|cff2ecc71[CharCreate]|r === batch queued (watch dialogs/server) ===')");
        return sb.ToString();
    }

    public static string RandomNameLuaScript() =>
        "pcall(function() local n=GetRandomName and GetRandomName(); "
        + "print('|cff2ecc71[CharCreate]|r GetRandomName=',tostring(n)) end)";

    public static string EnumProbeScript() =>
        "pcall(function() "
        + "local n=GetNumCharacters and GetNumCharacters() or -1; "
        + "print('|cff2ecc71[CharCreate]|r GetNumCharacters=',n); "
        + "if GetCharacterInfo and n and n>0 then "
        + "  for i=1,math.min(n,16) do "
        + "    local name,race,class,level,zone,sex=GetCharacterInfo(i); "
        + "    print(string.format('  [%d] %s race=%s class=%s lvl=%s', i, tostring(name), tostring(race), tostring(class), tostring(level))) "
        + "  end "
        + "end end)";

    public static string SelectCharacterScript(int index) =>
        $"pcall(function() if SelectCharacter then SelectCharacter({index}); "
        + $"print('|cff2ecc71[CharCreate]|r SelectCharacter({index})') end end)";

    public static string EnterWorldScript(int index) =>
        $"pcall(function() "
        + $"if SelectCharacter then SelectCharacter({index}) end; "
        + $"if EnterWorld then EnterWorld(); print('|cff2ecc71[CharCreate]|r EnterWorld') "
        + $"elseif CharSelectEnterWorld then CharSelectEnterWorld(); print('|cff2ecc71[CharCreate]|r CharSelectEnterWorld') "
        + $"else print('|cffff5555[CharCreate]|r no EnterWorld API') end end)";

    /// <summary>WotLK CMSG_PLAYER_LOGIN: opcode u32 LE + guid u64 LE (Ascension may remap — prefer sniffed).</summary>
    public static byte[] BuildPlayerLoginPacket(ulong guid, uint opcode = 0x003D)
    {
        var pkt = new byte[12];
        BitConverter.TryWriteBytes(pkt.AsSpan(0, 4), opcode);
        BitConverter.TryWriteBytes(pkt.AsSpan(4, 8), guid);
        return pkt;
    }

    public static byte[]? BuildPlayerLoginFromHexGuid(string guidHex, uint opcode = 0x003D)
    {
        guidHex = (guidHex ?? "").Trim();
        if (guidHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) guidHex = guidHex[2..];
        if (!ulong.TryParse(guidHex, System.Globalization.NumberStyles.HexNumber, null, out var g) || g == 0)
            return null;
        return BuildPlayerLoginPacket(g, opcode);
    }

    /// <summary>CMSG_CHAR_ENUM empty body (stock 0x37) — Ascension may remap; prefer sniffed.</summary>
    public static byte[] BuildCharEnumPacket(uint opcode = 0x0037)
    {
        var pkt = new byte[4];
        BitConverter.TryWriteBytes(pkt.AsSpan(0, 4), opcode);
        return pkt;
    }

    static string SanitizeLuaString(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) s = RandomName();
        s = s.Trim();
        if (s.Length > 40) s = s[..40];
        // Keep unicode; only strip Lua string breakers
        return s.Replace("\\", "").Replace("\"", "").Replace("\n", "").Replace("\r", "");
    }
}
