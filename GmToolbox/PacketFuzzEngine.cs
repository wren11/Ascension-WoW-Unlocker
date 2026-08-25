using System.Collections.Concurrent;

namespace AscensionNetTool;

sealed class PacketFuzzEngine : IDisposable
{
    readonly ConcurrentDictionary<string, byte> _blacklist = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<FuzzHit> _hits = new();

    CancellationTokenSource? _cts;
    Task? _task;
    ManualResetEventSlim _pause = new(true);
    long _sent;

    public PacketFuzzSettings Settings { get; set; } = new();
    public byte[] Seed { get; private set; } = Array.Empty<byte>();
    public uint Opcode { get; private set; }
    public List<DetectedField> Fields { get; private set; } = new();
    public OmValuePool Pool { get; } = new();
    public byte[]? LoginPacket { get; set; }

    public long NextIndex { get; private set; }
    public long Sent => Interlocked.Read(ref _sent);
    public long Interesting { get; private set; }
    public long Crashes { get; private set; }
    public long NoResponse { get; private set; }
    public long Blacklisted => _blacklist.Count;
    public double PacketsPerSec { get; private set; }
    public string Phase { get; private set; } = "idle";
    public string LastMessage { get; private set; } = "";
    public bool IsRunning => _task is { IsCompleted: false };
    public bool IsPaused => !_pause.IsSet;

    public event Action? StatsChanged;
    public event Action<string>? LogLine;
    public event Action<FuzzHit>? HitFound;

    readonly Func<ProxyClient> _proxy;
    readonly Func<Task> _recoverAsync;
    readonly Func<IReadOnlyList<CapturedPacket>> _drain;
    readonly Action _ensureSniff;

    public PacketFuzzEngine(
        Func<ProxyClient> proxy,
        Func<IReadOnlyList<CapturedPacket>> drain,
        Action ensureSniff,
        Func<Task> recoverAsync)
    {
        _proxy = proxy;
        _drain = drain;
        _ensureSniff = ensureSniff;
        _recoverAsync = recoverAsync;
    }

    public void LoadSeed(byte[] seed, uint opcode)
    {
        // Allow opcode-only seeds (2 or 4 byte framing)
        if (seed.Length == 0)
        {
            seed = OpcodeBruteBuilder.Frame(opcode, Array.Empty<byte>(), u32: false);
        }
        Seed = (byte[])seed.Clone();
        Opcode = Opcodes.Normalize(opcode);
        Fields = PacketStructure.Detect(Seed, Opcode);
    }

    public void RefreshPool(IReadOnlyList<CapturedPacket>? recent = null)
    {
        try
        {
            Pool.Refresh(_proxy(), recent, Settings.ScanOtherInstances);
            Log($"OM pool: {Pool.Instances.Count} instance(s), {Pool.Atoms.Count} atoms, self={Pool.SelfGuid:X16}");
        }
        catch (Exception ex)
        {
            Log("OM refresh: " + ex.Message);
        }
    }

    public void ImportBlacklist(IEnumerable<string> fps)
    {
        foreach (string fp in fps)
            _blacklist[fp] = 1;
    }

    public PacketFuzzPersist Snapshot()
    {
        return new PacketFuzzPersist
        {
            SeedHex = string.Join(" ", Seed.Select(b => b.ToString("X2"))),
            Opcode = Opcode,
            NextIndex = NextIndex,
            Sent = Sent,
            Interesting = Interesting,
            Crashes = Crashes,
            Blacklisted = Blacklisted,
            LoginPacketHex = LoginPacket is null
                ? null
                : string.Join(" ", LoginPacket.Select(b => b.ToString("X2"))),
            Blacklist = _blacklist.Keys.ToList(),
            Settings = Settings,
        };
    }

    public void Start(long resumeIndex = 0)
    {
        if (IsRunning)
            return;
        if (Seed.Length == 0 && !Settings.OpcodeBruteMode)
            throw new InvalidOperationException("no seed packet");

        RefreshPool(_drain());
        NextIndex = resumeIndex;
        _cts = new CancellationTokenSource();
        _pause.Set();
        Phase = "running";
        _task = Task.Run(() => RunLoop(_cts.Token));
        Raise();
    }

    public void Pause()
    {
        _pause.Reset();
        Phase = "paused";
        Raise();
    }

    public void Resume()
    {
        _pause.Set();
        Phase = "running";
        Raise();
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _pause.Set();
        try { _task?.Wait(3000); } catch { }
        Phase = "stopped";
        Raise();
    }

    async Task RunLoop(CancellationToken ct)
    {
        var bl = new HashSet<string>(_blacklist.Keys, StringComparer.Ordinal);
        IEnumerable<FuzzVariant> stream = PacketFuzzMutator.Generate(
            Seed, Opcode, Fields, Settings, bl, Pool, NextIndex);

        int parallel = Math.Clamp(Settings.Parallel, 1, 8);
        int pps = Math.Clamp(Settings.PacketsPerSec, 1, 500);
        int delayMs = Math.Max(1, 1000 / Math.Max(1, pps / parallel));

        using var gate = new SemaphoreSlim(parallel, parallel);
        var workers = new List<Task>();
        long windowStart = Environment.TickCount64;
        long windowSent = 0;

        try
        {
            _ensureSniff();
            _ = _drain();

            foreach (var variant in stream)
            {
                if (ct.IsCancellationRequested) break;
                if (Sent >= Settings.MaxVariants) break;

                _pause.Wait(ct);
                await gate.WaitAsync(ct).ConfigureAwait(false);

                var v = variant;
                workers.Add(Task.Run(async () =>
                {
                    try { await SendOne(v, delayMs, ct).ConfigureAwait(false); }
                    finally { gate.Release(); }
                }, ct));

                workers.RemoveAll(t => t.IsCompleted);

                long now = Environment.TickCount64;
                if (now - windowStart >= 1000)
                {
                    PacketsPerSec = windowSent * 1000.0 / Math.Max(1, now - windowStart);
                    windowStart = now;
                    windowSent = 0;
                    // Refresh OM occasionally so ticks/pose stay live
                    if (Settings.RefreshDynamicFields)
                    {
                        try { Pool.Refresh(_proxy(), null, scanOtherInstances: false); }
                        catch { }
                    }
                    Raise();
                    PacketFuzzStore.Save(Snapshot());
                }
                Interlocked.Increment(ref windowSent);

                if (workers.Count > parallel * 4)
                {
                    await Task.WhenAny(workers).ConfigureAwait(false);
                    workers.RemoveAll(t => t.IsCompleted);
                }
            }

            await Task.WhenAll(workers.Where(t => !t.IsCompleted)).ConfigureAwait(false);
            Phase = "complete";
            LastMessage = $"done sent={Sent} hits={Interesting} crashes={Crashes}";
            PacketFuzzStore.Save(Snapshot());
            Raise();
        }
        catch (OperationCanceledException)
        {
            Phase = "stopped";
            Raise();
        }
        catch (Exception ex)
        {
            Phase = "error";
            LastMessage = ex.Message;
            Log($"engine error: {ex.Message}");
            Raise();
        }
    }

    async Task SendOne(FuzzVariant v, int paceMs, CancellationToken ct)
    {
        if (_blacklist.ContainsKey(v.Fingerprint))
            return;

        NextIndex = Math.Max(NextIndex, v.Index + 1);
        Phase = "send";

        byte[] wire = v.Packet;
        if (Settings.RefreshDynamicFields)
            wire = PacketForge.Legitimize(wire, Pool);

        bool ok;
        try
        {
            // ReplayViaNet → NetClient::Send (same path as real client; ARC4 on wire inside send)
            ok = _proxy().Replay(wire);
        }
        catch (Exception ex)
        {
            ok = false;
            Log($"send fail: {ex.Message}");
        }

        Interlocked.Increment(ref _sent);
        if (!ok)
        {
            await HandlePossibleCrash(v, ct).ConfigureAwait(false);
            return;
        }

        await Task.Delay(Math.Max(1, Settings.CorrelateMs), ct).ConfigureAwait(false);
        var responses = _drain()
            .Where(p => p.Dir == PktDir.In)
            .Take(32)
            .ToList();

        CaptureLoginFromDrain(_drain());

        if (responses.Count == 0)
        {
            NoResponse++;
            if (Settings.RetryNoResponse)
            {
                for (int r = 0; r < Settings.NoResponseRetries && !ct.IsCancellationRequested; r++)
                {
                    await Task.Delay(paceMs, ct).ConfigureAwait(false);
                    try { _proxy().Replay(wire); } catch { }
                    await Task.Delay(Settings.CorrelateMs, ct).ConfigureAwait(false);
                    responses = _drain().Where(p => p.Dir == PktDir.In).Take(32).ToList();
                    if (responses.Count > 0) break;
                }
            }
        }

        if (!ProxyAlive())
        {
            await HandlePossibleCrash(v, ct).ConfigureAwait(false);
            return;
        }

        if (responses.Count > 0)
        {
            var hit = new FuzzHit
            {
                VariantIndex = v.Index,
                Strategy = v.Strategy,
                Description = v.Description,
                SentHex = string.Join(" ", wire.Select(b => b.ToString("X2"))),
                Responses = responses.Select(p =>
                    $"0x{p.Opcode:X4} {Opcodes.Name(p.Opcode)} ({p.Data.Length}b)").Distinct().Take(12).ToList(),
            };
            Interesting++;
            _hits.Enqueue(hit);
            PacketFuzzStore.AppendHit(hit);
            HitFound?.Invoke(hit);
            Log($"HIT [{v.Strategy}] {v.Description} → {string.Join(", ", hit.Responses.Take(4))}");
        }

        await Task.Delay(paceMs, ct).ConfigureAwait(false);
        Raise();
    }

    void CaptureLoginFromDrain(IReadOnlyList<CapturedPacket> all)
    {
        foreach (var p in all)
        {
            if (p.Dir == PktDir.Out && p.Opcode == 0x003D && p.Data.Length >= 12)
                LoginPacket = (byte[])p.Data.Clone();
        }
    }

    bool ProxyAlive()
    {
        try
        {
            var p = _proxy();
            return p.Connected && p.Ping();
        }
        catch { return false; }
    }

    async Task HandlePossibleCrash(FuzzVariant v, CancellationToken ct)
    {
        await Task.Delay(400, ct).ConfigureAwait(false);
        if (ProxyAlive())
            return;

        Crashes++;
        _blacklist[v.Fingerprint] = 1;
        Phase = "crash";
        LastMessage = $"crash/dc on [{v.Strategy}] {v.Description}";
        Log($"BLACKLIST crash variant {v.Fingerprint} — {v.Description}");
        PacketFuzzStore.Save(Snapshot());
        Raise();

        if (!Settings.AutoRecover)
        {
            Phase = "stopped";
            try { _cts?.Cancel(); } catch { }
            return;
        }

        Phase = "recover";
        Log("recovering game (relaunch + wait proxy + login)…");
        Raise();
        try
        {
            await _recoverAsync().ConfigureAwait(false);
            if (LoginPacket is { Length: > 0 })
            {
                await Task.Delay(4000, ct).ConfigureAwait(false);
                try
                {
                    _ensureSniff();
                    _proxy().Replay(LoginPacket);
                    Log("replayed CMSG_PLAYER_LOGIN");
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log("login replay: " + ex.Message);
                }
            }
            else
            {
                Log("no saved login packet — waiting for world enter");
                await WaitForWorld(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            }
            _ensureSniff();
            _ = _drain();
            RefreshPool();
            Phase = "running";
            Log("recovery complete — continuing fuzz");
        }
        catch (Exception ex)
        {
            Phase = "error";
            LastMessage = "recover failed: " + ex.Message;
            Log(LastMessage);
            try { _cts?.Cancel(); } catch { }
        }
        Raise();
    }

    async Task WaitForWorld(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!ProxyAlive())
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }
            var pkts = _drain();
            CaptureLoginFromDrain(pkts);
            foreach (var p in pkts)
            {
                if (p.Dir == PktDir.In && (p.Opcode == 0x003E || p.Opcode == 0x0236 || p.Opcode == 0x01F1))
                {
                    Log($"world signal 0x{p.Opcode:X4}");
                    return;
                }
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    void Log(string m)
    {
        LastMessage = m;
        LogLine?.Invoke(m);
    }

    void Raise() => StatsChanged?.Invoke();

    public void Dispose()
    {
        Stop();
        _pause.Dispose();
        _cts?.Dispose();
    }
}
