using System.Text.Json;

namespace AscensionNetTool;

static class SettingsStore
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public const string DefaultAscensionExe = "";
    public const string AscensionBrowseHint = DefaultAscensionExe;

    /// <summary>Portable settings beside GMToolBox.exe; falls back to LocalAppData.</summary>
    public static string SettingsPath
    {
        get
        {
            string portable = Path.Combine(Paths.AppRoot, "Config", "settings.json");
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GmToolbox", "settings.json");
            if (File.Exists(portable))
                return portable;
            if (Directory.Exists(Paths.AppRoot) && Paths.IsDistLayout)
                return portable;
            if (File.Exists(legacy))
                return legacy;
            return portable;
        }
    }

    public sealed class InstanceSettings
    {
        public int Id { get; set; }
        public string? Label { get; set; }
        public string? AccountHint { get; set; }
        public string? CharacterHint { get; set; }
    }

    public sealed class Data
    {
        /// <summary>Full path to Ascension.exe (stock client).</summary>
        public string AscensionExe { get; set; } = "";

        /// <summary>Folder containing %04u.mmap mesh headers (e.g. ...\mmaps\maps).</summary>
        public string MapsDir { get; set; } = "";

        /// <summary>Folder containing %04u%02u%02u.mmtile tiles (e.g. ...\mmaps\mmtiles).</summary>
        public string MmapsDir { get; set; } = "";

        public bool AutoSyncAddons { get; set; } = true;
        /// <summary>Host↔client shared-world sync period (ms). Default 300.</summary>
        public int SharedSyncMs { get; set; } = 500;
        public int PollMs { get; set; } = 400;
        public int ObjectsIntervalMs { get; set; } = 500;
        public int StatusIntervalMs { get; set; } = 1000;
        public int LogTailMaxLines { get; set; } = 20;

        /// <summary>When true, extract printable strings from every drained packet (CPU-heavy).</summary>
        public bool LogPacketStrings { get; set; }

        /// <summary>When true, WebSocket packet events include full hex (default: metadata only).</summary>
        public bool BroadcastPacketHex { get; set; }

        /// <summary>Write live chat into Adaptive LLM corpus (Config\chat-corpus).</summary>
        public bool CorpusEnabled { get; set; } = false;

        /// <summary>POST each chat line to the Adaptive LLM /api/learn for real-time fine-tuning.
        /// Prefer scripts/live_train_chat.py for controlled online learning; keep this off in cities.</summary>
        public bool CorpusOnlineLearn { get; set; }

        /// <summary>Corpus root directory (JSONL knowledge base). Empty = Config\chat-corpus under app root.</summary>
        public string TrainingDataDir { get; set; } = "";

        /// <summary>Adaptive LLM learn endpoint.</summary>
        public string CorpusLearnApi { get; set; } = @"http://127.0.0.1:8000/api/learn";

        /// <summary>Auto-reply in Newcomers via Adaptive LLM + RunLua SendChatMessage.</summary>
        public bool NewcomersBotEnabled { get; set; } = false;

        /// <summary>Channel name to watch/reply on (default Newcomers).</summary>
        public string NewcomersBotChannel { get; set; } = "Newcomers";

        /// <summary>Adaptive LLM /api/chat endpoint for Newcomers replies.</summary>
        public string NewcomersBotChatApi { get; set; } = @"http://127.0.0.1:8000/api/chat";

        /// <summary>Minimum ms between bot replies (global).</summary>
        public int NewcomersBotMinIntervalMs { get; set; } = 10000;

        /// <summary>Max tokens requested from the LLM for a reply.</summary>
        public int NewcomersBotMaxTokens { get; set; } = 64;

        /// <summary>When false, only reply to questions / help-ish lines (and hellos).</summary>
        public bool NewcomersBotReplyToAll { get; set; }

        /// <summary>Prefix replies with [AI] so players know it's the helper bot.</summary>
        public bool NewcomersBotTagPrefix { get; set; } = true;

        /// <summary>Persona injected into the chat prompt.</summary>
        public string NewcomersBotPersona { get; set; } =
            "You are a friendly Ascension WoW helper in the Newcomers channel. Give short helpful replies (1 sentence). Be welcoming. No quotes, no roleplay prefixes, no markdown.";

        /// <summary>Desired concurrent game clients (default 2).</summary>
        public int InstanceCount { get; set; } = 1;

        /// <summary>Optional per-instance hints (account/character labels).</summary>
        public List<InstanceSettings> Instances { get; set; } = new();

        /// <summary>Dark Canva glass theme (default true).</summary>
        public bool DarkMode { get; set; } = true;

        /// <summary>Never-stop: subscribe to InstanceDied and recover.</summary>
        public bool WatchdogEnabled { get; set; } = false;

        /// <summary>Relaunch Ascension process when PID dies.</summary>
        public bool WatchdogAutoRelaunch { get; set; } = true;

        /// <summary>Replay sniffed CMSG_PLAYER_LOGIN after relaunch or world DC.</summary>
        public bool WatchdogAutoRelog { get; set; } = true;

        /// <summary>Re-enable previously running bots via Lua after world.</summary>
        public bool WatchdogRestoreBots { get; set; } = true;

        /// <summary>Optional shared account for GlueLogin recovery.</summary>
        public string WatchdogAccount { get; set; } = "";

        /// <summary>Optional shared password for GlueLogin recovery (stored locally).</summary>
        public string WatchdogPassword { get; set; } = "";

        /// <summary>Chat SQLite retention days (0 = no prune).</summary>
        public int ChatRetentionDays { get; set; } = 14;

        /// <summary>Play featured Suno ambient on launch / toggle.</summary>
        public bool AmbientAudio { get; set; }

        public bool AmbientMuted { get; set; } = true;
        public int AmbientVolume { get; set; } = 35;

        /// <summary>Obsolete — network auth only. Kept for settings JSON compatibility.</summary>
        public string LicensePath { get; set; } = "";

        /// <summary>Obsolete — network auth only.</summary>
        public string LicensePassword { get; set; } = "";

        /// <summary>SoftRealm / store base URL for Discord device login.</summary>
        public string SoftRealmUrl { get; set; } = "";

        /// <summary>Derived live/client directory (parent of Ascension.exe).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string LiveDir
        {
            get
            {
                if (string.IsNullOrWhiteSpace(AscensionExe))
                    return "";
                string? dir = Path.GetDirectoryName(AscensionExe);
                return string.IsNullOrEmpty(dir) ? "" : dir.TrimEnd('\\', '/');
            }
        }
    }

    public static Data Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            string envExe = Environment.GetEnvironmentVariable("GMTOOLBOX_ASCENSION") ?? "";
            string envLive = Environment.GetEnvironmentVariable("GMTOOLBOX_LIVE") ?? "";
            string envMaps = Environment.GetEnvironmentVariable("GMTOOLBOX_MAPS") ?? "";
            string envMmaps = Environment.GetEnvironmentVariable("GMTOOLBOX_MMAPS") ?? "";

            if (!string.IsNullOrWhiteSpace(envExe) && File.Exists(envExe.Trim()))
            {
                Current = new Data { AscensionExe = envExe.Trim() };
            }
            else if (!string.IsNullOrWhiteSpace(envLive))
            {
                string live = envLive.TrimEnd('\\', '/');
                string exe = Path.Combine(live, "Ascension.exe");
                Current = new Data { AscensionExe = File.Exists(exe) ? exe : live };
            }
            else if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize<Data>(json, JsonOpts) ?? new Data();
                MigrateLegacyLiveDir(json);
            }
            else
            {
                Current = new Data();
            }

            if (!IsAscensionConfigured())
                TryAutofillDefaults();

            if (!string.IsNullOrWhiteSpace(envMaps))
                Current.MapsDir = envMaps.TrimEnd('\\', '/');
            if (!string.IsNullOrWhiteSpace(envMmaps))
                Current.MmapsDir = envMmaps.TrimEnd('\\', '/');

            if (string.IsNullOrWhiteSpace(Current.TrainingDataDir)
                || Current.TrainingDataDir.StartsWith(@"C:\Users\Dean\", StringComparison.OrdinalIgnoreCase))
            {
                string root = AppContext.BaseDirectory.TrimEnd('\\', '/');
                Current.TrainingDataDir = Path.Combine(root, "Config", "chat-corpus");
            }

            NormalizeNavPaths();
            Current.SoftRealmUrl = "";
            if (Current.SharedSyncMs < 100 || Current.SharedSyncMs > 5000)
                Current.SharedSyncMs = 300;
            if (Current.ObjectsIntervalMs > 1000)
                Current.ObjectsIntervalMs = Current.SharedSyncMs;
            if (Current.PollMs > 1000)
                Current.PollMs = Current.SharedSyncMs;
            // Do not auto-Save until the user finishes first-run setup.
            if (IsFullyConfigured())
                Save();
            Paths.ApplySettings(Current);
        }
        catch
        {
            Current = new Data();
            TryAutofillDefaults();
            Paths.ApplySettings(Current);
        }
    }

    static void MigrateLegacyLiveDir(string json)
    {
        // Older settings used liveDir instead of ascensionExe.
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("liveDir", out var liveProp))
            {
                string? live = liveProp.GetString();
                if (!string.IsNullOrWhiteSpace(live)
                    && (string.IsNullOrWhiteSpace(Current.AscensionExe)
                        || Current.AscensionExe == DefaultAscensionExe))
                {
                    string exe = Path.Combine(live.TrimEnd('\\', '/'), "Ascension.exe");
                    if (File.Exists(exe))
                        Current.AscensionExe = exe;
                }
            }
        }
        catch { }
    }

    static void TryAutofillDefaults()
    {
        if (!File.Exists(DefaultAscensionExe))
            return;

        Current.AscensionExe = DefaultAscensionExe;
        string live = Path.GetDirectoryName(DefaultAscensionExe)!;
        Current.MapsDir = ResolveMapsDir(live) ?? Current.MapsDir;
        Current.MmapsDir = ResolveMmtilesDir(live) ?? Current.MmapsDir;
    }

    /// <summary>Prefer ...\mmaps\maps (*.mmap). Never use Data\.</summary>
    public static string? ResolveMapsDir(string liveDir)
    {
        string[] candidates =
        {
            Path.Combine(liveDir, "mmaps", "maps"),
            Path.Combine(liveDir, "maps"),
        };
        foreach (string c in candidates)
        {
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "*.mmap").Any())
                return c;
        }
        return null;
    }

    /// <summary>Prefer ...\mmaps\mmtiles (*.mmtile). Avoid classic_era unless that is all that exists.</summary>
    public static string? ResolveMmtilesDir(string liveDir)
    {
        string[] candidates =
        {
            Path.Combine(liveDir, "mmaps", "mmtiles"),
            Path.Combine(liveDir, "mmtiles"),
            Path.Combine(liveDir, "mmaps", "classic_era"),
        };
        foreach (string c in candidates)
        {
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "*.mmtile").Any())
                return c;
        }
        return null;
    }

    /// <summary>Fix legacy wrong defaults (Data as Maps, classic_era when mmtiles exists).</summary>
    public static void NormalizeNavPaths()
    {
        string live = Current.LiveDir;
        if (string.IsNullOrEmpty(live))
            return;

        bool mapsBad = !IsMapsConfigured();
        if (!mapsBad && Current.MapsDir.EndsWith("\\Data", StringComparison.OrdinalIgnoreCase))
            mapsBad = true;
        if (!mapsBad && !Directory.EnumerateFiles(Current.MapsDir, "*.mmap").Any())
            mapsBad = true;
        if (mapsBad)
        {
            string? fixedMaps = ResolveMapsDir(live);
            if (fixedMaps is not null)
                Current.MapsDir = fixedMaps;
        }

        bool tilesBad = !IsMmapsConfigured();
        if (!tilesBad && !Directory.EnumerateFiles(Current.MmapsDir, "*.mmtile").Any())
            tilesBad = true;
        // If pointing at classic_era but mmtiles exists, prefer mmtiles.
        string prefer = Path.Combine(live, "mmaps", "mmtiles");
        if (!tilesBad
            && Current.MmapsDir.IndexOf("classic_era", StringComparison.OrdinalIgnoreCase) >= 0
            && Directory.Exists(prefer)
            && Directory.EnumerateFiles(prefer, "*.mmtile").Any())
        {
            tilesBad = true;
        }
        if (tilesBad)
        {
            string? fixedTiles = ResolveMmtilesDir(live);
            if (fixedTiles is not null)
                Current.MmapsDir = fixedTiles;
        }
    }

    public static void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch { }
    }

    public static bool TrySetAscensionExe(string path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Ascension.exe path is empty";
            return false;
        }
        path = path.Trim().TrimEnd('\\', '/');
        if (Directory.Exists(path))
        {
            string exe = Path.Combine(path, "Ascension.exe");
            if (!File.Exists(exe))
            {
                error = "Ascension.exe not found in that folder";
                return false;
            }
            path = exe;
        }
        if (!File.Exists(path))
        {
            error = "Ascension.exe not found";
            return false;
        }
        if (!path.EndsWith("Ascension.exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "Select the Ascension.exe file";
            return false;
        }
        string? live = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(live) || !File.Exists(Path.Combine(live, "Extensions.dll")))
        {
            error = "Extensions.dll not found beside Ascension.exe";
            return false;
        }
        if (!Directory.Exists(Path.Combine(live, "Data")))
        {
            error = "Data\\ folder not found beside Ascension.exe";
            return false;
        }
        Current.AscensionExe = path;
        // Auto-suggest nav paths from the live client folder when unset/invalid.
        if (!IsMapsConfigured())
        {
            string? maps = ResolveMapsDir(live);
            if (!string.IsNullOrEmpty(maps))
                Current.MapsDir = maps;
        }
        if (!IsMmapsConfigured())
        {
            string? tiles = ResolveMmtilesDir(live);
            if (!string.IsNullOrEmpty(tiles))
                Current.MmapsDir = tiles;
        }
        Save();
        Paths.ApplySettings(Current);
        return true;
    }

    /// <summary>
    /// Optional nav path. Empty clears MapsDir (game still launches; nav calc disabled).
    /// Accepts ...\maps, a parent containing maps\, or a folder that itself has *.mmap.
    /// When a parent also has mmtiles\, auto-fills MmapsDir if unset/invalid.
    /// </summary>
    public static bool TrySetMapsDir(string path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            Current.MapsDir = "";
            Save();
            Paths.ApplySettings(Current);
            return true;
        }
        path = path.TrimEnd('\\', '/');
        if (!Directory.Exists(path))
        {
            error = "Maps folder does not exist";
            return false;
        }

        string original = path;
        string nestedMaps = Path.Combine(original, "maps");
        string nestedTiles = Path.Combine(original, "mmtiles");
        bool parentHasMaps = Directory.Exists(nestedMaps)
            && Directory.EnumerateFiles(nestedMaps, "*.mmap").Any();
        bool parentHasTiles = Directory.Exists(nestedTiles)
            && Directory.EnumerateFiles(nestedTiles, "*.mmtile").Any();

        if (!Directory.EnumerateFiles(path, "*.mmap").Any() && parentHasMaps)
            path = nestedMaps;

        if (!Directory.EnumerateFiles(path, "*.mmap").Any())
        {
            error = "Maps folder must contain .mmap files (or a maps\\ subfolder with them)";
            return false;
        }

        Current.MapsDir = path;
        if (!IsMmapsConfigured())
        {
            if (parentHasTiles)
                Current.MmapsDir = nestedTiles;
            else
            {
                string siblingTiles = Path.Combine(Path.GetDirectoryName(path) ?? "", "mmtiles");
                if (Directory.Exists(siblingTiles)
                    && Directory.EnumerateFiles(siblingTiles, "*.mmtile").Any())
                    Current.MmapsDir = siblingTiles;
                else if (Directory.EnumerateFiles(path, "*.mmtile").Any())
                    Current.MmapsDir = path;
            }
        }

        Save();
        Paths.ApplySettings(Current);
        return true;
    }

    /// <summary>Optional. Empty clears MmapsDir. Accepts ...\mmtiles or parent with mmtiles\.</summary>
    public static bool TrySetMmapsDir(string path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            Current.MmapsDir = "";
            Save();
            Paths.ApplySettings(Current);
            return true;
        }
        path = path.TrimEnd('\\', '/');
        if (!Directory.Exists(path))
        {
            error = "MMTILES folder does not exist";
            return false;
        }
        // Auto-descend into mmtiles\ when user picked the mmaps parent.
        string nested = Path.Combine(path, "mmtiles");
        if (!Directory.EnumerateFiles(path, "*.mmtile").Any()
            && Directory.Exists(nested)
            && Directory.EnumerateFiles(nested, "*.mmtile").Any())
        {
            path = nested;
        }
        if (!Directory.EnumerateFiles(path, "*.mmtile").Any())
        {
            error = "MMTILES folder must contain .mmtile files (or a mmtiles\\ subfolder with them)";
            return false;
        }
        Current.MmapsDir = path;
        Save();
        Paths.ApplySettings(Current);
        return true;
    }

    /// <summary>Legacy API — folder that contains Ascension.exe.</summary>
    public static bool TrySetLiveDir(string path, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path is empty";
            return false;
        }
        path = path.TrimEnd('\\', '/');
        return TrySetAscensionExe(Path.Combine(path, "Ascension.exe"), out error);
    }

    public static bool NeedsSetup() => !IsFullyConfigured();

    public static object SetupStatusDto() => new
    {
        needsSetup = NeedsSetup(),
        configured = IsFullyConfigured(),
        ascensionOk = IsAscensionConfigured(),
        mapsOk = IsMapsConfigured(),
        mmapsOk = IsMmapsConfigured(),
        navOptional = true,
        navConfigured = HasOptionalNav(),
        missing = DescribeMissing(),
        optionalNav = DescribeOptionalNav(),
        ascensionExe = Current.AscensionExe,
        mapsDir = Current.MapsDir,
        mmapsDir = Current.MmapsDir,
        liveDir = Current.LiveDir,
        appRoot = Paths.AppRoot,
        isDist = Paths.IsDistLayout,
        offsetScan = ClientOffsetService.LastSummary,
    };

    public static bool IsAscensionConfigured() =>
        !string.IsNullOrWhiteSpace(Current.AscensionExe)
        && File.Exists(Current.AscensionExe)
        && File.Exists(Path.Combine(Current.LiveDir, "Extensions.dll"))
        && Directory.Exists(Path.Combine(Current.LiveDir, "Data"));

    public static bool IsMapsConfigured() =>
        !string.IsNullOrWhiteSpace(Current.MapsDir)
        && Directory.Exists(Current.MapsDir)
        && Directory.EnumerateFiles(Current.MapsDir, "*.mmap").Any();

    public static bool IsMmapsConfigured() =>
        !string.IsNullOrWhiteSpace(Current.MmapsDir)
        && Directory.Exists(Current.MmapsDir)
        && Directory.EnumerateFiles(Current.MmapsDir, "*.mmtile").Any();

    public static bool IsLiveDirConfigured() => IsAscensionConfigured();

    /// <summary>Required for Launch: Ascension.exe only. Maps/mmtiles are optional nav aids.</summary>
    public static bool IsFullyConfigured() => IsAscensionConfigured();

    public static bool HasOptionalNav() => IsMapsConfigured() || IsMmapsConfigured();

    public static string DescribeMissing()
    {
        if (IsAscensionConfigured())
            return "";
        return "Missing or invalid:\n• Ascension.exe (stock client with Extensions.dll + Data\\)";
    }

    public static string DescribeOptionalNav()
    {
        var missing = new List<string>();
        if (!IsMapsConfigured())
            missing.Add("Maps (*.mmap) — optional, needed for nav mesh");
        if (!IsMmapsConfigured())
            missing.Add("MMTILES (*.mmtile) — optional, needed for nav tiles");
        return missing.Count == 0
            ? ""
            : string.Join("\n• ", missing.Prepend("Optional nav paths not set (Launch still works):"));
    }
}
