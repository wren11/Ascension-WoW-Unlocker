using System.Diagnostics;
using System.Text;

namespace AscensionNetTool;

/// <summary>
/// Prepares <c>dist/Runtime</c> so Ascension.launch.exe can sit beside Data\
/// without writing into the Ascension installation directory.
/// Nav mesh paths come only from settings (maps + mmtiles) — never hardcoded.
/// </summary>
static class RuntimeStaging
{
    static readonly string[] LiveDllsToLink =
    {
        "Extensions.dll",
        "discord_game_sdk.dll",
        "DivxDecoder.dll",
        "DivxTac.dll",
        "SetMoveConfig.exe",
    };

    /// <summary>
    /// Binaries shipped under dist\ (from vendor\) and staged into Runtime.
    /// Never sourced by deleting/replacing Ascension live copies.
    /// </summary>
    static readonly string[] PackagedSidecars =
    {
        "MMgr64.exe",
    };

    static readonly string[] LiveDirsToJunction =
    {
        "Data",
        "Cache",
        "WTF",
        "Logs",
        "Errors",
    };

    public static void EnsureReady(Action<string>? log = null) =>
        EnsureReady(1, log);

    public static void EnsureReady(int instanceId, Action<string>? log = null)
    {
        void L(string m) => log?.Invoke(m);

        if (!SettingsStore.IsFullyConfigured())
            throw new InvalidOperationException(SettingsStore.DescribeMissing());

        string runtime = Paths.RuntimeDirFor(instanceId);
        string live = Paths.LiveDir;
        Directory.CreateDirectory(runtime);

        foreach (string name in LiveDirsToJunction)
        {
            string src = Path.Combine(live, name);
            string dst = Path.Combine(runtime, name);
            if (!Directory.Exists(src))
            {
                L($"skip junction {name} (missing in live)");
                continue;
            }
            EnsureJunction(dst, src, L);
        }

        string mmapsParent = Path.Combine(runtime, "mmaps");
        Directory.CreateDirectory(mmapsParent);

        // Nav junctions are optional — only create when maps/mmtiles are configured.
        string mapsLink = Path.Combine(mmapsParent, "maps");
        string tilesLink = Path.Combine(mmapsParent, "mmtiles");
        if (SettingsStore.IsMapsConfigured())
            EnsureJunction(mapsLink, Paths.MapsDir, L);
        else
        {
            TryRemoveReparse(mapsLink, L);
            L("nav maps skipped (optional path not set)");
        }
        if (SettingsStore.IsMmapsConfigured())
            EnsureJunction(tilesLink, Paths.MmapsDir, L);
        else
        {
            TryRemoveReparse(tilesLink, L);
            L("nav mmtiles skipped (optional path not set)");
        }

        TryRemoveReparse(Path.Combine(mmapsParent, "classic_era"), L);
        TryRemoveReparse(Path.Combine(runtime, "Maps"), L);

        foreach (string dll in LiveDllsToLink)
        {
            string src = Path.Combine(live, dll);
            string dst = Path.Combine(runtime, dll);
            if (!File.Exists(src))
                continue;
            CopyOrReplace(src, dst, L);
        }

        foreach (string name in PackagedSidecars)
        {
            string packaged = Path.Combine(Paths.AppRoot, name);
            string dst = Path.Combine(runtime, name);
            if (File.Exists(packaged))
                CopyOrReplace(packaged, dst, L);
        }

        // Stage ExtProxy + Boot into this instance runtime.
        if (File.Exists(Paths.ProxySrc))
            CopyOrReplace(Paths.ProxySrc, Path.Combine(runtime, "ExtProxy64.dll"), L);
        if (File.Exists(Paths.BootSrc))
            CopyOrReplace(Paths.BootSrc, Path.Combine(runtime, "AscensionBoot.exe"), L);

        // Per-instance Interface\AddOns: every addon shipped with GMToolBox.
        // Core on the account gates use (Lua + GMT session). Files stay on disk.
        string addonsDst = Path.Combine(runtime, "Interface", "AddOns");
        Directory.CreateDirectory(addonsDst);
        string addonsSrc = Paths.AddonsSourceDir;
        if (Directory.Exists(addonsSrc))
        {
            foreach (string dir in Directory.EnumerateDirectories(addonsSrc))
            {
                string name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (Directory.GetFiles(dir, "*.toc").Length == 0) continue;
                string dst = Path.Combine(addonsDst, name);
                string disabled = dst + "_disabled";
                TryDeleteDir(disabled, L);
                CopyDirectory(dir, dst);
            }
        }

        // Prefer packaged sidecars; fall back to live read-only copy.
        foreach (string name in PackagedSidecars)
        {
            string dst = Path.Combine(runtime, name);
            if (File.Exists(dst)) continue;
            string packaged = Path.Combine(Paths.AppRoot, name);
            string liveSrc = Path.Combine(live, name);
            if (File.Exists(packaged))
                CopyOrReplace(packaged, dst, L);
            else if (File.Exists(liveSrc))
            {
                L("packaged " + name + " missing — copying from Ascension live (read-only)");
                CopyOrReplace(liveSrc, dst, L);
            }
            else
                L("WARNING: " + name + " not in dist\\ or Ascension live");
        }

        WriteNavConfig(runtime, instanceId, L);
        if (instanceId == 1)
            WriteNavConfig(Paths.AppRoot, instanceId, L);

        VerifyInstanceHealthy(instanceId);
        L($"Runtime inst{instanceId} ready: {runtime}");
        L("Nav maps (.mmap): " + (SettingsStore.IsMapsConfigured() ? Paths.MapsDir : "(optional — not set)"));
        L("Nav mmtiles (.mmtile): " + (SettingsStore.IsMmapsConfigured() ? Paths.MmapsDir : "(optional — not set)"));
    }

    /// <summary>
    /// Validates ExtProxy/Boot + optional nav junctions.
    /// Missing maps/mmtiles is allowed (nav calc simply unavailable).
    /// </summary>
    public static void VerifyInstanceHealthy(int instanceId)
    {
        string runtime = Paths.RuntimeDirFor(instanceId);
        if (SettingsStore.IsMapsConfigured())
        {
            AssertJunctionNonEmpty(
                Path.Combine(runtime, "mmaps", "maps"),
                Paths.MapsDir,
                "*.mmap",
                "maps");
        }
        if (SettingsStore.IsMmapsConfigured())
        {
            AssertJunctionNonEmpty(
                Path.Combine(runtime, "mmaps", "mmtiles"),
                Paths.MmapsDir,
                "*.mmtile",
                "mmtiles");
        }

        string proxy = Path.Combine(runtime, "ExtProxy64.dll");
        if (!File.Exists(proxy) || new FileInfo(proxy).Length < 50_000)
            throw new InvalidOperationException(
                $"inst{instanceId}: ExtProxy64.dll missing or too small at {proxy}");

        string boot = Path.Combine(runtime, "AscensionBoot.exe");
        if (!File.Exists(boot) || new FileInfo(boot).Length < 10_000)
            throw new InvalidOperationException(
                $"inst{instanceId}: AscensionBoot.exe missing or too small at {boot}");

        string cfg = Path.Combine(runtime, "ExtProxy.cfg");
        if (!File.Exists(cfg))
            throw new InvalidOperationException($"inst{instanceId}: ExtProxy.cfg missing");

        string cfgText = File.ReadAllText(cfg);
        if (cfgText.IndexOf("live=", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException($"inst{instanceId}: ExtProxy.cfg incomplete (missing live=)");
    }

    public static string? TryResolveLink(string linkPath) => TryGetJunctionTarget(linkPath);

    static void AssertJunctionNonEmpty(
        string linkPath, string expectedTarget, string pattern, string label)
    {
        if (!Directory.Exists(linkPath))
            throw new DirectoryNotFoundException($"Runtime {label} missing: {linkPath}");

        string want = Path.GetFullPath(expectedTarget).TrimEnd('\\', '/');
        string? resolved = TryGetJunctionTarget(linkPath);
        if (resolved is not null)
        {
            string got = Path.GetFullPath(resolved).TrimEnd('\\', '/');
            if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Runtime {label} junction points to '{got}', expected '{want}'");
            }
        }

        bool any;
        try { any = Directory.EnumerateFiles(linkPath, pattern).Any(); }
        catch (Exception ex)
        {
            throw new IOException($"Runtime {label} unreadable at {linkPath}: {ex.Message}", ex);
        }
        if (!any)
            throw new InvalidOperationException(
                $"Runtime {label} resolves to an empty folder (no {pattern}): {linkPath}");
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    static void TryDeleteDir(string path, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        try
        {
            Directory.Delete(path, recursive: true);
            log("removed paid leftover " + path);
        }
        catch (Exception ex)
        {
            log("could not remove " + path + ": " + ex.Message);
        }
    }

    static void TryRemoveReparse(string path, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
                return;
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) == 0)
                return;
            Directory.Delete(path);
            log("removed legacy junction " + path);
        }
        catch { }
    }

    static void WriteNavConfig(string dir, int instanceId, Action<string> log)
    {
        Directory.CreateDirectory(dir);
        string cfg = Path.Combine(dir, "ExtProxy.cfg");
        string body =
            "# GMToolBox nav paths (written on Launch; Ascension install is not modified)" + Environment.NewLine
            + "instance_id=" + instanceId + Environment.NewLine
            + "maps=" + (SettingsStore.IsMapsConfigured() ? Paths.MapsDir : "") + Environment.NewLine
            + "mmtiles=" + (SettingsStore.IsMmapsConfigured() ? Paths.MmapsDir : "") + Environment.NewLine
            + "mmaps=" + (SettingsStore.IsMmapsConfigured() ? Paths.MmapsDir : "") + Environment.NewLine
            + "live=" + Paths.LiveDir + Environment.NewLine;
        File.WriteAllText(cfg, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        log("Wrote " + cfg);
    }

    static void CopyOrReplace(string src, string dst, Action<string> log)
    {
        try
        {
            if (File.Exists(dst))
            {
                var s = new FileInfo(src);
                var d = new FileInfo(dst);
                if (s.Length == d.Length && s.LastWriteTimeUtc == d.LastWriteTimeUtc)
                    return;
                File.SetAttributes(dst, FileAttributes.Normal);
                File.Delete(dst);
            }
            File.Copy(src, dst, overwrite: true);
            log("staged " + Path.GetFileName(dst));
        }
        catch (Exception ex)
        {
            log("stage " + Path.GetFileName(src) + ": " + ex.Message);
            throw;
        }
    }

    public static void EnsureJunction(string linkPath, string targetPath, Action<string>? log = null)
    {
        targetPath = Path.GetFullPath(targetPath.TrimEnd('\\', '/'));
        linkPath = Path.GetFullPath(linkPath.TrimEnd('\\', '/'));

        if (!Directory.Exists(targetPath))
            throw new DirectoryNotFoundException("Junction target missing: " + targetPath);

        if (Directory.Exists(linkPath) || File.Exists(linkPath) || IsReparsePoint(linkPath))
        {
            string? existing = TryGetJunctionTarget(linkPath);
            if (existing is not null
                && string.Equals(
                    Path.GetFullPath(existing).TrimEnd('\\'),
                    targetPath.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            try
            {
                if (IsReparsePoint(linkPath))
                    Directory.Delete(linkPath);
                else if (Directory.Exists(linkPath))
                    Directory.Delete(linkPath, recursive: true);
                else if (File.Exists(linkPath))
                    File.Delete(linkPath);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    "Could not replace Runtime path " + linkPath + ": " + ex.Message, ex);
            }
        }

        string? parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (!CreateJunction(linkPath, targetPath))
            throw new IOException("mklink /J failed: " + linkPath + " → " + targetPath);

        log?.Invoke("junction " + linkPath + " → " + targetPath);
    }

    static bool IsReparsePoint(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
                return false;
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    static string? TryGetJunctionTarget(string linkPath)
    {
        try
        {
            if (!IsReparsePoint(linkPath))
                return null;
            var info = new DirectoryInfo(linkPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return info.ResolveLinkTarget(true)?.FullName;
        }
        catch { }
        return null;
    }

    static bool CreateJunction(string linkPath, string targetPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p is null)
            return false;
        p.WaitForExit(15_000);
        return p.ExitCode == 0 && (Directory.Exists(linkPath) || IsReparsePoint(linkPath));
    }
}
