namespace HeadlessClient.Infrastructure.Auth;

/// <summary>
/// Finds a 638-byte Ascension sealed logon challenge for Tcp auth.
/// Priority: configured path → HeadlessClient/data → latest ExtProxy AuthWire dump.
/// </summary>
public static class AscensionChallengeLocator
{
    public static byte[] Resolve(string? configuredPath, string? runtimeInstDir)
    {
        foreach (var path in CandidatePaths(configuredPath, runtimeInstDir))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == AscensionAuthPacketCodec.WrappedPacketLength)
            {
                AscensionAuthPacketCodec.Parse(bytes);
                return bytes;
            }
        }

        throw new FileNotFoundException(
            "No valid 638-byte Ascension auth challenge found. " +
            "Capture once via ExtProxy AuthWire (ExtProxy64.auth.challenge.<tick>.bin) " +
            "and copy to HeadlessClient/data/ascension-auth-challenge.bin.");
    }

    public static IEnumerable<string> CandidatePaths(string? configuredPath, string? runtimeInstDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return Path.GetFullPath(configuredPath);
        }

        var dataDir = FindHeadlessDataDir();
        if (dataDir is not null)
        {
            yield return Path.Combine(dataDir, "ascension-auth-challenge.bin");
        }

        foreach (var dump in FindAuthWireDumps(runtimeInstDir))
        {
            yield return dump;
        }

        foreach (var dump in FindAuthWireDumps(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "dist", "Runtime", "inst1")))
        {
            yield return dump;
        }
    }

    private static string? FindHeadlessDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(Path.Combine(dir.FullName, "HeadlessClient.sln")))
            {
                return Path.Combine(dir.FullName, "data");
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static IEnumerable<string> FindAuthWireDumps(string? runtimeInstDir)
    {
        if (string.IsNullOrWhiteSpace(runtimeInstDir))
        {
            return Array.Empty<string>();
        }

        try
        {
            var root = Path.GetFullPath(runtimeInstDir);
            if (!Directory.Exists(root))
            {
                return Array.Empty<string>();
            }

            return Directory
                .EnumerateFiles(root, "ExtProxy64.auth.challenge.*.bin", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(3);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
