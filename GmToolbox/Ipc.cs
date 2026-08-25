using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AscensionNetTool;

static class IpcConstants
{
    public const uint PktMagic = 0x504B5431;
    public const uint CmdMagic = 0x444D4341;
    public const string RingNameBase = "Local\\AscensionExtProxyRingV5";
    public const string PipeNameBase = "AscensionExtProxyV5";
    public static string PidFile => Paths.PidFile;
    public const int RingSlots = 2048;
    public const int RingMax = 2048;
    public const int ReplayMax = 2048;
    public const int BookmarkSlots = 16;
    public const int PipeTimeoutMs = 2000;

    public const int MaxCmdPayload = 24576;
}

enum PktDir : byte
{
    Out = 0,
    In = 1,
    Replay = 2,
}

enum PktCmd : uint
{
    Ping = 1,
    GetConfig = 2,
    SetConfig = 3,
    SetSniff = 4,
    Replay = 5,
    GetStatus = 6,
    RunLua = 12,
    OpcodeName = 17,
    ExtNetInfo = 18,
    SetSpeed = 19,
    MapObjects = 30,
    NavHeight = 31,
    LineOfSight = 32,
    Teleport = 33,
    Target   = 34,
    Loot     = 35,
    Face     = 36,
    FindPath = 37,
    SetMove  = 38,
    ClickToMove = 39,
    MoveStatus = 40,
    SetHacks = 41,
    FindOpcode = 42,
    SetAntiAfk = 43,
    FaceUnit = 44,
    FacingInfo = 45,
    LootAll = 46,
    InjectRecv = 47,
    BookmarkSet = 48,
    BookmarkClear = 49,
    BookmarkFire = 50,
    BookmarkLoop = 51,
    BookmarkBurst = 52,
    SetOpcodeIgnore = 53,
    GetOpcodeIgnore = 54,
    SetChatCapture = 55,
    SubscribeShared = 60,
    SharedQuery = 61,
    SetEntitlements = 72,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MovementConfig
{
    public uint Magic;
    public uint Enabled;
    public float MapX, MapY;
    public float WorldX, WorldY, WorldZ, Facing;
    public uint Opcode, Flags, Flags2, Sequence;
    public uint Flyhack, NoZClip, MapId, Hacks, PacketsOnly, InjectMode;
    public float SpeedScale;
    public uint SpeedCheat, AllowUndermap;
}

public static class ClientHacks
{
    public const uint Waterwalk = 0x00000001;
    public const uint Hover = 0x00000002;
    public const uint NoFall = 0x00000004;
    public const uint SuperJump = 0x00000008;
    public const uint AntiRoot = 0x00000010;
    public const uint Noclip = 0x00000020;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ObjSnapshotHeader
{
    public uint Magic;
    public ulong PlayerGuid;
    public float PlayerX;
    public float PlayerY;
    public float PlayerZ;
    public uint PosOff;
    public uint Count;
    public uint OwnerPid;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ObjUnit
{
    public ulong Guid;
    public ulong TargetGuid;
    public uint Entry;
    public uint TypeMask;
    public uint Health;
    public uint MaxHealth;
    public uint Level;
    public uint Faction;
    public uint UnitFlags;
    public uint DynFlags;
    public float X;
    public float Y;
    public float Z;
    public float Facing;
    public float Dist;
}

static class ObjTypeMask
{
    public const uint Player = 0x10;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct PktRingHeader
{
    public uint Magic;
    public uint SlotCount;
    public uint SlotBytes;
    public uint WriteSeq;
    public uint SniffEnabled;
    public uint DropCount;
    public uint OwnerPid;
    public uint ReadSeq;
    public uint R2, R3, R4, R5, R6, R7;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct PktRingSlot
{
    public uint Seq;
    public uint Tick;
    public byte Dir;
    public byte Pad0;
    public ushort Size;
    public uint Opcode;
    public fixed byte Data[IpcConstants.RingMax];
}

static class ProxyDiscovery
{
    public static uint? ReadActivePid()
    {
        try
        {
            if (!File.Exists(IpcConstants.PidFile))
                return null;
            string t = File.ReadAllText(IpcConstants.PidFile).Trim();
            if (uint.TryParse(t, out uint pid) && pid != 0)
                return pid;
        }
        catch { }
        return null;
    }

    static bool IsProcessAlive(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    public static bool PipeReachable(uint pid, int timeoutMs = 400)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", PipeNameForPid(pid), PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(Math.Clamp(timeoutMs, 50, IpcConstants.PipeTimeoutMs));
            return pipe.IsConnected;
        }
        catch { return false; }
    }

    public static uint? ResolveLivePid()
    {
        var fromFile = ReadActivePid();
        if (fromFile is uint filePid && IsProcessAlive(filePid) && PipeReachable(filePid))
            return filePid;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                // Live lab hosts: Ascension.go* (boot-patched), Ascension.launch, Ascension.exe
                string n = proc.ProcessName;
                bool asc =
                    n.StartsWith("Ascension.go", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Ascension.launch", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Ascension", StringComparison.OrdinalIgnoreCase);
                if (!asc)
                    continue;
                uint pid = (uint)proc.Id;
                if (PipeReachable(pid))
                    return pid;
            }
            catch { }
        }
        return null;
    }

    public static string PipeNameForPid(uint pid) => $"{IpcConstants.PipeNameBase}_{pid}";
    public static string RingNameForPid(uint pid) => $"{IpcConstants.RingNameBase}_{pid}";

    public static bool IsPidAlive(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    public static uint? ReadPidFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string t = File.ReadAllText(path).Trim();
            if (uint.TryParse(t, out uint pid) && pid != 0)
                return pid;
        }
        catch { }
        return null;
    }

    /// <summary>All Ascension clients whose ExtProxy V5 pipe answers.</summary>
    public static IEnumerable<uint> EnumerateInstances()
    {
        var found = new List<uint>();
        var seen = new HashSet<uint>();
        foreach (var proc in Process.GetProcesses())
        {
            uint pid;
            try
            {
                string n = proc.ProcessName;
                if (!n.Equals("Ascension.launch", StringComparison.OrdinalIgnoreCase)
                    && !n.StartsWith("Ascension.go", StringComparison.OrdinalIgnoreCase))
                    continue;
                pid = (uint)proc.Id;
            }
            catch { continue; }

            if (!seen.Add(pid)) continue;
            if (PipeReachable(pid, 200))
                found.Add(pid);
        }

        try
        {
            string runtime = Paths.RuntimeDir;
            if (Directory.Exists(runtime))
            {
                foreach (string dir in Directory.EnumerateDirectories(runtime, "inst*"))
                {
                    var pid = ReadPidFile(Path.Combine(dir, "ExtProxy64.pid"));
                    if (pid is uint p && seen.Add(p) && IsPidAlive(p) && PipeReachable(p, 200))
                        found.Add(p);
                }
            }
        }
        catch { }

        return found;
    }

    /// <summary>Deprecated single-instance helper — prefer <see cref="EnumerateInstances"/>.</summary>
    public static uint? ResolvePrimary() => ResolveLivePid();
}

public sealed class ProxyClient : IDisposable
{
    readonly object _gate = new();
    NamedPipeClientStream? _pipe;

    public bool Connected
    {
        get { lock (_gate) return _pipe is { IsConnected: true }; }
    }

    public bool TryConnect()
    {
        lock (_gate)
        {
            // Keep a healthy pipe — Dispose+reconnect was flooding ExtProxy with
            // "client connected/disconnected" and racing Bootstrap PingOnce.
            if (_pipe is { IsConnected: true })
                return true;

            DisposePipe_NoLock();
            var pid = ProxyDiscovery.ResolveLivePid();
            if (pid is null)
                return false;
            try
            {
                var p = new NamedPipeClientStream(
                    ".", ProxyDiscovery.PipeNameForPid(pid.Value), PipeDirection.InOut, PipeOptions.None);
                p.Connect(IpcConstants.PipeTimeoutMs);
                _pipe = p;
                return true;
            }
            catch
            {
                _pipe = null;
                return false;
            }
        }
    }

    /// <summary>Connect to a specific Ascension.go ExtProxy instance (multi-OM harvest).</summary>
    public bool TryConnectToPid(uint pid)
    {
        lock (_gate)
        {
            DisposePipe_NoLock();
            try
            {
                var p = new NamedPipeClientStream(
                    ".", ProxyDiscovery.PipeNameForPid(pid), PipeDirection.InOut, PipeOptions.None);
                p.Connect(IpcConstants.PipeTimeoutMs);
                _pipe = p;
                return true;
            }
            catch
            {
                _pipe = null;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) DisposePipe_NoLock();
    }

    void DisposePipe_NoLock()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    byte[] Transact(PktCmd cmd, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > IpcConstants.MaxCmdPayload)
            throw new InvalidOperationException(
                $"Payload {payload.Length}B exceeds proxy limit {IpcConstants.MaxCmdPayload}B ({cmd}).");

        lock (_gate)
        {
            if (_pipe is null || !_pipe.IsConnected)
                throw new InvalidOperationException("Not connected to ExtProxy64 pipe.");

            try
            {
                Span<byte> hdr = stackalloc byte[12];
                BitConverter.TryWriteBytes(hdr[..4], IpcConstants.CmdMagic);
                BitConverter.TryWriteBytes(hdr.Slice(4, 4), (uint)cmd);
                BitConverter.TryWriteBytes(hdr.Slice(8, 4), (uint)payload.Length);
                _pipe.Write(hdr);
                if (!payload.IsEmpty)
                    _pipe.Write(payload);
                _pipe.Flush();

                Span<byte> rh = stackalloc byte[12];
                ReadExact(_pipe, rh);
                uint magic = BitConverter.ToUInt32(rh);
                uint len = BitConverter.ToUInt32(rh.Slice(8));
                if (magic != IpcConstants.CmdMagic)
                    throw new IOException("Bad reply magic from proxy.");
                if (len > 1_000_000)
                    throw new IOException("Reply too large.");
                var body = len == 0 ? Array.Empty<byte>() : new byte[len];
                if (len > 0)
                    ReadExact(_pipe, body);
                return body;
            }
            catch
            {

                DisposePipe_NoLock();
                throw;
            }
        }
    }

    static void ReadExact(Stream s, Span<byte> buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf[off..]);
            if (n <= 0)
                throw new EndOfStreamException();
            off += n;
        }
    }

    public static bool PingOnceTimed(int timeoutMs = 1500)
    {
        try
        {
            var pid = ProxyDiscovery.ResolveLivePid();
            if (pid is null) return false;
            using var pipe = new NamedPipeClientStream(".", ProxyDiscovery.PipeNameForPid(pid.Value),
                PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(Math.Min(timeoutMs, 1000));
            var hdr = new byte[12];
            BitConverter.TryWriteBytes(hdr.AsSpan(0, 4), IpcConstants.CmdMagic);
            BitConverter.TryWriteBytes(hdr.AsSpan(4, 4), (uint)PktCmd.Ping);
            BitConverter.TryWriteBytes(hdr.AsSpan(8, 4), 0u);
            pipe.Write(hdr, 0, 12);
            pipe.Flush();

            var resp = new byte[16];
            using var cts = new CancellationTokenSource(timeoutMs);
            Task<int> readTask = pipe.ReadAsync(resp.AsMemory(0, resp.Length), cts.Token).AsTask();
            if (!readTask.Wait(timeoutMs))
                return false;
            int n = readTask.Result;
            if (n < 12)
                return false;
            uint magic = BitConverter.ToUInt32(resp, 0);
            uint len = BitConverter.ToUInt32(resp, 8);
            if (magic != IpcConstants.CmdMagic)
                return false;
            if (len >= 4 && n < 16)
            {
                Task<int> bodyTask = pipe.ReadAsync(resp.AsMemory(12, 4), cts.Token).AsTask();
                if (!bodyTask.Wait(timeoutMs) || bodyTask.Result < 4)
                    return false;
            }
            return len >= 4 && BitConverter.ToUInt32(resp, 12) == 1;
        }
        catch { return false; }
    }

    public bool Ping()
    {
        try
        {
            var r = Transact(PktCmd.Ping, ReadOnlySpan<byte>.Empty);
            return r.Length >= 4 && BitConverter.ToUInt32(r) == 1;
        }
        catch { return false; }
    }

    public bool SetSniff(bool on)
    {
        var r = Transact(PktCmd.SetSniff, BitConverter.GetBytes(on ? 1u : 0u));
        if (r.Length < 4)
            return false;
        return (BitConverter.ToUInt32(r) != 0) == on;
    }

    public uint SetOpcodeIgnore(IReadOnlyList<uint> opcodes)
    {
        int n = Math.Min(opcodes.Count, 4096);
        var buf = new byte[4 + n * 2];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), (uint)n);
        for (int i = 0; i < n; i++)
            BitConverter.TryWriteBytes(buf.AsSpan(4 + i * 2, 2), (ushort)Opcodes.Normalize(opcodes[i]));
        var r = Transact(PktCmd.SetOpcodeIgnore, buf);
        return r.Length >= 4 ? BitConverter.ToUInt32(r, 0) : 0;
    }

    public List<uint> GetOpcodeIgnore()
    {
        var r = Transact(PktCmd.GetOpcodeIgnore, ReadOnlySpan<byte>.Empty);
        var list = new List<uint>();
        if (r.Length < 4) return list;
        uint n = BitConverter.ToUInt32(r, 0);
        int max = Math.Max(0, (r.Length - 4) / 2);
        if (n > (uint)max) n = (uint)max;
        for (int i = 0; i < n; i++)
            list.Add(BitConverter.ToUInt16(r, 4 + i * 2));
        return list;
    }

    public bool SetChatCapture(bool on)
    {
        var r = Transact(PktCmd.SetChatCapture, BitConverter.GetBytes(on ? 1u : 0u));
        return r.Length >= 4 && BitConverter.ToUInt32(r, 0) != 0;
    }

    public bool Replay(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0 || packet.Length > IpcConstants.ReplayMax)
            return false;
        var payload = new byte[4 + packet.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), (uint)packet.Length);
        packet.CopyTo(payload.AsSpan(4));
        var r = Transact(PktCmd.Replay, payload);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }

    /// <summary>Queue a fake SMSG into the client's ProcessIncoming path.</summary>
    public bool InjectRecv(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0 || packet.Length > IpcConstants.ReplayMax)
            return false;
        var payload = new byte[4 + packet.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), (uint)packet.Length);
        packet.CopyTo(payload.AsSpan(4));
        var r = Transact(PktCmd.InjectRecv, payload);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }

    public bool BookmarkSet(int slot, int dir, ReadOnlySpan<byte> packet)
    {
        if (slot < 1 || slot > IpcConstants.BookmarkSlots) return false;
        if (packet.Length == 0 || packet.Length > IpcConstants.ReplayMax) return false;
        var payload = new byte[12 + packet.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), (uint)slot);
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), (uint)(dir == 0 ? 0 : 1));
        BitConverter.TryWriteBytes(payload.AsSpan(8, 4), (uint)packet.Length);
        packet.CopyTo(payload.AsSpan(12));
        var r = Transact(PktCmd.BookmarkSet, payload);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }

    public bool BookmarkClear(int slot)
    {
        var r = Transact(PktCmd.BookmarkClear, BitConverter.GetBytes((uint)slot));
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }

    public bool BookmarkFire(int slot)
    {
        var r = Transact(PktCmd.BookmarkFire, BitConverter.GetBytes((uint)slot));
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }

    public bool BookmarkLoop(bool on)
    {
        var r = Transact(PktCmd.BookmarkLoop, BitConverter.GetBytes(on ? 1u : 0u));
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }

    public int BookmarkBurst()
    {
        var r = Transact(PktCmd.BookmarkBurst, ReadOnlySpan<byte>.Empty);
        return r.Length >= 4 ? (int)BitConverter.ToUInt32(r) : 0;
    }

    /// <summary>Push aggregated shared-world view into this client (kCmdSubscribeShared).</summary>
    public bool SubscribeShared(ReadOnlySpan<byte> sharedViewBlob)
    {
        if (sharedViewBlob.Length == 0) return false;
        var r = Transact(PktCmd.SubscribeShared, sharedViewBlob);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }

    public (ObjSnapshotHeader Header, ObjUnit[] Units) GetObjects()
    {
        var r = Transact(PktCmd.MapObjects, ReadOnlySpan<byte>.Empty);
        int unitSize = Marshal.SizeOf<ObjUnit>();
        const int HdrV2 = 32; // magic+guid+xyz+pos+count (no owner_pid)
        const int HdrV3 = 36; // + owner_pid
        if (r.Length < HdrV2)
            return (default, Array.Empty<ObjUnit>());

        uint magic = BitConverter.ToUInt32(r, 0);
        if (magic != 0x314A424F && magic != 0x324A424F && magic != 0x334A424F)
            return (default, Array.Empty<ObjUnit>());

        int hdrSize = magic == 0x334A424F ? HdrV3 : HdrV2;
        if (r.Length < hdrSize)
            return (default, Array.Empty<ObjUnit>());

        var handle = GCHandle.Alloc(r, GCHandleType.Pinned);
        try
        {
            nint b = handle.AddrOfPinnedObject();
            var hdr = new ObjSnapshotHeader
            {
                Magic = magic,
                PlayerGuid = (ulong)Marshal.ReadInt64(b, 4),
                PlayerX = BitConverter.ToSingle(r, 12),
                PlayerY = BitConverter.ToSingle(r, 16),
                PlayerZ = BitConverter.ToSingle(r, 20),
                PosOff = BitConverter.ToUInt32(r, 24),
                Count = BitConverter.ToUInt32(r, 28),
                OwnerPid = magic == 0x334A424F && r.Length >= HdrV3
                    ? BitConverter.ToUInt32(r, 32) : 0u,
            };
            int have = (r.Length - hdrSize) / unitSize;
            int n = (int)Math.Min(hdr.Count, (uint)Math.Max(0, have));
            var units = new ObjUnit[n];
            for (int i = 0; i < n; i++)
                units[i] = Marshal.PtrToStructure<ObjUnit>(b + hdrSize + i * unitSize);
            return (hdr, units);
        }
        finally { handle.Free(); }
    }

    public string OpcodeName(uint opcode)
    {
        var r = Transact(PktCmd.OpcodeName, BitConverter.GetBytes(opcode));
        return r.Length == 0 ? "" : Encoding.ASCII.GetString(r).TrimEnd('\0');
    }

    public bool RunLua(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;
        var bytes = Encoding.UTF8.GetBytes(script);
        if (bytes.Length > IpcConstants.MaxCmdPayload)
            return false;
        var r = Transact(PktCmd.RunLua, bytes);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }

    public bool Teleport(float x, float y, float z, float o, uint flags = 0)
    {
        var p = new byte[20];
        BitConverter.TryWriteBytes(p.AsSpan(0, 4), x);
        BitConverter.TryWriteBytes(p.AsSpan(4, 4), y);
        BitConverter.TryWriteBytes(p.AsSpan(8, 4), z);
        BitConverter.TryWriteBytes(p.AsSpan(12, 4), o);
        BitConverter.TryWriteBytes(p.AsSpan(16, 4), flags);
        var r = Transact(PktCmd.Teleport, p);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }
    public bool Target(ulong guid)
    {
        var r = Transact(PktCmd.Target, BitConverter.GetBytes(guid));
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }
    public bool Loot(ulong guid, byte mode)
    {
        var p = new byte[9];
        BitConverter.TryWriteBytes(p.AsSpan(0, 8), guid);
        p[8] = mode;
        var r = Transact(PktCmd.Loot, p);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }
    public bool Face(float tx, float ty)
    {
        var p = new byte[8];
        BitConverter.TryWriteBytes(p.AsSpan(0, 4), tx);
        BitConverter.TryWriteBytes(p.AsSpan(4, 4), ty);
        var r = Transact(PktCmd.Face, p);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }
    public bool FaceUnit(ulong guid)
    {
        var r = Transact(PktCmd.FaceUnit, BitConverter.GetBytes(guid));
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }
    public bool LootAll(ulong guid)
    {
        var r = Transact(PktCmd.LootAll, BitConverter.GetBytes(guid));
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }
    public (uint PosOff, uint FacingOff, bool Resolved, float Facing) FacingInfo()
    {
        var r = Transact(PktCmd.FacingInfo, ReadOnlySpan<byte>.Empty);
        if (r.Length < 16)
            return (0, 0, false, -1000f);
        uint po = BitConverter.ToUInt32(r, 0);
        uint fo = BitConverter.ToUInt32(r, 4);
        uint rz = BitConverter.ToUInt32(r, 8);
        float f = BitConverter.ToSingle(r, 12);
        return (po, fo, rz != 0, f);
    }
    public float[] FindPath(uint map, float sx, float sy, float sz, float ex, float ey, float ez)
    {
        var p = new byte[28];
        BitConverter.TryWriteBytes(p.AsSpan(0, 4), map);
        BitConverter.TryWriteBytes(p.AsSpan(4, 4), sx);
        BitConverter.TryWriteBytes(p.AsSpan(8, 4), sy);
        BitConverter.TryWriteBytes(p.AsSpan(12, 4), sz);
        BitConverter.TryWriteBytes(p.AsSpan(16, 4), ex);
        BitConverter.TryWriteBytes(p.AsSpan(20, 4), ey);
        BitConverter.TryWriteBytes(p.AsSpan(24, 4), ez);
        var r = Transact(PktCmd.FindPath, p);
        if (r.Length < 4) return Array.Empty<float>();
        uint n = BitConverter.ToUInt32(r);
        int need = (int)(3 * n);
        if (r.Length - 4 < need * 4) need = (r.Length - 4) / 4;
        var pts = new float[need];
        Buffer.BlockCopy(r, 4, pts, 0, need * 4);
        return pts;
    }
    public bool SetMove(byte op, float durationS)
    {
        var p = new byte[5];
        p[0] = op;
        BitConverter.TryWriteBytes(p.AsSpan(1, 4), durationS);
        var r = Transact(PktCmd.SetMove, p);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }
    public bool ClickToMove(float x, float y, float z)
    {
        var p = new byte[12];
        BitConverter.TryWriteBytes(p.AsSpan(0, 4), x);
        BitConverter.TryWriteBytes(p.AsSpan(4, 4), y);
        BitConverter.TryWriteBytes(p.AsSpan(8, 4), z);
        var r = Transact(PktCmd.ClickToMove, p);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }
    public (bool Ready, bool Moving, float Tx, float Ty, float Tz, uint RemainMs) MoveStatus()
    {
        var r = Transact(PktCmd.MoveStatus, ReadOnlySpan<byte>.Empty);
        if (r.Length < 24) return (false, false, 0, 0, 0, 0);
        return (
            BitConverter.ToUInt32(r, 0) != 0,
            BitConverter.ToUInt32(r, 4) != 0,
            BitConverter.ToSingle(r, 8),
            BitConverter.ToSingle(r, 12),
            BitConverter.ToSingle(r, 16),
            BitConverter.ToUInt32(r, 20));
    }

    public MovementConfig GetConfig()
    {
        var r = Transact(PktCmd.GetConfig, ReadOnlySpan<byte>.Empty);
        if (r.Length < Marshal.SizeOf<MovementConfig>())
            return default;
        var handle = GCHandle.Alloc(r, GCHandleType.Pinned);
        try { return Marshal.PtrToStructure<MovementConfig>(handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
    }

    public MovementConfig SetConfig(MovementConfig cfg)
    {
        cfg.Magic = 0x4D4F5645u; 
        int n = Marshal.SizeOf<MovementConfig>();
        var buf = new byte[n];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(cfg, handle.AddrOfPinnedObject(), false);
            var r = Transact(PktCmd.SetConfig, buf);
            if (r.Length < n) return cfg;
            var rh = GCHandle.Alloc(r, GCHandleType.Pinned);
            try { return Marshal.PtrToStructure<MovementConfig>(rh.AddrOfPinnedObject()); }
            finally { rh.Free(); }
        }
        finally { handle.Free(); }
    }

    public float SetSpeed(float scale, bool speedCheat = false)
    {
        var p = new byte[8];
        BitConverter.TryWriteBytes(p.AsSpan(0, 4), scale);
        BitConverter.TryWriteBytes(p.AsSpan(4, 4), speedCheat ? 1u : 0u);
        var r = Transact(PktCmd.SetSpeed, p);
        return r.Length >= 4 ? BitConverter.ToSingle(r) : scale;
    }

    public uint SetHacks(uint hacks, bool flyhack = false)
    {
        var p = new byte[8];
        BitConverter.TryWriteBytes(p.AsSpan(0, 4), hacks);
        BitConverter.TryWriteBytes(p.AsSpan(4, 4), flyhack ? 1u : 0u);
        var r = Transact(PktCmd.SetHacks, p);
        return r.Length >= 4 ? BitConverter.ToUInt32(r) : hacks;
    }

    public uint? FindOpcode(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        var r = Transact(PktCmd.FindOpcode, bytes);
        if (r.Length < 4) return null;
        uint op = BitConverter.ToUInt32(r);
        return op == 0 ? null : op;
    }

    public string? ExtNetInfo()
    {
        var r = Transact(PktCmd.ExtNetInfo, ReadOnlySpan<byte>.Empty);
        if (r.Length == 0) return null;
        return Encoding.UTF8.GetString(r).TrimEnd('\0');
    }
    public bool ReplayGmCheat(string opcodeName, ReadOnlySpan<byte> bodyAfterOpcode)
    {
        var op = FindOpcode(opcodeName);
        if (op is null) return false;
        // Wire format: opcode is u16 LE (3.3.5a / Ascension ExtProxy ReadOpcode).
        var pkt = new byte[2 + bodyAfterOpcode.Length];
        BitConverter.TryWriteBytes(pkt.AsSpan(0, 2), (ushort)op.Value);
        bodyAfterOpcode.CopyTo(pkt.AsSpan(2));
        return Replay(pkt);
    }

    /// <summary>Push linked names + instance cap. gateOn must stay false — dropping PLAYER_LOGIN freezes the client.</summary>
    public bool SetEntitlements(bool hasAccount, bool gateOn, int maxInstances, IEnumerable<string>? allowedNames)
    {
        var names = (allowedNames ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim().ToLowerInvariant())
            .Where(n => n.Length is >= 2 and <= 24 && n.All(char.IsLetter))
            .Distinct()
            .Take(16)
            .ToArray();
        const int nameLen = 25;
        var payload = new byte[12 + names.Length * nameLen];
        uint flags = 0;
        if (hasAccount) flags |= 1;
        if (gateOn) flags |= 2;
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), flags);
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), (uint)Math.Clamp(maxInstances, 1, GmtLimits.MaxInstances));
        BitConverter.TryWriteBytes(payload.AsSpan(8, 4), (uint)names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            var bytes = Encoding.ASCII.GetBytes(names[i]);
            int n = Math.Min(bytes.Length, nameLen - 1);
            bytes.AsSpan(0, n).CopyTo(payload.AsSpan(12 + i * nameLen));
        }
        var r = Transact(PktCmd.SetEntitlements, payload);
        return r.Length >= 4 && BitConverter.ToUInt32(r) != 0;
    }
}

sealed class PacketRingReader : IDisposable
{
    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    uint _lastSeq;

    public bool IsOpen => _view is not null;

    public bool TryOpen() =>
        TryOpen(ProxyDiscovery.ReadActivePid() ?? ProxyDiscovery.ResolveLivePid());

    public bool TryOpen(uint? pid)
    {
        Dispose();
        if (pid is null) return false;
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(ProxyDiscovery.RingNameForPid(pid.Value), MemoryMappedFileRights.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            _view.Read(0, out PktRingHeader hdr);
            if (hdr.Magic != IpcConstants.PktMagic)
            {
                Dispose();
                return false;
            }
            _lastSeq = hdr.WriteSeq;
            return true;
        }
        catch
        {
            Dispose();
            return false;
        }
    }

    public bool TryOpen(uint pid) => TryOpen((uint?)pid);

    public List<CapturedPacket> DrainNew(int srcInstance = 0)
    {
        var list = new List<CapturedPacket>();
        if (_view is null) return list;
        _view.Read(0, out PktRingHeader hdr);
        if (hdr.Magic != IpcConstants.PktMagic) return list;
        uint cur = hdr.WriteSeq;
        if (cur == _lastSeq) return list;
        uint start = _lastSeq;
        if (cur > start && cur - start > IpcConstants.RingSlots)
            start = cur - IpcConstants.RingSlots;
        int hdrSize = Marshal.SizeOf<PktRingHeader>();
        int slotSize = Marshal.SizeOf<PktRingSlot>();
        // Cap drain per call — prevents multi-second GC spikes after long idle.
        uint maxTake = Math.Min(cur - start, 256u);
        start = cur - maxTake;
        for (uint seq = start + 1; seq <= cur; seq++)
        {
            uint idx = (seq - 1) % IpcConstants.RingSlots;
            long slotOff = hdrSize + idx * slotSize;
            uint slotSeq = _view.ReadUInt32(slotOff);
            if (slotSeq != seq) continue;
            uint tick = _view.ReadUInt32(slotOff + 4);
            byte dir = _view.ReadByte(slotOff + 8);
            ushort size = _view.ReadUInt16(slotOff + 10);
            uint opcode = _view.ReadUInt32(slotOff + 12);
            if (size > IpcConstants.RingMax) size = IpcConstants.RingMax;
            var data = new byte[size];
            if (size > 0)
                _view.ReadArray(slotOff + 16, data, 0, size);
            list.Add(new CapturedPacket(slotSeq, tick, (PktDir)dir, opcode, data, srcInstance));
        }
        _lastSeq = cur;
        // Publish drain cursor so ExtProxy can count overwrite drops.
        try
        {
            int readSeqOff = (int)Marshal.OffsetOf<PktRingHeader>(nameof(PktRingHeader.ReadSeq));
            _view.Write(readSeqOff, cur);
        }
        catch { }
        return list;
    }

    /// <summary>Advance read cursor to latest write without copying packet payloads.</summary>
    public void SkipToLatest()
    {
        if (_view is null) return;
        try
        {
            _view.Read(0, out PktRingHeader hdr);
            if (hdr.Magic == IpcConstants.PktMagic)
            {
                _lastSeq = hdr.WriteSeq;
                int readSeqOff = (int)Marshal.OffsetOf<PktRingHeader>(nameof(PktRingHeader.ReadSeq));
                _view.Write(readSeqOff, _lastSeq);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { _view?.Dispose(); } catch { }
        try { _mmf?.Dispose(); } catch { }
        _view = null;
        _mmf = null;
    }
}

readonly record struct CapturedPacket(uint Seq, uint Tick, PktDir Dir, uint Opcode, byte[] Data, int SrcInstance = 0);
