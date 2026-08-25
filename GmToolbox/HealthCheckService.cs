namespace AscensionNetTool;

enum HealthSeverity
{
    Ok,
    Warn,
    Fail,
}

sealed class HealthItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public HealthSeverity Severity { get; init; }
    public bool Blocking => Severity == HealthSeverity.Fail;
}

/// <summary>
/// Startup path / junction / binary / addon health probe for portable dist Runtime.
/// </summary>
static class HealthCheckService
{
    public const string DiscordInviteUrl = LocalAccess.DiscordInviteUrl;
    public const string DiscordInviteLabel = LocalAccess.DiscordInviteLabel;

    public static IReadOnlyList<HealthItem> RunAll(Action<string, int>? progress = null)
    {
        var items = new List<HealthItem>(32);
        int step = 0;
        int total = 12;

        void Report(string label)
        {
            step++;
            int pct = Math.Clamp((int)(100.0 * step / total), 0, 100);
            progress?.Invoke(label, pct);
        }

        Report("Resolving portable root…");
        items.Add(CheckAppRoot());

        Report("Validating Ascension.exe…");
        items.Add(CheckAscension());

        Report("Validating maps (.mmap)…");
        items.Add(CheckMaps());

        Report("Validating mmtiles (.mmtile)…");
        items.Add(CheckMmaps());

        Report("Checking ExtProxy64.dll…");
        items.Add(CheckProxy());

        Report("Checking AscensionBoot.exe…");
        items.Add(CheckBoot());

        Report("Scanning packaged AddOns…");
        items.Add(CheckPackagedAddons());

        Report("Repairing Runtime junctions…");
        items.Add(EnsureAndVerifyStaging(progress: null));

        Report("Verifying Runtime/mmaps junctions…");
        items.Add(CheckRuntimeNavJunctions());

        Report("Verifying Runtime AddOns deploy…");
        items.Add(CheckRuntimeAddons());

        Report("Checking IPC pipe namespace…");
        items.Add(CheckIpcReady());

        Report("Health check complete");
        items.Add(new HealthItem
        {
            Id = "summary",
            Title = "Startup health",
            Detail = Summarize(items),
            Severity = items.Any(i => i.Id != "summary" && i.Severity == HealthSeverity.Fail)
                ? HealthSeverity.Fail
                : items.Any(i => i.Id != "summary" && i.Severity == HealthSeverity.Warn)
                    ? HealthSeverity.Warn
                    : HealthSeverity.Ok,
        });

        return items;
    }

    public static bool HasBlockingFailures(IReadOnlyList<HealthItem> items) =>
        items.Any(i => i.Id != "summary" && i.Blocking);

    static string Summarize(IReadOnlyList<HealthItem> items)
    {
        int ok = items.Count(i => i.Id != "summary" && i.Severity == HealthSeverity.Ok);
        int warn = items.Count(i => i.Id != "summary" && i.Severity == HealthSeverity.Warn);
        int fail = items.Count(i => i.Id != "summary" && i.Severity == HealthSeverity.Fail);
        return $"{ok} ok · {warn} warn · {fail} fail";
    }

    static HealthItem CheckAppRoot()
    {
        string root = Paths.AppRoot;
        bool dist = Paths.IsDistLayout;
        return new HealthItem
        {
            Id = "approot",
            Title = "Portable root",
            Detail = dist ? root : root + " (dev layout)",
            Severity = Directory.Exists(root) ? HealthSeverity.Ok : HealthSeverity.Fail,
        };
    }

    static HealthItem CheckAscension()
    {
        string exe = Paths.StockExe;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return new HealthItem
            {
                Id = "ascension",
                Title = "Ascension.exe",
                Detail = string.IsNullOrWhiteSpace(exe) ? "not configured" : "missing: " + exe,
                Severity = HealthSeverity.Fail,
            };
        }
        var fi = new FileInfo(exe);
        return new HealthItem
        {
            Id = "ascension",
            Title = "Ascension.exe",
            Detail = $"{exe} ({fi.Length:N0} bytes)",
            Severity = HealthSeverity.Ok,
        };
    }

    static HealthItem CheckMaps()
    {
        string dir = Paths.MapsDir;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return new HealthItem
            {
                Id = "maps",
                Title = "Maps (.mmap)",
                Detail = "optional — not set (nav mesh disabled)",
                Severity = HealthSeverity.Warn,
            };
        }
        int n = SafeCount(dir, "*.mmap");
        return new HealthItem
        {
            Id = "maps",
            Title = "Maps (.mmap)",
            Detail = $"{dir} · {n} files",
            Severity = n > 0 ? HealthSeverity.Ok : HealthSeverity.Warn,
        };
    }

    static HealthItem CheckMmaps()
    {
        string dir = Paths.MmapsDir;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return new HealthItem
            {
                Id = "mmtiles",
                Title = "MMAPS (.mmtile)",
                Detail = "optional — not set (nav tiles disabled)",
                Severity = HealthSeverity.Warn,
            };
        }
        int n = SafeCount(dir, "*.mmtile");
        return new HealthItem
        {
            Id = "mmtiles",
            Title = "MMAPS (.mmtile)",
            Detail = $"{dir} · {n} tiles",
            Severity = n > 0 ? HealthSeverity.Ok : HealthSeverity.Warn,
        };
    }

    static HealthItem CheckProxy()
    {
        string path = Paths.ProxySrc;
        if (!File.Exists(path))
        {
            return new HealthItem
            {
                Id = "proxy",
                Title = "ExtProxy64.dll",
                Detail = "missing: " + path,
                Severity = HealthSeverity.Fail,
            };
        }
        var fi = new FileInfo(path);
        return new HealthItem
        {
            Id = "proxy",
            Title = "ExtProxy64.dll",
            Detail = $"{fi.Length:N0} bytes · {fi.LastWriteTime:yyyy-MM-dd HH:mm}",
            Severity = fi.Length > 50_000 ? HealthSeverity.Ok : HealthSeverity.Fail,
        };
    }

    static HealthItem CheckBoot()
    {
        string path = Paths.BootSrc;
        if (!File.Exists(path))
        {
            return new HealthItem
            {
                Id = "boot",
                Title = "AscensionBoot.exe",
                Detail = "missing: " + path,
                Severity = HealthSeverity.Fail,
            };
        }
        var fi = new FileInfo(path);
        return new HealthItem
        {
            Id = "boot",
            Title = "AscensionBoot.exe",
            Detail = $"{fi.Length:N0} bytes · {fi.LastWriteTime:yyyy-MM-dd HH:mm}",
            Severity = fi.Length > 10_000 ? HealthSeverity.Ok : HealthSeverity.Fail,
        };
    }

    static HealthItem CheckPackagedAddons()
    {
        string src = Paths.AddonsSourceDir;
        if (!Directory.Exists(src))
        {
            return new HealthItem
            {
                Id = "addons_pkg",
                Title = "Packaged AddOns",
                Detail = "missing: " + src,
                Severity = HealthSeverity.Fail,
            };
        }
        string[] required =
        {
            "GmToolbox", "GmShared", "HuntingBot", "GatherBot",
            "GmMapTeleport", "GmTeleport", "GmExplore",
            "PlayerProfileExport", "KnightOfXoroth", "GmtShare",
            "LootCollector",
        };
        var missing = required.Where(n => !Directory.Exists(Path.Combine(src, n))).ToList();
        int total = Directory.GetDirectories(src).Length;
        if (missing.Count > 0)
        {
            return new HealthItem
            {
                Id = "addons_pkg",
                Title = "Packaged AddOns",
                Detail = $"missing: {string.Join(", ", missing)}",
                Severity = HealthSeverity.Fail,
            };
        }
        return new HealthItem
        {
            Id = "addons_pkg",
            Title = "Packaged AddOns",
            Detail = $"{total} folders · core suite present",
            Severity = HealthSeverity.Ok,
        };
    }

    static HealthItem EnsureAndVerifyStaging(Action<string>? progress)
    {
        try
        {
            int count = Math.Max(1, SettingsStore.Current.InstanceCount);
            var log = new List<string>();
            for (int i = 1; i <= count; i++)
            {
                RuntimeStaging.EnsureReady(i, m => log.Add(m));
                RuntimeStaging.VerifyInstanceHealthy(i);
            }
            return new HealthItem
            {
                Id = "staging",
                Title = "Runtime staging",
                Detail = $"inst1..inst{count} junctions + ExtProxy.cfg written",
                Severity = HealthSeverity.Ok,
            };
        }
        catch (Exception ex)
        {
            return new HealthItem
            {
                Id = "staging",
                Title = "Runtime staging",
                Detail = ex.Message,
                Severity = HealthSeverity.Fail,
            };
        }
    }

    static HealthItem CheckRuntimeNavJunctions()
    {
        int count = Math.Max(1, SettingsStore.Current.InstanceCount);
        if (!SettingsStore.IsMapsConfigured() && !SettingsStore.IsMmapsConfigured())
        {
            return new HealthItem
            {
                Id = "nav_junctions",
                Title = "Nav junctions",
                Detail = "skipped — optional maps/mmtiles not configured",
                Severity = HealthSeverity.Warn,
            };
        }

        var problems = new List<string>();
        for (int i = 1; i <= count; i++)
        {
            string runtime = Paths.RuntimeDirFor(i);
            if (SettingsStore.IsMapsConfigured())
            {
                string mapsLink = Path.Combine(runtime, "mmaps", "maps");
                TryValidateJunction(mapsLink, Paths.MapsDir, "*.mmap", problems, $"inst{i}/maps");
            }
            if (SettingsStore.IsMmapsConfigured())
            {
                string tilesLink = Path.Combine(runtime, "mmaps", "mmtiles");
                TryValidateJunction(tilesLink, Paths.MmapsDir, "*.mmtile", problems, $"inst{i}/mmtiles");
            }

            string cfg = Path.Combine(runtime, "ExtProxy.cfg");
            if (!File.Exists(cfg))
                problems.Add($"inst{i}: ExtProxy.cfg missing");
            else
            {
                string text = File.ReadAllText(cfg);
                if (!text.Contains("live=", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"inst{i}: ExtProxy.cfg incomplete");
            }
        }

        if (problems.Count > 0)
        {
            return new HealthItem
            {
                Id = "nav_junctions",
                Title = "Nav junctions",
                Detail = string.Join("; ", problems.Take(4)),
                Severity = HealthSeverity.Fail,
            };
        }
        return new HealthItem
        {
            Id = "nav_junctions",
            Title = "Nav junctions",
            Detail = $"inst1..inst{count} configured nav paths resolve",
            Severity = HealthSeverity.Ok,
        };
    }

    static void TryValidateJunction(
        string linkPath, string expectedTarget, string pattern, List<string> problems, string label)
    {
        if (!Directory.Exists(linkPath))
        {
            problems.Add($"{label}: missing");
            return;
        }
        string? resolved = RuntimeStaging.TryResolveLink(linkPath);
        if (resolved is null)
        {
            // Plain directory is acceptable if it contains files (copied fallback).
            if (SafeCount(linkPath, pattern) == 0)
                problems.Add($"{label}: empty");
            return;
        }
        string want = Path.GetFullPath(expectedTarget).TrimEnd('\\');
        string got = Path.GetFullPath(resolved).TrimEnd('\\');
        if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{label}: points to {got}");
        else if (SafeCount(linkPath, pattern) == 0)
            problems.Add($"{label}: target empty");
    }

    static HealthItem CheckRuntimeAddons()
    {
        int count = Math.Max(1, SettingsStore.Current.InstanceCount);
        var missing = new List<string>();
        string[] need = { "GmShared", "GmUI", "GmTooltipFix" };

        string stock = Paths.StockInterfaceAddOns;
        if (!string.IsNullOrWhiteSpace(stock))
        {
            foreach (string n in need)
            {
                if (!Directory.Exists(Path.Combine(stock, n)))
                    missing.Add($"stock/{n}");
            }
        }

        for (int i = 1; i <= count; i++)
        {
            string addons = Paths.LiveAddOnsFor(i);
            foreach (string n in need)
            {
                if (!Directory.Exists(Path.Combine(addons, n)))
                    missing.Add($"inst{i}/{n}");
            }
        }
        if (missing.Count > 0)
        {
            return new HealthItem
            {
                Id = "addons_rt",
                Title = "Interface AddOns",
                Detail = "missing: " + string.Join(", ", missing)
                    + (string.IsNullOrWhiteSpace(stock) ? "" : $" · stock={stock}"),
                Severity = HealthSeverity.Warn,
            };
        }
        return new HealthItem
        {
            Id = "addons_rt",
            Title = "Interface AddOns",
            Detail = string.IsNullOrWhiteSpace(stock)
                ? $"GmToolbox suite staged in inst1..inst{count}"
                : $"synced → stock Interface + inst1..inst{count}",
            Severity = HealthSeverity.Ok,
        };
    }

    static HealthItem CheckIpcReady()
    {
        // Pipe servers appear after Launch; splash confirms instance slot config is sane.
        try
        {
            int slots = Math.Clamp(SettingsStore.Current.InstanceCount, 1, GmtLimits.MaxInstances);
            return new HealthItem
            {
                Id = "ipc",
                Title = "IPC readiness",
                Detail = slots > 1
                    ? $"multi-instance ready · {slots} slots · pipes bind on Launch"
                    : "single-instance · pipes bind on Launch",
                Severity = HealthSeverity.Ok,
            };
        }
        catch (Exception ex)
        {
            return new HealthItem
            {
                Id = "ipc",
                Title = "IPC readiness",
                Detail = ex.Message,
                Severity = HealthSeverity.Warn,
            };
        }
    }

    static int SafeCount(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).Take(5000).Count(); }
        catch { return 0; }
    }
}
