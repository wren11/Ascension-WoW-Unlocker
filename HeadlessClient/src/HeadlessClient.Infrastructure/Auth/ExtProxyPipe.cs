using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace HeadlessClient.Infrastructure.Auth;

public static class ExtProxyPipe
{
    public const uint CmdMagic = 0x444D4341u;
    public const uint CmdSetChatCapture = 55;
    public const uint CmdRunLua = 12;
    public const uint CmdReplay = 5;

    public static int ResolveProxyPid(string? runtimeInstDir)
    {
        if (!string.IsNullOrWhiteSpace(runtimeInstDir))
        {
            var pidPath = Path.Combine(runtimeInstDir, "ExtProxy64.pid");
            if (File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath).Trim(), out var filePid))
            {
                return filePid;
            }
        }

        var procs = Process.GetProcessesByName("Ascension.launch");
        return procs.Length > 0 ? procs[0].Id : 0;
    }

    public static async Task<(bool Ok, byte[] Body)> SendAsync(
        int pid,
        uint cmd,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        int connectTimeoutMs = 8000)
    {
        if (pid <= 0)
        {
            return (false, Array.Empty<byte>());
        }

        var pipeName = $"AscensionExtProxyV5_{pid}";
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeoutMs, cancellationToken).ConfigureAwait(false);

        var hdr = new byte[12];
        BitConverter.TryWriteBytes(hdr.AsSpan(0, 4), CmdMagic);
        BitConverter.TryWriteBytes(hdr.AsSpan(4, 4), cmd);
        BitConverter.TryWriteBytes(hdr.AsSpan(8, 4), (uint)payload.Length);
        await pipe.WriteAsync(hdr, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

        var rh = new byte[12];
        await ReadExactAsync(pipe, rh, cancellationToken).ConfigureAwait(false);
        var len = BitConverter.ToUInt32(rh, 8);
        var body = len == 0 ? Array.Empty<byte>() : new byte[len];
        if (len > 0)
        {
            await ReadExactAsync(pipe, body, cancellationToken).ConfigureAwait(false);
        }

        return (true, body);
    }

    public static async Task<bool> SetChatCaptureAsync(int pid, bool on, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        BitConverter.TryWriteBytes(payload.AsSpan(), on ? 1u : 0u);
        var (ok, _) = await SendAsync(pid, CmdSetChatCapture, payload, cancellationToken).ConfigureAwait(false);
        return ok;
    }

    public static async Task<bool> RunLuaAsync(int pid, string script, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(script))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(script);
        var (ok, _) = await SendAsync(pid, CmdRunLua, bytes, cancellationToken, 2000).ConfigureAwait(false);
        return ok;
    }

    public static async Task<bool> ReplayAsync(int pid, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (packet.IsEmpty)
        {
            return false;
        }

        var (ok, _) = await SendAsync(pid, CmdReplay, packet, cancellationToken, 2000).ConfigureAwait(false);
        return ok;
    }

    static async Task ReadExactAsync(PipeStream pipe, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("ExtProxy pipe closed.");
            }

            read += n;
        }
    }
}
