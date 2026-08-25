namespace AscensionNetTool;

sealed class GameInstance
{
    public int Id { get; init; }
    public uint Pid { get; set; }
    public ProxyClient Proxy { get; } = new();
    public PacketRingReader Ring { get; } = new();
    public ChatReportReader ChatReports { get; } = new();
    public string RuntimeDir { get; init; } = "";
    public bool Connected { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public string? PlayerName { get; set; }
    /// <summary>
    /// Who this client entered the world as (CHAR_ENUM + PLAYER_LOGIN, or Lua UnitName("player")).
    /// Nearby nameplates must not replace this.
    /// </summary>
    public string? LockedPlayerName { get; set; }
    public List<string> CharSelectNames { get; set; } = new();
    public byte[]? LastCharEnum { get; set; }
    public ulong PlayerGuid { get; set; }
    /// <summary>Last guid+name pair sent to SoftRealm entitlement sync (re-run when name resolves).</summary>
    public string? LastEntitlementKey { get; set; }
    public float PlayerX { get; set; }
    public float PlayerY { get; set; }
    public float PlayerZ { get; set; }

    /// <summary>True when this slot was created by LaunchOne (not an orphan pipe adopt).</summary>
    public bool OwnedByLauncher { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(PlayerName)
            ? $"Instance {Id}"
            : $"Inst {Id}: {PlayerName}";
}

/// <summary>Owns launch/reconcile lifecycle for N concurrent Ascension clients.</summary>
sealed class InstanceManager : IDisposable
{
    readonly Dictionary<int, GameInstance> _byId = new();
    readonly object _gate = new();
    readonly BootstrapService _boot;
    readonly SharedStateManager _shared;
    readonly System.Threading.Timer _reconcile;
    int _nextId = 1;
    bool _mmgrCleared;

    public InstanceManager(BootstrapService boot, SharedStateManager shared)
    {
        _boot = boot;
        _shared = shared;
        DesiredCount = Math.Clamp(SettingsStore.Current.InstanceCount, 1, GmtLimits.MaxInstances);
        _reconcile = new System.Threading.Timer(_ =>
        {
            try { Reconcile(); }
            catch { }
        }, null, 2000, 2000);
    }

    public int DesiredCount { get; set; } = 1;
    public bool LaunchesSuspended { get; private set; }
    public GameInstance? Active { get; set; }
    public event Action? Changed;

    public void SuspendLaunches()
    {
        LaunchesSuspended = true;
        DesiredCount = 0;
    }

    public void ResumeLaunches()
    {
        LaunchesSuspended = false;
    }

    public IEnumerable<GameInstance> All
    {
        get { lock (_gate) return _byId.Values.OrderBy(i => i.Id).ToList(); }
    }

    public GameInstance? ById(int id)
    {
        lock (_gate) return _byId.TryGetValue(id, out var g) ? g : null;
    }

    static bool WillAutoRelaunch() =>
        SettingsStore.Current.WatchdogEnabled && SettingsStore.Current.WatchdogAutoRelaunch;

    int AllocateId()
    {
        lock (_gate)
        {
            for (int i = 1; i <= GmtLimits.MaxInstances; i++)
            {
                if (!_byId.ContainsKey(i))
                    return i;
            }
            return _nextId++;
        }
    }

    public async Task LaunchN(int n)
    {
        if (LaunchesSuspended) return;
        n = Math.Clamp(n, 1, GmtLimits.MaxInstances);
        DesiredCount = n;
        SettingsStore.Current.InstanceCount = n;
        SettingsStore.Save();

        // Drop dead/orphan tabs first so Count reflects real launcher slots.
        PruneStaleSlots(forceDeadOwned: true);

        if (!_mmgrCleared)
        {
            await Task.Run(() => _boot.ClearStaleMmgrOnly()).ConfigureAwait(false);
            _mmgrCleared = true;
        }

        // Re-bind pid files for existing launcher slots only (never invent orphan tabs).
        await Task.Run(ReconnectOwnedSlots).ConfigureAwait(false);

        while (All.Count(i => i.OwnedByLauncher) < n)
        {
            int id = AllocateId();
            await LaunchOne(id).ConfigureAwait(false);
        }

        // If user asked for fewer than we have alive, leave extras (user closes them).
        Active ??= All.FirstOrDefault(i => i.Connected) ?? All.FirstOrDefault();
        Changed?.Invoke();
    }

    public async Task LaunchOne(int instanceId)
    {
        if (LaunchesSuspended) return;
        string runtime = Paths.RuntimeDirFor(instanceId);
        var inst = new GameInstance
        {
            Id = instanceId,
            RuntimeDir = runtime,
            StartedAt = DateTime.UtcNow,
            OwnedByLauncher = true,
        };

        lock (_gate)
        {
            if (_byId.TryGetValue(instanceId, out var old))
            {
                try { old.Proxy.Dispose(); } catch { }
                try { old.Ring.Dispose(); } catch { }
                try { old.ChatReports.Dispose(); } catch { }
            }
            _byId[instanceId] = inst;
            if (instanceId >= _nextId) _nextId = instanceId + 1;
        }

        try
        {
            uint pid = await Task.Run(() => _boot.LaunchOne(instanceId)).ConfigureAwait(false);
            inst.Pid = pid;
            await WaitConnect(inst, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            EventBus.Publish(new InstanceLaunchedEvent(instanceId, pid));
        }
        catch (Exception ex)
        {
            _boot.Log($"instance {instanceId} launch failed: {ex.Message}");
        }

        Active ??= inst;
        Changed?.Invoke();
    }

    public async Task StopInstance(int id)
    {
        PruneInstance(id, killProcess: true, publishDied: true);
        await Task.CompletedTask;
    }

    public async Task StopAll()
    {
        SuspendLaunches();
        foreach (var id in All.Select(i => i.Id).ToList())
            await StopInstance(id).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove a slot from the UI/manager. When <paramref name="killProcess"/> is false,
    /// only tears down IPC (process already gone or orphan we refuse to track).
    /// </summary>
    public void PruneInstance(int id, bool killProcess, bool publishDied)
    {
        GameInstance? inst;
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out inst))
                return;
            _byId.Remove(id);
        }

        uint pid = inst.Pid;
        try
        {
            inst.Proxy.Dispose();
            inst.Ring.Dispose();
            inst.ChatReports.Dispose();
            if (killProcess && pid != 0)
                _boot.KillPid(pid);
        }
        catch { }

        _shared.ClearInstance(id);
        if (Active?.Id == id)
            Active = All.FirstOrDefault(i => i.Connected) ?? All.FirstOrDefault();

        if (publishDied)
            EventBus.Publish(new InstanceDiedEvent(id, pid));

        _boot.Log($"instance {id} pruned (pid={pid}, kill={killProcess})");
        Changed?.Invoke();
    }

    /// <summary>
    /// Drop dead launcher slots when auto-relaunch is off, and always drop orphan
    /// (non-launcher) tabs so Launch can allocate fresh slots.
    /// </summary>
    public void PruneStaleSlots(bool forceDeadOwned = false)
    {
        bool relaunch = WillAutoRelaunch();
        foreach (var inst in All.ToList())
        {
            bool alive = IsAlive(inst);
            if (!inst.OwnedByLauncher)
            {
                // Orphans never belong in the tab strip.
                PruneInstance(inst.Id, killProcess: false, publishDied: false);
                continue;
            }
            if (!alive && (forceDeadOwned || !relaunch))
            {
                PruneInstance(inst.Id, killProcess: false, publishDied: false);
            }
        }
    }

    static bool IsAlive(GameInstance inst) =>
        inst.Pid != 0
        && ProxyDiscovery.IsPidAlive(inst.Pid)
        && ProxyDiscovery.PipeReachable(inst.Pid, 150);

    public void Reconcile()
    {
        bool changed = false;
        bool relaunch = WillAutoRelaunch();

        // Always strip orphan tabs (not launched by us).
        foreach (var orphan in All.Where(i => !i.OwnedByLauncher).ToList())
        {
            PruneInstance(orphan.Id, killProcess: false, publishDied: false);
            changed = true;
        }

        foreach (var inst in All.ToList())
        {
            bool alive = IsAlive(inst);
            if (alive)
            {
                if (!inst.Connected)
                {
                    try
                    {
                        if (inst.Proxy.TryConnectToPid(inst.Pid))
                        {
                            inst.Ring.TryOpen(inst.Pid);
                            inst.ChatReports.TryOpen(inst.Pid);
                            inst.Connected = true;
                            changed = true;
                        }
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        if (!inst.Proxy.Ping())
                        {
                            inst.Connected = false;
                            changed = true;
                        }
                    }
                    catch
                    {
                        inst.Connected = false;
                        changed = true;
                    }
                }
            }
            else
            {
                // Process gone (or never came up).
                bool wasLive = inst.Connected || inst.Pid != 0;
                if (!wasLive)
                    continue;

                uint deadPid = inst.Pid;
                if (inst.Connected)
                {
                    inst.Connected = false;
                    changed = true;
                }

                // Notify watchdog first (may relaunch this slot id).
                EventBus.Publish(new InstanceDiedEvent(inst.Id, deadPid));

                if (relaunch && inst.OwnedByLauncher)
                {
                    // Keep the tab for recovery; clear stale pid so we don't thrash.
                    inst.Pid = 0;
                    changed = true;
                }
                else
                {
                    // No auto-relaunch → remove tab and free the launch slot.
                    PruneInstance(inst.Id, killProcess: false, publishDied: false);
                    changed = true;
                }
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Re-attach pipes for existing launcher-owned slots via per-instance pid files.
    /// Does NOT invent new tabs from random ExtProxy pipes on the machine.
    /// </summary>
    void ReconnectOwnedSlots()
    {
        foreach (var inst in All.Where(i => i.OwnedByLauncher).ToList())
        {
            if (IsAlive(inst))
            {
                if (!inst.Connected && inst.Proxy.TryConnectToPid(inst.Pid))
                {
                    inst.Ring.TryOpen(inst.Pid);
                    inst.ChatReports.TryOpen(inst.Pid);
                    inst.Connected = true;
                }
                continue;
            }

            uint? pid = ProxyDiscovery.ReadPidFile(Paths.PidFileFor(inst.Id));
            if (pid is uint p && ProxyDiscovery.IsPidAlive(p) && ProxyDiscovery.PipeReachable(p))
            {
                inst.Pid = p;
                if (inst.Proxy.TryConnectToPid(p))
                {
                    inst.Ring.TryOpen(p);
                    inst.ChatReports.TryOpen(p);
                    inst.Connected = true;
                }
            }
        }
        Active ??= All.FirstOrDefault(i => i.Connected) ?? All.FirstOrDefault();
        Changed?.Invoke();
    }

    static async Task WaitConnect(GameInstance inst, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            uint? pid = inst.Pid != 0 ? inst.Pid : null;
            pid ??= ProxyDiscovery.ReadPidFile(Paths.PidFileFor(inst.Id));
            if (pid is uint p && ProxyDiscovery.PipeReachable(p))
            {
                inst.Pid = p;
                if (inst.Proxy.TryConnectToPid(p))
                {
                    inst.Ring.TryOpen(p);
                    inst.ChatReports.TryOpen(p);
                    inst.Connected = true;
                    return;
                }
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _reconcile.Dispose();
        foreach (var inst in All)
        {
            try { inst.Proxy.Dispose(); } catch { }
            try { inst.Ring.Dispose(); } catch { }
            try { inst.ChatReports.Dispose(); } catch { }
        }
    }
}
