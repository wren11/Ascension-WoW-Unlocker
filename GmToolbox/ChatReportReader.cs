using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace AscensionNetTool;

/// <summary>Drains ExtProxy ChatReport MMF (Lua GmReportChat / GmReportPlayer).</summary>
sealed class ChatReportReader : IDisposable
{
    public const uint Magic = 0x43525631; // 1VRC
    public const int Slots = 256;
    public const int SenderLen = 48;
    public const int ChannelLen = 48;
    public const int MsgLen = 320;
    public const int ExtraLen = 96;

    public const uint KindChat = 1;
    public const uint KindPlayer = 2;
    public const uint KindWho = 3;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Header
    {
        public uint Magic;
        public uint SlotCount;
        public uint WriteSeq;
        public uint OwnerPid;
        public uint DropCount;
        public uint R0, R1, R2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct Slot
    {
        public uint Seq;
        public uint TickMs;
        public uint Kind;
        public uint InstanceId;
        public ulong Guid;
        public int Level;
        public int ClassId;
        public int Race;
        public int Gender;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = SenderLen)]
        public byte[] Sender;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ChannelLen)]
        public byte[] Channel;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MsgLen)]
        public byte[] Message;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ExtraLen)]
        public byte[] Extra;
    }

    public sealed class Report
    {
        public uint Kind;
        public int InstanceId;
        public string Guid = "";
        public string Sender = "";
        public string Channel = "";
        public string Message = "";
        public string Extra = "";
        public int Level = -1;
        public int ClassId = -1;
        public int Race = -1;
        public int Gender = -1;
        public uint Seq;
    }

    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    uint _lastSeq;
    uint? _pid;

    static readonly int HeaderSize = 32; // 8 x uint32
    static readonly int SlotSize = 552;  // must match ExtProxy ChatReportSlot pack(1)

    public bool IsOpen => _view is not null;

    public bool TryOpen(uint pid)
    {
        Close();
        try
        {
            string name = $"Local\\AscensionExtProxyChatV1_{pid}";
            _mmf = MemoryMappedFile.OpenExisting(name);
            _view = _mmf.CreateViewAccessor(0, HeaderSize + Slots * SlotSize, MemoryMappedFileAccess.Read);
            var hdr = ReadHeader();
            if (hdr.Magic != Magic) { Close(); return false; }
            _pid = pid;
            _lastSeq = hdr.WriteSeq;
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    Header ReadHeader()
    {
        if (_view is null) return default;
        _view.Read(0, out Header h);
        return h;
    }

    static string ZString(byte[]? b)
    {
        if (b is null || b.Length == 0) return "";
        int n = Array.IndexOf(b, (byte)0);
        if (n < 0) n = b.Length;
        if (n == 0) return "";
        return Encoding.UTF8.GetString(b, 0, n).Trim();
    }

    public IEnumerable<Report> DrainNew()
    {
        if (_view is null) yield break;
        var hdr = ReadHeader();
        if (hdr.Magic != Magic || hdr.SlotCount == 0) yield break;
        uint write = hdr.WriteSeq;
        if (write == _lastSeq) yield break;

        // Catch up at most one full ring to avoid floods after reconnect.
        uint start = _lastSeq;
        uint behind = write - start;
        if (behind > Slots)
            start = write - Slots;

        for (uint seq = start + 1; seq <= write; seq++)
        {
            int idx = (int)((seq - 1) % Slots);
            long off = HeaderSize + (long)idx * SlotSize;
            var slot = new Slot
            {
                Sender = new byte[SenderLen],
                Channel = new byte[ChannelLen],
                Message = new byte[MsgLen],
                Extra = new byte[ExtraLen],
            };
            // Manual field reads — ByValArray via Read<T> is unreliable across runtimes.
            slot.Seq = _view.ReadUInt32(off + 0);
            slot.TickMs = _view.ReadUInt32(off + 4);
            slot.Kind = _view.ReadUInt32(off + 8);
            slot.InstanceId = _view.ReadUInt32(off + 12);
            slot.Guid = _view.ReadUInt64(off + 16);
            slot.Level = _view.ReadInt32(off + 24);
            slot.ClassId = _view.ReadInt32(off + 28);
            slot.Race = _view.ReadInt32(off + 32);
            slot.Gender = _view.ReadInt32(off + 36);
            _view.ReadArray(off + 40, slot.Sender, 0, SenderLen);
            _view.ReadArray(off + 40 + SenderLen, slot.Channel, 0, ChannelLen);
            _view.ReadArray(off + 40 + SenderLen + ChannelLen, slot.Message, 0, MsgLen);
            _view.ReadArray(off + 40 + SenderLen + ChannelLen + MsgLen, slot.Extra, 0, ExtraLen);

            if (slot.Seq != seq) continue;
            yield return new Report
            {
                Kind = slot.Kind,
                InstanceId = (int)slot.InstanceId,
                Guid = PlayerDirectory.NormGuid(slot.Guid),
                Sender = ZString(slot.Sender),
                Channel = ZString(slot.Channel),
                Message = ZString(slot.Message),
                Extra = ZString(slot.Extra),
                Level = slot.Level,
                ClassId = slot.ClassId,
                Race = slot.Race,
                Gender = slot.Gender,
                Seq = slot.Seq,
            };
        }
        _lastSeq = write;
    }

    public void Close()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _pid = null;
    }

    public void Dispose() => Close();
}
