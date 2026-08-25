using HeadlessClient.Domain.Abstractions;
using MoonSharp.Interpreter;

namespace HeadlessClient.Infrastructure.Lua;

public sealed class Lua51AddonHost : IAddonHost
{
    private readonly IHeadlessOptions _options;
    private readonly IObjectDirectory _objects;
    private readonly IWorldActions _world;
    private readonly List<string> _loadedAddons = new();
    private readonly List<string> _firedEvents = new();
    private readonly object _gate = new();
    private Script? _script;
    private readonly List<string> _printLog = new();

    public Lua51AddonHost(
        IHeadlessOptions options,
        IObjectDirectory objects,
        IWorldActions world)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public IReadOnlyList<string> LoadedAddons => _loadedAddons;
    public IReadOnlyList<string> FiredEvents => _firedEvents;

    public DynValue GetGlobal(string name)
    {
        if (_script is null)
        {
            throw new InvalidOperationException("Addons have not been loaded.");
        }

        return _script.Globals.Get(name);
    }

    public Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_script is not null)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_script is not null)
            {
                return Task.CompletedTask;
            }

            // Lazy sandbox — do not preload every EnabledAddon (some Lua patterns
            // exceed MoonSharp limits and would block unrelated @addons run calls).
            _script = CreateSandbox();
            _loadedAddons.Clear();
            _firedEvents.Clear();
            _printLog.Clear();
        }

        return Task.CompletedTask;
    }

    public async Task LoadConfiguredAddonsAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var script = _script ?? throw new InvalidOperationException("Sandbox not ready.");
        var addonsRoot = ResolveAddonsRoot(_options.AddonsRoot);
        foreach (var addonName in _options.EnabledAddons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(addonName))
            {
                continue;
            }

            try
            {
                await LoadAddonIntoScriptAsync(script, addonsRoot, addonName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[lua] skip preload {addonName}: {ex.Message}");
            }
        }
    }

    public async Task<string> LoadAddonByNameAsync(string addonName, CancellationToken cancellationToken)
    {
        addonName = (addonName ?? "").Trim();
        if (addonName.Length == 0)
        {
            throw new ArgumentException("Addon name required.");
        }

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var script = _script ?? throw new InvalidOperationException("Sandbox not ready.");
        if (_loadedAddons.Any(a => a.Equals(addonName, StringComparison.OrdinalIgnoreCase)))
        {
            return addonName;
        }

        var addonsRoot = ResolveAddonsRoot(_options.AddonsRoot);

        // Soft-load shared GM stubs first when present (OptionalDeps for most bots).
        if (!addonName.Equals("GmShared", StringComparison.OrdinalIgnoreCase)
            && !_loadedAddons.Any(a => a.Equals("GmShared", StringComparison.OrdinalIgnoreCase))
            && Directory.Exists(Path.Combine(addonsRoot, "GmShared")))
        {
            try
            {
                await LoadAddonIntoScriptAsync(script, addonsRoot, "GmShared", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[lua] optional GmShared load failed: {ex.Message}");
            }
        }

        await LoadAddonIntoScriptAsync(script, addonsRoot, addonName, cancellationToken).ConfigureAwait(false);
        return addonName;
    }

    public Task<object> EvalAsync(string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("code required");
        }

        var script = _script ?? throw new InvalidOperationException("Sandbox not ready — load addons first.");
        lock (_gate)
        {
            _printLog.Clear();
            var result = script.DoString(code);
            return Task.FromResult<object>(new
            {
                ok = true,
                type = result.Type.ToString(),
                value = result.ToPrintString(),
                print = _printLog.ToArray()
            });
        }
    }

    public Task<object> RunSlashCommandAsync(string slashLine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        slashLine = (slashLine ?? "").Trim();
        if (slashLine.Length == 0)
        {
            throw new ArgumentException("slash command required");
        }

        var script = _script ?? throw new InvalidOperationException("Sandbox not ready.");
        lock (_gate)
        {
            _printLog.Clear();
            script.Globals["__HC_SLASH_LINE"] = slashLine;
            const string lua = """
local line = rawget(_G, "__HC_SLASH_LINE") or ""
local cmd, msg = string.match(line, "^%s*(/%S+)%s*(.*)$")
if not cmd then return "bad_slash" end
local cmdU = string.upper(cmd)
local list = rawget(_G, "SlashCmdList")
if type(list) == "table" then
  for name, fn in pairs(list) do
    if type(fn) == "function" and type(name) == "string" then
      local i = 1
      while true do
        local slash = rawget(_G, "SLASH_" .. name .. i)
        if type(slash) ~= "string" then break end
        if string.upper(slash) == cmdU then
          local ok, err = pcall(fn, msg or "", cmdU)
          if not ok then return tostring(err) end
          return "ok:" .. name
        end
        i = i + 1
      end
    end
  end
  -- Fallback: handler key matches command body (BGAFK ↔ /bgafk)
  local key = string.upper((string.match(cmd, "^/(.+)$") or ""))
  local fn = list[key]
  if type(fn) == "function" then
    local ok, err = pcall(fn, msg or "", cmdU)
    if not ok then return tostring(err) end
    return "ok:" .. key
  end
end
local hc = rawget(_G, "__HC_Slash")
if type(hc) == "function" then return hc(cmdU, msg) end
return "no_handler:" .. cmdU
""";
            var result = script.DoString(lua);
            return Task.FromResult<object>(new
            {
                ok = true,
                result = result.ToPrintString(),
                print = _printLog.ToArray()
            });
        }
    }

    public Task FireEventAsync(string eventName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("Event name is required.", nameof(eventName));
        }

        if (_script is null)
        {
            throw new InvalidOperationException("Addons have not been loaded.");
        }

        _firedEvents.Add(eventName);

        var fire = _script.Globals.Get("__HC_FireEvent");
        if (fire.Type == DataType.Function)
        {
            _script.Call(fire, DynValue.NewString(eventName));
        }
        else
        {
            var onEvent = _script.Globals.Get("OnEvent");
            if (onEvent.Type == DataType.Function)
            {
                _script.Call(onEvent, DynValue.NewString(eventName));
            }
        }

        return Task.CompletedTask;
    }

    private Script CreateSandbox()
    {
        var script = new Script(CoreModules.Preset_SoftSandbox);
        WowApiStubTable.Register(script);
        new ExtProxyNativeStubs(_objects, _world).Register(script);
        script.Globals["SlashCmdList"] = new Table(script);
        script.Globals["print"] = DynValue.NewCallback((_, args) =>
        {
            var parts = new string[args.Count];
            for (var i = 0; i < args.Count; i++)
            {
                parts[i] = args[i].ToPrintString();
            }

            var line = string.Join("\t", parts);
            lock (_gate)
            {
                _printLog.Add(line);
            }

            Console.WriteLine("[lua] " + line);
            return DynValue.Nil;
        });
        return script;
    }

    private async Task LoadAddonIntoScriptAsync(
        Script script,
        string addonsRoot,
        string addonName,
        CancellationToken cancellationToken)
    {
        var addonDir = Path.Combine(addonsRoot, addonName);
        if (!Directory.Exists(addonDir))
        {
            throw new DirectoryNotFoundException($"Addon folder not found: {addonDir}");
        }

        var manifest = TocAddonLoader.LoadManifest(addonDir);
        lock (_gate)
        {
            // Scope RegisterEvent to this addon's declared/needed events while its files load.
            script.Globals["__HC_CURRENT_ADDON"] = manifest.Name;
            var eventsTable = new Table(script);
            var hasDeclared = false;
            foreach (var ev in manifest.DeclaredEvents ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(ev))
                {
                    eventsTable[ev.ToUpperInvariant()] = true;
                    hasDeclared = true;
                }
            }

            var map = script.Globals.Get("__HC_ADDON_EVENTS");
            Table addonEvents;
            if (map.Type == DataType.Table)
            {
                addonEvents = map.Table;
            }
            else
            {
                addonEvents = new Table(script);
                script.Globals["__HC_ADDON_EVENTS"] = addonEvents;
            }

            // Empty TOC allowlist → leave unset so stub permits RegisterEvent (legacy).
            if (hasDeclared)
            {
                addonEvents[manifest.Name] = eventsTable;
            }
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(file))
            {
                throw new FileNotFoundException($"TOC lists missing Lua file: {file}");
            }

            var source = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                script.DoString(source);
            }
        }

        lock (_gate)
        {
            script.Globals["__HC_CURRENT_ADDON"] = "";
            if (!_loadedAddons.Any(a => a.Equals(manifest.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _loadedAddons.Add(manifest.Name);
            }
        }
    }

    private static string ResolveAddonsRoot(string addonsRoot)
    {
        if (string.IsNullOrWhiteSpace(addonsRoot))
        {
            throw new InvalidOperationException("AddonsRoot is not configured.");
        }

        if (Path.IsPathRooted(addonsRoot))
        {
            return Path.GetFullPath(addonsRoot);
        }

        var fromCwd = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), addonsRoot));
        if (Directory.Exists(fromCwd))
        {
            return fromCwd;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, addonsRoot));
    }
}
