using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace HeadlessClient.Infrastructure.Protocol;

/// <summary>
/// Decodes LootCollector channel wire: LC1:OP:MID:EncodeForPrint(Deflate(AceSerializer(table))).
/// Full decode via MoonSharp LibDeflate+AceSerializer; falls back to partial Ace field scrape
/// when chat truncated the payload (common on CONF spam).
/// </summary>
public sealed class LootCollectorLc1Decoder
{
    static readonly Regex Lc1Rx = new(
        @"^LC1:(?:(M[1-3]):)?([A-Z0-9]+):([^:]+):(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex AcePairRx = new(
        @"\^S([^\^]+)\^(?:S([^\^]*)|N(-?\d+(?:\.\d+)?)|B([01]))",
        RegexOptions.Compiled);

    readonly object _gate = new();
    Script? _script;
    DynValue? _decodeFn;
    bool _failed;
    readonly string _libsRoot;

    public LootCollectorLc1Decoder(string? libsRoot = null)
    {
        _libsRoot = libsRoot
            ?? Path.Combine(AppContext.BaseDirectory, "LootCollectorLibs");
    }

    public bool TryDecodeMessage(string rawMessage, out string readable, out string? op, out string? mid)
    {
        readable = "";
        op = null;
        mid = null;
        if (string.IsNullOrWhiteSpace(rawMessage) || !rawMessage.StartsWith("LC1:", StringComparison.Ordinal))
        {
            return false;
        }

        var m = Lc1Rx.Match(rawMessage.Trim());
        if (!m.Success)
        {
            return false;
        }

        var multi = m.Groups[1].Success ? m.Groups[1].Value : "";
        op = m.Groups[2].Value;
        mid = m.Groups[3].Value;
        var encoded = m.Groups[4].Value;

        if (multi is "M1" or "M2")
        {
            readable = $"[LootCollector {op} chunk {multi} mid={mid}]";
            return true;
        }

        if (multi == "M3")
        {
            readable = $"[LootCollector {op} chunk M3 mid={mid} (incomplete multipart)]";
            return true;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["op"] = op!,
            ["mid"] = mid!
        };

        try
        {
            EnsureScript();

            // Prefer inflate + field scrape (survives chat truncation / missing ^^).
            if (TryInflateAce(encoded, out var aceRaw))
            {
                ScrapeAceFields(aceRaw, fields);
            }

            // If we still lack core fields, try full AceSerializer deserialize (with ^^ repair).
            if (!fields.ContainsKey("i") && _script is not null && _decodeFn is not null)
            {
                DynValue decoded;
                lock (_gate)
                {
                    decoded = _script.Call(_decodeFn, encoded);
                }

                if (decoded.Type == DataType.Tuple && decoded.Tuple.Length > 0)
                {
                    decoded = decoded.Tuple[0];
                }

                if (decoded.Type == DataType.Table)
                {
                    foreach (var pair in decoded.Table.Pairs)
                    {
                        var key = pair.Key.Type == DataType.String ? pair.Key.String : pair.Key.ToString();
                        if (string.IsNullOrEmpty(key))
                        {
                            continue;
                        }

                        fields[key] = DynToString(pair.Value);
                    }
                }
            }

            readable = FormatDiscovery(fields);
            return true;
        }
        catch
        {
            readable = $"[LootCollector {op} mid={mid}]";
            return true;
        }
    }

    bool TryInflateAce(string encoded, out string aceRaw)
    {
        aceRaw = "";
        EnsureScript();
        if (_script is null)
        {
            return false;
        }

        try
        {
            DynValue r;
            lock (_gate)
            {
                r = _script.Call(_script.Globals.Get("InflateOnly"), encoded);
            }

            if (r.Type == DataType.Tuple && r.Tuple.Length > 0)
            {
                r = r.Tuple[0];
            }

            if (r.Type != DataType.String || string.IsNullOrEmpty(r.String))
            {
                return false;
            }

            aceRaw = r.String;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void ScrapeAceFields(string aceRaw, Dictionary<string, string> fields)
    {
        foreach (Match hit in AcePairRx.Matches(aceRaw))
        {
            var key = hit.Groups[1].Value;
            string val;
            if (hit.Groups[2].Success)
            {
                val = hit.Groups[2].Value;
            }
            else if (hit.Groups[3].Success)
            {
                val = hit.Groups[3].Value;
            }
            else
            {
                val = hit.Groups[4].Value == "1" ? "true" : "false";
            }

            if (!string.IsNullOrEmpty(key))
            {
                fields[key] = val;
            }
        }
    }

    static string FormatDiscovery(IReadOnlyDictionary<string, string> f)
    {
        static string G(IReadOnlyDictionary<string, string> d, string k) =>
            d.TryGetValue(k, out var v) ? v : "";

        var op = G(f, "op");
        if (string.IsNullOrEmpty(op))
        {
            op = "?";
        }

        var dt = int.TryParse(G(f, "dt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dtn) ? dtn : 0;
        var kind = dt switch
        {
            1 => "Worldforged",
            2 => "MysticScroll",
            3 => "Blackmarket",
            _ => dt == 0 ? "Unknown" : $"type{dt}"
        };

        var parts = new List<string> { $"[LootCollector {op}] {kind}" };

        if (long.TryParse(G(f, "i"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var item) && item > 0)
        {
            parts.Add($"item={item}");
        }

        if (int.TryParse(G(f, "z"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var zone) && zone != 0)
        {
            parts.Add($"zone={zone}");
        }

        if (int.TryParse(G(f, "iz"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iz) && iz != 0)
        {
            parts.Add($"subzone={iz}");
        }

        var x = double.TryParse(G(f, "x"), NumberStyles.Float, CultureInfo.InvariantCulture, out var xv) ? xv : 0;
        var y = double.TryParse(G(f, "y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var yv) ? yv : 0;
        if (x != 0 || y != 0)
        {
            parts.Add($"@{x.ToString("0.##", CultureInfo.InvariantCulture)},{y.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        if (int.TryParse(G(f, "q"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) && q != 1)
        {
            parts.Add($"qty={q}");
        }

        var src = G(f, "src");
        if (!string.IsNullOrEmpty(src))
        {
            parts.Add($"src={src}");
        }

        var fp = G(f, "fp");
        if (!string.IsNullOrEmpty(fp) && fp != "An Unnamed Collector")
        {
            parts.Add($"by={fp}");
        }

        var mid = G(f, "mid");
        if (!string.IsNullOrEmpty(mid))
        {
            parts.Add($"mid={mid}");
        }

        if (!aceLooksComplete(f))
        {
            parts.Add("(partial)");
        }

        return string.Join(' ', parts);
    }

    static bool aceLooksComplete(IReadOnlyDictionary<string, string> f) =>
        f.ContainsKey("i") && (f.ContainsKey("x") || f.ContainsKey("y") || f.ContainsKey("z"));

    static string DynToString(DynValue v) => v.Type switch
    {
        DataType.String => v.String,
        DataType.Number => v.Number.ToString(CultureInfo.InvariantCulture),
        DataType.Boolean => v.Boolean ? "true" : "false",
        DataType.Nil => "",
        _ => v.ToString()
    };

    void EnsureScript()
    {
        if (_script is not null || _failed)
        {
            return;
        }

        lock (_gate)
        {
            if (_script is not null || _failed)
            {
                return;
            }

            try
            {
                var libStub = Path.Combine(_libsRoot, "LibStub.lua");
                var libDeflate = Path.Combine(_libsRoot, "LibDeflate.lua");
                var ace = Path.Combine(_libsRoot, "AceSerializer-3.0.lua");
                if (!File.Exists(libStub) || !File.Exists(libDeflate) || !File.Exists(ace))
                {
                    _failed = true;
                    return;
                }

                var script = new Script(CoreModules.Preset_Default);
                script.Options.DebugPrint = _ => { };
                script.Globals["_G"] = script.Globals;
                script.DoString("""
strmatch = string.match
strfind = string.find
strlen = string.len
strsub = string.sub
strbyte = string.byte
strchar = string.char
tinsert = table.insert
tremove = table.remove
""");
                script.DoString(File.ReadAllText(libStub));
                if (script.Globals.Get("LibStub").IsNil())
                {
                    script.DoString("LibStub = _G.LibStub");
                }

                script.DoString("""
if LibStub and LibStub.NewLibrary and not LibStub.NewAscensionLibrary then
  function LibStub:NewAscensionLibrary(major, minor)
    if minor == nil then minor = 1 end
    return self:NewLibrary(major, minor)
  end
end
""");
                script.DoString(File.ReadAllText(libDeflate));
                script.DoString(File.ReadAllText(ace));
                script.DoString("""
function DecodePayload(encoded)
  local LibDeflate = LibStub("LibDeflate")
  local AceSerializer = LibStub("AceSerializer-3.0")
  local decoded = LibDeflate:DecodeForPrint(encoded)
  if not decoded then return nil end
  local decompressed = LibDeflate:DecompressDeflate(decoded)
  if not decompressed then return nil end
  -- Chat truncation often strips the AceSerializer ^^ terminator; repair when possible.
  if not decompressed:find("%^%^", 1, false) and decompressed:sub(1,2) == "^1" then
    decompressed = decompressed .. "^^"
  end
  local ok, data = AceSerializer:Deserialize(decompressed)
  if not ok then return nil end
  return data
end

function InflateOnly(encoded)
  local LibDeflate = LibStub("LibDeflate")
  local decoded = LibDeflate:DecodeForPrint(encoded)
  if not decoded then return nil end
  return LibDeflate:DecompressDeflate(decoded)
end
""");
                _decodeFn = script.Globals.Get("DecodePayload");
                _script = script;
            }
            catch
            {
                _failed = true;
                _script = null;
            }
        }
    }
}
