using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AscensionNetTool;

/// <summary>Server-side session: instances, poll loop, packet capture, WS fan-out.</summary>
sealed class ToolSession : IDisposable
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    readonly BootstrapService _boot = new();
    readonly AddonService _addons = new();
    readonly SharedStateManager _shared = new();
    readonly InstanceManager _instances;
    readonly ChatLogService _chatLog = new();
    readonly List<CapturedPacket> _packets = new();
    byte[]? _lastPlayerLogin;
    byte[]? _lastCharEnum;
    readonly List<object> _charBotLog = new();
    CancellationTokenSource? _charBotCts;
    bool _charUnlockDefault = true;
    readonly object _pktGate = new();
    readonly List<WebSocket> _sockets = new();
    readonly object _wsGate = new();
    readonly StringBuilder _log = new();
    readonly object _logGate = new();
    readonly System.Threading.Timer _poll;
    readonly ConcurrentQueue<string> _logQueue = new();
    WatchdogService? _watchdog;
    PacketFuzzEngine? _fuzz;

    int? _activeId;
    bool _viewShared;
    bool _sniff;
    bool _busy;
    string _addonSummary = "addons: —";
    DateTime _lastAddonScan = DateTime.MinValue;
    DateTime _lastEntitlementPing = DateTime.MinValue;
    DateTime _lastSessionPing = DateTime.MinValue;
    readonly Dictionary<int, string> _addonAccessKeys = new();
    DateTime _lastStatus = DateTime.MinValue;
    DateTime _lastObjects = DateTime.MinValue;
    DateTime _nextReconnect = DateTime.MinValue;
    int _reconnectBackoffMs = 3000;
    DateTime _nextPlayerFlush = DateTime.MinValue;
    readonly ConcurrentDictionary<string, DateTime> _nameQueryCooldown = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<(ProxyClient Proxy, string Guid)> _pendingNameQueries = new();
    readonly ConcurrentDictionary<string, byte> _recentLuaChat = new(StringComparer.Ordinal);
    int _pollGate;
    const int MaxPackets = 800;
    const int MaxPacketsHeld = 4000;
    const int GuiHoldMs = 10_000;
    const int MaxLogChars = 120_000;
    const int MaxWsPacketBurst = 40;
    readonly object _guiGate = new();
    DateTime _guiTouchedUtc = DateTime.MinValue;

    public ToolSession()
    {
        _instances = new InstanceManager(_boot, _shared);
        _boot.Progress += m => Log(m);
        _addons.Progress += m => Log(m);
        _instances.Changed += () =>
        {
            Broadcast("instances", BuildInstancesDto());
            Broadcast("status", BuildStatusDto());
        };
        EventBus.Subscribe<SharedUpdatedEvent>(e =>
            Broadcast("shared", new { e.ObjectCount, e.InstanceCount }));
        _poll = new System.Threading.Timer(_ => SafePoll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        Log("GMToolBox Pro web host online");
        Log("App root: " + Paths.AppRoot);
        if (SettingsStore.NeedsSetup())
            Log("FIRST RUN — configure Ascension.exe, Maps (.mmap), MMAPS (.mmtile) via Paths wizard");
        else
            Log("Paths OK — " + SettingsStore.Current.LiveDir);
        try
        {
            var scan = ClientOffsetService.EnsureFresh(Log);
            if (!scan.Skipped)
                Log("Client fingerprint: " + scan.Summary);
        }
        catch (Exception ex)
        {
            Log("Offset scan deferred: " + ex.Message);
        }
        OpcodeFilterStore.LoadOrSeed();
        PacketSchemaRegistry.EnsureSeeded();
        Log($"opcode ignore: {OpcodeFilterStore.Current.IgnoredOpcodes.Count} (SMSG_PATCH* defaults); chat DB → {_chatLog.DbPath}");
        _watchdog = new WatchdogService(
            _instances,
            Log,
            _ => _lastPlayerLogin,
            _ => _lastCharEnum);
        _fuzz = new PacketFuzzEngine(
            () => ResolveProxy()?.Proxy ?? throw new InvalidOperationException("no proxy"),
            () => { lock (_pktGate) return _packets.ToList(); },
            () => { if (!_sniff) SetSniff(true); },
            async () =>
            {
                var id = _activeId ?? _instances.All.FirstOrDefault()?.Id ?? 1;
                if (_watchdog is not null)
                    await _watchdog.RecoverNowAsync(id).ConfigureAwait(false);
            });
        if (SettingsStore.Current.WatchdogEnabled)
            Log("watchdog ON — auto-relaunch/relog/restorebots");
        _poll.Change(0, Math.Clamp(SettingsStore.Current.PollMs, 200, 5000));
        Broadcast("log", new { line = "ready" });
        _ = ApplyAccountEntitlementsAsync();
    }

    Task ApplyAccountEntitlementsAsync()
    {
        PushProxyEntitlements(loggedIn: true, GmtLimits.MaxInstances, Array.Empty<string>());
        return Task.CompletedTask;
    }

    static bool HasRealCharacter(GameInstance inst) =>
        inst.PlayerGuid != 0
        && CharEnumParser.IsPlayable(inst.LockedPlayerName ?? inst.PlayerName);

    public void Dispose()
    {
        _poll.Dispose();
        _fuzz?.Dispose();
        _watchdog?.Dispose();
        _chatLog.Dispose();
        _instances.Dispose();
        lock (_wsGate)
        {
            foreach (var ws in _sockets.ToArray())
            {
                try { ws.Abort(); } catch { }
            }
            _sockets.Clear();
        }
    }

    public ChatLogService ChatLog => _chatLog;
    public WatchdogService? Watchdog => _watchdog;
    public PacketFuzzEngine? Fuzz => _fuzz;

    public async Task AcceptWebSocketAsync(WebSocket ws, CancellationToken ct)
    {
        lock (_wsGate) _sockets.Add(ws);
        try
        {
            await SendAsync(ws, "hello", new { version = "2.0.0" });
            await SendAsync(ws, "status", BuildStatusDto());
            await SendAsync(ws, "instances", BuildInstancesDto());
            await SendAsync(ws, "settings", SettingsStore.Current);
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var buf = new byte[4096];
                var result = await ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
        finally
        {
            lock (_wsGate) _sockets.Remove(ws);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    void Broadcast(string type, object payload)
    {
        WebSocket[] copy;
        lock (_wsGate) copy = _sockets.ToArray();
        if (copy.Length == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var ws in copy)
            {
                try { await SendAsync(ws, type, payload); }
                catch { }
            }
        });
    }

    static async Task SendAsync(WebSocket ws, string type, object payload)
    {
        if (ws.State != WebSocketState.Open) return;
        var msg = JsonSerializer.Serialize(new { type, payload }, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(msg);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public void Log(string line)
    {
        string stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        lock (_logGate)
        {
            _log.AppendLine(stamped);
            if (_log.Length > MaxLogChars)
                _log.Remove(0, _log.Length - MaxLogChars / 2);
        }
        Broadcast("log", new { line = stamped });
    }

    public string GetLog()
    {
        lock (_logGate) return _log.ToString();
    }

    void SafePoll()
    {
        if (Interlocked.CompareExchange(ref _pollGate, 1, 0) != 0)
            return; /* previous poll still running — never overlap */
        try { Poll(); }
        catch (Exception ex) { Log("poll: " + ex.Message); }
        finally { Interlocked.Exchange(ref _pollGate, 0); }
    }

    void Poll()
    {
        EnsureConnections();
        SyncSharedWorld();
        /* Lua chat first so wire-path dedupe can suppress duplicates. */
        DrainLuaChatReports();
        DrainPackets();
        DrainPendingNameQueries();
        var now = DateTime.UtcNow;
        if ((now - _lastObjects).TotalMilliseconds >= SettingsStore.Current.ObjectsIntervalMs)
        {
            _lastObjects = now;
            Broadcast("objects", GetObjectsDto());
        }
        if ((now - _lastAddonScan).TotalMilliseconds >= 5000)
        {
            _lastAddonScan = now;
            RefreshAddons(false);
        }
        if ((now - _lastSessionPing).TotalSeconds >= 5)
            _lastSessionPing = now;
        if ((now - _lastStatus).TotalMilliseconds >= SettingsStore.Current.StatusIntervalMs)
        {
            _lastStatus = now;
            Broadcast("status", BuildStatusDto());
        }
        KickLogTail();
    }

    void EnsureConnections()
    {
        if (DateTime.UtcNow < _nextReconnect) return;
        bool any = false;
        foreach (var inst in _instances.All)
        {
            if (inst.Pid == 0) continue;
            if (!inst.Proxy.Connected)
            {
                if (inst.Proxy.TryConnectToPid(inst.Pid))
                {
                    inst.Connected = true;
                    inst.Ring.TryOpen(inst.Pid);
                    inst.ChatReports.TryOpen(inst.Pid);
                    if (_sniff) inst.Proxy.SetSniff(true);
                    SyncFiltersToProxy(inst.Proxy);
                    PushProxyEntitlementsTo(inst.Proxy);
                    if (_charUnlockDefault)
                    {
                        try
                        {
                            inst.Proxy.RunLua(CharCreateLab.UnlockScript(true));
                            Log($"charcreate: unlock default ON inst{inst.Id}");
                        }
                        catch { /* Lua may not be ready until glue UI */ }
                    }
                    Log($"proxy connected inst{inst.Id} pid={inst.Pid}");
                    any = true;
                }
            }
            else any = true;
        }
        if (any) _reconnectBackoffMs = 3000;
        else
        {
            _nextReconnect = DateTime.UtcNow.AddMilliseconds(_reconnectBackoffMs);
            _reconnectBackoffMs = Math.Min(30000, _reconnectBackoffMs + 2000);
        }
    }

    void SyncSharedWorld()
    {
        var connected = _instances.All.Where(i => i.Connected).ToList();
        if (connected.Count == 0) return;
        int nameBudget = 8; /* cap pipe/Lua resolve spam in crowded cities */
        foreach (var inst in connected)
        {
            try
            {
                var snap = inst.Proxy.GetObjects();
                _shared.Publish(inst.Id, snap.Header, snap.Units);
                bool flushPlayers = DateTime.UtcNow >= _nextPlayerFlush;
                if (flushPlayers) _nextPlayerFlush = DateTime.UtcNow.AddSeconds(2);
                foreach (var u in snap.Units)
                {
                    if ((u.TypeMask & ObjTypeMask.Player) == 0) continue;
                    PlayerDirectory.ObserveUnit(
                        u.Guid, (int)u.Level, (int)u.Faction,
                        (int)u.Health, (int)u.MaxHealth,
                        u.X, u.Y, u.Z, (int)u.TypeMask);
                    string g = PlayerDirectory.NormGuid(u.Guid);
                    if (PlayerDirectory.TryGet(g, out var info))
                    {
                        if (string.IsNullOrWhiteSpace(info.Name) && nameBudget > 0)
                        {
                            QueueNameQuery(inst.Proxy, g);
                            nameBudget--;
                        }
                        if (flushPlayers || !string.IsNullOrWhiteSpace(info.Name))
                            _chatLog.EnqueuePlayer(info);
                    }
                }
                inst.PlayerGuid = snap.Header.PlayerGuid;
                inst.PlayerX = snap.Header.PlayerX;
                inst.PlayerY = snap.Header.PlayerY;
                inst.PlayerZ = snap.Header.PlayerZ;
                _watchdog?.Observe(inst, snap.Header.PlayerGuid != 0);
                if (snap.Header.PlayerGuid != 0)
                {
                    if (string.IsNullOrWhiteSpace(inst.LockedPlayerName))
                    {
                        var fromEnum = NameFromCharEnum(inst, snap.Header.PlayerGuid);
                        if (CharEnumParser.IsPlayable(fromEnum))
                            LockPlayerIdentity(inst, fromEnum, snap.Header.PlayerGuid, "char-enum");
                    }
                    if (!string.IsNullOrWhiteSpace(inst.LockedPlayerName))
                        inst.PlayerName = inst.LockedPlayerName;
                    else if (string.IsNullOrWhiteSpace(inst.PlayerName) || inst.PlayerName.StartsWith("I"))
                        inst.PlayerName = $"I{inst.Id}-{snap.Header.PlayerGuid:X4}";

                    string live = inst.LockedPlayerName ?? inst.PlayerName ?? "";
                    string key = PlayerDirectory.NormGuid(snap.Header.PlayerGuid) + "|" + live;
                    bool placeholder = !CharEnumParser.IsPlayable(live);
                    if (!string.Equals(inst.LastEntitlementKey, key, StringComparison.Ordinal)
                        && !placeholder)
                    {
                        inst.LastEntitlementKey = key;
                        _ = SyncCharacterEntitlementsAsync(
                            PlayerDirectory.NormGuid(snap.Header.PlayerGuid),
                            live);
                    }
                }
            }
            catch { }
        }
        try
        {
            var body = _shared.SerializeBody();
            int total = Math.Max(1, SettingsStore.Current.InstanceCount);
            foreach (var inst in connected)
            {
                var blob = _shared.WrapForClient(body, inst.Id, total, inst.Pid);
                inst.Proxy.SubscribeShared(blob);
            }
        }
        catch { }
    }

    void DrainPackets()
    {
        var ignore = OpcodeFilterStore.IgnoredSet;
        bool logStrings = SettingsStore.Current.LogPacketStrings;
        bool broadcastHex = SettingsStore.Current.BroadcastPacketHex;
        int wsBudget = MaxWsPacketBurst;
        foreach (var inst in _instances.All.Where(i => i.Connected))
        {
            try
            {
                foreach (var pkt in inst.Ring.DrainNew(inst.Id))
                {
                    bool isChat = ChatDecoder.AlwaysCaptureOpcodes.Contains(Opcodes.Normalize(pkt.Opcode));
                    if (!isChat && ignore.Contains(Opcodes.Normalize(pkt.Opcode)))
                        continue;

                    if (ChatDecoder.TryDecodeNameQuery(pkt, out var nq) && nq is not null)
                    {
                        var p = PlayerDirectory.ObserveName(
                            nq.Guid, nq.Name, nq.Realm, nq.Race, nq.Gender, nq.Class);
                        _chatLog.EnqueuePlayer(p);
                        Broadcast("player", new
                        {
                            guid = p.Guid,
                            name = p.Name,
                            realm = p.Realm,
                            race = p.Race,
                            gender = p.Gender,
                            classId = p.Class,
                            level = p.Level,
                        });
                    }

                    if (ChatDecoder.TryDecode(pkt, out var chat) && chat is not null)
                    {
                        string sender = chat.SenderName ?? "";
                        if (string.IsNullOrWhiteSpace(sender) && !string.IsNullOrEmpty(chat.SenderGuid))
                            sender = PlayerDirectory.ResolveName(chat.SenderGuid);

                        /* When Lua chat-capture is on it is authoritative — avoid wire+Lua doubles.
                           Still resolve unnamed senders from the wire path. */
                        bool luaAuthoritative = OpcodeFilterStore.ChatCapture;
                        if (!luaAuthoritative)
                        {
                            chat = new ChatDecoder.DecodedChat
                            {
                                Opcode = chat.Opcode,
                                Kind = chat.Kind,
                                ChatType = chat.ChatType,
                                ChatTypeName = chat.ChatTypeName,
                                Language = chat.Language,
                                SenderGuid = chat.SenderGuid,
                                SenderName = sender,
                                TargetGuid = chat.TargetGuid,
                                Channel = chat.Channel,
                                Message = chat.Message,
                                ChatTag = chat.ChatTag,
                                RawHex = chat.RawHex,
                                InstanceId = inst.Id,
                            };
                            string dedupeKey = $"{inst.Id}|{chat.SenderGuid}|{chat.Message}";
                            bool luaSaw = !string.IsNullOrWhiteSpace(chat.Message)
                                && _recentLuaChat.ContainsKey(dedupeKey);
                            if (!luaSaw
                                && (!string.IsNullOrWhiteSpace(chat.Message) || !string.IsNullOrWhiteSpace(chat.SenderName)))
                            {
                                string ts = DateTime.UtcNow.ToString("o");
                                _chatLog.Enqueue(chat, inst.Id);
                                Broadcast("chat", new
                                {
                                    id = DateTime.UtcNow.Ticks,
                                    ts,
                                    inst = inst.Id,
                                    kind = chat.Kind,
                                    type = chat.ChatTypeName,
                                    channel = chat.Channel,
                                    sender = chat.SenderName,
                                    senderGuid = chat.SenderGuid,
                                    message = chat.Message,
                                });
                            }
                        }
                        if (string.IsNullOrWhiteSpace(sender) && !string.IsNullOrEmpty(chat.SenderGuid))
                        {
                            QueueNameQuery(inst.Proxy, chat.SenderGuid);
                            QueueLuaGuidResolve(inst.Proxy, chat.SenderGuid);
                        }
                    }

                    if (logStrings)
                        LogPacketStrings(inst.Id, pkt);

                    CaptureCharLabPackets(pkt, inst.Id);

                    if (!_sniff)
                        continue;

                    lock (_pktGate)
                    {
                        int cap = IsGuiHoldActive() ? MaxPacketsHeld : MaxPackets;
                        if (_packets.Count >= cap)
                        {
                            int drop = _packets.Count - cap + 1;
                            if (drop > 0) _packets.RemoveRange(0, drop);
                        }
                        _packets.Add(pkt);
                    }
                    if (wsBudget-- > 0)
                    {
                        // Live list is metadata-only unless hex broadcast is on or the GUI is inspecting.
                        bool includeHex = broadcastHex || IsGuiHoldActive();
                        Broadcast("packet", PacketDto(pkt, includeHex));
                    }
                }
            }
            catch { }
        }
    }

    void DrainLuaChatReports()
    {
        foreach (var inst in _instances.All.Where(i => i.Connected))
        {
            try
            {
                if (!inst.ChatReports.IsOpen)
                    inst.ChatReports.TryOpen(inst.Pid);
                foreach (var r in inst.ChatReports.DrainNew())
                {
                    if (r.Kind == ChatReportReader.KindPlayer || r.Kind == ChatReportReader.KindWho)
                    {
                        var p = PlayerDirectory.ObserveName(
                            r.Guid, r.Sender, realm: null, race: r.Race, gender: r.Gender, classId: r.ClassId);
                        if (r.Level > 0) p.Level = r.Level;
                        if (IsLuaSelfNameReport(r.Extra))
                        {
                            ulong selfGuid = 0;
                            ulong.TryParse(r.Guid, System.Globalization.NumberStyles.HexNumber, null, out selfGuid);
                            LockPlayerIdentity(inst, r.Sender, selfGuid, "lua-player");
                        }
                        if (!string.IsNullOrWhiteSpace(r.Extra)
                            && r.Extra.StartsWith("wd:", StringComparison.OrdinalIgnoreCase))
                        {
                            _watchdog?.NoteRunningBots(inst.Id, r.Extra);
                        }
                        else if (!string.IsNullOrWhiteSpace(r.Extra) && string.IsNullOrWhiteSpace(p.Realm))
                            p.Realm = r.Extra.Split('|')[0];
                        _chatLog.EnqueuePlayer(p);
                        Broadcast("player", new
                        {
                            guid = p.Guid,
                            name = p.Name,
                            level = p.Level,
                            classId = p.Class,
                            race = p.Race,
                            extra = r.Extra,
                            source = "lua",
                        });
                        continue;
                    }

                    string dedupeKey = $"{inst.Id}|{r.Guid}|{r.Message}";
                    _recentLuaChat[dedupeKey] = 1;
                    if (_recentLuaChat.Count > 2048)
                    {
                        foreach (var old in _recentLuaChat.Keys.Take(512).ToList())
                            _recentLuaChat.TryRemove(old, out _);
                    }

                    _chatLog.EnqueueLuaChat(r.Channel, r.Sender, r.Message, r.Guid, inst.Id, r.Extra);
                    string ts = DateTime.UtcNow.ToString("o");
                    Broadcast("chat", new
                    {
                        id = DateTime.UtcNow.Ticks,
                        ts,
                        inst = inst.Id,
                        kind = "lua_chat",
                        type = r.Channel,
                        channel = r.Channel,
                        sender = r.Sender,
                        senderGuid = r.Guid,
                        message = r.Message,
                    });
                }
            }
            catch { }
        }
    }

    void LogPacketStrings(int instanceId, CapturedPacket pkt)
    {
        try
        {
            var hits = PacketStringExtractor.Extract(pkt.Data);
            if (hits.Count == 0) return;
            string head = Convert.ToHexString(pkt.Data.AsSpan(0, Math.Min(pkt.Data.Length, 64)));
            string dir = pkt.Dir switch
            {
                PktDir.In => "SMSG",
                PktDir.Out => "CMSG",
                _ => "?",
            };
            foreach (var h in hits)
            {
                _chatLog.EnqueuePacketStrings(new ChatLogService.PacketStringRow
                {
                    InstanceId = instanceId,
                    Opcode = Opcodes.Normalize(pkt.Opcode),
                    Dir = dir,
                    Offset = h.Offset,
                    Text = h.Text,
                    RawHexHead = head,
                });
            }
        }
        catch { }
    }

    void QueueLuaGuidResolve(ProxyClient proxy, string guidHex)
    {
        try
        {
            guidHex = PlayerDirectory.NormGuid(guidHex);
            if (guidHex.Length == 0) return;
            _pendingNameQueries.Enqueue((proxy, "lua:" + guidHex));
        }
        catch { }
    }

    void QueueNameQuery(ProxyClient proxy, string guidHex)
    {
        try
        {
            guidHex = PlayerDirectory.NormGuid(guidHex);
            if (guidHex.Length == 0 || guidHex.Trim('0').Length == 0) return;
            if (!string.IsNullOrWhiteSpace(PlayerDirectory.ResolveName(guidHex))) return;
            if (_nameQueryCooldown.TryGetValue(guidHex, out var last)
                && DateTime.UtcNow - last < TimeSpan.FromSeconds(45))
                return;
            _nameQueryCooldown[guidHex] = DateTime.UtcNow;
            // Bound cooldown map
            if (_nameQueryCooldown.Count > 4000)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-10);
                foreach (var kv in _nameQueryCooldown)
                {
                    if (kv.Value < cutoff)
                        _nameQueryCooldown.TryRemove(kv.Key, out _);
                }
            }
            _pendingNameQueries.Enqueue((proxy, guidHex));
        }
        catch { }
    }

    void DrainPendingNameQueries()
    {
        int budget = 6;
        while (budget-- > 0 && _pendingNameQueries.TryDequeue(out var item))
        {
            try
            {
                if (item.Guid.StartsWith("lua:", StringComparison.Ordinal))
                {
                    RequestLuaGuidResolve(item.Proxy, item.Guid.Substring(4));
                    continue;
                }
                RequestNameQuery(item.Proxy, item.Guid);
            }
            catch { }
        }
        /* Drop backlog so cities don't queue forever. */
        while (_pendingNameQueries.Count > 64 && _pendingNameQueries.TryDequeue(out _)) { }
    }

    void RequestLuaGuidResolve(ProxyClient proxy, string guidHex)
    {
        try
        {
            guidHex = PlayerDirectory.NormGuid(guidHex);
            if (guidHex.Length == 0) return;
            proxy.RunLua(
                "if GmChatCapture and GmChatCapture.ResolveGuid then " +
                $"GmChatCapture.ResolveGuid('{guidHex}') end");
        }
        catch { }
    }

    /// <summary>
    /// Ask the realm for a player name (CMSG_NAME_QUERY 0x0050). Rate-limited per GUID.
    /// Response arrives as SMSG_NAME_QUERY_RESPONSE and feeds PlayerDirectory + players table.
    /// </summary>
    void RequestNameQuery(ProxyClient proxy, string guidHex)
    {
        try
        {
            guidHex = PlayerDirectory.NormGuid(guidHex);
            if (guidHex.Length == 0 || guidHex.Trim('0').Length == 0) return;
            if (!string.IsNullOrWhiteSpace(PlayerDirectory.ResolveName(guidHex))) return;
            if (_nameQueryCooldown.TryGetValue(guidHex, out var last)
                && DateTime.UtcNow - last < TimeSpan.FromSeconds(45))
                return;
            if (!ulong.TryParse(guidHex, System.Globalization.NumberStyles.HexNumber, null, out ulong g) || g == 0)
                return;
            _nameQueryCooldown[guidHex] = DateTime.UtcNow;
            var pkt = new byte[12];
            BitConverter.TryWriteBytes(pkt.AsSpan(0, 4), 0x0050u); // CMSG_NAME_QUERY
            BitConverter.TryWriteBytes(pkt.AsSpan(4, 8), g);
            proxy.Replay(pkt);
        }
        catch { }
    }

    void SyncFiltersToProxy(ProxyClient? proxy = null)
    {
        var data = OpcodeFilterStore.Current;
        void push(ProxyClient p)
        {
            try
            {
                p.SetOpcodeIgnore(data.IgnoredOpcodes);
                p.SetChatCapture(data.ChatCapture);
            }
            catch { }
        }
        if (proxy is not null) { push(proxy); return; }
        foreach (var inst in _instances.All.Where(i => i.Connected))
            push(inst.Proxy);
    }

    public object GetOpcodeFilter() => OpcodeFilterStore.Dto();

    public object SetOpcodeFilter(IEnumerable<uint>? opcodes, bool? chatCapture, bool resetDefaults)
    {
        if (resetDefaults)
            OpcodeFilterStore.ResetDefaults();
        else if (opcodes is not null)
            OpcodeFilterStore.SetIgnored(opcodes, chatCapture);
        else if (chatCapture is bool c)
            OpcodeFilterStore.SetIgnored(OpcodeFilterStore.Current.IgnoredOpcodes, c);
        SyncFiltersToProxy();
        Log($"opcode filter synced ({OpcodeFilterStore.Current.IgnoredOpcodes.Count} ignored, chatCapture={OpcodeFilterStore.ChatCapture})");
        return OpcodeFilterStore.Dto();
    }

    public object AddOpcodeIgnore(uint opcode)
    {
        OpcodeFilterStore.AddIgnore(opcode);
        SyncFiltersToProxy();
        return OpcodeFilterStore.Dto();
    }

    public object RemoveOpcodeIgnore(uint opcode)
    {
        OpcodeFilterStore.RemoveIgnore(opcode);
        SyncFiltersToProxy();
        return OpcodeFilterStore.Dto();
    }

    void KickLogTail()
    {
        try
        {
            string path = Paths.ProxyLog;
            if (!File.Exists(path)) return;
            // light touch — full tail on demand via API
        }
        catch { }
    }

    public object BuildStatusDto()
    {
        int connected = _instances.All.Count(i => i.Connected);
        int total = Math.Max(_instances.All.Count(), SettingsStore.Current.InstanceCount);
        var active = _viewShared ? null : (_activeId is int id ? _instances.ById(id) : _instances.Active);
        bool ring = active?.Ring.IsOpen == true;
        bool pipe = active?.Proxy.Connected == true;
        // "Proxy" in the status bar = ExtProxy staged/ready OR live pipe (not legacy Runtime\ root only).
        bool proxy = pipe || _boot.IsInstalled();
        bool game = BootstrapService.GoClientRunning();
        return new
        {
            proxy,
            game,
            connected,
            total,
            active = _viewShared ? "Shared" : (active is null ? "--" : $"inst{active.Id}"),
            pid = active?.Pid ?? 0,
            ring,
            pipe,
            addons = _addonSummary,
            sniff = _sniff,
            packetHold = IsGuiHoldActive(),
            packetCount = PacketCount,
            chatCapture = OpcodeFilterStore.ChatCapture,
            opcodeIgnoreCount = OpcodeFilterStore.Current.IgnoredOpcodes.Count,
            chatLog = _chatLog.Stats(),
            busy = _busy,
            watchdog = _watchdog?.StatusDto(),
            fuzz = _fuzz is null ? null : new
            {
                phase = _fuzz.Phase,
                running = _fuzz.IsRunning,
                sent = _fuzz.Sent,
                hits = _fuzz.Interesting,
                crashes = _fuzz.Crashes,
            },
            needsSetup = SettingsStore.NeedsSetup(),
            setup = SettingsStore.SetupStatusDto(),
            offsetScan = ClientOffsetService.LastSummary,
        };
    }

    public object BuildInstancesDto()
    {
        return new
        {
            activeId = _activeId,
            shared = _viewShared,
            items = _instances.All.OrderBy(i => i.Id).Select(i => new
            {
                id = i.Id,
                pid = i.Pid,
                connected = i.Connected,
                owned = i.OwnedByLauncher,
                name = i.DisplayName,
                playerName = i.PlayerName,
                runtimeDir = i.RuntimeDir,
            }).ToList(),
        };
    }

    public object GetObjectsDto()
    {
        if (_viewShared)
        {
            var all = _shared.All.Take(256).Select(o => new
            {
                guid = o.Guid.ToString("X16"),
                entry = o.Entry,
                x = o.X, y = o.Y, z = o.Z,
                hp = o.Health, maxHp = o.MaxHealth,
                level = o.Level, faction = o.Faction,
                typeMask = o.TypeMask,
                src = o.SrcInstance,
            }).ToList();
            return new { mode = "shared", objects = all };
        }
        var inst = ResolveProxy();
        if (inst is null) return new { mode = "none", objects = Array.Empty<object>() };
        try
        {
            var snap = inst.Proxy.GetObjects();
            var list = snap.Units.Take(256).Select(u => new
            {
                guid = u.Guid.ToString("X16"),
                entry = u.Entry,
                x = u.X, y = u.Y, z = u.Z,
                hp = u.Health, maxHp = u.MaxHealth,
                level = u.Level, faction = u.Faction,
                typeMask = u.TypeMask,
                src = inst.Id,
            }).ToList();
            return new { mode = "active", objects = list };
        }
        catch
        {
            return new { mode = "active", objects = Array.Empty<object>() };
        }
    }

    GameInstance? ResolveProxy()
    {
        if (_viewShared) return null;
        if (_activeId is int id) return _instances.ById(id);
        return _instances.Active ?? _instances.All.FirstOrDefault(i => i.Connected);
    }

    static object PacketDto(CapturedPacket p, bool includeHex = false) => new
    {
        seq = p.Seq,
        inst = p.SrcInstance,
        dir = p.Dir.ToString().ToLowerInvariant(),
        opcode = p.Opcode,
        name = Opcodes.Name(p.Opcode),
        size = p.Data?.Length ?? 0,
        time = p.Tick,
        hex = includeHex && p.Data is not null ? Convert.ToHexString(p.Data) : "",
    };

    public IReadOnlyList<object> GetPackets(string? dir, int? opcode)
    {
        lock (_pktGate)
        {
            IEnumerable<CapturedPacket> q = _packets;
            if (!string.IsNullOrWhiteSpace(dir) && !dir.Equals("all", StringComparison.OrdinalIgnoreCase))
                q = q.Where(p => p.Dir.ToString().Equals(dir, StringComparison.OrdinalIgnoreCase));
            if (opcode is int op && op >= 0)
                q = q.Where(p => p.Opcode == (uint)op);
            int take = IsGuiHoldActive() ? Math.Min(MaxPacketsHeld, _packets.Count) : 400;
            if (take <= 0) return Array.Empty<object>();
            return q.TakeLast(take).Select(p => PacketDto(p, includeHex: true)).ToList();
        }
    }

    public CapturedPacket? FindCaptured(uint seq, int? inst)
    {
        lock (_pktGate)
        {
            for (int i = _packets.Count - 1; i >= 0; i--)
            {
                var p = _packets[i];
                if (p.Seq != seq) continue;
                if (inst is int id && p.SrcInstance != id) continue;
                return p;
            }
            return null;
        }
    }

    public object? GetPacket(uint seq, int? inst)
    {
        var p = FindCaptured(seq, inst);
        return p is { } hit ? PacketDto(hit, includeHex: true) : null;
    }

    public object LoadFuzzSeed(string? hex, uint? opcode, uint? seq, int? inst)
    {
        byte[] bytes = Array.Empty<byte>();
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                bytes = PacketView.ParseHex(hex);
        }
        catch
        {
            bytes = Array.Empty<byte>();
        }

        CapturedPacket? cap = seq is > 0 ? FindCaptured(seq.Value, inst) : null;
        if (bytes.Length < 2 && cap?.Data is { Length: >= 2 } data)
            bytes = data;

        uint op = opcode ?? 0;
        if (op == 0 && cap is { } captured)
            op = captured.Opcode;
        if (op == 0 && bytes.Length >= 2)
            op = bytes.Length >= 4 ? BitConverter.ToUInt32(bytes, 0) : BitConverter.ToUInt16(bytes, 0);
        if (op > 0xFFFF && bytes.Length >= 2)
            op = BitConverter.ToUInt16(bytes, 0);

        if (bytes.Length < 2 && op > 0)
            bytes = OpcodeBruteBuilder.Frame(op, Array.Empty<byte>(), u32: false);

        if (bytes.Length < 2)
            return new { ok = false, error = "packet has no bytes — pick a captured row and send it to the fuzzer again" };

        _fuzz?.LoadSeed(bytes, Opcodes.Normalize(op));
        _fuzz?.RefreshPool();
        return new
        {
            ok = true,
            opcode = _fuzz?.Opcode,
            fields = _fuzz?.Fields.Count,
            seedLen = _fuzz?.Seed.Length ?? bytes.Length,
            hex = _fuzz is { Seed.Length: > 0 } ? Convert.ToHexString(_fuzz.Seed) : Convert.ToHexString(bytes),
        };
    }

    /// <summary>Photino GUI is focused (or was within the last 10s). Keep the captured log inspectable.</summary>
    public object TouchGui()
    {
        lock (_guiGate) _guiTouchedUtc = DateTime.UtcNow;
        return new { ok = true, holdMs = GuiHoldMs, hold = true, packets = PacketCount };
    }

    public bool IsGuiHoldActive()
    {
        DateTime t;
        lock (_guiGate) t = _guiTouchedUtc;
        if (t == DateTime.MinValue) return false;
        return (DateTime.UtcNow - t).TotalMilliseconds < GuiHoldMs;
    }

    int PacketCount
    {
        get { lock (_pktGate) return _packets.Count; }
    }

    public void ClearPackets()
    {
        lock (_pktGate) _packets.Clear();
        Broadcast("packetsCleared", new { });
    }

    public async Task<object> LaunchAsync(int count)
    {
        count = Math.Clamp(count, 1, GmtLimits.MaxInstances);
        if (_busy) return new { ok = false, error = "busy" };

        _instances.ResumeLaunches();
        int maxSlots = GmtLimits.MaxInstances;
        int already = _instances.All.Count(i => i.OwnedByLauncher);
        if (already >= maxSlots)
        {
            return new
            {
                ok = false,
                error = $"Already running {already}/{maxSlots} game window(s) for this account.",
                maxInstances = maxSlots,
            };
        }
        if (count > maxSlots)
        {
            Log($"Launch requested {count}, capping to {maxSlots} Core slot(s).");
            count = maxSlots;
        }

        _busy = true;
        Broadcast("status", BuildStatusDto());
        try
        {
            SettingsStore.Current.InstanceCount = count;
            SettingsStore.Current.AutoSyncAddons = true;
            SettingsStore.Save();
            Paths.ApplySettings(SettingsStore.Current);
            if (!SettingsStore.IsAscensionConfigured())
                return new { ok = false, error = SettingsStore.DescribeMissing() };
            try
            {
                var scan = ClientOffsetService.EnsureFresh(Log);
                if (!scan.Skipped)
                    Log("offsets: " + scan.Summary);
            }
            catch (Exception ex)
            {
                Log("offset scan: " + ex.Message);
            }
            _boot.EnsureLatestProxyDeployed(forceRebuild: false);
            try
            {
                _addons.DeployAll();
                _addons.ScanAndAutoSync(true);
                Log("addons deployed from " + Paths.AddonsSourceDir);
            }
            catch (Exception ex)
            {
                Log("addon deploy: " + ex.Message);
            }
            _boot.InstallCombatAddons();
            Log($"Launch: {count}/{maxSlots} instance(s)");
            await _instances.LaunchN(count);
            int connected = _instances.All.Count(i => i.Connected);
            Log($"instances up: {connected}/{_instances.All.Count()}");
            return new { ok = true, connected, total = _instances.All.Count(), maxInstances = maxSlots };
        }
        catch (Exception ex)
        {
            Log("launch failed: " + ex.Message);
            return new { ok = false, error = ex.Message };
        }
        finally
        {
            _busy = false;
            Broadcast("instances", BuildInstancesDto());
            Broadcast("status", BuildStatusDto());
        }
    }

    /// <summary>
    /// Local toolbox — no store entitlements.
    /// Account Core with time remaining unlocks every shipped addon.
    /// </summary>
    public Task<object> SyncCharacterEntitlementsAsync(string? characterGuid, string? characterName = null)
    {
        PushProxyEntitlements(loggedIn: true, GmtLimits.MaxInstances, Array.Empty<string>());
        return Task.FromResult<object>(new
        {
            ok = true,
            valid = true,
            characterGuid,
            characterName,
            hasCore = true,
            message = "local",
        });
    }

    static ulong? TryParseGuid(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        string n = PlayerDirectory.NormGuid(hex);
        if (n.Length > 16) n = n[^16..];
        if (ulong.TryParse(n, System.Globalization.NumberStyles.HexNumber, null, out var v) && v != 0)
            return v;
        return null;
    }

    public void SelectInstance(int? id, bool shared)
    {
        _viewShared = shared;
        _activeId = shared ? null : id;
        if (!shared && id is int i)
        {
            var inst = _instances.ById(i);
            if (inst is not null) _instances.Active = inst;
        }
        Broadcast("instances", BuildInstancesDto());
        Broadcast("status", BuildStatusDto());
    }

    public object DeployAll()
    {
        try
        {
            _boot.EnsureLatestProxyDeployed(forceRebuild: false);
            _boot.InstallCombatAddons();
            var result = SyncAllLiveEntitlementsAsync().GetAwaiter().GetResult();
            RefreshAddons(true);
            return result;
        }
        catch (Exception ex)
        {
            Log("deploy: " + ex.Message);
            return new { ok = false, error = ex.Message };
        }
    }

    public object DeployProxy(bool force)
    {
        try
        {
            _boot.EnsureLatestProxyDeployed(forceRebuild: force);
            _boot.InstallCombatAddons();
            return new { ok = true };
        }
        catch (Exception ex) { return new { ok = false, error = ex.Message }; }
    }

    public object DeployAddons()
    {
        try
        {
            return SyncAllLiveEntitlementsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) { return new { ok = false, error = ex.Message }; }
    }

    async Task<object> SyncAllLiveEntitlementsAsync()
    {
        var live = _instances.All.Where(HasRealCharacter).ToList();
        if (live.Count == 0)
        {
            await ApplyAccountEntitlementsAsync().ConfigureAwait(false);
            RefreshAddons(true);
            return new { ok = true, note = "No live character yet — Core on this account still unlocks the catalog." };
        }
        object last = new { ok = true };
        foreach (var inst in live)
        {
            last = await SyncCharacterEntitlementsAsync(
                PlayerDirectory.NormGuid(inst.PlayerGuid), inst.PlayerName).ConfigureAwait(false);
        }
        return last;
    }

    public object RefreshAddons(bool force)
    {
        try
        {
            var statuses = _addons.ScanAndAutoSync(SettingsStore.Current.AutoSyncAddons);
            int ok = statuses.Count(s => s.State == AddonSyncState.Ok);
            _addonSummary = $"addons={ok}/{statuses.Count}";
            var dto = statuses.Select(s => new
            {
                name = s.Name,
                status = s.Summary,
                state = s.State.ToString(),
                inSync = s.State == AddonSyncState.Ok,
                inRepo = s.SourceExists,
                inLive = s.LiveExists,
                notes = s.Notes,
            }).ToList();
            Broadcast("addons", dto);
            return dto;
        }
        catch (Exception ex)
        {
            Log("addons: " + ex.Message);
            return Array.Empty<object>();
        }
    }

    Task CheckSessionAsync() => Task.CompletedTask;

    public Task<object> RevokeLocalAccessAsync(string reason = "signed-out") =>
        Task.FromResult<object>(new { ok = true, revoked = false, reason });

    Task PingEntitlementsAsync() => Task.CompletedTask;

    void ForceReloadUi(int? instanceId = null)
    {
        var targets = _instances.All.Where(i => i.Connected && i.PlayerGuid != 0);
        if (instanceId is int id)
            targets = targets.Where(i => i.Id == id);
        foreach (var inst in targets)
        {
            try
            {
                if (inst.Proxy.RunLua("if ReloadUI then ReloadUI() end"))
                    Log($"ReloadUI queued on instance {inst.Id}");
            }
            catch (Exception ex)
            {
                Log($"ReloadUI inst {inst.Id}: {ex.Message}");
            }
        }
    }

    public void SetSniff(bool on)
    {
        _sniff = on;
        foreach (var inst in _instances.All.Where(i => i.Connected))
        {
            inst.Proxy.SetSniff(on);
            SyncFiltersToProxy(inst.Proxy);
        }
        Log(on ? "Capture ON" : "Capture OFF");
        Broadcast("status", BuildStatusDto());
    }

    public object Replay(string hex)
    {
        var p = ResolveProxy();
        if (p is null || !p.Proxy.Connected) return new { ok = false, error = "no proxy" };
        var bytes = PacketView.ParseHex(hex);
        if (bytes is null || bytes.Length < 4) return new { ok = false, error = "bad hex" };
        p.Proxy.Replay(bytes);
        return new { ok = true };
    }

    public object Inject(string hex)
    {
        var p = ResolveProxy();
        if (p is null || !p.Proxy.Connected) return new { ok = false, error = "no proxy" };
        var bytes = PacketView.ParseHex(hex);
        if (bytes is null || bytes.Length < 4) return new { ok = false, error = "bad hex" };
        p.Proxy.InjectRecv(bytes);
        return new { ok = true };
    }

    void CaptureCharLabPackets(CapturedPacket pkt, int instanceId)
    {
        try
        {
            uint op = Opcodes.Normalize(pkt.Opcode);
            string name = Opcodes.Name(op);
            var inst = _instances.ById(instanceId) ?? (_activeId is int aid ? _instances.ById(aid) : _instances.Active);
            if (pkt.Dir == PktDir.In
                && (op is 0x003B or 0x075E
                    || name.Contains("CHAR_ENUM", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("CHARACTER_LIST", StringComparison.OrdinalIgnoreCase)))
            {
                _lastCharEnum = (byte[])pkt.Data.Clone();
                var roster = CharEnumParser.Parse(pkt.Data);
                if (roster.Count > 0 && inst is not null)
                {
                    inst.LastCharEnum = (byte[])pkt.Data.Clone();
                    inst.CharSelectNames = roster.Select(e => e.Name).ToList();
                    inst.LockedPlayerName = null;
                    inst.LastEntitlementKey = null;
                    foreach (var e in roster)
                        PlayerDirectory.ObserveName(PlayerDirectory.NormGuid(e.Guid), e.Name);
                    Log($"char-select inst{inst.Id}: " + string.Join(", ", inst.CharSelectNames));
                    _ = ApplyAccountEntitlementsAsync();
                }
            }
            if (pkt.Dir != PktDir.Out
                && !(pkt.Dir == PktDir.In
                    && (op is 0x004D or 0x01EC
                        || name.Contains("LOGOUT_COMPLETE", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("AUTH_CHALLENGE", StringComparison.OrdinalIgnoreCase))))
            {
                if (pkt.Dir != PktDir.Out) goto dc;
            }
            if (pkt.Dir == PktDir.Out)
            {
                // Stock WotLK CMSG_PLAYER_LOGIN=0x003D; Ascension may remap — also keep 12-byte outbound guid packets.
                if (op is 0x003D or 0x03C2
                    || name.Contains("PLAYER_LOGIN", StringComparison.OrdinalIgnoreCase)
                    || (pkt.Data.Length == 12 && op != 0))
                {
                    _lastPlayerLogin = (byte[])pkt.Data.Clone();
                    if (inst is not null)
                        _watchdog?.NoteHealthy(inst, _lastPlayerLogin);
                    var loginName = NameFromLoginPacket(pkt.Data, inst);
                    if (!string.IsNullOrWhiteSpace(loginName) && inst is not null)
                    {
                        LockPlayerIdentity(inst, loginName, GuidFromLoginPacket(pkt.Data), "player-login");
                    }
                }
                if (op is 0x0037
                    || name.Contains("CHAR_ENUM", StringComparison.OrdinalIgnoreCase)
                    || (pkt.Data.Length == 4 && name.Contains("ENUM", StringComparison.OrdinalIgnoreCase)))
                {
                    _lastCharEnum = (byte[])pkt.Data.Clone();
                    if (inst is not null)
                        inst.LastCharEnum = (byte[])pkt.Data.Clone();
                }
            }
            dc:
            // World DC / kicked to login while the process is still up.
            if (pkt.Dir == PktDir.In
                && (op is 0x004D or 0x01EC
                    || name.Contains("LOGOUT_COMPLETE", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("AUTH_CHALLENGE", StringComparison.OrdinalIgnoreCase)))
            {
                if (inst is not null)
                {
                    inst.LockedPlayerName = null;
                    inst.LastEntitlementKey = null;
                    _watchdog?.NoteDisconnectHint(inst);
                }
            }
        }
        catch { }
    }

    static bool IsLuaSelfNameReport(string extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) return false;
        if (extra.Equals("player", StringComparison.OrdinalIgnoreCase)) return true;
        if (extra.StartsWith("player|", StringComparison.OrdinalIgnoreCase)) return true;
        if (extra.StartsWith("wd:", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    void LockPlayerIdentity(GameInstance inst, string? name, ulong guid, string source)
    {
        name = CharEnumParser.Norm(name ?? "");
        if (!CharEnumParser.IsPlayable(name)) return;
        if (guid != 0) inst.PlayerGuid = guid;
        bool changed = !string.Equals(inst.LockedPlayerName, name, StringComparison.OrdinalIgnoreCase);
        inst.LockedPlayerName = name;
        inst.PlayerName = name;
        if (inst.PlayerGuid != 0)
            PlayerDirectory.ObserveName(PlayerDirectory.NormGuid(inst.PlayerGuid), name);
        if (!changed) return;
        inst.LastEntitlementKey = PlayerDirectory.NormGuid(inst.PlayerGuid) + "|" + name;
        Log($"player identity inst{inst.Id}: {name} ({source})");
        _ = SyncCharacterEntitlementsAsync(PlayerDirectory.NormGuid(inst.PlayerGuid), name);
    }

    string? NameFromCharEnum(GameInstance inst, ulong guid)
    {
        if (guid == 0) return null;
        var raw = inst.LastCharEnum is { Length: > 0 } ? inst.LastCharEnum : _lastCharEnum;
        if (raw is not { Length: > 0 }) return null;
        return CharEnumParser.Parse(raw).FirstOrDefault(e => e.Guid == guid).Name;
    }

    static ulong GuidFromLoginPacket(byte[] data)
    {
        if (data.Length < 10) return 0;
        int off = data.Length >= 12 ? 4 : 2;
        if (off + 8 > data.Length) return 0;
        return BitConverter.ToUInt64(data, off);
    }

    string? NameFromLoginPacket(byte[] data, GameInstance? inst)
    {
        if (data.Length < 10) return null;
        int off = data.Length >= 12 ? 4 : 2;
        if (off + 8 > data.Length) return null;
        ulong guid = BitConverter.ToUInt64(data, off);
        var raw = inst?.LastCharEnum is { Length: > 0 } ? inst.LastCharEnum : _lastCharEnum;
        var roster = raw is { Length: > 0 } ? CharEnumParser.Parse(raw) : Array.Empty<CharEnumParser.Entry>();
        return roster.FirstOrDefault(e => e.Guid == guid).Name;
    }

    void PushProxyEntitlements(bool loggedIn, int maxInstances, IEnumerable<string> allowedNames)
    {
        foreach (var inst in _instances.All.Where(i => i.Connected && i.Proxy.Connected))
            PushProxyEntitlementsTo(inst.Proxy, loggedIn, maxInstances, allowedNames);
    }

    void PushProxyEntitlementsTo(ProxyClient? proxy) =>
        PushProxyEntitlementsTo(proxy, loggedIn: true, GmtLimits.MaxInstances, Array.Empty<string>());

    void PushProxyEntitlementsTo(ProxyClient? proxy, bool loggedIn, int maxInstances, IEnumerable<string> allowedNames)
    {
        if (proxy is null || !proxy.Connected) return;
        try
        {
            var names = allowedNames.Select(CharEnumParser.Norm).Where(CharEnumParser.IsPlayable).Distinct().ToArray();
            proxy.SetEntitlements(loggedIn, gateOn: false, maxInstances, names);
        }
        catch (Exception ex)
        {
            Log("entitlement push: " + ex.Message);
        }
    }

    public object CharCreateStatus()
    {
        var p = ResolveProxy();
        return new
        {
            unlockDefault = _charUnlockDefault,
            hasLoginPacket = _lastPlayerLogin is { Length: > 0 },
            loginHex = _lastPlayerLogin is null ? "" : Convert.ToHexString(_lastPlayerLogin),
            hasEnumPacket = _lastCharEnum is { Length: > 0 },
            enumHex = _lastCharEnum is null ? "" : Convert.ToHexString(_lastCharEnum),
            botRunning = _charBotCts is { IsCancellationRequested: false },
            botLog = _charBotLog.TakeLast(40).ToList(),
            presets = CharCreateLab.Presets.Select(x => new { race = x.Race, classId = x.Class, label = x.Label }),
            notes = new[]
            {
                "Client unlock does not force server accept.",
                "Stock CMSG_PLAYER_LOGIN=0x003D may be remapped — prefer sniffed loginHex.",
                "One world session per account is normal; dual-instance same account is the multi-login experiment.",
                "GmCharCreateUnlock patches ValidateName + IsRaceClassRestricted in Ascension.exe.",
            },
            proxy = p?.Proxy.Connected == true,
            instanceId = p?.Id,
        };
    }

    public object CharCreateUnlock(bool on)
    {
        _charUnlockDefault = on;
        return RunLua(CharCreateLab.UnlockScript(on));
    }

    public object CharCreateProbe()
    {
        var r1 = RunLua(CharCreateLab.StatusScript());
        var r2 = RunLua(CharCreateLab.EnumProbeScript());
        return new { ok = true, status = r1, enumProbe = r2, lab = CharCreateStatus() };
    }

    public object CharCreateRandomName()
    {
        // Client GetRandomName + host-generated weird name for unlock tests.
        RunLua(CharCreateLab.RandomNameLuaScript());
        return new
        {
            ok = true,
            hostName = CharCreateLab.RandomName(),
            weirdName = CharCreateLab.RandomName(weird: true),
        };
    }

    public object CharCreateExperiments()
    {
        if (_charUnlockDefault)
            RunLua(CharCreateLab.UnlockScript(true));
        var script = CharCreateLab.ExperimentBatchScript(unlockFirst: false);
        var r = RunLua(script);
        Log("charcreate: experiment batch queued");
        return new
        {
            ok = true,
            count = CharCreateLab.Experiments.Length,
            experiments = CharCreateLab.Experiments.Select(e => new { e.Id, e.Name, e.Race, e.Class, e.Note }),
            result = r,
        };
    }

    public object CharCreateChaos()
    {
        if (_charUnlockDefault)
            RunLua(CharCreateLab.UnlockScript(true));
        var r = RunLua(CharCreateLab.ChaosScript());
        Log("charcreate: chaos create queued");
        return new { ok = true, result = r };
    }

    public object CharCreateForceAppear(int race, int classId, int sex, int skin, int face, int hairStyle, int hairColor, int facial)
    {
        var r = RunLua(CharCreateLab.ForceClassScript(race, classId, sex, skin, face, hairStyle, hairColor, facial));
        return new { ok = true, race, classId, sex, skin, face, hairStyle, hairColor, facial, result = r };
    }

    public object CharCreateOne(string? name, int race, int classId, int sex, bool randomize, bool weird, bool unlock)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = weird ? CharCreateLab.RandomName(weird: true) : CharCreateLab.RandomName();
        if (unlock || _charUnlockDefault)
            RunLua(CharCreateLab.UnlockScript(true));
        var script = CharCreateLab.CreateScript(name!, race, classId, sex, randomize, unlockFirst: false);
        var r = RunLua(script);
        Log($"charcreate: CreateCharacter name={name} race={race} class={classId} sex={sex}");
        return new { ok = true, name, race, classId, sex, result = r };
    }

    public object CharCreateBotStart(int count, int delayMs, bool weird, bool unlock, int race, int classId, int sex)
    {
        count = Math.Clamp(count, 1, 50);
        delayMs = Math.Clamp(delayMs, 500, 15000);
        _charBotCts?.Cancel();
        _charBotCts = new CancellationTokenSource();
        var ct = _charBotCts.Token;
        _charBotLog.Clear();
        if (unlock || _charUnlockDefault)
            RunLua(CharCreateLab.UnlockScript(true));
        _ = Task.Run(async () =>
        {
            Log($"charbot: start count={count} delay={delayMs}ms weird={weird}");
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                string name = weird ? CharCreateLab.RandomName(weird: true) : CharCreateLab.RandomName();
                // Slight race/class jitter if race/class left 0
                int r = race > 0 ? race : CharCreateLab.Presets[i % CharCreateLab.Presets.Length].Race;
                int c = classId > 0 ? classId : CharCreateLab.Presets[i % CharCreateLab.Presets.Length].Class;
                try
                {
                    var script = CharCreateLab.CreateScript(name, r, c, sex, randomize: true, unlockFirst: false);
                    RunLua(script);
                    var entry = new { t = DateTime.UtcNow.ToString("o"), i = i + 1, name, race = r, classId = c, ok = true };
                    lock (_charBotLog) _charBotLog.Add(entry);
                    Broadcast("charbot", entry);
                    Log($"charbot: [{i + 1}/{count}] {name}");
                }
                catch (Exception ex)
                {
                    var entry = new { t = DateTime.UtcNow.ToString("o"), i = i + 1, name, error = ex.Message, ok = false };
                    lock (_charBotLog) _charBotLog.Add(entry);
                    Broadcast("charbot", entry);
                }
                try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            Log("charbot: done");
            Broadcast("charbot", new { done = true });
            try { _charBotCts?.Cancel(); } catch { }
        }, ct);
        return new { ok = true, count, delayMs };
    }

    public object CharCreateBotStop()
    {
        _charBotCts?.Cancel();
        return new { ok = true };
    }

    public object CharCreateSelect(int index)
    {
        return RunLua(CharCreateLab.SelectCharacterScript(Math.Clamp(index, 1, 16)));
    }

    public object CharCreateEnterWorld(int index)
    {
        return RunLua(CharCreateLab.EnterWorldScript(Math.Clamp(index, 1, 16)));
    }

    public object CharCreateReplayLogin(string? guidHex, string? hexPacket)
    {
        var p = ResolveProxy();
        if (p is null || !p.Proxy.Connected) return new { ok = false, error = "no proxy" };
        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(hexPacket))
            bytes = PacketView.ParseHex(hexPacket);
        if (bytes is null && !string.IsNullOrWhiteSpace(guidHex))
            bytes = CharCreateLab.BuildPlayerLoginFromHexGuid(guidHex!);
        if (bytes is null)
            bytes = _lastPlayerLogin;
        if (bytes is null || bytes.Length < 4)
            return new { ok = false, error = "no login packet — sniff a CMSG_PLAYER_LOGIN first or pass guid/hex" };
        p.Proxy.Replay(bytes);
        Log($"charcreate: replayed login {bytes.Length}b op=0x{BitConverter.ToUInt32(bytes, 0):X}");
        return new { ok = true, hex = Convert.ToHexString(bytes), size = bytes.Length };
    }

    public object CharCreateReplayEnum(string? hexPacket)
    {
        var p = ResolveProxy();
        if (p is null || !p.Proxy.Connected) return new { ok = false, error = "no proxy" };
        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(hexPacket))
            bytes = PacketView.ParseHex(hexPacket);
        bytes ??= _lastCharEnum ?? CharCreateLab.BuildCharEnumPacket();
        p.Proxy.Replay(bytes);
        Log($"charcreate: replayed CHAR_ENUM {bytes.Length}b");
        return new { ok = true, hex = Convert.ToHexString(bytes) };
    }

    /// <summary>
    /// Dual-login experiment: while this instance is in-world, fire PLAYER_LOGIN for another GUID
    /// on the same connection (usually rejected). Also documents multi-instance same-account path.
    /// </summary>
    public object CharCreateMultiLoginExperiment(string? secondGuidHex)
    {
        var notes = new List<string>();
        var p = ResolveProxy();
        if (p is null || !p.Proxy.Connected)
            return new { ok = false, error = "no proxy", notes };

        notes.Add("Experiment A: second CMSG_PLAYER_LOGIN on SAME world session (expect server reject / kick).");
        var login = !string.IsNullOrWhiteSpace(secondGuidHex)
            ? CharCreateLab.BuildPlayerLoginFromHexGuid(secondGuidHex!)
            : _lastPlayerLogin;
        object? replay = null;
        if (login is { Length: > 0 })
        {
            p.Proxy.Replay(login);
            replay = new { hex = Convert.ToHexString(login) };
            notes.Add("Replayed login packet on active instance.");
        }
        else
        {
            notes.Add("No login packet available — enable Sniff, enter world once, retry.");
        }

        notes.Add("Experiment B: launch 2 instances, log the SAME account into both (client-side parallel sessions).");
        notes.Add("Auth/realm may allow dual client or kick the older session — observe SMSG / disconnect.");
        notes.Add("True simultaneous two characters in one TCP session is almost never supported on Ascension/WotLK.");

        // Soft enum refresh
        try { p.Proxy.Replay(_lastCharEnum ?? CharCreateLab.BuildCharEnumPacket()); }
        catch { }

        Log("charcreate: multi-login experiment fired");
        return new
        {
            ok = true,
            replay,
            instances = _instances.All.Select(i => new { i.Id, i.Pid, i.Connected, i.PlayerName }),
            notes,
        };
    }

    public object RunLua(string script)
    {
        var p = ResolveProxy();
        if (p is null || !p.Proxy.Connected)
            return new { ok = false, error = "no proxy", instanceId = (int?)null, pid = 0 };
        bool queued = p.Proxy.RunLua(script);
        return new
        {
            ok = queued,
            error = queued ? null : "RunLua rejected (pipe/queue)",
            instanceId = p.Id,
            pid = p.Pid,
            player = p.PlayerName,
        };
    }

    public object SetHacks(uint bits, bool fly = false)
    {
        var p = ResolveProxy();
        if (p is null || !p.Proxy.Connected)
            return new { ok = false, error = "no proxy", bits = 0u, fly };
        uint applied = p.Proxy.SetHacks(bits, fly);
        return new { ok = true, bits = applied, fly, instanceId = p.Id, pid = p.Pid };
    }

    public object ReplayGmCheat(string opcodeName, ReadOnlySpan<byte> bodyAfterOpcode = default)
    {
        var p = ResolveProxy();
        if (p is null || !p.Proxy.Connected)
            return new { ok = false, error = "no proxy", opcode = opcodeName };
        bool ok = p.Proxy.ReplayGmCheat(opcodeName, bodyAfterOpcode);
        var op = p.Proxy.FindOpcode(opcodeName);
        return new { ok, opcode = opcodeName, id = op, instanceId = p.Id, pid = p.Pid };
    }

    /// <summary>
    /// Send chat into a named CHANNEL (default Newcomers) via Lua SendChatMessage → client CMSG_MESSAGECHAT.
    /// Falls back to raw CMSG_MESSAGECHAT Replay if Lua queue fails.
    /// </summary>
    public object ChatSay(string channel, string text)
    {
        channel = string.IsNullOrWhiteSpace(channel) ? "SAY" : channel.Trim();
        text = (text ?? "").Trim();
        if (text.Length < 1)
            return new { ok = false, error = "empty", channel, text };

        var p = ResolveProxy();
        if (p is null || !p.Proxy.Connected)
        {
            // Prefer any live pipe if Active is unset
            p = _instances.All.FirstOrDefault(i => i.Connected && i.Proxy.Connected);
        }
        if (p is null || !p.Proxy.Connected)
        {
            Log($"chat say BLOCKED — no ExtProxy pipe (need Launch + in-world). wanted #{channel}: {text}");
            return new
            {
                ok = false,
                error = "no proxy — launch an instance and wait until Pipe: Yes",
                channel,
                text,
                connected = _instances.All.Count(i => i.Connected),
            };
        }

        string script = ChatSend.BuildSendScript(channel, text);
        bool luaOk = p.Proxy.RunLua(script);
        bool pktOk = false;
        string? pktErr = null;
        if (!luaOk)
        {
            try
            {
                var pkt = ChatSend.BuildCmsgMessageChatChannel(channel, text);
                pktOk = p.Proxy.Replay(pkt);
                if (!pktOk) pktErr = "Replay(CMSG_MESSAGECHAT) rejected";
            }
            catch (Exception ex)
            {
                pktErr = ex.Message;
            }
        }

        bool ok = luaOk || pktOk;
        Log(ok
            ? $"chat say → inst{p.Id} #{channel} via={(luaOk ? "lua" : "cmsg")} : {text}"
            : $"chat say FAIL inst{p.Id} #{channel}: lua={luaOk} pkt={pktOk} {pktErr}");

        return new
        {
            ok,
            channel,
            text,
            via = luaOk ? "lua-SendChatMessage" : (pktOk ? "cmsg-replay" : "none"),
            instanceId = p.Id,
            pid = p.Pid,
            player = p.PlayerName,
            error = ok ? null : (pktErr ?? "failed to queue chat send"),
            note = luaOk
                ? "Lua queued — client will SendChatMessage CHANNEL (CMSG_MESSAGECHAT 0x0095). Join may take ~1s."
                : null,
        };
    }

    public object DescribePacket(string hex)
    {
        var bytes = PacketView.ParseHex(hex);
        if (bytes is null || bytes.Length < 4) return new { text = "", fields = Array.Empty<object>(), schema = (object?)null };
        uint op = BitConverter.ToUInt32(bytes, 0);
        if (op > 0xFFFF) op = BitConverter.ToUInt16(bytes, 0);
        op = Opcodes.Normalize(op);
        var pkt = new CapturedPacket(0, 0, PktDir.Out, op, bytes, 0);
        var fields = PacketSchemaRegistry.DecodeFields(op, bytes);
        var schema = PacketSchemaRegistry.Get(op);
        return new
        {
            text = PacketView.Describe(pkt, Opcodes.Name(op)),
            opcode = op,
            name = Opcodes.Name(op),
            fields,
            schema = schema is null ? null : new
            {
                schema.Opcode,
                schema.Name,
                schema.Dir,
                schema.Source,
                fieldCount = schema.Fields.Count,
            },
        };
    }

    public object ExportBookmarkForBot(int slot)
    {
        var b = PacketBookmarkStore.Get(slot);
        if (b is null || string.IsNullOrWhiteSpace(b.Hex))
            return new { ok = false, error = "empty bookmark" };
        // BotBuilder Catalog pkt_replay action import snippet
        string lua =
            "-- BotBuilder action: pkt_replay\n" +
            $"-- slot={slot} label={b.Label}\n" +
            "{\n" +
            "  id = \"pkt_replay\",\n" +
            $"  args = {{ slot = {slot}, hex = \"{b.Hex}\", note = \"{b.Label?.Replace("\"", "'")}\" }},\n" +
            "}\n";
        return new { ok = true, slot, hex = b.Hex, label = b.Label, botBuilderSnippet = lua };
    }

    public object Health()
    {
        try
        {
            var items = HealthCheckService.RunAll(null);
            return items.Select(i => new
            {
                id = i.Id,
                title = i.Title,
                detail = i.Detail,
                severity = i.Severity.ToString(),
                blocking = i.Blocking,
            }).ToList();
        }
        catch (Exception ex)
        {
            return new[]
            {
                new
                {
                    id = "health",
                    title = "Startup health",
                    detail = ex.Message,
                    severity = "Warn",
                    blocking = false,
                },
            };
        }
    }

    public InstanceManager Instances => _instances;
    public BootstrapService Boot => _boot;
    public AddonService Addons => _addons;

    public object BookmarkFire(int slot)
    {
        var p = ResolveProxy();
        if (p is null) return new { ok = false };
        p.Proxy.BookmarkFire(slot);
        return new { ok = true };
    }

    public object BookmarkSync()
    {
        var p = ResolveProxy();
        if (p is null) return new { ok = false, error = "no proxy" };
        PacketBookmarkStore.SyncToProxy(p.Proxy);
        return new { ok = true };
    }

    public object BookmarkLoop(bool on)
    {
        var p = ResolveProxy();
        if (p is null) return new { ok = false };
        p.Proxy.BookmarkLoop(on);
        return new { ok = true };
    }

    public object BookmarkBurst()
    {
        var p = ResolveProxy();
        if (p is null) return new { ok = false };
        p.Proxy.BookmarkBurst();
        return new { ok = true };
    }
}
