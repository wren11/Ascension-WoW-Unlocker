using System.Text.Json;

namespace AscensionNetTool;

/// <summary>
/// Persisted SMSG opcode ignore list. Default: all Ascension SMSG_PATCH_* spam
/// (spell/item/DBC patches) so sniff + tools stay usable. Chat opcodes are never ignored.
/// </summary>
static class OpcodeFilterStore
{
    /// <summary>Bump when default flood-ignore set changes; migrates older files once.</summary>
    public const int CurrentSeedVersion = 2;

    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    static string Path => System.IO.Path.Combine(Paths.AppRoot, "Config", "opcode-ignore.json");

    public sealed class Data
    {
        public bool ChatCapture { get; set; } = true;
        public int SeedVersion { get; set; }
        public List<uint> IgnoredOpcodes { get; set; } = new();
    }

    static Data _data = new();

    public static Data Current
    {
        get { lock (Gate) return Clone(_data); }
    }

    public static HashSet<uint> IgnoredSet
    {
        get { lock (Gate) return new HashSet<uint>(_data.IgnoredOpcodes); }
    }

    public static bool ChatCapture
    {
        get { lock (Gate) return _data.ChatCapture; }
    }

    static Data Clone(Data d) => new()
    {
        ChatCapture = d.ChatCapture,
        SeedVersion = d.SeedVersion,
        IgnoredOpcodes = d.IgnoredOpcodes.ToList(),
    };

    public static void LoadOrSeed()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path))
                {
                    var json = File.ReadAllText(Path);
                    var d = JsonSerializer.Deserialize<Data>(json, JsonOpts);
                    if (d is not null)
                    {
                        _data = d;
                        _data.IgnoredOpcodes ??= new();
                        if (_data.SeedVersion < CurrentSeedVersion)
                        {
                            foreach (var op in BuildDefaultPatchIgnores())
                            {
                                if (!_data.IgnoredOpcodes.Contains(op))
                                    _data.IgnoredOpcodes.Add(op);
                            }
                            _data.SeedVersion = CurrentSeedVersion;
                            Sanitize();
                            SaveUnlocked();
                        }
                        else
                        {
                            Sanitize();
                        }
                        return;
                    }
                }
            }
            catch { }

            _data = new Data
            {
                ChatCapture = true,
                SeedVersion = CurrentSeedVersion,
                IgnoredOpcodes = BuildDefaultPatchIgnores(),
            };
            SaveUnlocked();
        }
    }

    static void Sanitize()
    {
        // Never ignore chat / notification — required for chat DB.
        // Do NOT re-add defaults here — that would fight RemoveIgnore.
        foreach (var keep in ChatDecoder.AlwaysCaptureOpcodes)
            _data.IgnoredOpcodes.Remove(keep);
        _data.IgnoredOpcodes = _data.IgnoredOpcodes.Distinct().OrderBy(x => x).ToList();
    }

    public static List<uint> BuildDefaultPatchIgnores()
    {
        var list = new List<uint>();
        foreach (var (op, name) in Opcodes.All)
        {
            if (name.StartsWith("SMSG_PATCH_", StringComparison.OrdinalIgnoreCase))
                list.Add(op);
        }
        // Verified against Opcodes.cs — high-frequency city flood (tooling noise only).
        uint[] extra =
        {
            0x00A9, // SMSG_UPDATE_OBJECT
            0x00AA, // SMSG_DESTROY_OBJECT
            0x00DD, // SMSG_MONSTER_MOVE
            0x00EE, // MSG_MOVE_HEARTBEAT
            0x0131, // SMSG_SPELL_START
            0x0132, // SMSG_SPELL_GO
            0x013C, // SMSG_AI_REACTION
            0x014A, // SMSG_ATTACKERSTATEUPDATE
            0x01F6, // SMSG_COMPRESSED_UPDATE_OBJECT
            0x024E, // SMSG_PERIODICAURALOG
            0x02AE, // SMSG_MONSTER_MOVE_TRANSPORT
            0x0496, // SMSG_AURA_UPDATE
            0x0495, // SMSG_AURA_UPDATE_ALL
        };
        foreach (var op in extra)
        {
            if (!ChatDecoder.AlwaysCaptureOpcodes.Contains(op))
                list.Add(op);
        }
        return list.Distinct().OrderBy(x => x).ToList();
    }

    public static void SetIgnored(IEnumerable<uint> opcodes, bool? chatCapture = null)
    {
        lock (Gate)
        {
            _data.IgnoredOpcodes = opcodes.Select(Opcodes.Normalize).Distinct().OrderBy(x => x).ToList();
            if (chatCapture is bool c) _data.ChatCapture = c;
            _data.SeedVersion = CurrentSeedVersion;
            Sanitize();
            SaveUnlocked();
        }
    }

    public static void AddIgnore(uint opcode)
    {
        lock (Gate)
        {
            opcode = Opcodes.Normalize(opcode);
            if (ChatDecoder.AlwaysCaptureOpcodes.Contains(opcode)) return;
            if (!_data.IgnoredOpcodes.Contains(opcode))
                _data.IgnoredOpcodes.Add(opcode);
            _data.IgnoredOpcodes.Sort();
            SaveUnlocked();
        }
    }

    public static void RemoveIgnore(uint opcode)
    {
        lock (Gate)
        {
            _data.IgnoredOpcodes.Remove(Opcodes.Normalize(opcode));
            SaveUnlocked();
        }
    }

    public static void ResetDefaults()
    {
        lock (Gate)
        {
            _data = new Data
            {
                ChatCapture = true,
                SeedVersion = CurrentSeedVersion,
                IgnoredOpcodes = BuildDefaultPatchIgnores(),
            };
            SaveUnlocked();
        }
    }

    static void SaveUnlocked()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(_data, JsonOpts));
        }
        catch { }
    }

    public static object Dto()
    {
        lock (Gate)
        {
            return new
            {
                chatCapture = _data.ChatCapture,
                seedVersion = _data.SeedVersion,
                count = _data.IgnoredOpcodes.Count,
                ignored = _data.IgnoredOpcodes.Select(op => new
                {
                    opcode = op,
                    hex = $"0x{op:X4}",
                    name = Opcodes.Name(op),
                }).ToList(),
                path = Path,
            };
        }
    }
}
