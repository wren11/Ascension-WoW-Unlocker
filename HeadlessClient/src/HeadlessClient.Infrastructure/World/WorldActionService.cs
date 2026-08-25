using System.Buffers.Binary;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;

namespace HeadlessClient.Infrastructure.World;

public sealed class WorldActionService : IWorldActions
{
    private readonly IWorldClient _world;

    public WorldActionService(IWorldClient world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public Task SelectAsync(ulong guid, CancellationToken cancellationToken)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, guid);
        return _world.SendAsync(new Packet(Opcodes.CmsgSetSelection, payload), cancellationToken);
    }

    public Task LootAsync(ulong guid, CancellationToken cancellationToken)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, guid);
        return _world.SendAsync(new Packet(Opcodes.CmsgLoot, payload), cancellationToken);
    }

    public Task UseGameObjectAsync(ulong guid, CancellationToken cancellationToken)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, guid);
        return _world.SendAsync(new Packet(Opcodes.CmsgGameobjUse, payload), cancellationToken);
    }

    public Task MoveFallLandAsync(float x, float y, float z, float o, CancellationToken cancellationToken) =>
        SendMovePacketAsync(Opcodes.MsgMoveFallLand, x, y, z, o, cancellationToken);

    public Task MoveHeartbeatAsync(float x, float y, float z, float o, CancellationToken cancellationToken) =>
        SendMovePacketAsync(Opcodes.MsgMoveHeartbeat, x, y, z, o, cancellationToken);

    private Task SendMovePacketAsync(uint opcode, float x, float y, float z, float o, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream(64);
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(0UL);
            bw.Write(0u);
            bw.Write((uint)Environment.TickCount);
            bw.Write(x);
            bw.Write(y);
            bw.Write(z);
            bw.Write(o);
            bw.Write(0u);
        }

        return _world.SendAsync(new Packet(opcode, ms.ToArray()), cancellationToken);
    }
}
