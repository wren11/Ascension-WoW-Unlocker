using System.Collections.Concurrent;
using System.Text.Json;

namespace AscensionNetTool;

/// <summary>Persisted per-instance recovery snapshot for never-stop relaunch/relog.</summary>
sealed class SessionSnapshot
{
    public int InstanceId { get; set; }
    public uint LastPid { get; set; }
    public string? Account { get; set; }
    public string? Password { get; set; }
    public string? LoginHex { get; set; }
    public string? CharEnumHex { get; set; }
    public List<string> EnabledAddons { get; set; } = new();
    public Dictionary<string, bool> RunningBots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
}

static class SessionSnapshotStore
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    static string Dir => Path.Combine(Paths.AppRoot, "Config", "sessions");

    public static string PathFor(int instanceId) =>
        Path.Combine(Dir, $"inst{instanceId}.json");

    public static void Save(SessionSnapshot snap)
    {
        Directory.CreateDirectory(Dir);
        snap.SavedUtc = DateTime.UtcNow;
        File.WriteAllText(PathFor(snap.InstanceId), JsonSerializer.Serialize(snap, JsonOpts));
    }

    public static SessionSnapshot? Load(int instanceId)
    {
        string path = PathFor(instanceId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path), JsonOpts);
        }
        catch { return null; }
    }
}

/// <summary>
/// Never-stop recovery:
/// process death → relaunch → login-packet replay → wait world → resume bots.
/// world DC (client still up) → relog + resume without relaunch.
/// </summary>
sealed class WatchdogService : IDisposable
{
    const int WorldLostDebounceSec = 12;
    const int DisconnectHintDebounceSec = 3;
    const int WorldRecoverCooldownSec = 45;
    const int PulseSec = 8;
    const int AddonReadySec = 10;

    readonly InstanceManager _instances;
    readonly Action<string> _log;
    readonly Func<int, byte[]?> _loginFor;
    readonly Func<int, byte[]?> _enumFor;
    readonly ConcurrentDictionary<int, DateTime> _recovering = new();
    readonly ConcurrentDictionary<int, string> _phase = new();
    readonly ConcurrentDictionary<int, bool> _sawWorld = new();
    readonly ConcurrentDictionary<int, DateTime> _lostSince = new();
    readonly ConcurrentDictionary<int, DateTime> _nextWorldRecover = new();
    readonly ConcurrentDictionary<int, DateTime> _nextPulse = new();
    readonly ConcurrentDictionary<int, List<string>> _lastBots = new();
    readonly ConcurrentDictionary<int, DateTime> _disconnectHint = new();
    IDisposable? _diedSub;
    readonly System.Threading.Timer _tick;
    int _busy;

    public WatchdogService(
        InstanceManager instances,
        Action<string> log,
        Func<int, byte[]?> loginFor,
        Func<int, byte[]?> enumFor)
    {
        _instances = instances;
        _log = log;
        _loginFor = loginFor;
        _enumFor = enumFor;
        _diedSub = EventBus.Subscribe<InstanceDiedEvent>(OnDied);
        _tick = new System.Threading.Timer(_ => SafeTick(), null, 4000, 4000);
    }

    public object StatusDto()
    {
        var s = SettingsStore.Current;
        return new
        {
            enabled = s.WatchdogEnabled,
            autoRelaunch = s.WatchdogAutoRelaunch,
            autoRelog = s.WatchdogAutoRelog,
            restoreBots = s.WatchdogRestoreBots,
            phases = _phase.Select(kv => new { id = kv.Key, phase = kv.Value }).ToList(),
            recovering = _recovering.Keys.ToList(),
        };
    }

    public void NoteHealthy(GameInstance inst, byte[]? login, IEnumerable<string>? runningBots = null)
    {
        if (inst.Id <= 0) return;
        var snap = SessionSnapshotStore.Load(inst.Id) ?? new SessionSnapshot { InstanceId = inst.Id };
        snap.LastPid = inst.Pid;
        if (login is { Length: > 0 })
            snap.LoginHex = Convert.ToHexString(login);
        var enumPkt = _enumFor(inst.Id);
        if (enumPkt is { Length: > 0 })
            snap.CharEnumHex = Convert.ToHexString(enumPkt);
        var bots = runningBots?.ToList()
            ?? (_lastBots.TryGetValue(inst.Id, out var cached) ? cached : null);
        if (bots is not null)
        {
            snap.RunningBots.Clear();
            foreach (var b in bots)
            {
                if (!string.IsNullOrWhiteSpace(b))
                    snap.RunningBots[b.Trim()] = true;
            }
            _lastBots[inst.Id] = snap.RunningBots.Keys.ToList();
        }
        var hint = SettingsStore.Current.Instances.FirstOrDefault(i => i.Id == inst.Id);
        if (hint is not null && !string.IsNullOrWhiteSpace(hint.AccountHint))
            snap.Account = hint.AccountHint;
        if (!string.IsNullOrWhiteSpace(SettingsStore.Current.WatchdogAccount))
            snap.Account = SettingsStore.Current.WatchdogAccount;
        if (!string.IsNullOrWhiteSpace(SettingsStore.Current.WatchdogPassword))
            snap.Password = SettingsStore.Current.WatchdogPassword;
        SessionSnapshotStore.Save(snap);
        _phase[inst.Id] = "healthy";
    }

    public void NoteRunningBots(int instanceId, string csv)
    {
        var bots = ParseBotCsv(csv);
        if (bots.Count == 0 && string.IsNullOrWhiteSpace(csv)) return;
        _lastBots[instanceId] = bots;
        var inst = _instances.ById(instanceId);
        if (inst is not null)
            NoteHealthy(inst, _loginFor(instanceId), bots);
    }

    public void NoteDisconnectHint(GameInstance inst)
    {
        if (inst.Id <= 0) return;
        if (!_sawWorld.TryGetValue(inst.Id, out var saw) || !saw) return;
        _disconnectHint[inst.Id] = DateTime.UtcNow;
        _lostSince.TryAdd(inst.Id, DateTime.UtcNow);
        _phase[inst.Id] = "world_lost_hint";
    }

    /// <summary>Called from the object poll: player GUID present or gone.</summary>
    public void Observe(GameInstance inst, bool inWorld)
    {
        if (inst.Id <= 0) return;
        if (inWorld)
        {
            _sawWorld[inst.Id] = true;
            _lostSince.TryRemove(inst.Id, out _);
            _disconnectHint.TryRemove(inst.Id, out _);
            if (DateTime.UtcNow >= _nextPulse.GetOrAdd(inst.Id, DateTime.MinValue))
            {
                _nextPulse[inst.Id] = DateTime.UtcNow.AddSeconds(PulseSec);
                PulseSession(inst);
                NoteHealthy(inst, _loginFor(inst.Id));
            }
            return;
        }

        if (!_sawWorld.TryGetValue(inst.Id, out var saw) || !saw) return;
        _lostSince.TryAdd(inst.Id, DateTime.UtcNow);
    }

    static List<string> ParseBotCsv(string? csv)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        csv = csv.Trim();
        if (csv.StartsWith("wd:", StringComparison.OrdinalIgnoreCase))
            csv = csv[3..];
        if (csv.Equals("none", StringComparison.OrdinalIgnoreCase)
            || csv.Equals("(none marked)", StringComparison.OrdinalIgnoreCase)
            || csv.Length == 0)
            return list;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0 && !list.Contains(part, StringComparer.OrdinalIgnoreCase))
                list.Add(part.ToLowerInvariant());
        }
        return list;
    }

    void PulseSession(GameInstance inst)
    {
        try
        {
            inst.Proxy.RunLua(
                "if type(GmSession_Pulse)=='function' then pcall(GmSession_Pulse) " +
                "elseif type(GmSession_StatusLine)=='function' and GmReportPlayer then " +
                "pcall(GmReportPlayer, UnitGUID('player') or '', UnitName('player') or '', -1,-1,-1,-1, " +
                "'wd:'..tostring(GmSession_StatusLine())) end");
        }
        catch { }
    }

    void SafeTick()
    {
        try { TickWorldWatch(); }
        catch { }
    }

    void TickWorldWatch()
    {
        if (_instances.LaunchesSuspended) return;
        if (!SettingsStore.Current.WatchdogEnabled) return;
        if (!SettingsStore.Current.WatchdogAutoRelog) return;
        foreach (var inst in _instances.All.Where(i => i.Connected).ToList())
        {
            if (_recovering.ContainsKey(inst.Id)) continue;
            if (!_lostSince.TryGetValue(inst.Id, out var lostAt)) continue;
            if (_nextWorldRecover.TryGetValue(inst.Id, out var cool) && DateTime.UtcNow < cool)
                continue;

            int debounce = _disconnectHint.ContainsKey(inst.Id)
                ? DisconnectHintDebounceSec
                : WorldLostDebounceSec;
            if ((DateTime.UtcNow - lostAt).TotalSeconds < debounce) continue;

            _log($"watchdog: instance {inst.Id} left world — auto-relog");
            _ = Task.Run(() => RecoverWorldAsync(inst.Id));
        }
    }

    void OnDied(InstanceDiedEvent e)
    {
        if (_instances.LaunchesSuspended) return;
        if (!SettingsStore.Current.WatchdogEnabled) return;
        if (!SettingsStore.Current.WatchdogAutoRelaunch) return;
        if (_recovering.ContainsKey(e.Id)) return;
        _ = Task.Run(() => RecoverProcessAsync(e.Id, e.Pid));
    }

    public async Task<object> RecoverNowAsync(int instanceId)
    {
        var inst = _instances.ById(instanceId);
        if (inst is { Connected: true })
            await RecoverWorldAsync(instanceId).ConfigureAwait(false);
        else
            await RecoverProcessAsync(instanceId, 0).ConfigureAwait(false);
        return StatusDto();
    }

    async Task RecoverWorldAsync(int instanceId)
    {
        if (_instances.LaunchesSuspended) return;
        if (!_recovering.TryAdd(instanceId, DateTime.UtcNow)) return;
        _phase[instanceId] = "world_dc";
        try
        {
            if (!LicenseOk(instanceId)) return;
            var inst = _instances.ById(instanceId);
            if (inst is not { Connected: true })
            {
                _phase[instanceId] = "world_dc_no_proxy";
                return;
            }
            var snap = SessionSnapshotStore.Load(instanceId) ?? new SessionSnapshot { InstanceId = instanceId };
            MergeLastBots(snap, instanceId);

            if (SettingsStore.Current.WatchdogAutoRelog)
            {
                _phase[instanceId] = "relog";
                await RelogAsync(inst, snap, waitGlue: true).ConfigureAwait(false);
                _phase[instanceId] = "wait_world";
                await WaitWorldAsync(inst, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            }

            await AfterWorldAsync(inst, snap).ConfigureAwait(false);
            _sawWorld[instanceId] = false;
            _lostSince.TryRemove(instanceId, out _);
            _disconnectHint.TryRemove(instanceId, out _);
            _nextWorldRecover[instanceId] = DateTime.UtcNow.AddSeconds(WorldRecoverCooldownSec);
            _phase[instanceId] = "healthy";
            _log($"watchdog: instance {instanceId} world recovered");
        }
        catch (Exception ex)
        {
            _phase[instanceId] = "error:" + ex.Message;
            _log($"watchdog: world recover failed — {ex.Message}");
            _nextWorldRecover[instanceId] = DateTime.UtcNow.AddSeconds(WorldRecoverCooldownSec);
        }
        finally
        {
            _recovering.TryRemove(instanceId, out _);
        }
    }

    async Task RecoverProcessAsync(int instanceId, uint deadPid)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1 && _recovering.ContainsKey(instanceId))
            return;
        if (!_recovering.TryAdd(instanceId, DateTime.UtcNow))
        {
            Interlocked.Exchange(ref _busy, 0);
            return;
        }
        _phase[instanceId] = "process_dead";
        try
        {
            if (_instances.LaunchesSuspended) return;
            if (!LicenseOk(instanceId)) return;
            _log($"watchdog: instance {instanceId} died (pid={deadPid}) — recovering");
            var snap = SessionSnapshotStore.Load(instanceId) ?? new SessionSnapshot { InstanceId = instanceId };
            MergeLastBots(snap, instanceId);

            if (SettingsStore.Current.WatchdogAutoRelaunch)
            {
                _phase[instanceId] = "relaunch";
                await _instances.LaunchOne(instanceId).ConfigureAwait(false);
                _phase[instanceId] = "wait_proxy";
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
                GameInstance? inst = null;
                while (DateTime.UtcNow < deadline)
                {
                    inst = _instances.All.FirstOrDefault(i => i.Id == instanceId);
                    if (inst is { Connected: true }) break;
                    await Task.Delay(500).ConfigureAwait(false);
                }
                if (inst is not { Connected: true })
                {
                    _phase[instanceId] = "proxy_timeout";
                    _log($"watchdog: instance {instanceId} proxy timeout");
                    _instances.PruneInstance(instanceId, killProcess: false, publishDied: false);
                    return;
                }

                // Login UI needs a beat after ExtProxy attaches.
                await Task.Delay(5000).ConfigureAwait(false);

                if (SettingsStore.Current.WatchdogAutoRelog)
                {
                    _phase[instanceId] = "relog";
                    await RelogAsync(inst, snap, waitGlue: true).ConfigureAwait(false);
                    _phase[instanceId] = "wait_world";
                    await WaitWorldAsync(inst, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
                }

                await AfterWorldAsync(inst, snap).ConfigureAwait(false);
                _sawWorld[instanceId] = false;
                _lostSince.TryRemove(instanceId, out _);
                _phase[instanceId] = "healthy";
                _log($"watchdog: instance {instanceId} recovered");
            }
        }
        catch (Exception ex)
        {
            _phase[instanceId] = "error:" + ex.Message;
            _log($"watchdog: recover failed — {ex.Message}");
            try
            {
                var left = _instances.ById(instanceId);
                if (left is not { Connected: true })
                    _instances.PruneInstance(instanceId, killProcess: false, publishDied: false);
            }
            catch { }
        }
        finally
        {
            _recovering.TryRemove(instanceId, out _);
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    bool LicenseOk(int instanceId) => true;

    void MergeLastBots(SessionSnapshot snap, int instanceId)
    {
        if (_lastBots.TryGetValue(instanceId, out var bots) && bots.Count > 0)
        {
            foreach (var b in bots)
                snap.RunningBots[b] = true;
        }
    }

    async Task AfterWorldAsync(GameInstance inst, SessionSnapshot snap)
    {
        if (!SettingsStore.Current.WatchdogRestoreBots) return;
        _phase[inst.Id] = "wait_addons";
        await Task.Delay(TimeSpan.FromSeconds(AddonReadySec)).ConfigureAwait(false);
        _phase[inst.Id] = "restore_bots";
        RestoreBots(inst, snap);
    }

    async Task RelogAsync(GameInstance inst, SessionSnapshot snap, bool waitGlue)
    {
        _ = waitGlue;
        byte[]? login = null;
        if (!string.IsNullOrWhiteSpace(snap.LoginHex))
        {
            try { login = Convert.FromHexString(snap.LoginHex!); }
            catch { login = PacketView.ParseHex(snap.LoginHex!); }
        }
        login ??= _loginFor(inst.Id);
        if (login is { Length: > 0 })
        {
            inst.Proxy.Replay(login);
            _log($"watchdog: replayed login {login.Length}b");
            await Task.Delay(2000).ConfigureAwait(false);
            return;
        }

        inst.Proxy.RunLua(CharCreateLab.EnterWorldScript(1));
        _log("watchdog: EnterWorld Lua fallback (slot 1) — no sniffed login packet");
        await Task.Delay(3000).ConfigureAwait(false);
    }

    static async Task WaitWorldAsync(GameInstance inst, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var objs = inst.Proxy.GetObjects();
                if (objs.Header.PlayerGuid != 0)
                    return;
            }
            catch { }
            try
            {
                inst.Proxy.RunLua(
                    "if UnitName and UnitName('player') then " +
                    "if GmReportPlayer then GmReportPlayer(UnitGUID('player') or '', UnitName('player') or '') end end");
            }
            catch { }
            await Task.Delay(1000).ConfigureAwait(false);
        }
    }

    static void RestoreBots(GameInstance inst, SessionSnapshot snap)
    {
        inst.Proxy.RunLua(
            "if type(GmSession_Resume)=='function' then pcall(GmSession_Resume) end");

        foreach (var kv in snap.RunningBots.Where(k => k.Value))
        {
            string script = kv.Key.ToLowerInvariant() switch
            {
                "gatherbot" or "gather" =>
                    "if SlashCmdList and SlashCmdList.GATHERBOT then SlashCmdList.GATHERBOT('start') " +
                    "elseif type(GatherBot_Start)=='function' then GatherBot_Start() " +
                    "elseif GatherBot and GatherBot.Start then GatherBot.Start() end",
                "huntingbot" or "hbot" =>
                    "if SlashCmdList and SlashCmdList.HUNTINGBOT then SlashCmdList.HUNTINGBOT('start') " +
                    "elseif HuntingBot and HuntingBot.Start then HuntingBot.Start() end",
                "bgafk" =>
                    "if SlashCmdList and SlashCmdList.BGAFK then SlashCmdList.BGAFK('start') " +
                    "elseif BgAfk and BgAfk.Start then BgAfk.Start() end",
                "botbuilder" or "bb" =>
                    "if BotBuilder and BotBuilder.StartEngine then BotBuilder.StartEngine() " +
                    "elseif BotBuilderDB then BotBuilderDB.engineOn=true end",
                "actionflow" or "af" or "flow" =>
                    "if ActionFlow and ActionFlow.Runtime and ActionFlow.Runtime.Start then ActionFlow.Runtime.Start() " +
                    "elseif SlashCmdList and SlashCmdList.ACTIONFLOW then SlashCmdList.ACTIONFLOW('start') " +
                    "elseif ActionFlowDB then ActionFlowDB.engineOn=true end",
                "ctfcap" or "ctf" or "wsg" =>
                    "if SlashCmdList and SlashCmdList.CTFCAP then SlashCmdList.CTFCAP('start') " +
                    "elseif CtfCap and CtfCap.Start then CtfCap.Start() end",
                "combat" or "gmcombat" =>
                    "if SlashCmdList and SlashCmdList.GMCOMBAT then SlashCmdList.GMCOMBAT('start') " +
                    "elseif GmCombat and GmCombat.Scheduler and GmCombat.Scheduler.Start then GmCombat.Scheduler.Start() end",
                "explore" or "gmexplore" =>
                    "if SlashCmdList and SlashCmdList.GMEXPLORE then SlashCmdList.GMEXPLORE('start') " +
                    "elseif GmExplore and GmExplore.Start then GmExplore.Start() end",
                _ => $"-- unknown bot {kv.Key}",
            };
            if (!script.StartsWith("--", StringComparison.Ordinal))
                inst.Proxy.RunLua(script);
        }
    }

    public void Dispose()
    {
        _diedSub?.Dispose();
        _tick.Dispose();
    }
}
