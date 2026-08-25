using System.Collections.Concurrent;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.Session;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Monitoring;

namespace HeadlessClient.Infrastructure.Probe;

public sealed record ProbeHit(
    DateTimeOffset At,
    uint Cmsg,
    string CmsgName,
    string Variant,
    string Notes,
    string PayloadHex,
    int PayloadLen,
    IReadOnlyList<ProbeSmsg> Responses,
    bool Interesting,
    string Summary);

public sealed record ProbeSmsg(
    uint Opcode,
    string Name,
    int Len,
    string HexPreview,
    long OffsetMs,
    string HexFull = "",
    string WpeDump = "",
    IReadOnlyList<string>? AsciiStrings = null,
    string DecodedJson = "",
    bool Truncated = false);

public sealed class OpcodeProbeService
{
    private readonly ProbeDataPool _pool = new();
    private readonly ConcurrentQueue<ProbeHit> _hits = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly ConcurrentDictionary<uint, byte> _tested = new();
    private readonly ConcurrentDictionary<uint, int> _responseCounts = new();
    private readonly object _gate = new();
    private IWorldClient? _world;
    private InMemoryObjectDirectory? _objects;
    private string _character = "";
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private int _armed;
    private int _sent;
    private int _interesting;
    private string _phase = "idle";
    private string _lastError = "";

    // Ambient world traffic — exclude from "interesting" correlation.
    private static readonly HashSet<uint> NoiseSmsg = new()
    {
        0x00A9, 0x01F6, 0x0578, // UPDATE_OBJECT / ADDON
        0x00AA, // DESTROY_OBJECT
        0x00DD, // MONSTER_MOVE
        0x00B5, 0x00B6, 0x00B7, 0x00B8, 0x00B9, 0x00BA, 0x00BB, 0x00BC, 0x00BD, 0x00BE,
        0x00C9, 0x00C5, 0x00CD, 0x00DA, 0x00EE, // move family
        0x02B1, 0x02CA, 0x02FE, 0x037E, 0x0385, // water walk / flight / spline speed
        0x0130, 0x0131, 0x0132, 0x01F3, 0x024C, 0x024E, 0x0250, // spell/combat spam
        0x0144, // ATTACKSTOP
        0x047F, 0x0480, 0x0495, 0x0496, 0x0673, 0x0674, // health/power/aura
        0x0390, 0x01DD, // TIME_SYNC / PONG
        0x0096, 0x0099, // chat / channel notify (ambient)
        0x077C, // AREA_POI_PAYLOAD Ascension spam
        0x03AC, // DISMOUNT
    };

    private static readonly Dictionary<uint, uint[]> ExpectedResponses = new()
    {
        [0x0050] = [0x0051], // NAME_QUERY → RESPONSE
        [0x0052] = [0x0053], // PET_NAME_QUERY
        [0x0056] = [0x0058, 0x0932], // ITEM_QUERY (+ Ascension PATCH_ITEM)
        [0x005A] = [0x005B], // PAGE_TEXT
        [0x005C] = [0x005D], // QUEST_QUERY
        [0x005E] = [0x005F], // GAMEOBJECT_QUERY
        [0x0060] = [0x0061], // CREATURE_QUERY
        [0x0062] = [0x0063], // WHO
        [0x007C] = [0x007D], // NPC_TEXT
        [0x01CE] = [0x01CF], // PLAYED_TIME
        [0x01DC] = [0x01DD], // PING → PONG
        [0x01F1] = [0x01F2], // QUERY_TIME
        [0x020A] = [0x020C], // REQUEST_ACCOUNT_DATA → UPDATE_ACCOUNT_DATA
        [0x0284] = [0x0284], // NEXT_MAIL_TIME echo
        [0x046B] = [0x046C], // INSPECT_ACHIEVEMENTS
        [0x04FF] = [0x0209], // READY_FOR_ACCOUNT_DATA_TIMES → ACCOUNT_DATA_TIMES
    };

    private static readonly HashSet<string> BlacklistNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CMSG_CHAR_DELETE", "CMSG_CHAR_CREATE", "CMSG_CHAR_RENAME", "CMSG_CHAR_CUSTOMIZE",
        "CMSG_CHAR_FACTION_CHANGE", "CMSG_CHAR_RACE_CHANGE",
        "CMSG_LOGOUT_REQUEST", "CMSG_LOGOUT_CANCEL", "CMSG_PLAYER_LOGOUT",
        "CMSG_AUTH_SESSION", "CMSG_AUTH_SRP6_BEGIN", "CMSG_AUTH_SRP6_PROOF",
        "CMSG_REBOOT_ME", "CMSG_BOOTME",
        "CMSG_DESTROYITEM", "CMSG_SWAP_ITEM", "CMSG_AUTOSTORE_BAG_ITEM",
        "CMSG_SELL_ITEM", "CMSG_BUYITEM", "CMSG_BUYITEM_IN_SLOT",
        "CMSG_GUILD_DISBAND", "CMSG_GUILD_DEMOTE", "CMSG_GUILD_LEAVE",
        "CMSG_GUILD_LEADER", "CMSG_GUILD_MOTD", "CMSG_GUILD_INVITE",
        "CMSG_PETITION_BUY", "CMSG_OFFER_PETITION",
        "CMSG_MESSAGECHAT", "CMSG_GM_NUKE", "CMSG_GM_DESTROY_ONLINE_CORPSE",
        "CMSG_SERVERTIME", // rare but skip
        "CMSG_WORLD_TELEPORT", "CMSG_TELEPORT_TO_UNIT",
        "CMSG_MOVE_SET_RAW_POSITION",
        "CMSG_LEARN_SPELL", "CMSG_LEARN_TALENT", "CMSG_LEARN_PREVIEW_TALENTS",
        "CMSG_SET_FACTION_ATWAR", "CMSG_SET_FACTION_INACTIVE",
        "CMSG_DUEL_ACCEPTED", "CMSG_DUEL_CANCELLED",
        "CMSG_RESURRECT_RESPONSE",
        "CMSG_COMPLETE_CINEMATIC", "CMSG_NEXT_CINEMATIC_CAMERA",
    };

    public ProbeDataPool Pool => _pool;
    public bool IsArmed => Volatile.Read(ref _armed) != 0;
    public bool IsRunning => _runTask is { IsCompleted: false };
    public string Phase => _phase;
    public int Sent => Volatile.Read(ref _sent);
    public int Interesting => Volatile.Read(ref _interesting);
    public string LastError => _lastError;
    public string Character => _character;

    public event Action? Changed;
    public event Action<ProbeHit>? Hit;

    public void Arm(bool on)
    {
        Interlocked.Exchange(ref _armed, on ? 1 : 0);
        Log(on ? "probe ARMED — will send CMSG probes" : "probe disarmed");
        Changed?.Invoke();
    }

    public void Attach(IWorldClient world, InMemoryObjectDirectory objects, string character, ulong selfGuid)
    {
        lock (_gate)
        {
            if (_world is not null)
            {
                _world.PacketReceived -= OnPacket;
            }

            _world = world;
            _objects = objects;
            _character = character ?? "";
            _world.PacketReceived += OnPacket;
            if (selfGuid != 0)
            {
                _pool.SetSelf(selfGuid, 0, 0, 0, 0);
            }
        }

        RefreshPool();
        Log($"probe attached as {_character} guid={selfGuid:X16}");
        Changed?.Invoke();
    }

    public void Detach(IWorldClient world)
    {
        Stop();
        lock (_gate)
        {
            if (_world == world)
            {
                _world.PacketReceived -= OnPacket;
                _world = null;
            }
        }

        Changed?.Invoke();
    }

    public void RefreshPool()
    {
        var objs = _objects?.Snapshot() ?? Array.Empty<WorldObject>();
        _pool.IngestObjects(objs);
        Log($"OM refresh: objects={objs.Count} guids={_pool.Guids().Count} entries={_pool.Entries().Count} self={_pool.SelfGuid:X16}");
        Changed?.Invoke();
    }

    public object GetStatus() => new
    {
        ok = true,
        armed = IsArmed,
        running = IsRunning,
        phase = _phase,
        character = _character,
        sent = Sent,
        interesting = Interesting,
        testedOpcodes = _tested.Count,
        hits = _hits.Count,
        selfGuid = _pool.SelfGuid.ToString("X16"),
        guidAtoms = _pool.Guids().Count,
        entryAtoms = _pool.Entries().Count,
        lastError = _lastError,
        catalogSize = WotlkCmsgCatalog.All.Length
    };

    public IReadOnlyList<ProbeHit> RecentHits(int max = 200)
    {
        var all = _hits.ToArray();
        if (all.Length <= max) return all;
        return all.AsSpan(all.Length - max).ToArray();
    }

    public IReadOnlyList<string> RecentLog(int max = 100)
    {
        var all = _log.ToArray();
        if (all.Length <= max) return all;
        return all.AsSpan(all.Length - max).ToArray();
    }

    public IReadOnlyList<object> ResponseSummary() =>
        _responseCounts
            .OrderByDescending(kv => kv.Value)
            .Take(80)
            .Select(kv => (object)new
            {
                opcode = kv.Key,
                name = NameOf(kv.Key),
                count = kv.Value
            })
            .ToList();

    public void StartSweep(bool knownOnly, int maxOpcodes, int delayMs)
    {
        if (!IsArmed)
        {
            throw new InvalidOperationException("Arm the opcode probe first.");
        }

        if (IsRunning)
        {
            throw new InvalidOperationException("Probe already running.");
        }

        RefreshPool();
        _runCts = new CancellationTokenSource();
        _phase = knownOnly ? "known-sweep" : "full-sweep";
        _runTask = Task.Run(() => RunSweepAsync(knownOnly, maxOpcodes, delayMs, _runCts.Token));
        Changed?.Invoke();
    }

    public async Task ProbeOneAsync(uint opcode, bool includeGeneric, CancellationToken ct)
    {
        if (!IsArmed) throw new InvalidOperationException("Arm the opcode probe first.");
        var name = NameOf(opcode);
        if (IsBlacklisted(name))
        {
            throw new InvalidOperationException($"Blacklisted: {name}");
        }

        RefreshPool();
        var variants = ProbePayloadFactory.BuildVariants(opcode, name, _pool, includeGeneric);
        foreach (var v in variants)
        {
            ct.ThrowIfCancellationRequested();
            await SendAndObserveAsync(opcode, name, v, ct).ConfigureAwait(false);
            await Task.Delay(120, ct).ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        try { _runCts?.Cancel(); } catch { /* ignore */ }
        _phase = "idle";
        Changed?.Invoke();
    }

    private async Task RunSweepAsync(bool knownOnly, int maxOpcodes, int delayMs, CancellationToken ct)
    {
        try
        {
            delayMs = Math.Clamp(delayMs, 50, 2000);
            maxOpcodes = Math.Clamp(maxOpcodes, 1, knownOnly ? 200 : 400);

            var targets = WotlkCmsgCatalog.All
                .Where(t => !IsBlacklisted(t.Name))
                .Where(t => !knownOnly || IsKnownPriority(t.Name))
                .Where(t => !_tested.ContainsKey(t.Opcode))
                .Take(maxOpcodes)
                .ToList();

            Log($"sweep start knownOnly={knownOnly} count={targets.Count} delay={delayMs}ms");
            foreach (var (opcode, name) in targets)
            {
                ct.ThrowIfCancellationRequested();
                var world = RequireWorld();
                if (world.State != SessionState.InWorld)
                {
                    Log("not InWorld — stopping sweep");
                    break;
                }

                RefreshPool();
                var variants = ProbePayloadFactory.BuildVariants(opcode, name, _pool, includeGeneric: !knownOnly);
                // For full sweep keep 1–2 variants to limit load; known gets all structured ones.
                if (!knownOnly)
                {
                    variants = variants.Take(2).ToList();
                }

                foreach (var v in variants)
                {
                    ct.ThrowIfCancellationRequested();
                    await SendAndObserveAsync(opcode, name, v, ct).ConfigureAwait(false);
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }

                _tested[opcode] = 1;
            }

            _phase = "done";
            Log($"sweep complete sent={Sent} interesting={Interesting}");
        }
        catch (OperationCanceledException)
        {
            _phase = "stopped";
            Log("sweep cancelled");
        }
        catch (Exception ex)
        {
            _phase = "error";
            _lastError = ex.Message;
            Log("sweep error: " + ex.Message);
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    private async Task SendAndObserveAsync(uint opcode, string name, ProbeVariant variant, CancellationToken ct)
    {
        var world = RequireWorld();
        var before = DateTimeOffset.UtcNow;
        var inbox = new ConcurrentQueue<Packet>();
        void Grab(Packet p) => inbox.Enqueue(p);
        world.PacketReceived += Grab;
        try
        {
            await world.SendAsync(new Packet(opcode, variant.Payload), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _sent);
            // Observe window
            await Task.Delay(350, ct).ConfigureAwait(false);
        }
        finally
        {
            world.PacketReceived -= Grab;
        }

        var responses = new List<ProbeSmsg>();
        while (inbox.TryDequeue(out var pkt))
        {
            var offset = (long)(DateTimeOffset.UtcNow - before).TotalMilliseconds;
            responses.Add(ProbePacketFormatter.Build(pkt.Opcode, NameOf(pkt.Opcode), pkt.Payload.Span, offset));
            _responseCounts.AddOrUpdate(pkt.Opcode, 1, (_, n) => n + 1);
        }

        bool interesting;
        string summary;
        if (ExpectedResponses.TryGetValue(opcode, out var expected))
        {
            var matched = responses.Where(r => expected.Contains(r.Opcode)).ToList();
            interesting = matched.Count > 0;
            summary = interesting
                ? "MATCH " + string.Join(", ", matched.Select(r => $"{r.Name}(0x{r.Opcode:X4}) len={r.Len}"))
                : (responses.Count == 0 ? "no SMSG in window" : "no expected pair (ambient only)");
        }
        else
        {
            var rare = responses.Where(r => !NoiseSmsg.Contains(r.Opcode) && !IsMoveFamily(r.Opcode)).Take(6).ToList();
            interesting = rare.Count > 0;
            summary = interesting
                ? string.Join(", ", rare.Select(r => $"{r.Name}(0x{r.Opcode:X4}) len={r.Len}"))
                : (responses.Count == 0 ? "no SMSG in window" : $"noise-only ({responses.Count})");
        }

        if (interesting)
        {
            Interlocked.Increment(ref _interesting);
        }

        var hit = new ProbeHit(
            before,
            opcode,
            name,
            variant.Label,
            variant.Notes,
            Convert.ToHexString(variant.Payload),
            variant.Payload.Length,
            responses,
            interesting,
            summary);

        _hits.Enqueue(hit);
        while (_hits.Count > 500 && _hits.TryDequeue(out _))
        {
        }

        if (interesting)
        {
            Log($"HIT 0x{opcode:X4} {name} [{variant.Label}] → {summary}");
            Hit?.Invoke(hit);
        }

        Changed?.Invoke();
    }

    private void OnPacket(Packet packet)
    {
        _pool.NoteInboundPacket(packet.Opcode, packet.Payload.Span);
    }

    private IWorldClient RequireWorld()
    {
        lock (_gate)
        {
            if (_world is null || _world.State != SessionState.InWorld)
            {
                throw new InvalidOperationException("World not ready for probing.");
            }

            return _world;
        }
    }

    private void Log(string line)
    {
        var msg = $"{DateTimeOffset.Now:HH:mm:ss} {line}";
        _log.Enqueue(msg);
        while (_log.Count > 400 && _log.TryDequeue(out _))
        {
        }

        Console.WriteLine($"[probe] {line}");
    }

    private static bool IsMoveFamily(uint opcode) =>
        (opcode >= 0x00B5 && opcode <= 0x00DF)
        || (opcode >= 0x00E1 && opcode <= 0x00EE)
        || opcode is 0x02B1u or 0x02CAu or 0x02FEu or 0x037Eu or 0x0385u;

    private static bool IsBlacklisted(string name) =>
        BlacklistNames.Contains(name)
        || name.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
        || name.Contains("DESTROY", StringComparison.OrdinalIgnoreCase)
        || name.Contains("NUKE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CMSG_GM_", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownPriority(string name) =>
        name.Contains("QUERY", StringComparison.OrdinalIgnoreCase)
        || name is "CMSG_SET_SELECTION" or "CMSG_GOSSIP_HELLO" or "CMSG_LIST_INVENTORY"
            or "CMSG_TRAINER_LIST" or "CMSG_BANKER_ACTIVATE" or "CMSG_AUCTION_HELLO"
            or "CMSG_ZONEUPDATE" or "CMSG_PLAYED_TIME" or "CMSG_QUERY_TIME"
            or "CMSG_WHO" or "CMSG_PING" or "CMSG_REQUEST_ACCOUNT_DATA"
            or "CMSG_BATTLEFIELD_STATUS" or "CMSG_TAXINODE_STATUS_QUERY"
            or "CMSG_TAXIQUERYAVAILABLENODES" or "CMSG_NPC_TEXT_QUERY"
            or "CMSG_PAGE_TEXT_QUERY" or "CMSG_AREATRIGGER"
            or "CMSG_GAMEOBJ_USE" or "CMSG_LOOT" or "CMSG_QUERY_INSPECT_ACHIEVEMENTS"
            or "MSG_QUERY_NEXT_MAIL_TIME" or "CMSG_READY_FOR_ACCOUNT_DATA_TIMES";

    private static string NameOf(uint opcode) => WotlkCmsgCatalog.NameOf(opcode);
}
