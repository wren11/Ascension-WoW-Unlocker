using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Infrastructure.Chat;
using HeadlessClient.Infrastructure.Config;
using HeadlessClient.Infrastructure.Logging;
using HeadlessClient.Infrastructure.Monitoring;
using HeadlessClient.Infrastructure.Probe;
using HeadlessClient.Infrastructure.Query;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeadlessClient.Infrastructure.Fleet;

/// <summary>
/// Spawns one <see cref="AccountSessionRunner"/> per configured account and keeps them alive.
/// </summary>
public sealed class AccountFleetService : BackgroundService
{
    private readonly HeadlessOptions _options;
    private readonly PacketWireLogger _packetLog;
    private readonly IChatLog _chat;
    private readonly ChatMediator _mediator;
    private readonly OpcodeProbeService _probe;
    private readonly EconomySecurityAudit _audit;
    private readonly QueryCache _queries;
    private readonly InMemoryObjectDirectory _objects;
    private readonly WorldIntelService _intel;
    private readonly PlayerProfileService _profiles;
    private readonly ILogger<AccountFleetService>? _log;
    private readonly List<AccountSessionRunner> _runners = new();
    private readonly Dictionary<AccountSessionRunner, Task> _runnerTasks = new();
    private readonly Dictionary<AccountSessionRunner, CancellationTokenSource> _runnerCts = new();
    private readonly object _runnerGate = new();
    private CancellationToken _fleetCt;
    private long _lastEnsureUtcTicks;

    public AccountFleetService(
        HeadlessOptions options,
        PacketWireLogger packetLog,
        IChatLog chat,
        ChatMediator mediator,
        OpcodeProbeService probe,
        EconomySecurityAudit audit,
        QueryCache queries,
        InMemoryObjectDirectory objects,
        WorldIntelService intel,
        PlayerProfileService profiles,
        ILogger<AccountFleetService>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _packetLog = packetLog ?? throw new ArgumentNullException(nameof(packetLog));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _intel = intel ?? throw new ArgumentNullException(nameof(intel));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _log = log;
    }

    public IReadOnlyList<AccountSessionRunner> Runners
    {
        get { lock (_runnerGate) { return _runners.ToList(); } }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _fleetCt = stoppingToken;
        var mode = string.IsNullOrWhiteSpace(_options.AuthMode) ? "Tcp" : _options.AuthMode;
        if (!mode.Equals("Tcp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var accounts = _options.ResolveAccounts();
        if (accounts.Count == 0)
        {
            Console.WriteLine("[fleet] no accounts configured (Account/Password or Accounts[]).");
            return;
        }

        Console.WriteLine($"[fleet] starting {accounts.Count} account(s); packetLog={_options.PacketLog.Enabled} dir={_options.PacketLog.Directory}");
        Console.WriteLine($"[fleet] autoReconnect={_options.Fleet.AutoReconnect} keepalive={_options.Fleet.KeepAliveSeconds}s");
        Console.WriteLine($"[fleet] chatroom http://127.0.0.1:{_options.MonitorPort}/");

        // SoftRealm mandate: system/Wooz always reconnects.
        _options.Fleet.AutoReconnect = true;

        var tasks = new List<Task>(accounts.Count);
        for (var i = 0; i < accounts.Count; i++)
        {
            var entry = accounts[i];
            entry.IsSystemDefault = true;
            tasks.Add(StartRunner(entry, stoppingToken, stagger: i > 0));
        }

        // Park until host stop — do not exit when a runner task ends; watchdog restarts Wooz.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Fleet task faulted");
        }
    }

    /// <summary>
    /// SoftRealm webservice contract: character Wooz (system fleet) must always be running.
    /// Restarts dead loops, force-drops half-open sockets, and never leaves AutoReconnect off.
    /// </summary>
    public object EnsureWoozAlwaysOnline()
    {
        _options.Fleet.AutoReconnect = true;
        Interlocked.Exchange(ref _lastEnsureUtcTicks, DateTime.UtcNow.Ticks);

        var mode = string.IsNullOrWhiteSpace(_options.AuthMode) ? "Tcp" : _options.AuthMode;
        if (!mode.Equals("Tcp", StringComparison.OrdinalIgnoreCase))
        {
            return new { ok = false, reason = "AuthMode is not Tcp", authMode = mode };
        }

        var ct = _fleetCt == default ? CancellationToken.None : _fleetCt;
        var actions = new List<string>();
        List<AccountSessionRunner> system;

        lock (_runnerGate)
        {
            // Cull zombies — RunAsync exited but runner still listed.
            var dead = _runners.Where(r => r.IsSystemDefault && !r.IsLoopRunning).ToList();
            foreach (var z in dead)
            {
                _runners.Remove(z);
                _runnerTasks.Remove(z);
                actions.Add($"removed_zombie:{z.Tag}");
                try { _ = z.DisposeAsync().AsTask(); } catch { /* ignore */ }
            }

            system = _runners.Where(r => r.IsSystemDefault).ToList();
        }

        if (system.Count == 0)
        {
            var accounts = _options.ResolveAccounts();
            if (accounts.Count == 0)
            {
                actions.Add("no_accounts_configured");
                return new { ok = false, actions, inWorld = 0 };
            }

            Console.WriteLine("[fleet] EnsureWoozAlwaysOnline: starting system/Wooz runners");
            for (var i = 0; i < accounts.Count; i++)
            {
                var entry = accounts[i];
                entry.IsSystemDefault = true;
                _ = StartRunner(entry, ct, stagger: i > 0);
                actions.Add($"started:{entry.Character ?? entry.Account}");
            }

            return new
            {
                ok = true,
                actions,
                inWorld = 0,
                note = "system runners starting"
            };
        }

        foreach (var r in system)
        {
            if (r.IsInWorld)
            {
                actions.Add($"ok:{r.CurrentCharacter?.Name ?? r.Tag}");
                continue;
            }

            // Half-open: SessionState still InWorld but TCP dead — abort + maybe hard restart.
            if (r.State == Domain.Session.SessionState.InWorld)
            {
                r.ForceDropForWatchdog("half_open_inworld");
                actions.Add($"force_drop:{r.Tag}:half_open");

                var since = r.HalfOpenSinceUtcTicks;
                if (since > 0)
                {
                    var stuckSec = (DateTime.UtcNow - new DateTime(since, DateTimeKind.Utc)).TotalSeconds;
                    if (stuckSec >= 12)
                    {
                        HardRestartSystemRunner(r, ct, actions, $"half_open_{stuckSec:F0}s");
                    }
                }

                continue;
            }

            if (r.IsLoopRunning)
            {
                var last = r.LastInWorldUtcTicks;
                if (last > 0)
                {
                    var awaySec = (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalSeconds;
                    if (awaySec >= 90)
                    {
                        HardRestartSystemRunner(r, ct, actions, $"offline_{awaySec:F0}s");
                        continue;
                    }
                }

                actions.Add($"connecting:{r.Tag}:state={r.State}");
                continue;
            }

            HardRestartSystemRunner(r, ct, actions, "loop_dead");
        }

        var inWorld = Runners.Count(x => x.IsSystemDefault && x.IsInWorld);
        return new
        {
            ok = true,
            actions,
            inWorld,
            runners = Runners.Where(x => x.IsSystemDefault).Select(r => new
            {
                tag = r.Tag,
                character = r.CurrentCharacter?.Name,
                inWorld = r.IsInWorld,
                state = r.State.ToString(),
                loop = r.IsLoopRunning
            }).ToList()
        };
    }

    void HardRestartSystemRunner(AccountSessionRunner r, CancellationToken fleetCt, List<string> actions, string reason)
    {
        Console.WriteLine($"[fleet] HARD RESTART system runner {r.Tag}: {reason}");
        AccountEntry? entry = null;
        CancellationTokenSource? cts = null;
        lock (_runnerGate)
        {
            _runners.Remove(r);
            _runnerTasks.Remove(r);
            if (_runnerCts.TryGetValue(r, out cts))
            {
                _runnerCts.Remove(r);
            }

            var accounts = _options.ResolveAccounts();
            entry = accounts.FirstOrDefault(a =>
                string.Equals(a.Account, r.SessionAccount, StringComparison.OrdinalIgnoreCase))
                ?? accounts.FirstOrDefault();
        }

        try { cts?.Cancel(); } catch { /* ignore */ }
        try { r.HardKillForRestart(reason); } catch { /* ignore */ }
        try { _ = r.DisposeAsync().AsTask(); } catch { /* ignore */ }
        try { cts?.Dispose(); } catch { /* ignore */ }

        if (entry is null)
        {
            actions.Add($"hard_restart_failed:{r.Tag}:no_account");
            return;
        }

        entry.IsSystemDefault = true;
        _ = StartRunner(entry, fleetCt, stagger: false);
        actions.Add($"hard_restart:{r.Tag}:{reason}");
    }

    /// <summary>
    /// Restart system/Wooz fleet runners if they disappeared (watchdog). Safe to call repeatedly.
    /// </summary>
    public void EnsureSystemFleet()
    {
        EnsureWoozAlwaysOnline();
    }

    /// <summary>Start additional user-owned game accounts (portal login). System default stays first.</summary>
    public IReadOnlyList<object> StartUserAccounts(IEnumerable<AccountEntry> entries, string ownerUserId)
    {
        var started = new List<object>();
        foreach (var entry in entries)
        {
            entry.IsSystemDefault = false;
            entry.OwnerUserId = ownerUserId;
            lock (_runnerGate)
            {
                if (_runners.Any(r =>
                        r.Tag.Equals(entry.Account, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.SessionAccount, entry.Account, StringComparison.OrdinalIgnoreCase)))
                {
                    started.Add(new { account = entry.Account, status = "already_running" });
                    continue;
                }
            }

            _ = StartRunner(entry, _fleetCt, stagger: true);
            started.Add(new { account = entry.Account, status = "starting", ownerUserId });
        }

        return started;
    }

    /// <summary>Stop user-owned sessions (opt-out). Never stops system/default backbone.</summary>
    public async Task<object> StopUserAccountsAsync(string ownerUserId)
    {
        List<AccountSessionRunner> toStop;
        lock (_runnerGate)
        {
            toStop = _runners
                .Where(r =>
                    !r.IsSystemDefault
                    && !string.IsNullOrWhiteSpace(r.OwnerUserId)
                    && r.OwnerUserId!.Equals(ownerUserId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var r in toStop)
            {
                _runners.Remove(r);
                _runnerTasks.Remove(r);
                if (_runnerCts.TryGetValue(r, out var cts))
                {
                    _runnerCts.Remove(r);
                    try { cts.Cancel(); } catch { /* ignore */ }
                    try { cts.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        foreach (var r in toStop)
        {
            try
            {
                r.RequestStop();
                await r.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        return new
        {
            ok = true,
            stopped = toStop.Select(r => r.SessionAccount).ToList(),
            note = "Your game accounts disconnected. Chatroom still works without world login."
        };
    }

    public bool TryGetRunner(string accountOrTag, out AccountSessionRunner? runner)
    {
        runner = null;
        if (string.IsNullOrWhiteSpace(accountOrTag))
        {
            return false;
        }

        lock (_runnerGate)
        {
            runner = _runners.FirstOrDefault(r =>
                r.SessionAccount.Equals(accountOrTag, StringComparison.OrdinalIgnoreCase)
                || r.Tag.Equals(accountOrTag, StringComparison.OrdinalIgnoreCase));
            return runner is not null;
        }
    }

    public object GetCharacterList(string account)
    {
        if (!TryGetRunner(account, out var runner) || runner is null)
        {
            return new { ok = false, error = "Account not connected." };
        }

        return new
        {
            ok = true,
            account = runner.SessionAccount,
            inWorld = runner.IsInWorld,
            current = runner.CurrentCharacter is null ? null : new
            {
                name = runner.CurrentCharacter.Name,
                guid = runner.CurrentCharacter.Guid.ToString("X16"),
                level = runner.CurrentCharacter.Level,
                race = runner.CurrentCharacter.Race,
                classId = runner.CurrentCharacter.Class,
                map = runner.CurrentCharacter.Map,
                zone = runner.CurrentCharacter.Zone
            },
            characters = runner.LastCharacters.Select(c => new
            {
                name = c.Name,
                guid = c.Guid.ToString("X16"),
                level = c.Level,
                race = c.Race,
                classId = c.Class,
                map = c.Map,
                zone = c.Zone
            }).ToList()
        };
    }

    public object RequestCharacterSwitch(string account, string characterName)
    {
        if (!TryGetRunner(account, out var runner) || runner is null)
        {
            return new { ok = false, error = "Account not connected." };
        }

        characterName = (characterName ?? "").Trim();
        if (characterName.Length == 0)
        {
            return new { ok = false, error = "character required" };
        }

        runner.PendingCharacterSwitch = characterName;
        return new
        {
            ok = true,
            account = runner.SessionAccount,
            switchingTo = characterName,
            note = "Session will leave world and re-enter as the selected character."
        };
    }

    private async Task StartRunner(AccountEntry entry, CancellationToken stoppingToken, bool stagger)
    {
        if (stagger && _options.Fleet.LoginStaggerMs > 0)
        {
            try
            {
                await Task.Delay(_options.Fleet.LoginStaggerMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var sessionOpts = new AccountSessionOptions(_options, entry);
        var runner = new AccountSessionRunner(
            sessionOpts,
            _options.Fleet,
            _packetLog,
            _chat,
            _mediator,
            _probe,
            _audit,
            _queries,
            _objects,
            _intel,
            _profiles,
            _log,
            _options.Fleet.AutoJoinChannels);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_runnerGate)
        {
            _runners.Add(runner);
            _runnerCts[runner] = linkedCts;
            _runnerTasks[runner] = Task.Run(async () =>
            {
                try
                {
                    await runner.RunAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Runner {Tag} faulted", runner.Tag);
                }
            }, CancellationToken.None);
        }

        // Return immediately — ExecuteAsync parks forever; watchdog owns restarts.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        List<AccountSessionRunner> copy;
        lock (_runnerGate)
        {
            copy = _runners.ToList();
        }

        foreach (var runner in copy)
        {
            try { await runner.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
