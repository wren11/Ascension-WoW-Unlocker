using System.IO.MemoryMappedFiles;
using System.Text;
using HeadlessClient.Domain.World;

namespace HeadlessClient.Infrastructure.Auth;

/// <summary>Drains ExtProxy ChatReport MMF (Lua GmReportChat / GmReportPlayer).</summary>
public sealed class ExtProxyChatReportReader : IDisposable
{
    public const uint Magic = 0x43525631;
    public const int Slots = 256;
    public const int SenderLen = 48;
    public const int ChannelLen = 48;
    public const int MsgLen = 320;
    public const int ExtraLen = 96;
    public const uint KindChat = 1;
    public const uint KindPlayer = 2;
    public const uint KindWho = 3;

    const int HeaderSize = 32;
    const int SlotSize = 552;

    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    uint _lastSeq;

    public sealed class Report
    {
        public uint Kind;
        public ulong Guid;
        public string Sender = "";
        public string Channel = "";
        public string Message = "";
        public string Extra = "";
        public int Level = -1;
        public int ClassId = -1;
        public int Race = -1;
        public int Gender = -1;
    }

    public bool IsOpen => _view is not null;

    public bool TryOpen(int pid)
    {
        Close();
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            var name = $"Local\\AscensionExtProxyChatV1_{pid}";
            _mmf = MemoryMappedFile.OpenExisting(name);
            _view = _mmf.CreateViewAccessor(0, HeaderSize + (long)Slots * SlotSize, MemoryMappedFileAccess.Read);
            if (_view.ReadUInt32(0) != Magic)
            {
                Close();
                return false;
            }

            _lastSeq = _view.ReadUInt32(8);
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    public List<Report> DrainReports()
    {
        var list = new List<Report>();
        if (_view is null)
        {
            return list;
        }

        var magic = _view.ReadUInt32(0);
        var slotCount = _view.ReadUInt32(4);
        var write = _view.ReadUInt32(8);
        if (magic != Magic || slotCount == 0 || write == _lastSeq)
        {
            return list;
        }

        var start = _lastSeq;
        if (write - start > Slots)
        {
            start = write - Slots;
        }

        for (var seq = start + 1; seq <= write; seq++)
        {
            var idx = (int)((seq - 1) % Slots);
            long off = HeaderSize + (long)idx * SlotSize;
            if (_view.ReadUInt32(off) != seq)
            {
                continue;
            }

            var kind = _view.ReadUInt32(off + 8);
            var guid = _view.ReadUInt64(off + 16);
            var level = _view.ReadInt32(off + 24);
            var classId = _view.ReadInt32(off + 28);
            var race = _view.ReadInt32(off + 32);
            var gender = _view.ReadInt32(off + 36);
            var sender = new byte[SenderLen];
            var channel = new byte[ChannelLen];
            var message = new byte[MsgLen];
            var extra = new byte[ExtraLen];
            _view.ReadArray(off + 40, sender, 0, SenderLen);
            _view.ReadArray(off + 40 + SenderLen, channel, 0, ChannelLen);
            _view.ReadArray(off + 40 + SenderLen + ChannelLen, message, 0, MsgLen);
            _view.ReadArray(off + 40 + SenderLen + ChannelLen + MsgLen, extra, 0, ExtraLen);

            list.Add(new Report
            {
                Kind = kind,
                Guid = guid,
                Sender = ZString(sender),
                Channel = ZString(channel),
                Message = ZString(message),
                Extra = ZString(extra),
                Level = level,
                ClassId = classId,
                Race = race,
                Gender = gender
            });
        }

        _lastSeq = write;
        return list;
    }

    public List<ChatLine> DrainNew()
    {
        var list = new List<ChatLine>();
        foreach (var r in DrainReports())
        {
            if (r.Kind != KindChat)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(r.Message) && string.IsNullOrWhiteSpace(r.Sender))
            {
                continue;
            }

            list.Add(new ChatLine(
                DateTimeOffset.UtcNow,
                1,
                string.IsNullOrWhiteSpace(r.Extra) ? "0" : r.Extra,
                r.Sender,
                string.IsNullOrWhiteSpace(r.Channel) ? "SAY" : r.Channel,
                r.Message,
                r.Guid == 0 ? "" : r.Guid.ToString("X16")));
        }

        return list;
    }

    static string ZString(byte[] b)
    {
        var n = Array.IndexOf(b, (byte)0);
        if (n < 0)
        {
            n = b.Length;
        }

        return n <= 0 ? string.Empty : Encoding.UTF8.GetString(b, 0, n).Trim();
    }

    public void Close()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
    }

    public void Dispose() => Close();
}
