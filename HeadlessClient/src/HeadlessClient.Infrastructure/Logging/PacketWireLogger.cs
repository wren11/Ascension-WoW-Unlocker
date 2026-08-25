using System.Text;
using HeadlessClient.Infrastructure.Config;

namespace HeadlessClient.Infrastructure.Logging;

public enum PacketDirection : byte
{
    Recv = 0,
    Send = 1
}

/// <summary>
/// WPE-style hex|ASCII packet logger with per-tag rotating files.
/// Does not retain packet bodies in memory — write-through only.
/// </summary>
public sealed class PacketWireLogger : IDisposable
{
    private readonly PacketLogOptions _opts;
    private readonly object _gate = new();
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _activePaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PacketWireLogger(PacketLogOptions opts)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        if (_opts.Enabled)
        {
            Directory.CreateDirectory(_opts.Directory);
        }
    }

    public void Log(
        string tag,
        PacketDirection direction,
        uint opcode,
        ReadOnlySpan<byte> payload)
    {
        if (!_opts.Enabled || _disposed)
        {
            return;
        }

        var safeTag = SanitizeTag(tag);
        var dir = direction == PacketDirection.Recv ? "S->C" : "C->S";
        var dumpLen = Math.Min(payload.Length, Math.Max(0, _opts.MaxPayloadDumpBytes));
        var truncated = payload.Length > dumpLen;

        var header =
            $"{DateTime.Now:HH:mm:ss.fff} [{safeTag}] {dir} opcode=0x{opcode:X4} ({opcode}) len={payload.Length}" +
            (truncated ? $" dump={dumpLen}+trunc" : string.Empty);

        var body = FormatWpe(payload.Slice(0, dumpLen));

        if (_opts.MirrorToConsole)
        {
            Console.WriteLine(header);
            if (dumpLen > 0 && dumpLen <= 64)
            {
                // Short bodies only on console to keep terminal readable.
                Console.WriteLine(body);
            }
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var writer = GetWriter(safeTag);
            writer.WriteLine(header);
            writer.WriteLine(body);
            writer.WriteLine();
            writer.Flush();
            MaybeRotate(safeTag, writer);
        }
    }

    public static string FormatWpe(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return "0000";
        }

        var sb = new StringBuilder((data.Length / 16 + 1) * 80);
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var lineLen = Math.Min(16, data.Length - offset);
            sb.Append(offset.ToString("X4"));
            sb.Append("  ");

            for (var i = 0; i < 16; i++)
            {
                if (i == 8)
                {
                    sb.Append(' ');
                }

                if (i < lineLen)
                {
                    sb.Append(data[offset + i].ToString("X2"));
                    sb.Append(' ');
                }
                else
                {
                    sb.Append("   ");
                }
            }

            sb.Append(' ');
            for (var i = 0; i < lineLen; i++)
            {
                var b = data[offset + i];
                sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            }

            if (offset + 16 < data.Length)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private StreamWriter GetWriter(string tag)
    {
        if (_writers.TryGetValue(tag, out var existing))
        {
            return existing;
        }

        var path = Path.Combine(_opts.Directory, $"{DateTime.Now:yyyyMMdd}_{tag}.log");
        var writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            Encoding.UTF8)
        {
            AutoFlush = false
        };
        _writers[tag] = writer;
        _activePaths[tag] = path;
        return writer;
    }

    private void MaybeRotate(string tag, StreamWriter writer)
    {
        if (!_activePaths.TryGetValue(tag, out var path))
        {
            return;
        }

        try
        {
            writer.Flush();
            var len = new FileInfo(path).Length;
            if (len < _opts.MaxFileBytes)
            {
                return;
            }

            writer.Dispose();
            _writers.Remove(tag);

            var rotated = Path.Combine(
                _opts.Directory,
                $"{DateTime.Now:yyyyMMdd_HHmmss}_{tag}.log");
            File.Move(path, rotated, overwrite: true);
            PruneOldFiles(tag);

            var fresh = GetWriter(tag);
            fresh.WriteLine($"--- rotated from {Path.GetFileName(path)} at {DateTime.Now:O} ---");
            fresh.Flush();
        }
        catch
        {
            // Logging must never take down a session.
        }
    }

    private void PruneOldFiles(string tag)
    {
        try
        {
            var files = Directory.GetFiles(_opts.Directory, $"*_{tag}.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var old in files.Skip(Math.Max(1, _opts.MaxFilesPerAccount)))
            {
                try { old.Delete(); } catch { /* ignore */ }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string SanitizeTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "unknown";
        }

        var chars = tag.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] is not ('_' or '-'))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var w in _writers.Values)
            {
                try { w.Flush(); w.Dispose(); } catch { /* ignore */ }
            }

            _writers.Clear();
            _activePaths.Clear();
        }
    }
}
