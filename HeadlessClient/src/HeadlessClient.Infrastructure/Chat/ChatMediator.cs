using System.Collections.Concurrent;
using System.Globalization;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.Session;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Monitoring;
using HeadlessClient.Infrastructure.Protocol;
using HeadlessClient.Infrastructure.Query;

namespace HeadlessClient.Infrastructure.Chat;

/// <summary>
/// Transparent in-game voice for the website chatroom.
/// Listen-only until <see cref="OutboundEnabled"/> is turned on.
/// </summary>
public sealed class ChatMediator
{
    private readonly IChatLog _chat;
    private readonly QueryCache? _queries;
    private readonly PlayerRosterStore? _roster;
    private readonly ChannelRosterStore? _channelsStore;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly ConcurrentDictionary<string, AttachedSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private IWorldClient? _world;
    private string _characterName = string.Empty;
    private string _accountTag = string.Empty;
    private string _outboundAccountTag = string.Empty;
    private readonly ConcurrentDictionary<string, WhoEntry> _players =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _guidToName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, byte> _nameQueryPending = new();
    private readonly ConcurrentDictionary<string, byte> _channels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _joinedConfirmed =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _invalidJoinNames =
        new(StringComparer.OrdinalIgnoreCase);
    private long _version;
    private int _outboundEnabled;
    private int _saveScheduled;
    private int _channelSaveScheduled;
    private int _chatDecodeFails;
    private readonly SemaphoreSlim _joinAllGate = new(1, 1);
    private DateTimeOffset _lastWhoBatchUtc = DateTimeOffset.MinValue;

    public ChatMediator(
        IChatLog chat,
        QueryCache? queries = null,
        PlayerRosterStore? roster = null,
        ChannelRosterStore? channelsStore = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _queries = queries;
        _roster = roster;
        _channelsStore = channelsStore;
        if (chat is IObservableChatLog obs)
        {
            obs.LineAppended += OnChatLine;
        }

        LoadRoster();
        LoadChannels();
    }

    public long Version => Interlocked.Read(ref _version);
    public string CharacterName => _characterName;
    public string AccountTag => _accountTag;
    public bool OutboundEnabled => Volatile.Read(ref _outboundEnabled) != 0;

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _world is { State: SessionState.InWorld };
            }
        }
    }

    public event Action<ChatLine>? ChatPushed;
    public event Action? PlayersChanged;
    public event Action? ChannelsChanged;

    public const string MiscChannel = DefaultChatChannels.Misc;

    public void SetOutboundEnabled(bool enabled)
    {
        Interlocked.Exchange(ref _outboundEnabled, enabled ? 1 : 0);
        Bump();
    }

    /// <summary>
    /// Post into the shared chatroom without sending to the game world.
    /// Registered portal users use this until they opt into world connect.
    /// </summary>
    public void PostCommunityMessage(string sender, string message, string? channel = null)
    {
        sender = (sender ?? "").Trim();
        message = (message ?? "").Trim();
        if (sender.Length == 0)
        {
            sender = "anon";
        }

        if (message.Length == 0)
        {
            throw new ArgumentException("Message required.");
        }

        var ch = string.IsNullOrWhiteSpace(channel) ? "COMMUNITY" : channel.Trim();
        if (ch.Equals("COMMUNITY", StringComparison.OrdinalIgnoreCase)
            || ch.Equals("CHATROOM", StringComparison.OrdinalIgnoreCase))
        {
            ch = "COMMUNITY";
        }

        var line = new ChatLine(
            DateTimeOffset.UtcNow,
            ChatTypes.System,
            "0",
            sender,
            ch,
            message,
            ReadableText: $"{sender}: {message}",
            Direction: "out");
        _chat.Append(line);
        Bump();
    }

    /// <summary>Select which attached game account may send / act (user-owned unlocks).</summary>
    public bool SelectOutboundAccount(string accountTag)
    {
        accountTag = (accountTag ?? "").Trim();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(accountTag, out var sess)
                || sess.World.State != SessionState.InWorld)
            {
                return false;
            }

            _outboundAccountTag = accountTag;
            _world = sess.World;
            _characterName = sess.Character;
            _accountTag = sess.Account;
            Bump();
            return true;
        }
    }

    public IReadOnlyList<object> GetAttachedSessions() =>
        _sessions.Values
            .Select(s => (object)new
            {
                account = s.Account,
                character = s.Character,
                inWorld = s.World.State == SessionState.InWorld,
                isOutbound = s.Account.Equals(_outboundAccountTag, StringComparison.OrdinalIgnoreCase)
                             || (string.IsNullOrEmpty(_outboundAccountTag)
                                 && s.Account.Equals(_accountTag, StringComparison.OrdinalIgnoreCase)),
                isDefault = s.IsDefault
            })
            .ToList();

    public void Attach(IWorldClient world, string characterName, string accountTag, bool isDefault = true)
    {
        ArgumentNullException.ThrowIfNull(world);
        accountTag = (accountTag ?? "").Trim();
        characterName = characterName?.Trim() ?? string.Empty;
        lock (_gate)
        {
            // Keep listening on every attached world (multi-account fan-in).
            if (_sessions.TryGetValue(accountTag, out var old)
                && !ReferenceEquals(old.World, world))
            {
                old.World.PacketReceived -= OnPacket;
            }

            world.PacketReceived -= OnPacket;
            world.PacketReceived += OnPacket;
            _sessions[accountTag] = new AttachedSession(world, characterName, accountTag, isDefault);

            // New world socket => prior CHANNEL_NOTIFY confirms are stale; force rejoin.
            _joinedConfirmed.Clear();
            _invalidJoinNames.Clear();

            // Default/system account stays preferred for listening status unless none set.
            if (isDefault || _world is null || string.IsNullOrEmpty(_accountTag))
            {
                _world = world;
                _characterName = characterName;
                _accountTag = accountTag;
                if (string.IsNullOrEmpty(_outboundAccountTag))
                {
                    _outboundAccountTag = accountTag;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(characterName))
        {
            var now = DateTimeOffset.UtcNow;
            _players.AddOrUpdate(
                characterName,
                _ => new WhoEntry(
                    characterName, "", -1, -1, -1, 0, 0,
                    MessageCount: 0,
                    LastSeenUtc: now,
                    LastWhoUtc: now,
                    Presence: PlayerPresence.Online),
                (_, old) => old with
                {
                    LastSeenUtc = now,
                    LastWhoUtc = now
                });
            ScheduleRosterSave();
        }

        Bump();
    }

    public void Attach(IWorldClient world, string characterName, string accountTag) =>
        Attach(world, characterName, accountTag, isDefault: true);

    public void Detach(IWorldClient world)
    {
        lock (_gate)
        {
            string? removeKey = null;
            foreach (var kv in _sessions)
            {
                if (ReferenceEquals(kv.Value.World, world))
                {
                    removeKey = kv.Key;
                    break;
                }
            }

            if (removeKey is not null)
            {
                _sessions.TryRemove(removeKey, out _);
            }

            world.PacketReceived -= OnPacket;
            if (ReferenceEquals(_world, world))
            {
                var next = _sessions.Values.FirstOrDefault(s => s.IsDefault)
                           ?? _sessions.Values.FirstOrDefault();
                if (next is not null)
                {
                    _world = next.World;
                    _characterName = next.Character;
                    _accountTag = next.Account;
                }
                else
                {
                    _world = null;
                    _characterName = "";
                    _accountTag = "";
                }
            }
        }

        Bump();
    }

    private sealed record AttachedSession(
        IWorldClient World,
        string Character,
        string Account,
        bool IsDefault);

    /// <summary>Fill names / whisper labels before the chat log stores the line.</summary>
    public ChatLine EnrichIncoming(ChatLine line)
    {
        var sender = line.Sender;
        if (string.IsNullOrWhiteSpace(sender) && !string.IsNullOrWhiteSpace(line.SenderGuid))
        {
            if (_guidToName.TryGetValue(line.SenderGuid, out var cached))
            {
                sender = cached;
            }
            else
            {
                RequestNameQuery(line.SenderGuid);
            }
        }
        else if (!string.IsNullOrWhiteSpace(sender) && !string.IsNullOrWhiteSpace(line.SenderGuid))
        {
            _guidToName[line.SenderGuid] = sender;
        }

        var channel = line.Channel;
        var direction = line.Direction;
        var targetName = string.Empty;
        if (!string.IsNullOrWhiteSpace(line.TargetGuid))
        {
            if (_guidToName.TryGetValue(line.TargetGuid, out var tn))
            {
                targetName = tn;
            }
            else
            {
                RequestNameQuery(line.TargetGuid);
            }
        }

        if (line.Type == ChatTypes.WhisperInform)
        {
            direction = "out";
            channel = string.IsNullOrWhiteSpace(targetName)
                ? "WHISPER_OUT"
                : $"to:{targetName}";
            if (string.IsNullOrWhiteSpace(sender))
            {
                sender = _characterName;
            }
        }
        else if (line.Type is ChatTypes.Whisper or ChatTypes.WhisperForeign)
        {
            direction = "in";
            channel = "WHISPER";
        }

        var readable = string.IsNullOrWhiteSpace(sender)
            ? line.Message
            : $"{sender}: {line.Message}";

        if (line.Type == ChatTypes.Channel && !string.IsNullOrWhiteSpace(channel)
            && !channel.Equals("CHANNEL", StringComparison.OrdinalIgnoreCase))
        {
            channel = NormalizeChannelName(channel);
            if (channel.Equals(MiscChannel, StringComparison.OrdinalIgnoreCase))
            {
                _channels[MiscChannel] = 1;
            }
            else
            {
                _channels[channel] = 1;
            }
        }

        var enriched = line with
        {
            Sender = sender,
            Channel = channel,
            Direction = direction,
            ReadableText = readable
        };
        _queries?.NoteChatText(enriched.Message);
        _queries?.NoteChatText(enriched.ReadableText);
        return enriched;
    }

    public IReadOnlyList<WhoEntry> GetPlayers()
    {
        var now = DateTimeOffset.UtcNow;
        var selfOnline = IsReady;
        return _players.Values
            .Select(p => p with
            {
                Presence = PlayerPresence.Compute(p, now, _characterName, selfOnline)
            })
            .OrderBy(p => PresenceRank(p.Presence))
            .ThenByDescending(p => p.MessageCount)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<object> GetUsers() =>
        GetPlayers()
            .Where(p => !HeadlessClient.Infrastructure.Config.HiddenOperators.IsHiddenName(p.Name))
            .Select(p =>
            {
                var now = DateTimeOffset.UtcNow;
                var away = PlayerPresence.FormatAway(p, now, _characterName, IsReady);
                var last = PlayerPresence.BestLastSeen(p);
                return (object)new
                {
                    guid = p.Guid,
                    name = p.Name,
                    display = string.IsNullOrWhiteSpace(p.Name) ? p.Guid : p.Name,
                    messageCount = p.MessageCount,
                    level = p.Level,
                    classId = p.ClassId,
                    race = p.Race,
                    guild = p.Guild,
                    presence = p.Presence,
                    lastSeenUtc = p.LastSeenUtc,
                    lastWhoUtc = p.LastWhoUtc,
                    away,
                    offlineFor = p.Presence == PlayerPresence.Online ? null : away,
                    lastSeenAgoSeconds = last is null ? (long?)null : Math.Max(0, (long)(now - last.Value).TotalSeconds)
                };
            })
            .ToList();

    /// <summary>Channel names remembered for rejoin (joined + known, minus invalid).</summary>
    public IReadOnlyList<string> GetRememberedChannels() =>
        _joinedConfirmed.Keys
            .Concat(_channels.Keys)
            .Where(c => !_invalidJoinNames.ContainsKey(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Lookup a roster player with live presence + away text (WHO/whois).</summary>
    public object? WhoisLocal(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
        {
            return null;
        }
        if (HeadlessClient.Infrastructure.Config.HiddenOperators.IsHiddenName(name))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (!_players.TryGetValue(name, out var entry))
        {
            return null;
        }

        var presence = PlayerPresence.Compute(entry, now, _characterName, IsReady);
        var enriched = entry with { Presence = presence };
        var last = PlayerPresence.BestLastSeen(enriched);
        var away = PlayerPresence.FormatAway(enriched, now, _characterName, IsReady);
        return new
        {
            ok = true,
            name = enriched.Name,
            guild = enriched.Guild,
            level = enriched.Level,
            classId = enriched.ClassId,
            race = enriched.Race,
            zoneId = enriched.ZoneId,
            guid = enriched.Guid,
            messageCount = enriched.MessageCount,
            presence,
            away,
            offlineFor = presence == PlayerPresence.Online ? null : away,
            lastSeenUtc = enriched.LastSeenUtc,
            lastWhoUtc = enriched.LastWhoUtc,
            lastSeenAgoSeconds = last is null ? (long?)null : Math.Max(0, (long)(now - last.Value).TotalSeconds),
            online = presence == PlayerPresence.Online
        };
    }

    public IReadOnlyList<string> GetChannels()
    {
        var list = _joinedConfirmed.Keys
            .Concat(_channels.Keys)
            .Where(c => !_invalidJoinNames.ContainsKey(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_invalidJoinNames.Count > 0 || _channels.ContainsKey(MiscChannel))
        {
            if (!list.Any(c => c.Equals(MiscChannel, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(MiscChannel);
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }

        return list;
    }

    public IReadOnlyList<string> GetJoinedChannels() =>
        _joinedConfirmed.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

    public object GetChannelsDetail() => new
    {
        ok = true,
        joined = GetJoinedChannels(),
        pending = _channels.Keys
            .Where(c => !_joinedConfirmed.ContainsKey(c) && !_invalidJoinNames.ContainsKey(c))
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        invalid = _invalidJoinNames.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList(),
        misc = MiscChannel,
        display = GetChannels()
    };

    public object GetStatus() => new
    {
        ok = true,
        ready = IsReady,
        character = _characterName,
        account = _accountTag,
        outboundEnabled = OutboundEnabled,
        playerCount = _players.Count,
        rosterPath = _roster?.PathUsed,
        lastWhoUtc = _lastWhoBatchUtc == DateTimeOffset.MinValue ? (DateTimeOffset?)null : _lastWhoBatchUtc,
        channels = GetChannels(),
        joinedChannels = GetJoinedChannels(),
        invalidChannels = _invalidJoinNames.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList(),
        miscChannel = MiscChannel,
        attached = GetAttachedSessions(),
        outboundAccount = string.IsNullOrEmpty(_outboundAccountTag) ? _accountTag : _outboundAccountTag,
        socialLines = _chat.SocialCount,
        version = Version
    };

    public async Task JoinChannelAsync(string channel, CancellationToken cancellationToken)
    {
        channel = (channel ?? string.Empty).Trim();
        if (channel.Length == 0)
        {
            throw new ArgumentException("Channel name required.");
        }

        var ids = GuessChannelIds(channel);
        foreach (var id in ids)
        {
            await JoinChannelAsync(id, channel, cancellationToken).ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private static uint[] GuessChannelIds(string channel) =>
        channel.Trim().ToLowerInvariant() switch
        {
            "general" => [0u, 1u],
            "trade" => [0u, 2u],
            "localdefense" => [0u, 22u],
            "worlddefense" => [0u, 23u],
            "guildrecruitment" => [0u, 24u, 25u],
            "lookingforgroup" or "lfg" => [0u, 26u],
            _ => [0u]
        };

    public async Task JoinChannelAsync(uint channelId, string channel, CancellationToken cancellationToken)
    {
        var world = RequireWorld();
        channel = (channel ?? string.Empty).Trim();
        if (channel.Length == 0)
        {
            throw new ArgumentException("Channel name required.");
        }

        await world.SendAsync(ChatMessageBuilder.BuildJoinChannel(channelId, channel), cancellationToken)
            .ConfigureAwait(false);
        // Optimistic list; SMSG_CHANNEL_NOTIFY confirms / rejects.
        _channels[channel] = 1;
        ScheduleChannelSave();
        Bump();
    }

    /// <summary>
    /// Join every configured / default Ascension channel.
    /// Retries with TBC layout when Ascension replies INVALID_NAME (0x1B) to the WotLK layout.
    /// </summary>
    public async Task JoinAllChannelsAsync(
        IEnumerable<string>? extraNames,
        CancellationToken cancellationToken)
    {
        if (!await _joinAllGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            Console.WriteLine($"[{_characterName}] join-all already running — skip overlapping call");
            return;
        }

        try
        {
            await JoinAllChannelsCoreAsync(extraNames, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _joinAllGate.Release();
        }
    }

    private async Task JoinAllChannelsCoreAsync(
        IEnumerable<string>? extraNames,
        CancellationToken cancellationToken)
    {
        var world = RequireWorld();
        _invalidJoinNames.Clear();

        // Warm the channel system with channels that always work, then SoftRealm customs.
        var warm = new[] { "World", "Global", "Hardcore", "Services", "LFG", "LookingForGuild" };
        var softRealm = new[] { "Ascension", "Newcomers" };
        var extras = (extraNames ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Where(n => !warm.Contains(n, StringComparer.OrdinalIgnoreCase)
                        && !softRealm.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var zoneProbes = new (uint Id, string Name)[]
        {
            (0, "General"), (1, "General"),
            (0, "LocalDefense"), (22, "LocalDefense"),
            (0, "WorldDefense"), (23, "WorldDefense"),
            (0, "Trade"), (2, "Trade"), (5, "Trade"),
            (0, "GuildRecruitment"), (24, "GuildRecruitment"),
            (0, "LookingForGroup"), (26, "LookingForGroup"),
        };

        async Task SendWotlkAsync(string name)
        {
            if (_joinedConfirmed.ContainsKey(name))
            {
                return;
            }

            _invalidJoinNames.TryRemove(name, out _);
            await world.SendAsync(ChatMessageBuilder.BuildJoinChannel(0, name), cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[{_characterName}] join → {name} (id=0)");
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }

        async Task SendTbcAsync(string name)
        {
            if (_joinedConfirmed.ContainsKey(name))
            {
                return;
            }

            _invalidJoinNames.TryRemove(name, out _);
            await world.SendAsync(ChatMessageBuilder.BuildJoinChannelTbc(name), cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[{_characterName}] join TBC → {name}");
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }

        // 1) Warm customs first so Ascension channel service is ready.
        foreach (var name in warm)
        {
            await SendWotlkAsync(name).ConfigureAwait(false);
        }

        await Task.Delay(2500, cancellationToken).ConfigureAwait(false);

        foreach (var name in warm.Where(n => !_joinedConfirmed.ContainsKey(n)))
        {
            await SendTbcAsync(name).ConfigureAwait(false);
        }

        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);

        // 2) SoftRealm primary channels after warm confirms.
        foreach (var name in softRealm.Concat(extras))
        {
            await SendWotlkAsync(name).ConfigureAwait(false);
        }

        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);

        foreach (var name in softRealm.Concat(extras).Where(n => !_joinedConfirmed.ContainsKey(n)))
        {
            await SendTbcAsync(name).ConfigureAwait(false);
        }

        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

        // 3) One more SoftRealm attempt (server sometimes rejects the first probe).
        foreach (var name in softRealm)
        {
            if (_joinedConfirmed.ContainsKey(name))
            {
                _invalidJoinNames.TryRemove(name, out _);
                continue;
            }

            await SendWotlkAsync(name).ConfigureAwait(false);
            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            if (!_joinedConfirmed.ContainsKey(name))
            {
                await SendTbcAsync(name).ConfigureAwait(false);
            }
        }

        // Ascension often ACKs SoftRealm customs several seconds after the join CMSG.
        await Task.Delay(4000, cancellationToken).ConfigureAwait(false);

        // 4) Zone DBC probes — quiet failures.
        foreach (var (id, name) in zoneProbes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_joinedConfirmed.ContainsKey(name))
            {
                continue;
            }

            try
            {
                await world.SendAsync(ChatMessageBuilder.BuildJoinChannel(id, name), cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine($"[{_characterName}] join zone → {name} (id={id})");
            }
            catch
            {
                // ignore
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            if (_joinedConfirmed.ContainsKey(name))
            {
                _invalidJoinNames.TryRemove(name, out _);
            }
        }

        foreach (var name in softRealm.Where(n => _joinedConfirmed.ContainsKey(n)))
        {
            _invalidJoinNames.TryRemove(name, out _);
        }

        if (_invalidJoinNames.Count > 0)
        {
            _channels[MiscChannel] = 1;
            ChannelsChanged?.Invoke();
            Bump();
        }

        Console.WriteLine(
            $"[{_characterName}] channel join pass complete joined=[{string.Join(',', _joinedConfirmed.Keys.OrderBy(k => k))}] invalid=[{string.Join(',', _invalidJoinNames.Keys.OrderBy(k => k))}]");
    }

    public async Task RequestChannelListAsync(string channel, CancellationToken cancellationToken)
    {
        var world = RequireWorld();
        channel = (channel ?? string.Empty).Trim();
        if (channel.Length == 0)
        {
            return;
        }

        await world.SendAsync(ChannelListCodec.BuildRequest(channel), cancellationToken)
            .ConfigureAwait(false);
    }

    public void RequestNameQueryPublic(string guidHex) => RequestNameQuery(guidHex);

    public async Task RefreshWhoAsync(string? filter, CancellationToken cancellationToken)
    {
        var world = RequireWorld();
        await world.SendAsync(WhoPacketCodec.BuildRequest(filter), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SendAsync(
        string typeName,
        string message,
        string? channel,
        string? whisperTarget,
        CancellationToken cancellationToken)
        => SendWorldAsync(typeName, message, channel, whisperTarget, requireOutbound: true, cancellationToken);

    /// <summary>
    /// Wooz Discord middleman send — does not arm website outbound.
    /// Website ListenOnly stays in force for the public chatroom.
    /// </summary>
    public Task SendRelayAsync(
        string typeName,
        string message,
        string? channel,
        string? whisperTarget,
        CancellationToken cancellationToken)
        => SendWorldAsync(typeName, message, channel, whisperTarget, requireOutbound: false, cancellationToken);

    public Task SendPacketAsync(Packet packet, CancellationToken cancellationToken)
        => RequireWorld().SendAsync(packet, cancellationToken);

    private async Task SendWorldAsync(
        string typeName,
        string message,
        string? channel,
        string? whisperTarget,
        bool requireOutbound,
        CancellationToken cancellationToken)
    {
        if (requireOutbound && !OutboundEnabled)
        {
            throw new InvalidOperationException(
                "In-game send is disabled. Enable outbound in the chatroom first.");
        }

        var world = RequireWorld();
        message = (message ?? string.Empty).Trim();
        if (message.Length == 0)
        {
            throw new ArgumentException("Message is empty.");
        }

        if (ContainsForbiddenLeak(message))
        {
            throw new InvalidOperationException("Message rejected.");
        }

        var type = ChatTypes.Parse(typeName);
        string? extra = null;
        if (type == ChatTypes.Whisper)
        {
            extra = (whisperTarget ?? string.Empty).Trim();
            if (extra.Length == 0)
            {
                throw new ArgumentException("Whisper target required.");
            }
        }
        else if (type == ChatTypes.Channel)
        {
            extra = (channel ?? string.Empty).Trim();
            if (extra.Length == 0)
            {
                throw new ArgumentException("Channel name required.");
            }

            _channels[extra] = 1;
        }

        await world.SendAsync(ChatMessageBuilder.Build(type, message, extra), cancellationToken)
            .ConfigureAwait(false);

        var echoChannel = type == ChatTypes.Channel
            ? extra!
            : type == ChatTypes.Whisper
                ? $"to:{extra}"
                : ChatTypes.Name(type);
        var echo = new ChatLine(
            DateTimeOffset.UtcNow,
            type,
            "0",
            _characterName,
            echoChannel,
            message,
            ReadableText: $"{_characterName}: {message}",
            Direction: type == ChatTypes.Whisper ? "out" : "out");
        _chat.Append(echo);
    }

    private void OnPacket(Packet packet)
    {
        if (packet.Opcode == ChannelNotifyCodec.Opcode || packet.Opcode == Opcodes.SmsgChannelNotify)
        {
            if (ChannelNotifyCodec.TryParse(packet.Payload.Span, out var type, out var channel))
            {
                var desc = ChannelNotifyCodec.Describe(type);
                Console.WriteLine($"[{_characterName}] channel notify {desc} #{channel}");
                if (!string.IsNullOrWhiteSpace(channel))
                {
                    if (ChannelNotifyCodec.IsJoined(type))
                    {
                        _joinedConfirmed[channel] = 1;
                        _channels[channel] = 1;
                        _invalidJoinNames.TryRemove(channel, out _);
                        ScheduleChannelSave();
                        // Keep SoftRealm chat clean — join noise belongs in the console/status bar.
                        ChannelsChanged?.Invoke();
                    }
                    else if (type == ChannelNotifyCodec.InvalidName)
                    {
                        // Duplicate join layouts (id probes / parallel join-all) often emit
                        // INVALID_NAME after a successful YouJoined. Never demote a live channel.
                        if (_joinedConfirmed.ContainsKey(channel))
                        {
                            Console.WriteLine(
                                $"[{_characterName}] ignore invalid_name for already-joined #{channel}");
                        }
                        else
                        {
                            _invalidJoinNames[channel] = 1;
                            _channels.TryRemove(channel, out _);
                            ScheduleChannelSave();
                            // Console only — never dump join failures into SoftRealm chat.
                            ChannelsChanged?.Invoke();
                        }
                    }
                    else if (type == ChannelNotifyCodec.YouLeft)
                    {
                        _joinedConfirmed.TryRemove(channel, out _);
                        ScheduleChannelSave();
                        ChannelsChanged?.Invoke();
                    }
                }

                Bump();
                PlayersChanged?.Invoke();
            }

            return;
        }

        if (packet.Opcode == NameQueryCodec.SmsgNameQueryResponse)
        {
            if (NameQueryCodec.TryParseResponse(
                    packet.Payload.Span,
                    out var guid,
                    out var name,
                    out var race,
                    out var gender,
                    out var classId))
            {
                var hex = guid.ToString("X16");
                _guidToName[hex] = name;
                _nameQueryPending.TryRemove(guid, out _);
                NotePlayer(name, hex, null, race, classId, gender);
                if (_chat is IObservableChatLog obs)
                {
                    obs.ApplySenderName(hex, name);
                }

                PlayersChanged?.Invoke();
                Bump();
            }

            return;
        }

        if (packet.Opcode == WhoPacketCodec.SmsgWho)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                _lastWhoBatchUtc = now;
                var entries = WhoPacketCodec.ParseResponse(packet.Payload.Span);
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Name))
                    {
                        continue;
                    }

                    _players.AddOrUpdate(
                        e.Name,
                        _ => e with
                        {
                            LastSeenUtc = now,
                            LastWhoUtc = now,
                            Presence = PlayerPresence.Online
                        },
                        (_, old) => e with
                        {
                            MessageCount = old.MessageCount,
                            Guid = string.IsNullOrWhiteSpace(e.Guid) ? old.Guid : e.Guid,
                            LastSeenUtc = now,
                            LastWhoUtc = now,
                            Presence = PlayerPresence.Online
                        });
                }

                ScheduleRosterSave();
                PlayersChanged?.Invoke();
                Bump();
            }
            catch
            {
                // ignore malformed who
            }

            return;
        }

        if (packet.Opcode is Opcodes.SmsgMessageChat or Opcodes.SmsgGmMessageChat)
        {
            if (ChatPacketDecoder.TryDecode(packet.Payload.Span, out var line))
            {
                var enriched = EnrichIncoming(line);
                NoteChannel(enriched);
                // Persist here too — fleet WorldInboundProjector also Appends; ChatDedupeIndex
                // collapses the double-hear. This keeps SoftRealm fed if projector order changes.
                _chat.Append(enriched with
                {
                    Scope = string.IsNullOrWhiteSpace(enriched.Scope) ? "shared" : enriched.Scope,
                    ObserverAccount = string.IsNullOrWhiteSpace(enriched.ObserverAccount)
                        ? _accountTag
                        : enriched.ObserverAccount
                });
            }
            else if ((Interlocked.Increment(ref _chatDecodeFails) % 20) == 1)
            {
                Console.WriteLine(
                    $"[{_characterName}] MESSAGECHAT decode miss opcode=0x{packet.Opcode:X4} len={packet.Payload.Length}");
            }
        }
    }

    private void OnChatLine(ChatLine line)
    {
        NoteChannel(line);
        if (!string.IsNullOrWhiteSpace(line.Sender)
            && !line.Sender.Equals(_characterName, StringComparison.OrdinalIgnoreCase))
        {
            NotePlayer(line.Sender, line.SenderGuid, line);
        }

        if (line.Type is ChatTypes.WhisperInform
            && line.Channel.StartsWith("to:", StringComparison.OrdinalIgnoreCase))
        {
            var target = line.Channel[3..].Trim();
            if (!string.IsNullOrWhiteSpace(target))
            {
                NotePlayer(target, line.TargetGuid, line);
            }
        }

        ChatPushed?.Invoke(line);
        Bump();
    }

    private void NotePlayer(
        string name,
        string? guid,
        ChatLine? line,
        int race = -1,
        int classId = -1,
        int gender = -1)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _players.AddOrUpdate(
            name,
            _ => new WhoEntry(
                name,
                line?.Guild ?? "",
                line?.Level ?? -1,
                classId >= 0 ? classId : line?.ClassId ?? -1,
                race >= 0 ? race : line?.Race ?? -1,
                0,
                gender >= 0 ? (byte)gender : (byte)0,
                guid ?? "",
                MessageCount: line is null ? 0 : 1,
                LastSeenUtc: now,
                LastWhoUtc: null,
                Presence: PlayerPresence.Recent),
            (_, old) => old with
            {
                MessageCount = old.MessageCount + (line is null ? 0 : 1),
                Guid = string.IsNullOrWhiteSpace(old.Guid) ? (guid ?? "") : old.Guid,
                ClassId = old.ClassId < 0 && classId >= 0 ? classId : old.ClassId,
                Race = old.Race < 0 && race >= 0 ? race : old.Race,
                Guild = string.IsNullOrWhiteSpace(old.Guild) ? (line?.Guild ?? "") : old.Guild,
                LastSeenUtc = now
            });
        ScheduleRosterSave();
        PlayersChanged?.Invoke();
    }

    private void LoadRoster()
    {
        if (_roster is null)
        {
            return;
        }

        foreach (var entry in _roster.Load())
        {
            _players.TryAdd(entry.Name, entry);
            if (!string.IsNullOrWhiteSpace(entry.Guid) && !string.IsNullOrWhiteSpace(entry.Name))
            {
                _guidToName[entry.Guid] = entry.Name;
            }
        }
    }

    private void ScheduleRosterSave()
    {
        if (_roster is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);
                lock (_saveGate)
                {
                    _roster.Flush(_players.Values);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _saveScheduled, 0);
            }
        });
    }

    private static int PresenceRank(string presence) => presence switch
    {
        PlayerPresence.Online => 0,
        PlayerPresence.Recent => 1,
        _ => 2
    };

    private void RequestNameQuery(string guidHex)
    {
        if (string.IsNullOrWhiteSpace(guidHex))
        {
            return;
        }

        if (!ulong.TryParse(guidHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var guid)
            || guid == 0)
        {
            return;
        }

        if (!_nameQueryPending.TryAdd(guid, 1))
        {
            return;
        }

        IWorldClient? world;
        lock (_gate)
        {
            world = _world is { State: SessionState.InWorld } ? _world : null;
        }

        if (world is null)
        {
            _nameQueryPending.TryRemove(guid, out _);
            return;
        }

        _ = world.SendAsync(NameQueryCodec.BuildRequest(guid), CancellationToken.None);
    }

    private void NoteChannel(ChatLine line)
    {
        if (!string.IsNullOrWhiteSpace(line.Channel)
            && !line.Channel.StartsWith("to:", StringComparison.OrdinalIgnoreCase)
            && line.Type == ChatTypes.Channel
            && !line.Channel.Equals("CHANNEL", StringComparison.OrdinalIgnoreCase))
        {
            var ch = NormalizeChannelName(line.Channel);
            _channels[ch] = 1;
            ScheduleChannelSave();
            ChannelsChanged?.Invoke();
        }
    }

    private void LoadChannels()
    {
        if (_channelsStore is null)
        {
            return;
        }

        var snap = _channelsStore.Load();
        foreach (var name in snap.Known.Concat(snap.Joined))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _channels[name.Trim()] = 1;
            }
        }

        // Never seed _joinedConfirmed or _invalidJoinNames from disk.
        // Saved names are only remembered for re-CMSG_JOIN; seeding either set made
        // SoftRealm skip Ascension/Newcomers or treat them as permanently invalid.
    }

    private void ScheduleChannelSave()
    {
        // Channel lists are not persisted — SoftRealm saves chat + users/members only.
    }

    private string NormalizeChannelName(string channel)
    {
        channel = (channel ?? "").Trim();
        if (channel.Length == 0)
        {
            return MiscChannel;
        }

        if (_invalidJoinNames.ContainsKey(channel))
        {
            return MiscChannel;
        }

        if (!IsValidChannelName(channel))
        {
            _invalidJoinNames[channel] = 1;
            return MiscChannel;
        }

        return channel;
    }

    private static bool IsValidChannelName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 31)
        {
            return false;
        }

        if (name.Equals(MiscChannel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c) || c is '|' or '\n' or '\r')
            {
                return false;
            }
        }

        return name.Any(char.IsLetterOrDigit);
    }

    private IWorldClient RequireWorld()
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_outboundAccountTag)
                && _sessions.TryGetValue(_outboundAccountTag, out var sess)
                && sess.World.State == SessionState.InWorld)
            {
                _world = sess.World;
                _characterName = sess.Character;
                _accountTag = sess.Account;
                return sess.World;
            }

            if (_world is null || _world.State != SessionState.InWorld)
            {
                foreach (var attached in _sessions.Values)
                {
                    if (attached.World.State == SessionState.InWorld)
                    {
                        _world = attached.World;
                        _characterName = attached.Character;
                        _accountTag = attached.Account;
                        return attached.World;
                    }
                }

                throw new InvalidOperationException("Proxy character is not in the world yet.");
            }

            return _world;
        }
    }

    private static bool ContainsForbiddenLeak(string message)
    {
        var m = message.ToLowerInvariant();
        return m.Contains("website")
            || m.Contains("chatroom")
            || m.Contains("mediator")
            || m.Contains("headless")
            || m.Contains("from the web");
    }

    private void Bump() => Interlocked.Increment(ref _version);
}
