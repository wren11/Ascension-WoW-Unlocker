using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AscensionNetTool;

/// <summary>
/// All internal resources resolve under <see cref="AppRoot"/> (directory of GMToolBox.exe).
/// The only machine-specific paths are Ascension.exe, Maps, and MMAPS from settings.
/// </summary>
static class Paths
{
    static string _live = "";
    static string _maps = "";
    static string _mmaps = "";

    static Paths()
    {
        SettingsStore.Load();
        ApplySettings(SettingsStore.Current);
    }

    /// <summary>Folder of the real GMToolBox.exe (not the single-file extract cache).</summary>
    public static string ExeDir
    {
        get
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    var dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrWhiteSpace(dir))
                        return Path.GetFullPath(dir.TrimEnd('\\', '/'));
                }
            }
            catch { /* fall through */ }
            return Path.GetFullPath(AppContext.BaseDirectory.TrimEnd('\\', '/'));
        }
    }

    /// <summary>Bundled content (wwwroot, ExtProxy) — extract dir for single-file, else ExeDir.</summary>
    public static string ContentRoot
    {
        get
        {
            var extracted = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            if (IsGmToolBoxWebRoot(Path.Combine(extracted, "wwwroot"))
                || File.Exists(Path.Combine(extracted, "ExtProxy64.dll")))
                return extracted;
            return ExeDir;
        }
    }

    /// <summary>Portable root: Config/logs live beside the real exe.</summary>
    public static string AppRoot
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("GMTOOLBOX_HOME");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
                return Path.GetFullPath(env.TrimEnd('\\', '/'));
            return ExeDir;
        }
    }

    public static bool IsGmToolBoxWebRoot(string wwwroot)
    {
        var index = Path.Combine(wwwroot, "index.html");
        if (!File.Exists(index)) return false;
        try
        {
            var html = File.ReadAllText(index);
            if (html.Contains("<title>SoftRealm</title>", StringComparison.OrdinalIgnoreCase)
                || html.Contains("softrealm-", StringComparison.OrdinalIgnoreCase))
                return false;
            return html.Contains("GMToolBox", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string FindPackaged(string fileName)
    {
        foreach (var root in new[] { ContentRoot, AppRoot, ExeDir })
        {
            var p = Path.Combine(root, fileName);
            if (File.Exists(p)) return p;
        }
        return Path.Combine(ContentRoot, fileName);
    }

    /// <summary>True when boot + proxy are packaged (folder or single-file extract).</summary>
    public static bool IsDistLayout =>
        File.Exists(FindPackaged("AscensionBoot.exe"))
        && File.Exists(FindPackaged("ExtProxy64.dll"));

    public static void ApplySettings(SettingsStore.Data data)
    {
        _live = data.LiveDir ?? "";
        _maps = (data.MapsDir ?? "").TrimEnd('\\', '/');
        _mmaps = (data.MmapsDir ?? "").TrimEnd('\\', '/');
    }

    public static void ApplyLiveDir(string liveDir)
    {
        if (string.IsNullOrWhiteSpace(liveDir))
            return;
        _live = liveDir.TrimEnd('\\', '/');
    }

    public static string LiveDir => _live;
    public static string MapsDir => _maps;
    public static string MmapsDir => _mmaps;
    public static string StockExe =>
        string.IsNullOrEmpty(_live) ? "" : Path.Combine(_live, "Ascension.exe");
    public static string StockExt =>
        string.IsNullOrEmpty(_live) ? "" : Path.Combine(_live, "Extensions.dll");

    /// <summary>Portable runtime staging dir under dist/ — never the Ascension install.</summary>
    public static string RuntimeDir => Path.Combine(AppRoot, "Runtime");

    public static int DefaultInstanceCount => 1;

    public static string RuntimeDirFor(int instanceId) =>
        Path.Combine(AppRoot, "Runtime", $"inst{Math.Max(1, instanceId)}");

    public static string PidFileFor(int instanceId) =>
        Path.Combine(RuntimeDirFor(instanceId), "ExtProxy64.pid");

    public static string LiveAddOnsFor(int instanceId) =>
        Path.Combine(RuntimeDirFor(instanceId), "Interface", "AddOns");

    public static string BootDst => Path.Combine(RuntimeDir, "AscensionBoot.exe");
    public static string ProxyDst => Path.Combine(RuntimeDir, "ExtProxy64.dll");
    public static string ProxyDstNew => Path.Combine(RuntimeDir, "ExtProxy64.dll.new");
    public static string BootDstNew => Path.Combine(RuntimeDir, "AscensionBoot.exe.new");
    public static string ProxyLog => Path.Combine(RuntimeDir, "ExtProxy64.log");
    public static string PidFile => Path.Combine(RuntimeDir, "ExtProxy64.pid");
    public static string LiveAddOns => Path.Combine(RuntimeDir, "Interface", "AddOns");

    /// <summary>
    /// Ascension install Interface\AddOns inferred from settings.ascensionExe / LiveDir.
    /// Canonical sync target (WTF is junctioned to this install).
    /// </summary>
    public static string StockInterfaceAddOns =>
        string.IsNullOrEmpty(LiveDir)
            ? ""
            : Path.Combine(LiveDir, "Interface", "AddOns");

    public static string BootDstFor(int instanceId) =>
        Path.Combine(RuntimeDirFor(instanceId), "AscensionBoot.exe");
    public static string ProxyDstFor(int instanceId) =>
        Path.Combine(RuntimeDirFor(instanceId), "ExtProxy64.dll");
    public static string ProxyLogFor(int instanceId) =>
        Path.Combine(RuntimeDirFor(instanceId), "ExtProxy64.log");

    public static string FindRepoRoot()
    {
        string? env = Environment.GetEnvironmentVariable("GMTOOLBOX_REPO");
        if (!string.IsNullOrWhiteSpace(env) && IsRepoRoot(env.TrimEnd('\\', '/')))
            return env.TrimEnd('\\', '/');

        if (IsDistLayout)
            return AppRoot;

        var dir = new DirectoryInfo(AppRoot);
        while (dir is not null)
        {
            if (IsRepoRoot(dir.FullName))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate app root (expect ExtProxy/ + GmToolbox/ or packaged dist). "
            + "Set GMTOOLBOX_HOME or GMTOOLBOX_REPO.");
    }

    static bool IsRepoRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;
        if (File.Exists(Path.Combine(path, "AscensionBoot.exe"))
            && File.Exists(Path.Combine(path, "ExtProxy64.dll")))
            return true;
        return Directory.Exists(Path.Combine(path, "ExtProxy"))
            && Directory.Exists(Path.Combine(path, "GmToolbox"));
    }

    public static string ProxyDir
    {
        get
        {
            var packaged = FindPackaged("ExtProxy64.dll");
            if (File.Exists(packaged))
                return Path.GetDirectoryName(packaged)!;
            string root = FindRepoRoot();
            string ext = Path.Combine(root, "ExtProxy");
            if (Directory.Exists(ext)) return ext;
            return Path.Combine(AppRoot, "ExtProxy");
        }
    }

    public static string AddonsSourceDir
    {
        get
        {
            string distAddons = Path.Combine(AppRoot, "AddOns");
            if (Directory.Exists(distAddons) && HasTocAddons(distAddons))
                return distAddons;

            // Walk up for repo addons/ even when running from bin\Release (IsDistLayout).
            var dir = new DirectoryInfo(AppRoot);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "addons");
                if (Directory.Exists(candidate) && HasTocAddons(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            try
            {
                string repo = Path.Combine(FindRepoRoot(), "addons");
                if (Directory.Exists(repo) && HasTocAddons(repo))
                    return repo;
            }
            catch { }

            return distAddons;
        }
    }

    static bool HasTocAddons(string root)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                if (Directory.EnumerateFiles(d, "*.toc").Any())
                    return true;
            }
        }
        catch { }
        return false;
    }

    public static string ProxySrc => Path.Combine(ProxyDir, "ExtProxy64.dll");
    public static string BootSrc
    {
        get
        {
            try
            {
                string repoBoot = Path.Combine(FindRepoRoot(), "AscensionBoot", "AscensionBoot.exe");
                if (File.Exists(repoBoot)) return repoBoot;
            }
            catch { }
            string besideProxy = Path.Combine(ProxyDir, "AscensionBoot.exe");
            if (File.Exists(besideProxy)) return besideProxy;
            string packaged = FindPackaged("AscensionBoot.exe");
            if (File.Exists(packaged)) return packaged;
            return besideProxy;
        }
    }
    public static string ProxyBuildScript => Path.Combine(ProxyDir, "build.ps1");
    public static string ToolLogPath => Path.Combine(AppRoot, "GMToolBox.log");
}

public sealed class BootstrapService
{
    public event Action<string>? Progress;
    readonly object _logGate = new();

    static readonly string[] ProxySourceGlobs =
    {
        "*.c", "*.h", "*.def",
    };

    public void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Progress?.Invoke(line);
        try
        {
            lock (_logGate)
            {
                string path = Paths.ToolLogPath;
                const long maxBytes = 2L * 1024 * 1024;
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                    {
                        string bak = path + ".1";
                        try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                        try { File.Move(path, bak); } catch { File.WriteAllText(path, ""); }
                    }
                }
                catch { }
                using var fs = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, Encoding.UTF8);
                sw.WriteLine(line);
                sw.Flush();
                fs.Flush(true);
            }
        }
        catch { }
        Debug.WriteLine(line);
    }

    public bool IsInstalled()
    {
        try
        {
            // Legacy single Runtime\ExtProxy64.dll
            if (File.Exists(Paths.ProxyDst) && new FileInfo(Paths.ProxyDst).Length > 10_000)
                return true;

            // Multi-instance staging: Runtime\instN\ExtProxy64.dll
            if (Directory.Exists(Paths.RuntimeDir))
            {
                foreach (string dir in Directory.EnumerateDirectories(Paths.RuntimeDir, "inst*"))
                {
                    string dll = Path.Combine(dir, "ExtProxy64.dll");
                    if (File.Exists(dll) && new FileInfo(dll).Length > 10_000)
                        return true;
                }
            }

            // Packaged dist always ships the DLL beside GMToolBox.exe
            string beside = Path.Combine(Paths.AppRoot, "ExtProxy64.dll");
            if (File.Exists(beside) && new FileInfo(beside).Length > 10_000)
                return true;

            return false;
        }
        catch { return false; }
    }

    public void EnsureBuiltArtifacts()
    {
        if (!File.Exists(Paths.ProxySrc) || !File.Exists(Paths.BootSrc))
            throw new FileNotFoundException(
                $"Missing built proxy/boot:\n{Paths.ProxySrc}\n{Paths.BootSrc}\n"
                + "Run ExtProxy\\build.ps1 or use Launch (auto-builds).");
    }
    public bool ProxyBuildIsStale()
    {
        if (!File.Exists(Paths.ProxySrc) || !File.Exists(Paths.BootSrc))
            return true;

        // Packaged dist has no C sources — never try to rebuild from Launch.
        if (Paths.IsDistLayout || !File.Exists(Paths.ProxyBuildScript))
            return false;

        try
        {
            string bootC = Path.Combine(Paths.FindRepoRoot(), "AscensionBoot", "AscensionBoot.c");
            if (File.Exists(bootC) && File.Exists(Paths.BootSrc)
                && File.GetLastWriteTimeUtc(bootC) > File.GetLastWriteTimeUtc(Paths.BootSrc).AddSeconds(2))
                return true;
        }
        catch { }

        DateTime dllTime = File.GetLastWriteTimeUtc(Paths.ProxySrc);
        string dir = Paths.ProxyDir;
        foreach (string pattern in ProxySourceGlobs)
        {
            foreach (string path in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                if (name.Equals("ExtProxy64.dll", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("AscensionBoot.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.GetLastWriteTimeUtc(path) > dllTime.AddSeconds(2))
                    return true;
            }
        }
        return false;
    }
    public void RebuildProxyArtifacts()
    {
        string script = Paths.ProxyBuildScript;
        if (!File.Exists(script))
            throw new FileNotFoundException(
                "Missing ExtProxy build script: " + script
                + " (packaged builds ship prebuilt ExtProxy64.dll)");

        Log("Building latest ExtProxy (build.ps1 -SkipToolbox)...");
        Log("  repo: " + Paths.ProxyDir);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -SkipToolbox",
            WorkingDirectory = Paths.ProxyDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ExtProxy build.ps1");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            if (e.Data.Length > 0) Log("  build> " + e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            if (e.Data.Length > 0) Log("  build! " + e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("ExtProxy build.ps1 timed out after 180s");
        }

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "ExtProxy build failed (exit " + proc.ExitCode + "). "
                + "See Documents\\GmToolbox.log. Last error: "
                + TrimTail(stderr.ToString(), 400));
        }

        EnsureBuiltArtifacts();
        var fi = new FileInfo(Paths.ProxySrc);
        Log($"Built ExtProxy64.dll {fi.Length}b @ {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
    }
    /// <summary>
    /// Ensure ExtProxy64.dll + AscensionBoot.exe exist under the app root (dist or ExtProxy).
    /// Never copies proxy/boot into the Ascension installation directory.
    /// </summary>
    public void EnsureLatestProxyDeployed(bool forceRebuild = false)
    {
        Log("App root: " + Paths.AppRoot);
        Log("Proxy dir: " + Paths.ProxyDir);
        Log("Runtime staging: " + Paths.RuntimeDir);
        Log("Stock Ascension: " + Paths.StockExe);

        if (forceRebuild && !Paths.IsDistLayout && File.Exists(Paths.ProxyBuildScript))
        {
            Log("Force rebuild requested");
            RebuildProxyArtifacts();
        }
        else if (ProxyBuildIsStale())
        {
            Log("Proxy sources newer than ExtProxy64.dll — rebuilding");
            RebuildProxyArtifacts();
        }
        else
        {
            EnsureBuiltArtifacts();
            Log("ExtProxy artifacts ready");
        }

        Log($"Proxy ready: {FileStamp(Paths.ProxySrc)} sha={FileSha256(Paths.ProxySrc)[..12]}…");
        Log("Injector ready: " + Paths.BootSrc);
        SweepLiveProxyClutter();
    }

    /// <summary>
    /// Remove obsolete Launch clutter from the Ascension install (legacy deployments).
    /// Does not write any GMToolBox artifacts into the game directory.
    /// NEVER deletes Ascension stock files (Ascension.exe, Extensions.dll, MMgr64.exe, …).
    /// </summary>
    public void SweepLiveProxyClutter()
    {
        if (!Directory.Exists(Paths.LiveDir))
            return;

        string live = Paths.LiveDir;
        // Protected stock files — never delete from Ascension live:
        //   Ascension.exe, Extensions.dll, MMgr64.exe, SetMoveConfig.exe, Data\, …
        TryDelete(Path.Combine(live, "AscensionBoot.exe"));
        TryDelete(Path.Combine(live, "AscensionBoot.exe.new"));
        TryDelete(Path.Combine(live, "ExtProxy64.dll.new"));
        TryDelete(Path.Combine(live, "Ascension.launch.exe"));
        TryDelete(Path.Combine(live, "ExtProxy64.dll"));
        TryDelete(Path.Combine(live, "ExtProxy64.log"));
        TryDelete(Path.Combine(live, "ExtProxy64.pid"));

        try
        {
            foreach (string path in Directory.EnumerateFiles(live, "ExtProxy64.dll.bak-*"))
            {
                TryDelete(path);
                Log("Removed live clutter " + Path.GetFileName(path));
            }
            foreach (string path in Directory.EnumerateFiles(live, "Ascension.go.*.exe"))
            {
                TryDelete(path);
                Log("Removed live clutter " + Path.GetFileName(path));
            }
        }
        catch { }
    }
    /// <summary>Verifies stock game files exist; never copies into the Ascension install.</summary>
    public void DeployProxyFromRepo()
    {
        EnsureBuiltArtifacts();
        if (!File.Exists(Paths.StockExt))
            throw new FileNotFoundException("Missing stock Extensions.dll: " + Paths.StockExt);
        if (!File.Exists(Paths.StockExe))
            throw new FileNotFoundException("Missing stock Ascension.exe: " + Paths.StockExe);

        var extLen = new FileInfo(Paths.StockExt).Length;
        if (extLen < 1_000_000)
            throw new InvalidOperationException($"Extensions.dll looks wrong ({extLen} bytes). Repair via launcher.");

        Log("Stock game OK (Extensions.dll + Ascension.exe). Nothing written to Ascension install.");
        Log("Launch stages into: " + Paths.RuntimeDir);
        SweepLiveProxyClutter();
    }

    void CopyBootBestEffort(bool force = false)
    {
        if (!File.Exists(Paths.BootSrc))
            return;
        try
        {
            if (!force && File.Exists(Paths.BootDst)
                && FileSha256(Paths.BootSrc) == FileSha256(Paths.BootDst))
                return;
            File.Copy(Paths.BootSrc, Paths.BootDst, overwrite: true);
        }
        catch (Exception ex)
        {
            Log("AscensionBoot.exe copy: " + ex.Message);
            try
            {
                File.Copy(Paths.BootSrc, Paths.BootDstNew, overwrite: true);
                Log("Staged AscensionBoot.exe.new");
            }
            catch { }
        }
    }

    void ClearStaleNewArtifacts(string repoHash)
    {
        try
        {
            if (File.Exists(Paths.ProxyDstNew))
            {
                string newHash = FileSha256(Paths.ProxyDstNew);
                if (!string.Equals(newHash, repoHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(Paths.ProxyDstNew);
                    Log("Cleared stale ExtProxy64.dll.new");
                }
                else
                {
                    TryDelete(Paths.ProxyDstNew);
                }
            }
            TryDelete(Paths.BootDstNew);
        }
        catch { }
    }

    void VerifyDeployedProxy(string expectedSha)
    {
        if (!File.Exists(Paths.ProxyDst))
            throw new InvalidOperationException("Deploy verify FAILED: live ExtProxy64.dll missing after copy");

        long want = new FileInfo(Paths.ProxySrc).Length;
        long got = new FileInfo(Paths.ProxyDst).Length;
        if (want != got)
            throw new InvalidOperationException(
                $"Deploy verify FAILED: deployed {got}b but source is {want}b");

        string liveSha = FileSha256(Paths.ProxyDst);
        if (!string.Equals(liveSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Deploy verify FAILED: SHA256 mismatch after copy (live != repo)");
    }

    public void Bootstrap()
    {
        EnsureLatestProxyDeployed(forceRebuild: false);
    }
    /// <summary>Clear stale MMgr64 once per session without killing game clients.</summary>
    public void ClearStaleMmgrOnly()
    {
        KillByName("MMgr64", "clearing stale HD-patch memory manager");
        if (Process.GetProcessesByName("MMgr64").Length > 0)
            Thread.Sleep(400);
    }

    public void KillPid(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            Log($"  killing pid={p.Id} {p.ProcessName}");
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Log("  kill pid: " + ex.Message);
        }
    }

    /// <summary>
    /// Close any Ascension.launch that has this instance's launch EXE open,
    /// plus leftover AscensionBoot message boxes, so patching can take exclusive write.
    /// </summary>
    void UnlockLaunchExe(string launchExe)
    {
        try
        {
            string want = Path.GetFullPath(launchExe);
            foreach (var p in Process.GetProcessesByName("Ascension.launch"))
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!string.Equals(Path.GetFullPath(path), want, StringComparison.OrdinalIgnoreCase))
                        continue;
                    Log($"Unlock: stopping Ascension.launch pid={p.Id} holding {want}");
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(8000);
                }
                catch (Exception ex)
                {
                    Log("Unlock Ascension.launch: " + ex.Message);
                }
            }

            foreach (var p in Process.GetProcessesByName("AscensionBoot"))
            {
                try
                {
                    Log($"Unlock: stopping stuck AscensionBoot pid={p.Id}");
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
                catch { }
            }

            if (File.Exists(launchExe))
            {
                try { File.SetAttributes(launchExe, FileAttributes.Normal); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log("UnlockLaunchExe: " + ex.Message);
        }
    }

    /// <summary>
    /// Launch one hooked client into Runtime\inst{id} without killing other instances.
    /// </summary>
    public uint LaunchOne(int instanceId)
    {
        if (!SettingsStore.IsFullyConfigured())
            throw new InvalidOperationException(SettingsStore.DescribeMissing());
        if (!File.Exists(Paths.StockExe))
            throw new FileNotFoundException(Paths.StockExe);
        if (!File.Exists(Paths.BootSrc))
            throw new FileNotFoundException(
                "Missing injector: " + Paths.BootSrc + " — run build.ps1 / package dist");

        if (ProxyBuildIsStale())
        {
            Log("Proxy sources newer than ExtProxy64.dll — rebuilding");
            RebuildProxyArtifacts();
        }
        else
            EnsureBuiltArtifacts();

        SweepLiveProxyClutter();
        RuntimeStaging.EnsureReady(instanceId, Log);

        string runtime = Paths.RuntimeDirFor(instanceId);
        string launchExe = Path.Combine(runtime, "Ascension.launch.exe");
        UnlockLaunchExe(launchExe);

        // Snapshot existing launch pids so we can detect the new one.
        var before = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("Ascension.launch"))
        {
            try { before.Add(p.Id); } catch { }
        }

        var psi = new ProcessStartInfo
        {
            FileName = Paths.BootSrc,
            Arguments = "\"" + Paths.StockExe + "\" \"" + runtime + "\"",
            WorkingDirectory = Paths.ProxyDir,
            UseShellExecute = false,
        };
        Log($"Starting AscensionBoot → {runtime}\\Ascension.launch.exe (instance {instanceId})");
        Log($"AddOns sync roots include: {Paths.StockInterfaceAddOns}");
        var boot = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start AscensionBoot.exe");
        if (!boot.WaitForExit(60_000))
        {
            try { boot.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("AscensionBoot.exe did not exit within 60s");
        }
        if (boot.ExitCode != 0)
            throw new InvalidOperationException("AscensionBoot failed (exit " + boot.ExitCode + ")");

        Thread.Sleep(600);
        foreach (var p in Process.GetProcessesByName("Ascension.launch"))
        {
            try
            {
                if (before.Contains(p.Id)) continue;
                Log($"Instance {instanceId} started pid={p.Id}");
                return (uint)p.Id;
            }
            catch { }
        }

        // Fallback: pid file written by ExtProxy after inject
        for (int i = 0; i < 40; i++)
        {
            var pid = ProxyDiscovery.ReadPidFile(Paths.PidFileFor(instanceId));
            if (pid is uint p && ProxyDiscovery.IsPidAlive(p))
            {
                Log($"Instance {instanceId} pid-file → {p}");
                return p;
            }
            Thread.Sleep(250);
        }

        Log($"Instance {instanceId}: client pid not found yet");
        return 0;
    }

    /// <summary>
    /// Prepare portable Runtime under the app root, then start via AscensionBoot.
    /// Stock Ascension install is read-only — Launch writes only into dist/Runtime.
    /// Legacy single-instance entry: kills priors then launches instance 1.
    /// </summary>
    public int LaunchHookedGame()
    {
        if (!SettingsStore.IsFullyConfigured())
            throw new InvalidOperationException(SettingsStore.DescribeMissing());
        if (!File.Exists(Paths.StockExe))
            throw new FileNotFoundException(Paths.StockExe);
        if (!File.Exists(Paths.BootSrc))
            throw new FileNotFoundException(
                "Missing injector: " + Paths.BootSrc + " — run build.ps1 / package dist");

        if (ProxyBuildIsStale())
        {
            Log("Proxy sources newer than ExtProxy64.dll — rebuilding in " + Paths.ProxyDir);
            RebuildProxyArtifacts();
        }
        else
        {
            EnsureBuiltArtifacts();
            Log("ExtProxy ready: " + FileStamp(Paths.ProxySrc));
        }

        if (GoClientRunning() || LaunchClientRunning())
        {
            Log("Prior hooked client still running — stopping so Launch picks up fresh ExtProxy");
            KillHookedClients();
            Thread.Sleep(800);
        }
        else
        {
            KillByName("MMgr64", "clearing stale HD-patch memory manager");
            if (Process.GetProcessesByName("MMgr64").Length > 0) Thread.Sleep(500);
        }

        return (int)LaunchOne(1);
    }

    bool LaunchClientRunning()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.Equals("Ascension.launch", StringComparison.OrdinalIgnoreCase)
                    || p.ProcessName.StartsWith("Ascension.go", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
    }

    void KillHookedClients()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName;
                if (!n.StartsWith("Ascension.go", StringComparison.OrdinalIgnoreCase)
                    && !n.Equals("Ascension.launch", StringComparison.OrdinalIgnoreCase))
                    continue;
                Log($"  killing pid={p.Id} {n}");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log("  kill: " + ex.Message);
            }
        }
        // Kill the Ascension HD-patch companion (MMgr64.exe) too: a stale memory
        // manager from a prior session makes the next client's Extensions.dll assert
        // "Failed to initialize memory bridge" when it connects to a dead bridge.
        KillByName("MMgr64", "  killing stale HD-patch memory manager");
    }

    // Kill every process whose name starts with `namePrefix` (case-insensitive),
    // e.g. the Ascension HD patch's MMgr64.exe companion. Prevents bridge reuse.
    void KillByName(string namePrefix, string logMsg)
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                Log(logMsg + $" pid={p.Id} {p.ProcessName}");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log("  kill " + namePrefix + ": " + ex.Message);
            }
        }
    }

    void KillGoClients() => KillHookedClients();

    void SweepStaleGoCopies()
    {
        try
        {
            if (Directory.Exists(Paths.RuntimeDir))
            {
                foreach (string path in Directory.EnumerateFiles(Paths.RuntimeDir, "Ascension.go.*.exe"))
                {
                    try
                    {
                        File.Delete(path);
                        Log("Removed clutter " + Path.GetFileName(path));
                    }
                    catch (Exception ex)
                    {
                        Log("Could not remove " + Path.GetFileName(path) + ": " + ex.Message);
                    }
                }
            }
            TryDelete(Paths.ProxyDstNew);
            TryDelete(Paths.BootDstNew);
            SweepLiveProxyClutter();
        }
        catch { }
    }

    int LaunchViaAscensionBoot()
    {
        // AscensionBoot.exe <stock.exe> <runtimeDir>
        // Writes Ascension.launch.exe + ExtProxy64.dll into Runtime only.
        var psi = new ProcessStartInfo
        {
            FileName = Paths.BootSrc,
            Arguments = "\"" + Paths.StockExe + "\" \"" + Paths.RuntimeDir + "\"",
            WorkingDirectory = Paths.ProxyDir,
            UseShellExecute = false,
        };
        Log("Starting AscensionBoot → Runtime\\Ascension.launch.exe");
        var boot = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start AscensionBoot.exe");
        if (!boot.WaitForExit(60_000))
        {
            try { boot.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("AscensionBoot.exe did not exit within 60s (UAC pending?)");
        }
        if (boot.ExitCode != 0)
        {
            string hint = boot.ExitCode switch
            {
                3 => "stock Ascension.exe missing",
                4 => "could not write Runtime\\Ascension.launch.exe",
                5 => "patch Extensions.dll→ExtProxy64.dll failed",
                6 => "ExtProxy64.dll missing beside AscensionBoot or Runtime stage failed",
                7 => "start failed (often UAC/elevation — approve the prompt)",
                8 => "Runtime Data\\ missing (junction setup failed)",
                _ => "see AscensionBoot message box",
            };
            throw new InvalidOperationException(
                "AscensionBoot failed (exit " + boot.ExitCode + ": " + hint + ")");
        }

        // Boot returns after CreateProcess; find the launched client pid.
        Thread.Sleep(400);
        foreach (var p in Process.GetProcessesByName("Ascension.launch"))
        {
            try
            {
                Log("Started pid=" + p.Id + " Ascension.launch.exe");
                return p.Id;
            }
            catch { }
        }
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.StartsWith("Ascension", StringComparison.OrdinalIgnoreCase)
                    && !p.ProcessName.Equals("Ascension", StringComparison.OrdinalIgnoreCase))
                {
                    Log("Started pid=" + p.Id + " " + p.ProcessName);
                    return p.Id;
                }
            }
            catch { }
        }
        Log("AscensionBoot OK but client pid not found yet — WaitForProxy will poll");
        return boot.Id;
    }

    int LaunchPatchedCopy(string destExe)
    {
        // Legacy fallback — prefer LaunchViaAscensionBoot.
        Log("Copy stock to " + Path.GetFileName(destExe));
        File.Copy(Paths.StockExe, destExe, overwrite: true);
        File.SetAttributes(destExe, FileAttributes.Normal);

        int patches = PatchLoadLibraryName(destExe);
        Log("Patched Extensions.dll -> ExtProxy64.dll (" + patches + " sites)");
        if (patches <= 0)
            throw new InvalidOperationException("patch failed, Extensions.dll string not found in Ascension.exe copy");

        File.Copy(Paths.ProxySrc, Paths.ProxyDst, overwrite: true);
        string sha = FileSha256(Paths.ProxyDst);
        Log($"Pre-start ExtProxy64.dll sha={sha[..12]}… {FileStamp(Paths.ProxyDst)}");

        Log("Starting hooked client");
        var psi = new ProcessStartInfo
        {
            FileName = destExe,
            WorkingDirectory = Paths.RuntimeDir,
            UseShellExecute = true,
        };
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null");
        Log("Started pid=" + proc.Id + " " + destExe);
        return proc.Id;
    }

    static int PatchLoadLibraryName(string exePath)
    {
        byte[] oldBytes = Encoding.ASCII.GetBytes("Extensions.dll\0");
        byte[] newBytes = Encoding.ASCII.GetBytes("ExtProxy64.dll\0");
        byte[] data = File.ReadAllBytes(exePath);
        int patches = 0;
        for (int i = 0; i <= data.Length - oldBytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < oldBytes.Length; j++)
            {
                if (data[i + j] != oldBytes[j]) { match = false; break; }
            }
            if (!match) continue;
            Buffer.BlockCopy(newBytes, 0, data, i, newBytes.Length);
            patches++;
        }
        if (patches > 0)
            File.WriteAllBytes(exePath, data);
        return patches;
    }

    public static bool GoClientRunning() =>
        Process.GetProcesses().Any(p =>
        {
            try
            {
                string n = p.ProcessName;
                return n.Equals("Ascension.launch", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("Ascension.go", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        });

    public static bool AscensionProcessRunning() =>
        Process.GetProcesses().Any(p =>
        {
            try
            {
                string n = p.ProcessName;
                return n.StartsWith("Ascension", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        });

    public static bool RingAvailable()
    {
        try
        {
            var pid = ProxyDiscovery.ReadActivePid();
            if (pid is null) return false;
            using var mmf = MemoryMappedFile.OpenExisting(
                ProxyDiscovery.RingNameForPid(pid.Value), MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, Marshal.SizeOf<PktRingHeader>(), MemoryMappedFileAccess.Read);
            view.Read(0, out PktRingHeader hdr);
            return hdr.Magic == IpcConstants.PktMagic;
        }
        catch { return false; }
    }

    public static bool PipePingOnce() => ProxyClient.PingOnceTimed(1500);

    public async Task WaitForProxyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            bool go = GoClientRunning();
            bool ring = RingAvailable();
            bool pipe = false;
            if (go)
                pipe = await Task.Run(PipePingOnce, ct);
            Log($"wait client={(go ? "yes" : "no")} ring={(ring ? "yes" : "no")} pipe={(pipe ? "yes" : "no")} ({(int)sw.Elapsed.TotalSeconds}s)");
            if (go && pipe)
            {
                Log(ring ? "Proxy READY (pipe+ring)" : "Proxy READY (pipe only)");
                return;
            }

            if (go && ring && sw.Elapsed > TimeSpan.FromSeconds(5))
            {
                Log("Proxy READY (ring; will connect pipe next)");
                return;
            }
            await Task.Delay(1000, ct);
        }

        string tip = File.Exists(Paths.ProxyLog)
            ? " ExtProxy64.log: " + string.Join(" | ", File.ReadLines(Paths.ProxyLog).Reverse().Take(5).Reverse())
            : " (no ExtProxy64.log in Runtime — DLL never loaded)";
        throw new TimeoutException("Hooked client / ExtProxy did not come up." + tip);
    }

    public void RemoveWACasterAddon() => RemoveLiveAddon("AscensionWACaster", "WACaster");

    public void RemoveRotAddon() => RemoveLiveAddon("AscensionRot", "AscensionRot");

    public void InstallCombatAddons()
    {
        try { RemoveRotAddon(); } catch (Exception ex) { Log("AscensionRot remove failed: " + ex.Message); }
        try { RemoveWACasterAddon(); } catch (Exception ex) { Log("WACaster remove failed: " + ex.Message); }
        try { RemoveDiscoverAddon(); } catch { }
    }

    private void RemoveLiveAddon(string addonName, string label)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(Paths.StockInterfaceAddOns))
            roots.Add(Paths.StockInterfaceAddOns);
        roots.Add(Paths.LiveAddOns);
        int n = Math.Clamp(SettingsStore.Current.InstanceCount, 1, GmtLimits.MaxInstances);
        for (int i = 1; i <= Math.Max(n, 2); i++)
            roots.Add(Paths.LiveAddOnsFor(i));
        try
        {
            if (Directory.Exists(Paths.RuntimeDir))
            {
                foreach (string dir in Directory.EnumerateDirectories(Paths.RuntimeDir, "inst*"))
                    roots.Add(Path.Combine(dir, "Interface", "AddOns"));
            }
        }
        catch { }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (!seen.Add(root)) continue;
            string dir = Path.Combine(root, addonName);
            if (!Directory.Exists(dir)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                Log($"Removed legacy {label} from {dir}");
            }
            catch (Exception ex)
            {
                Log($"{label} remove failed ({dir}): " + ex.Message);
            }
        }
    }

    public void RemoveDiscoverAddon()
    {
        string dir = Path.Combine(Paths.LiveAddOns, "AscensionDiscover");
        if (!Directory.Exists(dir)) return;
        try
        {
            Directory.Delete(dir, recursive: true);
            Log("Removed legacy AscensionDiscover addon");
        }
        catch (Exception ex)
        {
            Log("AscensionDiscover remove failed: " + ex.Message);
        }
    }

    static string FileSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash);
    }

    static string FileStamp(string path)
    {
        var fi = new FileInfo(path);
        return $"{fi.Length}b {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
    }

    static string TrimTail(string s, int max)
    {
        s = (s ?? "").Trim();
        if (s.Length <= max) return s;
        return s[^max..];
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static bool IsSharingViolation(Exception ex)
    {
        const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
        const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021);
        return ex.HResult == ERROR_SHARING_VIOLATION || ex.HResult == ERROR_LOCK_VIOLATION
            || (ex.InnerException is not null && IsSharingViolation(ex.InnerException));
    }
}
