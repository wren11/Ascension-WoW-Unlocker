using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AscensionNetTool;

enum AddonSyncState
{
    MissingSource,
    MissingLive,
    OutOfDate,
    Ok,
    DisabledLive,
}

sealed record AddonFileDiff(string RelativePath, string Reason);

sealed record AddonStatus(
    string Name,
    string SourceDir,
    string LiveDir,
    bool SourceExists,
    bool LiveExists,
    bool DisabledPresent,
    AddonSyncState State,
    int SourceFiles,
    int LiveFiles,
    int MismatchedFiles,
    string Summary,
    IReadOnlyList<AddonFileDiff> Diffs,
    string Notes = "");
sealed class AddonService
{
    public static string RepoAddonsDir => Paths.AddonsSourceDir;

    /// <summary>
    /// Primary live AddOns tree: Ascension install Interface\AddOns from settings paths.
    /// Falls back to Runtime\Interface\AddOns when LiveDir is unset.
    /// </summary>
    public static string LiveAddonsDir
    {
        get
        {
            string stock = Paths.StockInterfaceAddOns;
            if (!string.IsNullOrWhiteSpace(stock))
                return stock;
            return Paths.LiveAddOns;
        }
    }

    static readonly Regex TocNotes = new(
        @"^##\s*Notes:\s*(.+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    public event Action<string>? Progress;

    void Log(string msg) => Progress?.Invoke(msg);
    public static string NotesFor(string name)
    {
        string dir = Path.Combine(RepoAddonsDir, name);
        if (!Directory.Exists(dir))
            return "";
        string preferred = Path.Combine(dir, name + ".toc");
        string? toc = File.Exists(preferred)
            ? preferred
            : Directory.GetFiles(dir, "*.toc").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (toc == null)
            return "";
        try
        {
            string text = File.ReadAllText(toc);
            Match m = TocNotes.Match(text);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        catch
        {
            return "";
        }
    }
    public static IReadOnlyList<string> KnownAddonNames()
    {
        string root = RepoAddonsDir;
        if (!Directory.Exists(root))
            return Array.Empty<string>();

        var names = new List<string>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            string? n = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(n))
                continue;
            if (Directory.GetFiles(dir, "*.toc").Length == 0)
                continue;
            names.Add(n);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public IReadOnlyList<AddonStatus> ScanAll() =>
        KnownAddonNames().Select(ScanOne).ToArray();

    public AddonStatus ScanOne(string name)
    {
        string src = Path.Combine(RepoAddonsDir, name);
        string live = Path.Combine(LiveAddonsDir, name);
        string disabled = Path.Combine(LiveAddonsDir, name + "_disabled");
        bool srcOk = Directory.Exists(src) && Directory.GetFiles(src, "*.toc").Length > 0;
        bool liveOk = Directory.Exists(live);
        bool disabledOk = Directory.Exists(disabled);
        string notes = NotesFor(name);

        var diffs = new List<AddonFileDiff>();
        int srcCount = 0, liveCount = 0, mismatch = 0;

        if (!srcOk)
        {
            return new AddonStatus(name, src, live, false, liveOk, disabledOk,
                AddonSyncState.MissingSource, 0, liveOk ? CountFiles(live) : 0, 0,
                "no source in repo/addons", diffs, notes);
        }

        var srcFiles = IndexFiles(src);
        srcCount = srcFiles.Count;

        if (disabledOk && !liveOk)
        {
            return new AddonStatus(name, src, live, true, false, true,
                AddonSyncState.DisabledLive, srcCount, CountFiles(disabled), srcCount,
                "live copy is disabled (*_disabled)", diffs, notes);
        }

        if (!liveOk)
        {
            foreach (var rel in srcFiles.Keys)
                diffs.Add(new AddonFileDiff(rel, "missing on live"));
            return new AddonStatus(name, src, live, true, false, disabledOk,
                AddonSyncState.MissingLive, srcCount, 0, srcCount,
                "not installed on live", diffs, notes);
        }

        var liveFiles = IndexFiles(live);
        liveCount = liveFiles.Count;

        foreach (var (rel, hash) in srcFiles)
        {
            if (!liveFiles.TryGetValue(rel, out var liveHash))
            {
                mismatch++;
                diffs.Add(new AddonFileDiff(rel, "missing on live"));
            }
            else if (!string.Equals(hash, liveHash, StringComparison.OrdinalIgnoreCase))
            {
                mismatch++;
                diffs.Add(new AddonFileDiff(rel, "hash mismatch"));
            }
        }

        foreach (var rel in liveFiles.Keys)
        {
            if (!srcFiles.ContainsKey(rel))
                diffs.Add(new AddonFileDiff(rel, "extra on live"));
        }

        if (mismatch > 0 || diffs.Any(d => d.Reason == "extra on live" && IsTrackedExtra(d.RelativePath)))
        {
            var state = mismatch > 0 ? AddonSyncState.OutOfDate : AddonSyncState.Ok;
            string summary = mismatch > 0
                ? $"{mismatch} file(s) out of date"
                : "ok (live has extra files)";
            return new AddonStatus(name, src, live, true, true, disabledOk,
                state, srcCount, liveCount, mismatch, summary, diffs, notes);
        }

        return new AddonStatus(name, src, live, true, true, disabledOk,
            AddonSyncState.Ok, srcCount, liveCount, 0, "in sync", diffs, notes);
    }

    static bool IsTrackedExtra(string rel) =>
        !rel.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);

    public void DeployAll()
    {
        var names = KnownAddonNames();
        if (names.Count == 0)
            names = PlatformCatalog.Concat(PaidCatalog).ToArray();
        DeployNames(names);
    }

    /// <summary>Deploy every shipped addon when the account has active Core. Logout still removes the paid catalog.</summary>
    public object DeployEntitled(bool hasCore, IEnumerable<string> entitledAddons, int? instanceId = null)
    {
        _ = entitledAddons;
        var roots = RootsForInstance(instanceId);
        if (roots.Count == 0)
            return new { ok = false, error = "No AddOns root for that instance.", hasCore, instanceId };
        var platform = new HashSet<string>(PlatformCatalog, StringComparer.OrdinalIgnoreCase);
        var catalog = KnownAddonNames().Count > 0
            ? KnownAddonNames()
            : PlatformCatalog.Concat(PaidCatalog).ToArray();

        var toDeploy = new List<string>();
        var disabled = new List<string>();

        foreach (var name in catalog)
        {
            if (platform.Contains(name) || IsPlatform(name) || hasCore)
                toDeploy.Add(name);
            else
            {
                DeletePaidOne(name, roots);
                disabled.Add(name);
            }
        }

        DeployNames(toDeploy, roots);
        return new
        {
            ok = true,
            hasCore,
            instanceId,
            deployed = toDeploy,
            disabled,
            model = "core-account",
        };
    }

    /// <summary>GMToolBox catalog — all included with Core. None are free without Core.</summary>
    public static readonly string[] PaidCatalog =
    {
        "HuntingBot", "GatherBot", "GmGatherPins", "BgAfk", "CtfCap", "WsgCap",
        "GmTeleport", "GmMapTeleport", "GmCombat", "GmExplore", "KnightOfXoroth",
        "GmNearby", "LootCollector", "BotBuilder",
        "GmChatCapture", "GmLab", "GmApiBrowser", "PlayerProfileExport", "GmCmds", "GmToolbox",
        "GmtShare",
    };

    /// <summary>Always deploy so the client can read entitlements and force reload. ActionFlow ships with the launcher AddOns pack (no GMToolBox required).</summary>
    public static readonly string[] PlatformCatalog =
    {
        "GmShared", "GmUI", "GmTooltipFix", "ActionFlow",
    };

    public static bool IsPlatform(string name) =>
        PlatformCatalog.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>All live Interface\AddOns roots (stock + runtime instances).</summary>
    public IEnumerable<string> EnumerateLiveAddonRoots() => AllLiveAddonRoots();

    /// <summary>Write GmtEntitlements.lua into every live GmShared folder so the client loads it.</summary>
    public void WriteEntitlementLua(
        string characterGuid,
        bool hasCore,
        bool valid,
        int maxInstances,
        string coreExpiresUtc,
        IEnumerable<string> addons,
        IEnumerable<string>? allowedNames = null,
        string? characterName = null,
        int? instanceId = null)
    {
        var list = (addons ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        hasCore = true;
        valid = true;
        if (list.Length == 0)
            list = PaidCatalog.ToArray();

        var names = (allowedNames ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (hasCore && !string.IsNullOrWhiteSpace(characterName)
            && !names.Any(n => string.Equals(n, characterName, StringComparison.OrdinalIgnoreCase)))
        {
            names = names.Concat(new[] { characterName.Trim() }).ToArray();
        }

        var addonsLua = string.Join(", ", list.Select(a => "[\"" + a.Replace("\"", "") + "\"]=true"));
        var namesLua = string.Join(", ", names.Select(n => "[\"" + n.ToLowerInvariant().Replace("\"", "") + "\"]=true"));
        var body = $@"-- Auto-generated by GMToolBox — do not edit
GmtEntitlements = GmtEntitlements or {{}}
GmtEntitlements.hasCore = {(hasCore ? "true" : "false")}
GmtEntitlements.valid = {(valid ? "true" : "false")}
GmtEntitlements.characterGuid = ""{(characterGuid ?? "").Replace("\"", "")}""
GmtEntitlements.characterName = ""{(characterName ?? "").Replace("\"", "")}""
GmtEntitlements.coreExpiresUtc = ""{(coreExpiresUtc ?? "").Replace("\"", "")}""
GmtEntitlements.maxInstances = {Math.Max(1, maxInstances)}
GmtEntitlements.addons = {{ {addonsLua} }}
GmtEntitlements.allowedNames = {{ {namesLua} }}
GmtEntitlements.updatedUtc = ""{DateTime.UtcNow:o}""
GmtEntitlements.source = ""local""
";

        foreach (var root in RootsForInstance(instanceId))
        {
            try
            {
                var gmShared = Path.Combine(root, "GmShared");
                Directory.CreateDirectory(gmShared);
                File.WriteAllText(Path.Combine(gmShared, "GmtEntitlements.lua"), body);
            }
            catch (Exception ex)
            {
                Log($"entitlement lua {root}: {ex.Message}");
            }
        }
    }

    IReadOnlyList<string> RootsForInstance(int? instanceId)
    {
        if (instanceId is int id && id > 0)
        {
            var one = Paths.LiveAddOnsFor(id);
            return string.IsNullOrWhiteSpace(one) ? Array.Empty<string>() : new[] { Path.GetFullPath(one) };
        }
        return AllLiveAddonRoots().ToList();
    }

    void DeployNames(IReadOnlyList<string> names, IReadOnlyList<string>? roots = null)
    {
        var targets = (roots ?? AllLiveAddonRoots().ToList()).ToList();
        if (names.Count == 0)
            return;

        foreach (string destRoot in targets)
        {
            Directory.CreateDirectory(destRoot);
            foreach (var name in names)
            {
                try { DeployOneTo(name, destRoot); }
                catch (Exception ex) { Log($"deploy {name}: {ex.Message}"); }
            }
            Log($"Deployed {names.Count} addon(s) → {destRoot}");
        }
    }

    /// <summary>Remove paid catalog addons from live trees. Platform addons stay.</summary>
    public object RemovePaidAddons(int? instanceId = null)
    {
        var roots = RootsForInstance(instanceId);
        var names = new HashSet<string>(PaidCatalog, StringComparer.OrdinalIgnoreCase);
        foreach (var n in KnownAddonNames())
        {
            if (!IsPlatform(n)) names.Add(n);
        }
        var removed = new List<string>();
        foreach (var name in names)
            DeletePaidOne(name, roots, removed);
        return new { ok = true, removed = removed.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    void DeletePaidOne(string name, IEnumerable<string>? roots = null, List<string>? removed = null)
    {
        if (IsPlatform(name)) return;
        foreach (string root in roots ?? AllLiveAddonRoots())
        {
            foreach (var dest in new[] { Path.Combine(root, name), Path.Combine(root, name + "_disabled") })
            {
                if (!Directory.Exists(dest)) continue;
                try
                {
                    Directory.Delete(dest, recursive: true);
                    removed?.Add(name);
                    Log($"Deleted {name} (session ended) → {dest}");
                }
                catch (Exception ex)
                {
                    Log($"Could not delete {name}: {ex.Message}");
                }
            }
        }
    }

    public void DisableOne(string name, IEnumerable<string>? roots = null)
    {
        foreach (string root in roots ?? AllLiveAddonRoots())
        {
            string dest = Path.Combine(root, name);
            string disabled = dest + "_disabled";
            if (!Directory.Exists(dest)) continue;
            try
            {
                if (Directory.Exists(disabled))
                    Directory.Delete(disabled, recursive: true);
                Directory.Move(dest, disabled);
                Log($"Disabled {name} (not entitled) → {disabled}");
            }
            catch (Exception ex)
            {
                Log($"Could not disable {name}: {ex.Message}");
            }
        }
    }

    public void DeployOne(string name, int? instanceId = null)
    {
        foreach (string root in RootsForInstance(instanceId))
            DeployOneTo(name, root);
    }

    IEnumerable<string> AllLiveAddonRoots()
    {
        var list = new List<string>();
        void push(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                string full = Path.GetFullPath(p);
                if (list.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                    return;
                list.Add(full);
            }
            catch { }
        }

        // 1) Ascension install Interface\AddOns (from settings.ascensionExe → LiveDir)
        push(Paths.StockInterfaceAddOns);
        // 2) Legacy Runtime\Interface\AddOns
        push(Paths.LiveAddOns);
        // 3) Per-instance Runtime\instN\Interface\AddOns (portable Ascension.launch)
        int n = Math.Clamp(SettingsStore.Current.InstanceCount, 1, GmtLimits.MaxInstances);
        for (int i = 1; i <= Math.Max(n, 2); i++)
            push(Paths.LiveAddOnsFor(i));
        try
        {
            string runtime = Paths.RuntimeDir;
            if (Directory.Exists(runtime))
            {
                foreach (string dir in Directory.EnumerateDirectories(runtime, "inst*"))
                    push(Path.Combine(dir, "Interface", "AddOns"));
            }
        }
        catch { }
        return list;
    }

    void DeployOneTo(string name, string destRoot)
    {
        string src = Path.Combine(RepoAddonsDir, name);
        if (!Directory.Exists(src) || Directory.GetFiles(src, "*.toc").Length == 0)
            throw new DirectoryNotFoundException("Missing addon source (need folder + .toc): " + src);

        Directory.CreateDirectory(destRoot);
        string dest = Path.Combine(destRoot, name);
        string disabled = dest + "_disabled";

        if (Directory.Exists(disabled))
        {
            try
            {
                Directory.Delete(disabled, recursive: true);
                Log($"Removed disabled folder {name}_disabled");
            }
            catch (Exception ex)
            {
                Log($"Could not remove {name}_disabled: {ex.Message}");
            }
        }

        CopyDirectory(src, dest);
        Log($"Installed {name} → {dest}");
    }

    public int SyncOutdated(IReadOnlyList<AddonStatus>? preScan = null)
    {
        var list = preScan ?? ScanAll();
        int synced = 0;
        foreach (var a in list)
        {
            bool needsInstall = a.State is AddonSyncState.MissingLive
                or AddonSyncState.OutOfDate
                or AddonSyncState.DisabledLive;
            if (!needsInstall)
                continue;
            if (!a.SourceExists)
                continue;
            try
            {
                Log($"autosync {a.Name} ({a.Summary})");
                DeployOne(a.Name);
                synced++;
            }
            catch (Exception ex)
            {
                Log($"autosync failed {a.Name}: {ex.Message}");
            }
        }
        return synced;
    }

    public IReadOnlyList<AddonStatus> ScanAndAutoSync(bool autoSync)
    {
        var first = ScanAll();
        if (!autoSync)
            return first;

        int n = SyncOutdated(first);
        if (n <= 0)
            return first;
        Log($"autosync complete — {n} addon(s) updated across all instance AddOns trees");
        return ScanAll();
    }

    static int CountFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length
            : 0;

    static Dictionary<string, string> IndexFiles(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
            return map;
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            map[rel] = Sha1File(file);
        }
        return map;
    }

    static string Sha1File(string path)
    {
        using var fs = File.OpenRead(path);
        byte[] hash = SHA1.HashData(fs);
        return Convert.ToHexString(hash);
    }

    static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            string target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            try { File.SetAttributes(target, FileAttributes.Normal); } catch { }
        }
    }
}
