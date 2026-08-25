using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.Session;
using HeadlessClient.Infrastructure.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeadlessClient.Infrastructure.World;

/// <summary>
/// Keeps the Tcp world session alive: periodic CMSG_PING, CMSG_KEEP_ALIVE, and movement heartbeat.
/// </summary>
public sealed class WorldSessionMaintenance : BackgroundService
{
    private readonly IWorldClient _world;
    private readonly IWorldActions _actions;
    private readonly IObjectDirectory _objects;
    private readonly HeadlessOptions _options;
    private readonly ILogger<WorldSessionMaintenance>? _log;
    private uint _pingSequence = 1;
    private long _lastPongTick;

    public WorldSessionMaintenance(
        IWorldClient world,
        IWorldActions actions,
        IObjectDirectory objects,
        HeadlessOptions options,
        ILogger<WorldSessionMaintenance>? log = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
        _world.PacketReceived += OnPacket;
    }

    public bool IsAlive { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mode = string.IsNullOrWhiteSpace(_options.AuthMode) ? "Tcp" : _options.AuthMode;
        if (!mode.Equals("Tcp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _log?.LogInformation("World session maintenance active (ping/keepalive/heartbeat).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_world.State == SessionState.InWorld)
                {
                    await PulseAsync(stoppingToken).ConfigureAwait(false);
                    IsAlive = true;
                }
                else if (_world.State is SessionState.Failed or SessionState.Disconnected)
                {
                    IsAlive = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "World session maintenance tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PulseAsync(CancellationToken cancellationToken)
    {
        await SendPingAsync(cancellationToken).ConfigureAwait(false);
        await _world.SendAsync(new Packet(Opcodes.CmsgKeepAlive, ReadOnlyMemory<byte>.Empty), cancellationToken)
            .ConfigureAwait(false);
        await SendMovementHeartbeatAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task SendMovementHeartbeatAsync(CancellationToken cancellationToken)
    {
        var player = _objects.Snapshot().FirstOrDefault(o => o.TypeId == 4);
        var x = player?.X ?? 0f;
        var y = player?.Y ?? 0f;
        var z = player?.Z ?? 0f;
        var o = player?.Orientation ?? 0f;
        await _actions.MoveHeartbeatAsync(x, y, z, o, cancellationToken).ConfigureAwait(false);
    }

    private void OnPacket(Packet packet)
    {
        if (packet.Opcode == Opcodes.SmsgPong)
        {
            _lastPongTick = Environment.TickCount64;
        }
    }

    public override void Dispose()
    {
        _world.PacketReceived -= OnPacket;
        base.Dispose();
    }
}
