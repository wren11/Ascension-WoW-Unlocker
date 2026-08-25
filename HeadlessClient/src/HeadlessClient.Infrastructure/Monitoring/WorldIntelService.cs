using System.Collections.Concurrent;
using System.Globalization;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Chat;
using HeadlessClient.Infrastructure.Protocol;
using HeadlessClient.Infrastructure.Query;

namespace HeadlessClient.Infrastructure.Monitoring;

/// <summary>
/// Shared world intelligence: object manager mirror, packet-derived events,
/// Lua event subscriptions, and startup visibility probes.
/// </summary>
public sealed class WorldIntelService
{
    private readonly IObjectDirectory _objects;
    private readonly QueryCache _queries;
    private readonly IAddonHost? _addons;
    private readonly PlayerProfileService? _profiles;
    private readonly ConcurrentQueue<WorldEvent> _events = new();
    private readonly ConcurrentDictionary<string, byte> _luaSubs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _luaHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private IWorldClient? _world;
    private string _character = "";
    private const int MaxEvents = 2000;

    public WorldIntelService(
        IObjectDirectory objects,
        QueryCache queries,
        IAddonHost? addons = null,
        PlayerProfileService? profiles = null)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _addons = addons;
        _profiles = profiles;
    }

    public IObjectDirectory Objects => _objects;

    public event Action<WorldEvent>? EventPushed;
    public event Action? ObjectsChanged;

    public void Attach(IWorldClient world, string character)
    {
        ArgumentNullException.ThrowIfNull(world);
        lock (_gate)
        {
            if (_world is not null)
            {
                _world.PacketReceived -= OnPacket;
            }

            _world = world;
            _character = character?.Trim() ?? "";
            _world.PacketReceived += OnPacket;
        }

        Push("session", "attached", new { character = _character });
    }

    public void Detach(IWorldClient world)
    {
        lock (_gate)
        {
            if (_world != world)
            {
                return;
            }

            _world.PacketReceived -= OnPacket;
            _world = null;
        }

        Push("session", "detached", new { character = _character });
    }

    public IReadOnlyList<object> GetObjects(string? typeFilter = null, int limit = 500, bool includeStatic = false, bool aliveOnly = true)
    {
        limit = Math.Clamp(limit, 1, 5000);
        var q = (typeFilter ?? "").Trim().ToLowerInvariant();
        var list = new List<object>();
        foreach (var o in _objects.Snapshot().OrderByDescending(x => x.LastSeenUtc))
        {
            if (!includeStatic && o.Source == "static")
            {
                continue;
            }

            if (aliveOnly && !o.Alive && o.Source != "static")
            {
                // Still show recently-seen cold cache briefly (identity-rich rows).
                if (o.LastSeenUtc < DateTimeOffset.UtcNow.AddMinutes(-30)
                    && string.IsNullOrWhiteSpace(o.Name)
                    && string.IsNullOrWhiteSpace(o.StaticName))
                {
                    continue;
                }
            }

            var typeId = o.TypeId != 0 ? o.TypeId : InferTypeId(o.Guid);
            var typeName = TypeNameOf(typeId);
            if (!string.IsNullOrEmpty(q) && q is not ("*" or "all")
                && !typeName.Contains(q, StringComparison.OrdinalIgnoreCase)
                && q != typeId.ToString(CultureInfo.InvariantCulture)
                && !(o.Source?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            var guidHex = o.Guid.ToString("X16", CultureInfo.InvariantCulture);
            var name = o.Name ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                name = _queries.TryGetCachedName(guidHex) ?? "";
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _objects.ApplyIdentity(o.Guid, name);
                }
            }

            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(o.StaticName))
            {
                name = o.StaticName!;
            }

            if (o.Entry != 0 && string.IsNullOrWhiteSpace(o.StaticName))
            {
                var sn = _queries.TryGetStaticName("creature", o.Entry)
                         ?? _queries.TryGetStaticName("gameobject", o.Entry)
                         ?? _queries.TryGetStaticName("item", o.Entry);
                if (!string.IsNullOrWhiteSpace(sn))
                {
                    _objects.ApplyIdentity(o.Guid, name, o.Entry, sn);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = sn!;
                    }
                }
            }

            PlayerProfile? profile = null;
            if ((typeId == 4 || InferTypeId(o.Guid) == 4) && _profiles is not null)
            {
                if (_profiles.TryGet(o.Guid, out var p) && p is not null)
                {
                    profile = p;
                    if (string.IsNullOrWhiteSpace(name) && p.HasName)
                    {
                        name = p.Name;
                        _objects.ApplyIdentity(o.Guid, name);
                    }
                }
            }

            var px = o.X; var py = o.Y; var pz = o.Z; var po = o.Orientation;
            var posOk = UpdateObjectProjector.IsSaneWorldPosition(px, py, pz);
            if (!posOk)
            {
                px = py = pz = po = 0;
            }

            list.Add(new
            {
                guid = guidHex,
                guidNum = o.Guid.ToString(CultureInfo.InvariantCulture),
                name,
                entry = o.Entry,
                x = px,
                y = py,
                z = pz,
                o = po,
                hasPosition = posOk && (Math.Abs(px) > 0.01f || Math.Abs(py) > 0.01f || Math.Abs(pz) > 0.01f),
                health = o.Health,
                maxHealth = o.MaxHealth,
                typeId,
                typeName,
                display = string.IsNullOrWhiteSpace(name) ? $"{typeName} {guidHex[^4..]}" : name,
                lastSeenUtc = o.LastSeenUtc,
                firstSeenUtc = o.FirstSeenUtc,
                seenBy = o.SeenBy ?? Array.Empty<string>(),
                staticName = o.StaticName,
                alive = o.Alive,
                source = o.Source,
                mapId = o.MapId,
                // Packet-intel enrichment (WHO / inspect / name-query).
                level = profile?.Level is > 0 ? profile.Level : (int?)null,
                classId = profile?.ClassId is >= 0 ? profile.ClassId : (int?)null,
                race = profile?.Race is >= 0 ? profile.Race : (int?)null,
                guild = !string.IsNullOrWhiteSpace(profile?.Guild) ? profile!.Guild : null,
                zoneId = profile?.ZoneId is > 0 ? profile.ZoneId : (int?)null,
                honorHk = profile?.Honor is null ? (uint?)null : profile.Honor.LifetimeHonorableKills,
                arenaBest = profile?.Arena is { Count: > 0 }
                    ? (int?)profile.Arena.Max(a => a.PersonalRating)
                    : (int?)null,
                itemCount = profile?.ItemIds.Count > 0 ? profile.ItemIds.Count : (int?)null,
                talentRanks = profile?.Talents.Count > 0
                    ? profile.Talents.Sum(t => t.Rank)
                    : (int?)null,
                dossier = profile is not null && (profile.HasName || profile.Level > 0 || profile.ItemIds.Count > 0)
            });
            if (list.Count >= limit)
            {
                break;
            }
        }

        return list;
    }

    public object GetObjectSummary()
    {
        var snap = _objects.Snapshot();
        var live = snap.Where(o => o.Source != "static").ToList();
        var byType = live.GroupBy(o => TypeNameOf(o.TypeId != 0 ? o.TypeId : InferTypeId(o.Guid)))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var aggregate = _objects is InMemoryObjectDirectory om
            ? om.GetAggregateSummary()
            : null;
        return new
        {
            ok = true,
            total = live.Count,
            alive = live.Count(o => o.Alive),
            staticTemplates = snap.Count(o => o.Source == "static"),
            byType,
            character = _character,
            luaSubscriptions = _luaSubs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            eventCount = _events.Count,
            aggregate,
            shared = true,
            path = (_objects as InMemoryObjectDirectory)?.PathUsed
        };
    }

    public IReadOnlyList<WorldEvent> RecentEvents(int max = 200)
    {
        max = Math.Clamp(max, 1, MaxEvents);
        return _events.Reverse().Take(max).ToList();
    }

    public void SubscribeLua(string eventName)
    {
        eventName = (eventName ?? "").Trim();
        if (eventName.Length == 0)
        {
            throw new ArgumentException("eventName required");
        }

        _luaSubs[eventName] = 1;
        Push("lua", "subscribe", new { eventName });
    }

    /// <summary>
    /// Replace packet-mapped Lua subscriptions with the exact set needed by enabled/loaded addons.
    /// Preserves non-packet manual handlers (e.g. PLAYER_LOGIN) that are not opcode-mapped.
    /// </summary>
    public void ReplacePacketLuaSubscriptions(IEnumerable<string> events)
    {
        var keep = new HashSet<string>(
            (events ?? Array.Empty<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var key in _luaSubs.Keys.ToList())
        {
            if (IsPacketMappedLuaEvent(key) && !keep.Contains(key))
            {
                _luaSubs.TryRemove(key, out _);
                _luaHandlers.TryRemove(key, out _);
            }
        }

        foreach (var ev in keep)
        {
            _luaSubs[ev] = 1;
        }

        Push("lua", "scope", new { events = keep.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList() });
    }

    public static bool IsPacketMappedLuaEvent(string eventName) =>
        eventName.Equals("WORLD_OBJECT_UPDATE", StringComparison.OrdinalIgnoreCase)
        || eventName.Equals("CHAT_MSG", StringComparison.OrdinalIgnoreCase)
        || eventName.Equals("CHAT_MSG_CHANNEL_NOTICE", StringComparison.OrdinalIgnoreCase)
        || eventName.Equals("TIME_PLAYED_MSG", StringComparison.OrdinalIgnoreCase)
        || eventName.StartsWith("CHAT_MSG_", StringComparison.OrdinalIgnoreCase);

    public void UnsubscribeLua(string eventName)
    {
        eventName = (eventName ?? "").Trim();
        _luaSubs.TryRemove(eventName, out _);
        _luaHandlers.TryRemove(eventName, out _);
        Push("lua", "unsubscribe", new { eventName });
    }

    public void RegisterLua(string eventName, string? script)
    {
        eventName = (eventName ?? "").Trim();
        if (eventName.Length == 0)
        {
            throw new ArgumentException("eventName required");
        }

        _luaSubs[eventName] = 1;
        if (!string.IsNullOrWhiteSpace(script))
        {
            _luaHandlers[eventName] = script!;
        }

        Push("lua", "register", new { eventName, hasScript = !string.IsNullOrWhiteSpace(script) });
    }

    public IReadOnlyList<object> GetLuaSubscriptions() =>
        _luaSubs.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => (object)new
            {
                eventName = k,
                hasHandler = _luaHandlers.ContainsKey(k)
            })
            .ToList();

    public async Task FireLuaAsync(string eventName, CancellationToken cancellationToken)
    {
        eventName = (eventName ?? "").Trim();
        if (eventName.Length == 0)
        {
            throw new ArgumentException("eventName required");
        }

        Push("lua", "fire", new { eventName });
        if (_addons is not null)
        {
            await _addons.FireEventAsync(eventName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Startup visibility pass — queries the server for data the UI normally hides.</summary>
    public async Task RunStartupProbesAsync(
        ChatMediator mediator,
        CancellationToken cancellationToken)
    {
        Push("probe", "startup_begin", new { character = _character });

        try
        {
            await mediator.RefreshWhoAsync(null, cancellationToken).ConfigureAwait(false);
            Push("probe", "who", new { ok = true });
        }
        catch (Exception ex)
        {
            Push("probe", "who", new { ok = false, error = ex.Message });
        }

        try
        {
            await _queries.RefreshMailAsync(cancellationToken).ConfigureAwait(false);
            Push("probe", "mail", _queries.GetStatusExtras());
        }
        catch (Exception ex)
        {
            Push("probe", "mail", new { ok = false, error = ex.Message });
        }

        try
        {
            IWorldClient? world;
            lock (_gate)
            {
                world = _world;
            }

            if (world is not null)
            {
                await world.SendAsync(new Packet(0x01CE, Array.Empty<byte>()), cancellationToken)
                    .ConfigureAwait(false); // CMSG_QUERY_TIME
                Push("probe", "query_time", new { ok = true });
            }
        }
        catch (Exception ex)
        {
            Push("probe", "query_time", new { ok = false, error = ex.Message });
        }

        foreach (var ch in mediator.GetJoinedChannels())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await mediator.RequestChannelListAsync(ch, cancellationToken).ConfigureAwait(false);
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore per-channel
            }
        }

        // Name-resolve nearby GUIDs so Object Manager shows identities.
        var named = 0;
        foreach (var obj in _objects.Snapshot().Take(80))
        {
            if (!string.IsNullOrWhiteSpace(obj.Name))
            {
                continue;
            }

            try
            {
                mediator.RequestNameQueryPublic(obj.Guid.ToString("X16", CultureInfo.InvariantCulture));
                named++;
            }
            catch
            {
                // ignore
            }
        }

        Push("probe", "startup_done", new
        {
            objects = _objects.Snapshot().Count,
            nameQueries = named,
            channels = mediator.GetChannels().Count
        });
        ObjectsChanged?.Invoke();
    }

    public void NotifyObjectsChanged() => ObjectsChanged?.Invoke();

    private void OnPacket(Packet packet)
    {
        // Surface otherwise-invisible world traffic into the intel feed.
        switch (packet.Opcode)
        {
            case Opcodes.SmsgUpdateObject:
            case Opcodes.SmsgCompressedUpdateObject:
                ObjectsChanged?.Invoke();
                if (_profiles is not null)
                {
                    foreach (var o in _objects.Snapshot())
                    {
                        if (InferTypeId(o.Guid) == 4 || o.TypeId == 4)
                        {
                            _profiles.NoteSeenPlayer(o.Guid, o.Name, _character);
                        }
                    }
                }

                break;
            case Opcodes.SmsgNotification:
                Push("smsg", "notification", new { len = packet.Payload.Length, hex = PreviewHex(packet.Payload.Span) });
                break;
            case 0x01F3: // SMSG_SPELL_GO-ish / zone related varies — keep generic
            case 0x01F5:
                Push("smsg", OpcodeName(packet.Opcode), new { len = packet.Payload.Length });
                break;
            case 0x009B: // SMSG_CHANNEL_LIST
                Push("channel", "list", new { len = packet.Payload.Length, hex = PreviewHex(packet.Payload.Span, 64) });
                break;
            case Opcodes.SmsgChannelNotify:
                break; // ChatMediator owns this
            case 0x01CF: // SMSG_QUERY_TIME_RESPONSE
                if (packet.Payload.Length >= 4)
                {
                    var t = BitConverter.ToUInt32(packet.Payload.Span[..4]);
                    Push("world", "server_time", new { unix = t });
                }

                break;
            case 0x00AE: // SMSG_MESSAGECHAT handled elsewhere
                break;
            default:
                // Sample rare/large system packets that often carry hidden GM/server data.
                if (packet.Payload.Length >= 32
                    && packet.Opcode is >= 0x0500 and <= 0x0A00)
                {
                    Push("smsg", OpcodeName(packet.Opcode), new
                    {
                        opcode = packet.Opcode,
                        len = packet.Payload.Length,
                        hex = PreviewHex(packet.Payload.Span, 48)
                    });
                }

                break;
        }

        // Fan out to Lua subscribers for known mapping.
        var mapped = MapOpcodeToLuaEvent(packet.Opcode);
        if (mapped is not null && _luaSubs.ContainsKey(mapped))
        {
            _ = FireLuaAsync(mapped, CancellationToken.None);
        }
    }

    private static string? MapOpcodeToLuaEvent(uint opcode) => opcode switch
    {
        Opcodes.SmsgUpdateObject or Opcodes.SmsgCompressedUpdateObject => "WORLD_OBJECT_UPDATE",
        Opcodes.SmsgMessageChat or Opcodes.SmsgGmMessageChat => "CHAT_MSG",
        Opcodes.SmsgChannelNotify => "CHAT_MSG_CHANNEL_NOTICE",
        0x01CF => "TIME_PLAYED_MSG",
        _ => null
    };

    private static string TypeNameOf(WorldObject o) => TypeNameOf(o.TypeId != 0 ? o.TypeId : InferTypeId(o.Guid));

    private static string TypeNameOf(byte typeId) => typeId switch
    {
        1 => "Item",
        2 => "Container",
        3 => "Unit",
        4 => "Player",
        5 => "GameObject",
        6 => "DynamicObject",
        7 => "Corpse",
        _ => "Object"
    };

    public static byte InferTypeId(ulong guid)
    {
        var high = (guid >> 48) & 0xFFFF;
        return high switch
        {
            0x0000 => 4,
            0xF130 => 3,
            0xF150 => 3,
            0xF110 => 5,
            0xF111 => 5,
            0xF100 => 6,
            0xF101 => 7,
            0x4000 => 1,
            _ when (high & 0x00F0) == 0x0040 => 1,
            _ => 0
        };
    }

    private void Push(string category, string name, object? data)
    {
        var ev = new WorldEvent(DateTimeOffset.UtcNow, category, name, data);
        _events.Enqueue(ev);
        while (_events.Count > MaxEvents && _events.TryDequeue(out _))
        {
        }

        EventPushed?.Invoke(ev);
    }

    private static string PreviewHex(ReadOnlySpan<byte> data, int max = 32)
    {
        var n = Math.Min(data.Length, max);
        return Convert.ToHexString(data[..n]);
    }

    private static string OpcodeName(uint opcode) => $"0x{opcode:X4}";
}

public sealed record WorldEvent(
    DateTimeOffset At,
    string Category,
    string Name,
    object? Data);
