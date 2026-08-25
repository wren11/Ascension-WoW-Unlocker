using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Auth;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.Session;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Crypto;
using HeadlessClient.Infrastructure.Logging;

namespace HeadlessClient.Infrastructure.World;

public sealed class TcpWorldClient : IWorldClient, IAsyncDisposable
{
    private readonly IHeadlessOptions _options;
    private readonly ICredentialStore _credentials;
    private readonly PacketWireLogger? _packetLog;
    private readonly SessionStateMachine _stateMachine = new();
    private readonly WowCrypt _crypt = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private byte[]? _sessionKey;
    private byte _realmId;
    private RealmInfo? _connectedRealm;
    private byte[] _authProofTail = Array.Empty<byte>();
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    private TaskCompletionSource<Packet>? _authChallengeTcs;
    private TaskCompletionSource<bool>? _authResponseTcs;
    private TaskCompletionSource<IReadOnlyList<CharacterInfo>>? _charEnumTcs;
    private TaskCompletionSource<bool>? _enterWorldTcs;
    private string _logTag = "world";

    public TcpWorldClient(
        IHeadlessOptions options,
        ICredentialStore credentials,
        PacketWireLogger? packetLog = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _packetLog = packetLog;
    }

    public SessionState State => _stateMachine.State;

    /// <summary>Tag used in packet logs as [CharacterName]. Updated after character select.</summary>
    public string LogTag
    {
        get => _logTag;
        set => _logTag = string.IsNullOrWhiteSpace(value) ? "world" : value.Trim();
    }

    public bool IsSocketConnected => _tcp?.Connected == true && _stream is not null;

    public event Action<Packet>? PacketReceived;

    /// <summary>Raised when the read loop dies (server close / framing error) or socket is aborted.</summary>
    public event Action? Disconnected;

    /// <summary>
    /// Immediate abortive teardown for watchdog / half-open sockets.
    /// Does not await hung Read/Write — closes the socket and raises <see cref="Disconnected"/>.
    /// </summary>
    public void AbortSocket(string reason = "")
    {
        try { _readCts?.Cancel(); } catch { /* ignore */ }

        try
        {
            // Abortive close so a hung WriteAsync/ReadAsync unblocks.
            var sock = _tcp?.Client;
            if (sock is not null)
            {
                sock.LingerState = new LingerOption(true, 0);
                sock.Close(0);
            }
        }
        catch { /* ignore */ }

        try { _stream?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        _tcp = null;

        try { _crypt.Reset(); } catch { /* ignore */ }
        _sessionKey = null;
        _connectedRealm = null;
        _authProofTail = Array.Empty<byte>();

        try { _stateMachine.Reset(); } catch { /* ignore */ }

        try { Disconnected?.Invoke(); } catch { /* never throw from event */ }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            Console.WriteLine($"[{LogTag}] AbortSocket: {reason}");
        }
    }

    public async Task ConnectAsync(RealmInfo realm, byte[] sessionKey, CancellationToken cancellationToken)
    {
        await ConnectAsync(realm, sessionKey, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConnectAsync(
        RealmInfo realm,
        byte[] sessionKey,
        ReadOnlyMemory<byte> authProofTail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(sessionKey);
        if (sessionKey.Length != 40)
        {
            throw new ArgumentException("Session key must be 40 bytes.", nameof(sessionKey));
        }

        if (string.IsNullOrWhiteSpace(realm.Address))
        {
            throw new ArgumentException("Realm address is required.", nameof(realm));
        }

        EnsureTransitionPathToWorldConnecting();

        var (host, port) = ParseAddress(realm.Address);
        _sessionKey = (byte[])sessionKey.Clone();
        _realmId = realm.Id;
        _connectedRealm = realm;
        _authProofTail = authProofTail.ToArray();

        _tcp = new TcpClient();
        try
        {
            await _tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            _stream = _tcp.GetStream();
            _authChallengeTcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
            _authResponseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readLoop = Task.Run(() => ReadLoopAsync(_readCts.Token), CancellationToken.None);

            var challengePacket = await _authChallengeTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await SendAuthSessionAsync(challengePacket, cancellationToken).ConfigureAwait(false);

            var ok = await _authResponseTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                _stateMachine.TransitionTo(SessionState.Failed);
                throw new InvalidOperationException(
                    "World auth response indicated failure. Close Ascension.launch if logged into the same account.");
            }

            _stateMachine.TransitionTo(SessionState.CharacterSelect);
        }
        catch
        {
            if (_stateMachine.State != SessionState.Failed)
            {
                try { _stateMachine.TransitionTo(SessionState.Failed); } catch { }
            }

            await DisposeSocketAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<CharacterInfo>> EnumerateCharactersAsync(CancellationToken cancellationToken)
    {
        EnsureState(SessionState.CharacterSelect);
        _charEnumTcs = new TaskCompletionSource<IReadOnlyList<CharacterInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync(new Packet(Opcodes.CmsgCharEnum, ReadOnlyMemory<byte>.Empty), cancellationToken).ConfigureAwait(false);
        return await _charEnumTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnterWorldAsync(ulong characterGuid, CancellationToken cancellationToken)
    {
        EnsureState(SessionState.CharacterSelect);
        _enterWorldTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, characterGuid);
        await SendAsync(new Packet(Opcodes.CmsgPlayerLogin, payload), cancellationToken).ConfigureAwait(false);
        await _enterWorldTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        _stateMachine.TransitionTo(SessionState.InWorld);
    }

    public async Task SendAsync(Packet packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var stream = _stream ?? throw new InvalidOperationException("World client is not connected.");
        var frame = WorldPacketFramer.BuildClientPacket(packet);
        if (_crypt.IsInitialized)
        {
            _crypt.EncryptSendHeader(frame.AsSpan(0, 6));
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            _packetLog?.Log(LogTag, PacketDirection.Send, packet.Opcode, packet.Payload.Span);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Tear down the socket and reset crypto/state so ConnectAsync can run again.</summary>
    public async Task DisconnectForReconnectAsync()
    {
        // Abort first so hung I/O cannot wedge Dispose forever.
        AbortSocket("disconnect");

        var readLoop = _readLoop;
        _readLoop = null;
        if (readLoop is not null)
        {
            try
            {
                await Task.WhenAny(readLoop, Task.Delay(750)).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        try { _readCts?.Dispose(); } catch { /* ignore */ }
        _readCts = null;
        _authChallengeTcs = null;
        _authResponseTcs = null;
        _charEnumTcs = null;
        _enterWorldTcs = null;
        try { _stateMachine.Reset(); } catch { /* ignore */ }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectForReconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private void EnsureTransitionPathToWorldConnecting()
    {
        if (_stateMachine.State == SessionState.Failed)
        {
            _stateMachine.Reset();
        }

        if (_stateMachine.State == SessionState.Disconnected)
        {
            _stateMachine.TransitionTo(SessionState.Authenticating);
            _stateMachine.TransitionTo(SessionState.RealmList);
            _stateMachine.TransitionTo(SessionState.WorldConnecting);
            return;
        }

        if (_stateMachine.State == SessionState.RealmList)
        {
            _stateMachine.TransitionTo(SessionState.WorldConnecting);
            return;
        }

        throw new InvalidOperationException($"Cannot connect to world from state {_stateMachine.State}.");
    }

    private void EnsureState(SessionState expected)
    {
        if (_stateMachine.State != expected)
        {
            throw new InvalidOperationException($"Expected state {expected}, current is {_stateMachine.State}.");
        }
    }

    private async Task SendAuthSessionAsync(Packet challengePacket, CancellationToken cancellationToken)
    {
        if (_sessionKey is null || _connectedRealm is null)
        {
            throw new InvalidOperationException("Session key or realm is missing.");
        }

        var payload = challengePacket.Payload.Span;
        if (payload.Length < 8)
        {
            throw new InvalidDataException("SMSG_AUTH_CHALLENGE payload too short.");
        }

        // SMSG_AUTH_CHALLENGE: uint32 unk, uint32 seed, uint32 seeds[8] (dos material).
        // Ascension SendAuthSession digests the seed (challenge object +0) and writes
        // dosResponse from challenge+8/+0xC — use seeds[0]|seeds[1] as the closest
        // payload-aligned uint64 when the full 40-byte challenge is present.
        // SMSG_AUTH_CHALLENGE: unk(u32), seed(u32), seeds[8]. Live Ascension uses dosResponse=unk (=1).
        var unk = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        ulong dosResponse = unk;

        var clientSeed = (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
        var account = _credentials.GetCredentials().Account;

        var authPacket = WorldAuthSessionBuilder.Build(
            _connectedRealm,
            _options.ClientBuild > 0 ? _options.ClientBuild : (int)WorldAuthSessionBuilder.AscensionWireBuild,
            account,
            serverSeed,
            clientSeed,
            _sessionKey,
            _authProofTail,
            loginServerId: 0,
            loginServerType: 0,
            regionId: 0,
            battlegroupId: 0,
            dosResponse: dosResponse);

        var frame = WorldPacketFramer.BuildClientPacket(authPacket);
        var stream = _stream ?? throw new InvalidOperationException("World client is not connected.");
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            _packetLog?.Log(LogTag, PacketDirection.Send, authPacket.Opcode, authPacket.Payload.Span);
            // Ascension sends several large PLAINTEXT SMSG (0x058D/0x0699/…) then SMSG_AUTH_RESPONSE
            // (0x01EE). ARC4 is NOT enabled here — stock WotLK Init-after-AUTH_SESSION decrypts the
            // first plaintext header into a garbage size and hangs the read loop.
            // Crypt enable is Ascension-specific (SMSG 0x50D / CMSG 0x510 at NetClient+0x538).
        }
        finally
        {
            _sendLock.Release();
        }
    }
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("Stream missing.");
            while (!cancellationToken.IsCancellationRequested)
            {
                // Ascension/WotLK server header: 4 bytes normally, 5 bytes when size > 0x7FFF
                // (high bit on first size byte). Mis-reading large headers desyncs the stream.
                var headerBuf = new byte[5];
                await ReadExactIntoAsync(stream, headerBuf.AsMemory(0, 4), cancellationToken)
                    .ConfigureAwait(false);
                var headerLen = 4;
                if (_crypt.IsInitialized)
                {
                    _crypt.DecryptRecvHeader(headerBuf.AsSpan(0, 4));
                }

                if (WorldPacketFramer.IsLargeServerHeaderPrefix(headerBuf.AsSpan(0, 4)))
                {
                    await ReadExactIntoAsync(stream, headerBuf.AsMemory(4, 1), cancellationToken)
                        .ConfigureAwait(false);
                    if (_crypt.IsInitialized)
                    {
                        _crypt.DecryptRecvHeader(headerBuf.AsSpan(4, 1));
                    }

                    headerLen = 5;
                }

                var (size, _, _) = WorldPacketFramer.ParseServerHeader(headerBuf.AsSpan(0, headerLen));
                if (size < 2 || size > WorldPacketFramer.MaxServerPacketSize)
                {
                    throw new InvalidDataException(
                        $"World frame size {size} is out of range (hdr={Convert.ToHexString(headerBuf.AsSpan(0, headerLen))}, crypt={_crypt.IsInitialized}).");
                }

                var payloadLength = WorldPacketFramer.PayloadLengthFromServerSize(size);
                var payload = payloadLength == 0
                    ? Array.Empty<byte>()
                    : await ReadExactAsync(stream, payloadLength, cancellationToken).ConfigureAwait(false);
                var packet = WorldPacketFramer.ParseServerPacket(headerBuf.AsSpan(0, headerLen), payload);
                _packetLog?.Log(LogTag, PacketDirection.Recv, packet.Opcode, packet.Payload.Span);

                try
                {
                    HandlePacket(packet);
                    PacketReceived?.Invoke(packet);
                }
                catch (Exception ex)
                {
                    // Never let a projector/logger fault tear down the socket.
                    Console.WriteLine($"[{LogTag}] packet handler error opcode=0x{packet.Opcode:X4}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { Disconnected?.Invoke(); } catch { /* never throw from event */ }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{LogTag}] world read loop ended: {ex.GetType().Name}: {ex.Message}");
            _authResponseTcs?.TrySetException(new IOException("World read loop terminated unexpectedly.", ex));
            _charEnumTcs?.TrySetException(new IOException("World read loop terminated unexpectedly.", ex));
            _enterWorldTcs?.TrySetException(new IOException("World read loop terminated unexpectedly.", ex));
            try
            {
                if (_stateMachine.State is not (SessionState.Failed or SessionState.Disconnected))
                {
                    _stateMachine.TransitionTo(SessionState.Failed);
                }
            }
            catch
            {
            }

            try { Disconnected?.Invoke(); } catch { /* never throw from event */ }
        }
        finally
        {
            // Always wake StayInWorld / watchdog waiters even on clean cancel.
            try { Disconnected?.Invoke(); } catch { /* ignore */ }
        }
    }

    private void HandlePacket(Packet packet)
    {
        switch (packet.Opcode)
        {
            case Opcodes.SmsgAuthChallenge:
                _authChallengeTcs?.TrySetResult(packet);
                break;
            case Opcodes.SmsgAuthResponse:
                HandleAuthResponse(packet);
                break;
            case Opcodes.SmsgCharEnum:
                HandleCharEnum(packet);
                break;
            case Opcodes.SmsgLoginVerifyWorld:
            case Opcodes.SmsgNewWorld:
            case Opcodes.SmsgAscensionEnterWorldAck:
                _enterWorldTcs?.TrySetResult(true);
                break;
            case Opcodes.SmsgAscensionCryptEnable:
                _ = HandleAscensionCryptEnableAsync(packet);
                break;
            case Opcodes.SmsgTimeSyncReq:
                HandleTimeSyncRequest(packet);
                break;
        }
    }

    private async Task HandleAscensionCryptEnableAsync(Packet packet)
    {
        // Client RVA 0x233020: read u32 seed, send CMSG 0x510, set NetClient+0x538=1.
        if (packet.Payload.Length < 4 || _sessionKey is null)
        {
            return;
        }

        var seed = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Span);
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, seed);
        try
        {
            await SendAsync(new Packet(Opcodes.CmsgAscensionCryptAck, body), CancellationToken.None)
                .ConfigureAwait(false);
            if (!_crypt.IsInitialized)
            {
                _crypt.Init(_sessionKey);
                Console.WriteLine("[world] Ascension crypt enabled after SMSG 0x50F");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[world] crypt enable failed: {ex.Message}");
        }
    }

    private void HandleTimeSyncRequest(Packet packet)
    {
        if (packet.Payload.Length < 4)
        {
            return;
        }

        var counter = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Span);
        var response = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(0, 4), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4, 4), (uint)Environment.TickCount);
        _ = SendAsync(new Packet(Opcodes.CmsgTimeSyncResp, response), CancellationToken.None);
    }

    private void HandleAuthResponse(Packet packet)
    {
        if (packet.Payload.Length < 1)
        {
            _authResponseTcs?.TrySetResult(false);
            return;
        }

        var code = packet.Payload.Span[0];
        _authResponseTcs?.TrySetResult(code is 0x0C or 0x00);
    }

    private void HandleCharEnum(Packet packet)
    {
        try
        {
            var characters = ParseCharEnum(packet.Payload.Span);
            _charEnumTcs?.TrySetResult(characters);
        }
        catch (Exception ex)
        {
            _charEnumTcs?.TrySetException(ex);
        }
    }

    private static IReadOnlyList<CharacterInfo> ParseCharEnum(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            return Array.Empty<CharacterInfo>();
        }

        var count = payload[0];
        var offset = 1;
        var list = new List<CharacterInfo>(count);
        for (var i = 0; i < count; i++)
        {
            if (payload.Length < offset + 8)
            {
                throw new InvalidDataException("SMSG_CHAR_ENUM truncated guid.");
            }

            var guid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset, 8));
            offset += 8;
            var name = ReadCString(payload, ref offset);
            if (payload.Length < offset + 1 + 1 + 1 + 5 + 1 + 4 + 4 + 12)
            {
                throw new InvalidDataException("SMSG_CHAR_ENUM truncated character body.");
            }

            var race = payload[offset++];
            var @class = payload[offset++];
            var gender = payload[offset++];
            offset += 5;
            var level = payload[offset++];
            var zone = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var map = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var x = BitConverter.ToSingle(payload.Slice(offset, 4));
            offset += 4;
            var y = BitConverter.ToSingle(payload.Slice(offset, 4));
            offset += 4;
            var z = BitConverter.ToSingle(payload.Slice(offset, 4));
            offset += 4;
            if (payload.Length < offset + 4 + 4 + 4 + 1 + 4 + 4 + 4)
            {
                throw new InvalidDataException("SMSG_CHAR_ENUM truncated character footer.");
            }

            offset += 4;
            offset += 4;
            offset += 4;
            offset += 1;
            offset += 4 + 4 + 4;
            for (var slot = 0; slot < 23; slot++)
            {
                if (payload.Length < offset + 4 + 1 + 4)
                {
                    throw new InvalidDataException("SMSG_CHAR_ENUM truncated equipment.");
                }

                offset += 4 + 1 + 4;
            }

            list.Add(new CharacterInfo(guid, name, race, @class, gender, level, zone, map, x, y, z));
        }

        return list;
    }

    private async Task DisposeSocketAsync()
    {
        AbortSocket("dispose");
        var readLoop = _readLoop;
        _readLoop = null;
        if (readLoop is not null)
        {
            try { await Task.WhenAny(readLoop, Task.Delay(500)).ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        try { _readCts?.Dispose(); } catch { /* ignore */ }
        _readCts = null;
    }

    private static (string Host, int Port) ParseAddress(string address)
    {
        var parts = address.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
        {
            throw new InvalidOperationException($"Realm address '{address}' must be host:port.");
        }

        return (parts[0], port);
    }

    private static string ReadCString(ReadOnlySpan<byte> data, ref int offset)
    {
        var start = offset;
        while (offset < data.Length && data[offset] != 0)
        {
            offset++;
        }

        if (offset >= data.Length)
        {
            throw new InvalidDataException("Unterminated CString.");
        }

        var value = Encoding.UTF8.GetString(data.Slice(start, offset - start));
        offset++;
        return value;
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.ASCII.GetBytes(value));
        writer.Write((byte)0);
    }

    private static async Task ReadExactIntoAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("World server closed the connection.");
            }

            read += n;
        }
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await ReadExactIntoAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }
}
