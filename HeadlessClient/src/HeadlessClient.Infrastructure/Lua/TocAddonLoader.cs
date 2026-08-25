using System.Text.RegularExpressions;

namespace HeadlessClient.Infrastructure.Lua;

public sealed record AddonManifest(
    string Name,
    string Title,
    IReadOnlyList<string> Files,
    string Notes = "",
    string Author = "",
    string Version = "",
    IReadOnlyList<string>? DeclaredEvents = null);

public static class TocAddonLoader
{
    private static readonly Regex RegisterEventRe = new(
        @"RegisterEvent\s*\(\s*[""']([A-Z0-9_]+)[""']\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static AddonManifest LoadManifest(string addonDir)
    {
        if (string.IsNullOrWhiteSpace(addonDir))
        {
            throw new ArgumentException("Addon directory is required.", nameof(addonDir));
        }

        var fullDir = Path.GetFullPath(addonDir);
        if (!Directory.Exists(fullDir))
        {
            throw new DirectoryNotFoundException($"Addon directory not found: {fullDir}");
        }

        var name = new DirectoryInfo(fullDir).Name;
        var tocFiles = Directory.GetFiles(fullDir, "*.toc");
        if (tocFiles.Length == 0)
        {
            throw new FileNotFoundException($"No .toc file found in {fullDir}");
        }

        var tocPath = tocFiles
            .OrderBy(p => Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .First();

        var title = name;
        var notes = "";
        var author = "";
        var version = "";
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();

        foreach (var raw in File.ReadAllLines(tocPath))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                ParseMeta(line, ref title, ref notes, ref author, ref version, declared);
                continue;
            }

            var relative = line.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            files.Add(Path.GetFullPath(Path.Combine(fullDir, relative)));
        }

        // Infer RegisterEvent names from Lua when TOC omits ## HC-Events.
        if (declared.Count == 0)
        {
            foreach (var file in files.Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    foreach (Match m in RegisterEventRe.Matches(File.ReadAllText(file)))
                    {
                        declared.Add(m.Groups[1].Value.ToUpperInvariant());
                    }
                }
                catch
                {
                    // ignore unreadable sources
                }
            }
        }

        return new AddonManifest(
            name,
            title,
            files,
            notes,
            author,
            version,
            declared.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static void ParseMeta(
        string line,
        ref string title,
        ref string notes,
        ref string author,
        ref string version,
        HashSet<string> declared)
    {
        static string ValueAfter(string src, string prefix) =>
            src.Length > prefix.Length ? src[prefix.Length..].Trim() : "";

        if (line.StartsWith("## Title:", StringComparison.OrdinalIgnoreCase))
        {
            title = StripWowUiEscapes(ValueAfter(line, "## Title:"));
        }
        else if (line.StartsWith("## Notes:", StringComparison.OrdinalIgnoreCase))
        {
            notes = StripWowUiEscapes(ValueAfter(line, "## Notes:"));
        }
        else if (line.StartsWith("## Author:", StringComparison.OrdinalIgnoreCase)
                 || line.StartsWith("## X-Author:", StringComparison.OrdinalIgnoreCase))
        {
            author = ValueAfter(line, line.StartsWith("## X-", StringComparison.OrdinalIgnoreCase) ? "## X-Author:" : "## Author:");
        }
        else if (line.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase)
                 || line.StartsWith("## X-Version:", StringComparison.OrdinalIgnoreCase))
        {
            version = ValueAfter(line, line.Contains("X-Version", StringComparison.OrdinalIgnoreCase) ? "## X-Version:" : "## Version:");
        }
        else if (line.StartsWith("## HC-Events:", StringComparison.OrdinalIgnoreCase)
                 || line.StartsWith("## X-HC-Events:", StringComparison.OrdinalIgnoreCase)
                 || line.StartsWith("## RequiredEvents:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = line.IndexOf(':');
            if (idx > 0)
            {
                foreach (var part in line[(idx + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (part.Length > 0)
                    {
                        declared.Add(part.ToUpperInvariant());
                    }
                }
            }
        }
    }

    /// <summary>
    /// Remove WoW UI color/texture escapes so SoftRealm shows "GmApiBrowser" not "|cffe6007eGm|rApiBrowser".
    /// </summary>
    public static string StripWowUiEscapes(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('|') < 0)
        {
            return text ?? "";
        }

        var s = text.Replace("||", "\u0001", StringComparison.Ordinal);
        s = Regex.Replace(s, @"\|[cC][0-9A-Fa-f]{8}", "");
        s = Regex.Replace(s, @"\|[rR]", "");
        s = Regex.Replace(s, @"\|[nN]", " ");
        s = Regex.Replace(s, @"\|T[^|]*\|[tT]", "");
        s = Regex.Replace(s, @"\|H[^|]*\|h(?:\[[^\]]*\]\|h)?", m =>
        {
            var br = Regex.Match(m.Value, @"\[([^\]]*)\]");
            return br.Success ? br.Groups[1].Value : "";
        });
        s = Regex.Replace(s, @"\|.", "");
        return s.Replace("\u0001", "|", StringComparison.Ordinal).Trim();
    }
}
