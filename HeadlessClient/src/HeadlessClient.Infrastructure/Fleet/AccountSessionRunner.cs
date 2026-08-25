using HeadlessClient.Application;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.Session;
using HeadlessClient.Infrastructure.Auth;
using HeadlessClient.Infrastructure.Config;
using HeadlessClient.Infrastructure.Logging;
using HeadlessClient.Infrastructure.Monitoring;
using HeadlessClient.Infrastructure.Probe;
using HeadlessClient.Infrastructure.Protocol;
using HeadlessClient.Infrastructure.Query;
using HeadlessClient.Infrastructure.World;
using Microsoft.Extensions.Logging;

namespace HeadlessClient.Infrastructure.Fleet;

/// <summary>
/// One account's full login → InWorld → keepalive → auto-reconnect loop.
/// Shares the process-wide global Object Manager with all other sessions.
/// </summary>
public sealed class AccountSessionRunner : IAsyncDisposable
{
    private readonly AccountSessionOptions _sessionOpts;
    private readonly FleetOptions _fleet;
    private readonly PacketWireLogger _packetLog;
    private readonly IChatLog _chat;
    private readonly Chat.ChatMediator _mediator;
    private readonly OpcodeProbeService _probe;
    private readonly EconomySecurityAudit _audit;
    private readonly QueryCache _queries;
    private readonly InMemoryObjectDirectory _objects;
    private readonly WorldIntelService _intel;
    private readonly PlayerProfileService _profiles;
    private readonly ILogger? _log;
    private readonly TcpAuthClient _auth;
    private readonly TcpWorldClient _world;
    private readonly WorldActionService _actions;
    private readonly LoginAndEnterWorldUseCase _login;
    private readonly WorldInboundProjector _projector;
    private uint _pingSequence = 1;
    private long _lastPongTick;
    private string _displayTag;
    private readonly string[] _autoJoinChannels;

    public AccountSessionRunner(
        AccountSessionOptions sessionOpts,
        FleetOptions fleet,
        PacketWireLogger packetLog,
        IChatLog chat,
        Chat.ChatMediator mediator,
        OpcodeProbeService probe,
        EconomySecurityAudit audit,
        QueryCache queries,
        InMemoryObjectDirectory objects,
        WorldIntelService intel,
        PlayerProfileService profiles,
        ILogger? log = null,
        IEnumerable<string>? autoJoinChannels = null)
    {
        _sessionOpts = sessionOpts ?? throw new ArgumentNullException(nameof(sessionOpts));
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
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
        _displayTag = sessionOpts.InitialLogTag;
        _autoJoinChannels = (autoJoinChannels ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _auth = new TcpAuthClient(sessionOpts);
        _world = new TcpWorldClient(sessionOpts, sessionOpts, packetLog)
        {
            LogTag = _displayTag
        };
        _actions = new WorldActionService(_world);
        _login = new LoginAndEnterWorldUseCase(_auth, _world, sessionOpts, sessionOpts);
        _objects.SetObserver(_displayTag);
        var updates = new UpdateObjectProjector(_objects);
        _projector = new WorldInboundProjector(
            _world,
            _chat,
            updates,
            _mediator,
            sessionOpts.Entry.Account,
            sessionOpts.Entry.OwnerUserId);
        _world.PacketReceived += OnPacket;
    }

    public string Tag => _displayTag;
    public string SessionAccount => _sessionOpts.Entry.Account;
    public string? OwnerUserId => _sessionOpts.Entry.OwnerUserId;
    public bool IsSystemDefault => _sessionOpts.Entry.IsSystemDefault;
    public SessionState State => _world.State;
    public bool IsInWorld => _world.State == SessionState.InWorld && _world.IsSocketConnected;
    public IWorldClient WorldClient => _world;
    public WorldActionService Actions => _actions;
    public IReadOnlyList<Domain.World.CharacterInfo> LastCharacters { get; private set; } =
        Array.Empty<Domain.World.CharacterInfo>();
    public Domain.World.CharacterInfo? CurrentCharacter { get; private set; }
    public string? PendingCharacterSwitch { get; set; }
    private volatile bool _stopRequested;
    private volatile bool _loopRunning;
    private long _lastInWorldUtcTicks;
    private long _halfOpenSinceUtcTicks;
    private CancellationTokenSource? _stayWatchdogCts;

    /// <summary>True while <see cref="RunAsync"/> is inside its reconnect loop.</summary>
    public bool IsLoopRunning => _loopRunning;

    /// <summary>UTC ticks of last time we observed a live InWorld+socket session (0 = never).</summary>
    public long LastInWorldUtcTicks => Interlocked.Read(ref _lastInWorldUtcTicks);

    /// <summary>UTC ticks when half-open was first observed (0 = not half-open).</summary>
    public long HalfOpenSinceUtcTicks => Interlocked.Read(ref _halfOpenSinceUtcTicks);

    /// <summary>Stop reconnect loop (user opt-out). Ignored for system/default (Wooz) runners.</summary>
    public void RequestStop()
    {
        if (IsSystemDefault)
        {
            Console.WriteLine($"[{_displayTag}] RequestStop ignored — system/Wooz must stay online");
            return;
        }

        _stopRequested = true;
        ForceDropForWatchdog("request_stop");
    }

    /// <summary>
    /// Watchdog / SoftRealm: abort half-dead world socket so StayInWorld exits and reconnect runs.
    /// Must not await hung I/O.
    /// </summary>
    public void ForceDropForWatchdog(string reason)
    {
        if (Interlocked.Read(ref _halfOpenSinceUtcTicks) == 0)
        {
            Interlocked.Exchange(ref _halfOpenSinceUtcTicks, DateTime.UtcNow.Ticks);
        }

        Console.WriteLine($"[{_displayTag}] watchdog force-drop: {reason} (state={_world.State} sock={_world.IsSocketConnected})");
        try { _stayWatchdogCts?.Cancel(); } catch { /* ignore */ }
        try { _world.AbortSocket(reason); } catch { /* ignore */ }
    }

    /// <summary>Hard-stop this runner's loop so fleet can spawn a fresh system session.</summary>
    public void HardKillForRestart(string reason)
    {
        Console.WriteLine($"[{_displayTag}] HARD KILL for restart: {reason}");
        _stopRequested = true; // system RunLoop ignores this unless we also cancel stay + abort
        try { _stayWatchdogCts?.Cancel(); } catch { /* ignore */ }
        try { _world.AbortSocket("hard_kill:" + reason); } catch { /* ignore */ }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _loopRunning = true;
        try
        {
            await RunLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loopRunning = false;
            Console.WriteLine($"[{_displayTag}] RunAsync exited (system={IsSystemDefault})");
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var delaySec = Math.Max(1, _fleet.ReconnectDelaySeconds);
        var maxDelay = Math.Max(delaySec, _fleet.ReconnectMaxDelaySeconds);
        // Wooz / system backbone never stops reconnecting while the host is alive.
        var mustStay = IsSystemDefault;

        while (!cancellationToken.IsCancellationRequested && (mustStay || !_stopRequested))
        {
            try
            {
                Console.WriteLine($"[{_displayTag}] connecting account={_sessionOpts.Entry.Account}");
                var result = await _login.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    Console.WriteLine($"[{_displayTag}] login failed: {result.Error}");
                    if (!mustStay && !_fleet.AutoReconnect)
                    {
                        return;
                    }

                    await BackoffAsync(delaySec, cancellationToken).ConfigureAwait(false);
                    delaySec = Math.Min(delaySec * 2, maxDelay);
                    await ResetSessionAsync().ConfigureAwait(false);
                    continue;
                }

                if (result.Character is not null)
                {
                    _displayTag = result.Character.Name;
                    _world.LogTag = _displayTag;
                    _objects.SetObserver(_displayTag);
                    CurrentCharacter = result.Character;
                }

                if (result.Characters is { Count: > 0 })
                {
                    LastCharacters = result.Characters.ToList();
                }

                Console.WriteLine($"[{_displayTag}] InWorld — keepalive every {_fleet.KeepAliveSeconds}s");
                delaySec = Math.Max(1, _fleet.ReconnectDelaySeconds);
                Interlocked.Exchange(ref _lastInWorldUtcTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _halfOpenSinceUtcTicks, 0);

                _mediator.Attach(_world, _displayTag, _sessionOpts.Entry.Account, _sessionOpts.Entry.IsSystemDefault);
                var selfGuid = result.Character?.Guid ?? 0UL;
                if (result.Character is not null)
                {
                    _probe.Pool.SetSelf(
                        result.Character.Guid,
                        result.Character.X,
                        result.Character.Y,
                        result.Character.Z,
                        0f,
                        result.Character.Map,
                        result.Character.Zone);
                }

                _probe.Attach(_world, _objects, _displayTag, selfGuid);
                if (result.Character is not null)
                {
                    _audit.Pool.SetSelf(
                        result.Character.Guid,
                        result.Character.X,
                        result.Character.Y,
                        result.Character.Z,
                        0f,
                        result.Character.Map,
                        result.Character.Zone);
                }

                _audit.Attach(_world, _objects, _displayTag, selfGuid);
                _queries.Attach(_world, _displayTag);
                _intel.Attach(_world, _displayTag);
                _profiles.Attach(_world);
                await BootstrapChatAsync(cancellationToken).ConfigureAwait(false);

                await StayInWorldAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"[{_displayTag}] disconnected (state={_world.State})");
                _profiles.Detach(_world);
                _intel.Detach(_world);
                _queries.Detach(_world);
                _audit.Detach(_world);
                _probe.Detach(_world);
                _mediator.Detach(_world);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_displayTag}] session error: {ex.Message}");
                _log?.LogWarning(ex, "[{Tag}] session error", _displayTag);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!mustStay && (!_fleet.AutoReconnect || _stopRequested))
            {
                break;
            }

            Console.WriteLine($"[{_displayTag}] reconnect in {delaySec}s…");
            await BackoffAsync(delaySec, cancellationToken).ConfigureAwait(false);
            delaySec = Math.Min(delaySec * 2, maxDelay);
            await ResetSessionAsync().ConfigureAwait(false);
        }
    }

    private async Task StayInWorldAsync(CancellationToken cancellationToken)
    {
        // Keepalive snappy for Wooz — detect dead sockets fast.
        var keepSec = IsSystemDefault
            ? Math.Clamp(_fleet.KeepAliveSeconds, 8, 20)
            : Math.Max(5, _fleet.KeepAliveSeconds);
        var interval = TimeSpan.FromSeconds(keepSec);
        using var lost = new CancellationTokenSource();
        using var stayWatch = new CancellationTokenSource();
        _stayWatchdogCts = stayWatch;
        void OnLost()
        {
            try { lost.Cancel(); } catch { /* ignore */ }
        }

        _world.Disconnected += OnLost;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, lost.Token, stayWatch.Token);

            // Let the enter-world SMSG flood finish before we emit anti-AFK traffic.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var whoEvery = 0;
            while (!linked.Token.IsCancellationRequested)
            {
                if (_world.State != SessionState.InWorld || !_world.IsSocketConnected)
                {
                    if (Interlocked.Read(ref _halfOpenSinceUtcTicks) == 0)
                    {
                        Interlocked.Exchange(ref _halfOpenSinceUtcTicks, DateTime.UtcNow.Ticks);
                    }

                    return;
                }

                Interlocked.Exchange(ref _lastInWorldUtcTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _halfOpenSinceUtcTicks, 0);

                if (!string.IsNullOrWhiteSpace(PendingCharacterSwitch))
                {
                    var next = PendingCharacterSwitch!.Trim();
                    PendingCharacterSwitch = null;
                    _sessionOpts.Entry.Character = next;
                    Console.WriteLine($"[{_displayTag}] character switch requested → {next}");
                    return;
                }

                try
                {
                    // Bound keepalive so a half-open TCP cannot wedge StayInWorld forever.
                    using var pulseCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    pulseCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(6, keepSec)));
                    await PulseAsync(pulseCts.Token).ConfigureAwait(false);
                    if (whoEvery++ % 2 == 0)
                    {
                        await _mediator.RefreshWhoAsync(null, linked.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{_displayTag}] keepalive failed: {ex.Message}");
                    return;
                }

                try
                {
                    await Task.Delay(interval, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            _world.Disconnected -= OnLost;
            if (ReferenceEquals(_stayWatchdogCts, stayWatch))
            {
                _stayWatchdogCts = null;
            }
        }
    }

    private async Task PulseAsync(CancellationToken cancellationToken)
    {
        await SendPingAsync(cancellationToken).ConfigureAwait(false);
        await _world.SendAsync(new Packet(Opcodes.CmsgKeepAlive, ReadOnlyMemory<byte>.Empty), cancellationToken)
            .ConfigureAwait(false);

        // Only emit movement once we have a real player object — zero-guid heartbeats
        // after enter-world can get the session dropped.
        var player = _objects.Snapshot().FirstOrDefault(o => o.TypeId == 4);
        if (player is null)
        {
            return;
        }

        await _actions.MoveHeartbeatAsync(player.X, player.Y, player.Z, player.Orientation, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendPingAsync(CancellationToken cancellationToken)
    {
        var payload = new byte[8];
        var seq = _pingSequence++;
        var latency = _lastPongTick > 0
            ? (uint)Math.Clamp(Environment.TickCount64 - _lastPongTick, 0, int.MaxValue)
            : 0u;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), seq);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), latency);
        await _world.SendAsync(new Packet(Opcodes.CmsgPing, payload), cancellationToken).ConfigureAwait(false);
    }

    private void OnPacket(Packet packet)
    {
        if (packet.Opcode == Opcodes.SmsgPong)
        {
            _lastPongTick = Environment.TickCount64;
        }
    }

    private async Task BootstrapChatAsync(CancellationToken cancellationToken)
    {
        var delaySec = Math.Max(0, _fleet.ChannelJoinDelaySeconds);
        if (delaySec > 0)
        {
            Console.WriteLine($"[{_displayTag}] waiting {delaySec}s before channel joins…");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySec), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        // Prefer a live player object before joining (Ascension rejects early joins as invalid_name).
        for (var i = 0; i < 40; i++)
        {
            if (_objects.Snapshot().Any(o => o.TypeId == 4))
            {
                break;
            }

            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        try
        {
            // Channel joins run in the background so world/chat updates keep flowing.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _mediator.JoinAllChannelsAsync(_autoJoinChannels, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"[{_displayTag}] channel join pass complete");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{_displayTag}] channel join pass failed: {ex.Message}");
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_displayTag}] channel join schedule failed: {ex.Message}");
        }

        try
        {
            await _intel.RunStartupProbesAsync(_mediator, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[{_displayTag}] world intel startup probes complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_displayTag}] world intel probes failed: {ex.Message}");
        }
    }

    private async Task ResetSessionAsync()
    {
        try { _probe.Detach(_world); } catch { /* ignore */ }
        try { _mediator.Detach(_world); } catch { /* ignore */ }
        // Global OM: never wipe shared aggregate — only drop this session's visibility tags.
        _objects.SoftClearSession(_displayTag);
        _pingSequence = 1;
        _lastPongTick = 0;
        await _world.DisconnectForReconnectAsync().ConfigureAwait(false);
    }

    private static async Task BackoffAsync(int seconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _mediator.Detach(_world); } catch { /* ignore */ }
        _world.PacketReceived -= OnPacket;
        await _world.DisposeAsync().ConfigureAwait(false);
        _objects.SoftClearSession(_displayTag);
    }
}
