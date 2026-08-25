using System.Buffers.Binary;
using System.IO.Compression;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Monitoring;

namespace HeadlessClient.Infrastructure.Protocol;

/// <summary>
/// Projects WotLK 3.3.5 <c>SMSG_UPDATE_OBJECT</c> / compressed updates into the shared Object Manager.
/// Layout verified against gtker/wow_messages <c>smsg_update_object_3_3_5.wowm</c>.
/// </summary>
public sealed class UpdateObjectProjector
{
    // UpdateFlag (u16)
    private const ushort UpdateLiving = 0x0020;
    private const ushort UpdateHasPosition = 0x0040;
    private const ushort UpdateVehicle = 0x0080;
    private const ushort UpdatePosition = 0x0100;
    private const ushort UpdateRotation = 0x0200;
    private const ushort UpdateHighGuid = 0x0010;
    private const ushort UpdateLowGuid = 0x0008;
    private const ushort UpdateHasAttackingTarget = 0x0004;
    private const ushort UpdateTransport = 0x0002;

    // MovementFlags (u48) — low 32 + high 16
    private const ulong MoveOnTransport = 0x0000_0000_0000_0200UL;
    private const ulong MoveFalling = 0x0000_0000_0000_1000UL;
    private const ulong MoveSwimming = 0x0000_0000_0020_0000UL;
    private const ulong MoveFlying = 0x0000_0000_0200_0000UL;
    private const ulong MoveSplineElevation = 0x0000_0000_0400_0000UL;
    private const ulong MoveSplineEnabled = 0x0000_0000_0800_0000UL;
    private const ulong MoveAlwaysAllowPitching = 0x0000_0020_0000_0000UL;
    private const ulong MoveInterpolatedMovement = 0x0000_0400_0000_0000UL;
    private const ulong MoveOnTransportAndInterpolated = MoveOnTransport | MoveInterpolatedMovement;

    // SplineFlag (u32)
    private const uint SplineFinalPoint = 0x0000_8000;
    private const uint SplineFinalTarget = 0x0001_0000;
    private const uint SplineFinalAngle = 0x0002_0000;

    /// <summary>WoW map XYZ never legitimately exceed this absolute magnitude.</summary>
    public const float MaxSaneAbsCoord = 100_000f;

    private readonly IObjectDirectory _directory;

    public UpdateObjectProjector(IObjectDirectory directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public void Project(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Opcode == Opcodes.SmsgUpdateObject)
        {
            TryProjectPayload(packet.Payload.Span);
            return;
        }

        if (packet.Opcode == Opcodes.SmsgCompressedUpdateObject)
        {
            if (!TryDecompress(packet.Payload.Span, out var inflated))
            {
                return;
            }

            TryProjectPayload(inflated);
        }
    }

    public static bool IsSaneWorldPosition(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            return false;
        }

        return Math.Abs(x) <= MaxSaneAbsCoord
               && Math.Abs(y) <= MaxSaneAbsCoord
               && Math.Abs(z) <= MaxSaneAbsCoord;
    }

    private void TryProjectPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            return;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        if (count == 0 || count > 10_000)
        {
            return;
        }

        var offset = 4;
        for (var i = 0; i < count && offset < payload.Length; i++)
        {
            if (payload.Length < offset + 1)
            {
                return;
            }

            var updateType = payload[offset++];
            switch (updateType)
            {
                case 0: // VALUES
                {
                    if (!TryReadPackedGuid(payload, ref offset, out var guid))
                    {
                        return;
                    }

                    if (!TrySkipValuesUpdate(payload, ref offset))
                    {
                        return;
                    }

                    // Identity-only touch so the GUID stays alive without inventing coords.
                    var entry = GuidIntel.EntryFromGuid(guid);
                    _directory.Observe(new WorldObject(
                        guid, null, entry, 0, 0, 0, 0, 0, 0,
                        WorldIntelService.InferTypeId(guid),
                        Source: "live"), seenBy: null);
                    break;
                }
                case 1: // MOVEMENT
                {
                    if (!TryReadPackedGuid(payload, ref offset, out var guid))
                    {
                        return;
                    }

                    if (!TryReadMovementBlock(payload, ref offset, out var x, out var y, out var z, out var o, out var hasPos))
                    {
                        return;
                    }

                    ObserveWithOptionalPos(guid, x, y, z, o, hasPos, typeHint: 0);
                    break;
                }
                case 2: // CREATE_OBJECT
                case 3: // CREATE_OBJECT2
                {
                    if (!TryReadPackedGuid(payload, ref offset, out var guid))
                    {
                        return;
                    }

                    if (offset >= payload.Length)
                    {
                        return;
                    }

                    var objectType = payload[offset++];
                    if (!TryReadMovementBlock(payload, ref offset, out var x, out var y, out var z, out var o, out var hasPos))
                    {
                        return;
                    }

                    if (!TrySkipValuesUpdate(payload, ref offset))
                    {
                        return;
                    }

                    ObserveWithOptionalPos(guid, x, y, z, o, hasPos, typeHint: objectType);
                    break;
                }
                case 4: // OUT_OF_RANGE_OBJECTS
                case 5: // NEAR_OBJECTS (same shape)
                {
                    if (payload.Length < offset + 4)
                    {
                        return;
                    }

                    var n = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
                    offset += 4;
                    if (n > 10_000)
                    {
                        return;
                    }

                    for (var g = 0; g < n; g++)
                    {
                        if (!TryReadPackedGuid(payload, ref offset, out var guid))
                        {
                            return;
                        }

                        if (updateType == 4)
                        {
                            _directory.Remove(guid);
                        }
                    }

                    break;
                }
                default:
                    return;
            }
        }
    }

    private void ObserveWithOptionalPos(
        ulong guid,
        float x,
        float y,
        float z,
        float o,
        bool hasPos,
        byte typeHint)
    {
        if (hasPos && !IsSaneWorldPosition(x, y, z))
        {
            hasPos = false;
            x = y = z = o = 0;
        }

        if (!float.IsFinite(o))
        {
            o = 0;
        }

        var typeId = typeHint != 0 ? typeHint : WorldIntelService.InferTypeId(guid);
        var entry = GuidIntel.EntryFromGuid(guid);
        _directory.Observe(new WorldObject(
            guid,
            null,
            entry,
            hasPos ? x : 0,
            hasPos ? y : 0,
            hasPos ? z : 0,
            hasPos ? o : 0,
            0,
            0,
            typeId,
            Source: hasPos ? "live" : "live-nopos"), seenBy: null);
    }

    private static bool TryDecompress(ReadOnlySpan<byte> payload, out byte[] inflated)
    {
        inflated = Array.Empty<byte>();
        if (payload.Length < 4)
        {
            return false;
        }

        var expected = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4));
        if (expected <= 0 || expected > 16 * 1024 * 1024)
        {
            return false;
        }

        try
        {
            using var input = new MemoryStream(payload.Slice(4).ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expected);
            zlib.CopyTo(output);
            inflated = output.ToArray();
            return inflated.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadPackedGuid(ReadOnlySpan<byte> data, ref int offset, out ulong guid)
    {
        guid = 0;
        if (offset >= data.Length)
        {
            return false;
        }

        var mask = data[offset++];
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) == 0)
            {
                continue;
            }

            if (offset >= data.Length)
            {
                return false;
            }

            value |= (ulong)data[offset++] << (8 * i);
        }

        guid = value;
        return true;
    }

    private static bool TryReadU48(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        if (data.Length < offset + 6)
        {
            return false;
        }

        value = data[offset]
                | ((ulong)data[offset + 1] << 8)
                | ((ulong)data[offset + 2] << 16)
                | ((ulong)data[offset + 3] << 24)
                | ((ulong)data[offset + 4] << 32)
                | ((ulong)data[offset + 5] << 40);
        offset += 6;
        return true;
    }

    private static bool TryReadF32(ReadOnlySpan<byte> data, ref int offset, out float value)
    {
        value = 0;
        if (data.Length < offset + 4)
        {
            return false;
        }

        value = BitConverter.ToSingle(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    private static bool TryReadU32(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        if (data.Length < offset + 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    private static bool TryReadVec3(ReadOnlySpan<byte> data, ref int offset, out float x, out float y, out float z)
    {
        x = y = z = 0;
        return TryReadF32(data, ref offset, out x)
               && TryReadF32(data, ref offset, out y)
               && TryReadF32(data, ref offset, out z);
    }

    private static bool TrySkipTransportInfo(ReadOnlySpan<byte> data, ref int offset)
    {
        // PackedGuid + Vector3d + orientation + timestamp + seat
        if (!TryReadPackedGuid(data, ref offset, out _))
        {
            return false;
        }

        if (!TryReadVec3(data, ref offset, out _, out _, out _))
        {
            return false;
        }

        if (!TryReadF32(data, ref offset, out _))
        {
            return false;
        }

        if (!TryReadU32(data, ref offset, out _))
        {
            return false;
        }

        if (offset >= data.Length)
        {
            return false;
        }

        offset += 1; // seat
        return true;
    }

    /// <summary>
    /// Full WotLK 3.3.5 MovementBlock. Returns false only on truncated/corrupt stream
    /// (caller must abort the remainder of the packet to avoid float desync).
    /// </summary>
    private static bool TryReadMovementBlock(
        ReadOnlySpan<byte> data,
        ref int offset,
        out float x,
        out float y,
        out float z,
        out float o,
        out bool hasPosition)
    {
        x = y = z = o = 0;
        hasPosition = false;
        if (data.Length < offset + 2)
        {
            return false;
        }

        var updateFlag = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;

        if ((updateFlag & UpdateLiving) != 0)
        {
            if (!TryReadU48(data, ref offset, out var moveFlags))
            {
                return false;
            }

            if (!TryReadU32(data, ref offset, out _)) // timestamp
            {
                return false;
            }

            if (!TryReadVec3(data, ref offset, out x, out y, out z))
            {
                return false;
            }

            if (!TryReadF32(data, ref offset, out o))
            {
                return false;
            }

            hasPosition = true;

            if ((moveFlags & MoveOnTransportAndInterpolated) == MoveOnTransportAndInterpolated)
            {
                if (!TrySkipTransportInfo(data, ref offset))
                {
                    return false;
                }

                if (!TryReadU32(data, ref offset, out _)) // transport_time
                {
                    return false;
                }
            }
            else if ((moveFlags & MoveOnTransport) != 0)
            {
                if (!TrySkipTransportInfo(data, ref offset))
                {
                    return false;
                }
            }

            if ((moveFlags & MoveSwimming) != 0
                || (moveFlags & MoveFlying) != 0
                || (moveFlags & MoveAlwaysAllowPitching) != 0)
            {
                if (!TryReadF32(data, ref offset, out _)) // pitch
                {
                    return false;
                }
            }

            if (!TryReadF32(data, ref offset, out _)) // fall_time
            {
                return false;
            }

            if ((moveFlags & MoveFalling) != 0)
            {
                // z_speed, cos, sin, xy_speed
                for (var i = 0; i < 4; i++)
                {
                    if (!TryReadF32(data, ref offset, out _))
                    {
                        return false;
                    }
                }
            }

            if ((moveFlags & MoveSplineElevation) != 0)
            {
                if (!TryReadF32(data, ref offset, out _))
                {
                    return false;
                }
            }

            // 9 speeds (walk, run, runback, swim, swimback, fly, flyback, turn, pitch)
            for (var i = 0; i < 9; i++)
            {
                if (!TryReadF32(data, ref offset, out _))
                {
                    return false;
                }
            }

            if ((moveFlags & MoveSplineEnabled) != 0)
            {
                if (!TrySkipSpline(data, ref offset))
                {
                    return false;
                }
            }
        }
        else if ((updateFlag & UpdatePosition) != 0)
        {
            if (!TryReadPackedGuid(data, ref offset, out _))
            {
                return false;
            }

            if (!TryReadVec3(data, ref offset, out x, out y, out z))
            {
                return false;
            }

            if (!TryReadVec3(data, ref offset, out _, out _, out _)) // transport_offset
            {
                return false;
            }

            if (!TryReadF32(data, ref offset, out o))
            {
                return false;
            }

            if (!TryReadF32(data, ref offset, out _)) // corpse_orientation
            {
                return false;
            }

            hasPosition = true;
        }
        else if ((updateFlag & UpdateHasPosition) != 0)
        {
            if (!TryReadVec3(data, ref offset, out x, out y, out z))
            {
                return false;
            }

            if (!TryReadF32(data, ref offset, out o))
            {
                return false;
            }

            hasPosition = true;
        }

        if ((updateFlag & UpdateHighGuid) != 0 && !TryReadU32(data, ref offset, out _))
        {
            return false;
        }

        if ((updateFlag & UpdateLowGuid) != 0 && !TryReadU32(data, ref offset, out _))
        {
            return false;
        }

        if ((updateFlag & UpdateHasAttackingTarget) != 0 && !TryReadPackedGuid(data, ref offset, out _))
        {
            return false;
        }

        if ((updateFlag & UpdateTransport) != 0 && !TryReadU32(data, ref offset, out _))
        {
            return false;
        }

        if ((updateFlag & UpdateVehicle) != 0)
        {
            if (!TryReadU32(data, ref offset, out _))
            {
                return false;
            }

            if (!TryReadF32(data, ref offset, out _))
            {
                return false;
            }
        }

        if ((updateFlag & UpdateRotation) != 0)
        {
            if (data.Length < offset + 8)
            {
                return false;
            }

            offset += 8;
        }

        if (hasPosition && !IsSaneWorldPosition(x, y, z))
        {
            // Keep stream sync, but discard garbage floats so OM merge won't adopt them.
            hasPosition = false;
            x = y = z = o = 0;
        }

        return true;
    }

    private static bool TrySkipSpline(ReadOnlySpan<byte> data, ref int offset)
    {
        if (!TryReadU32(data, ref offset, out var splineFlags))
        {
            return false;
        }

        if ((splineFlags & SplineFinalAngle) != 0)
        {
            if (!TryReadF32(data, ref offset, out _))
            {
                return false;
            }
        }
        else if ((splineFlags & SplineFinalTarget) != 0)
        {
            if (data.Length < offset + 8)
            {
                return false;
            }

            offset += 8;
        }
        else if ((splineFlags & SplineFinalPoint) != 0)
        {
            if (!TryReadVec3(data, ref offset, out _, out _, out _))
            {
                return false;
            }
        }

        // time_passed, duration, id
        for (var i = 0; i < 3; i++)
        {
            if (!TryReadU32(data, ref offset, out _))
            {
                return false;
            }
        }

        // duration_mod, duration_mod_next, vertical_acceleration, effect_start_time
        for (var i = 0; i < 4; i++)
        {
            if (!TryReadF32(data, ref offset, out _))
            {
                return false;
            }
        }

        if (!TryReadU32(data, ref offset, out var nodeCount))
        {
            return false;
        }

        if (nodeCount > 2048)
        {
            return false;
        }

        for (uint n = 0; n < nodeCount; n++)
        {
            if (!TryReadVec3(data, ref offset, out _, out _, out _))
            {
                return false;
            }
        }

        if (offset >= data.Length)
        {
            return false;
        }

        offset += 1; // mode
        return TryReadVec3(data, ref offset, out _, out _, out _); // final_node
    }

    private static bool TrySkipValuesUpdate(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length)
        {
            return false;
        }

        var blockCount = data[offset++];
        var maskBytes = blockCount * 4;
        if (data.Length < offset + maskBytes)
        {
            return false;
        }

        var setBits = 0;
        for (var i = 0; i < maskBytes; i++)
        {
            setBits += BitOperationsPopCount(data[offset + i]);
        }

        offset += maskBytes;
        var valueBytes = setBits * 4;
        if (data.Length < offset + valueBytes)
        {
            return false;
        }

        offset += valueBytes;
        return true;
    }

    private static int BitOperationsPopCount(byte value)
    {
        var count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }

        return count;
    }
}
