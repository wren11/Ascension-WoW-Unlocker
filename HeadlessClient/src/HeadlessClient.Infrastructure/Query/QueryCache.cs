using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.Session;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Monitoring;
using HeadlessClient.Infrastructure.Probe;
using HeadlessClient.Infrastructure.Protocol;

namespace HeadlessClient.Infrastructure.Query;

/// <summary>
/// Live CMSG query → SMSG decode cache for chatroom tooltips, dossiers, and mail.
/// Attaches to the fleet world client (same pattern as OpcodeProbeService).
/// </summary>
public sealed class QueryCache
{
    public const uint CmsgItemQuerySingle = 0x0056;
    public const uint SmsgItemQuerySingleResponse = 0x0058;
    public const uint SmsgPatchItem = 0x0932;
    public const uint CmsgQuestQuery = 0x005C;
    public const uint SmsgQuestQueryResponse = 0x005D;
    public const uint CmsgGameobjectQuery = 0x005E;
    public const uint SmsgGameobjectQueryResponse = 0x005F;
    public const uint CmsgCreatureQuery = 0x0060;
    public const uint SmsgCreatureQueryResponse = 0x0061;
    public const uint CmsgNpcTextQuery = 0x017F;
    public const uint SmsgNpcTextUpdate = 0x0180;
    public const uint MsgQueryNextMailTime = 0x0284;
    public const uint CmsgQueryTime = 0x01CE;
    public const uint SmsgQueryTimeResponse = 0x01CF;

    private static readonly Regex ChatLinkRx = new(
        @"\|c(?<color>[0-9a-fA-F]{8})\|H(?<kind>[a-zA-Z]+):(?<payload>[^|]+)\|h\[(?<label>[^\]]*)\]\|h\|r",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareLinkRx = new(
        @"\|H(?<kind>[a-zA-Z]+):(?<payload>[^|]+)\|h\[(?<label>[^\]]*)\]\|h",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly PlayerDirectory _players;
    private readonly IObjectDirectory _objects;
    private readonly GameDataCatalog? _catalog;
    private readonly object _gate = new();
    private IWorldClient? _world;
    private string _character = "";

    private readonly ConcurrentDictionary<uint, CachedEntity> _items = new();
    private readonly ConcurrentDictionary<uint, CachedEntity> _quests = new();
    private readonly ConcurrentDictionary<uint, CachedEntity> _creatures = new();
    private readonly ConcurrentDictionary<uint, CachedEntity> _gameObjects = new();
    private readonly ConcurrentDictionary<string, CachedEntity> _namesByGuid =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<CachedEntity>> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private MailStatusSnapshot _mail = new(false, null, null, "detached");
    private long _lastPingSentTick;
    private int _latencyMs = -1;
    private uint _pingSeq = 1;
    private long _serverTimeUnix;
    private long _serverTimeLocalTick;

    public QueryCache(PlayerDirectory players, IObjectDirectory objects, GameDataCatalog? catalog = null)
    {
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _catalog = catalog;
    }

    public void Attach(IWorldClient world, string characterName)
    {
        ArgumentNullException.ThrowIfNull(world);
        lock (_gate)
        {
            if (_world is not null)
            {
                _world.PacketReceived -= OnPacket;
            }

            _world = world;
            _character = characterName?.Trim() ?? "";
            _world.PacketReceived += OnPacket;
        }

        _ = RefreshMailAsync(CancellationToken.None);
        _ = MeasureLatencyAsync(CancellationToken.None);
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

        _mail = new MailStatusSnapshot(false, null, null, "detached");
        foreach (var kv in _pending)
        {
            kv.Value.TrySetCanceled();
        }

        _pending.Clear();
    }

    public object GetStatusExtras() => new
    {
        latencyMs = _latencyMs,
        serverTimeUnix = _serverTimeUnix,
        mail = _mail,
        cache = new
        {
            items = _items.Count,
            quests = _quests.Count,
            creatures = _creatures.Count,
            gameObjects = _gameObjects.Count,
            names = _namesByGuid.Count
        }
    };

    public MailStatusSnapshot GetMailStatus() => _mail;

    public async Task<MailStatusSnapshot> RefreshMailAsync(CancellationToken cancellationToken)
    {
        var world = TryWorld();
        if (world is null)
        {
            _mail = new MailStatusSnapshot(false, null, null, "not_in_world");
            return _mail;
        }

        var key = "mail:next";
        var tcs = new TaskCompletionSource<CachedEntity>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        try
        {
            await world.SendAsync(new Packet(MsgQueryNextMailTime, ReadOnlyMemory<byte>.Empty), cancellationToken)
                .ConfigureAwait(false);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(1500, cancellationToken)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                _mail = new MailStatusSnapshot(false, null, DateTimeOffset.UtcNow, "timeout");
                return _mail;
            }

            var ent = await tcs.Task.ConfigureAwait(false);
            var hasMail = InferHasMail(ent);
            _mail = new MailStatusSnapshot(hasMail, ent.Raw, DateTimeOffset.UtcNow, "ok");
            return _mail;
        }
        catch (Exception ex)
        {
            _mail = new MailStatusSnapshot(false, null, DateTimeOffset.UtcNow, ex.Message);
            return _mail;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public async Task<int> MeasureLatencyAsync(CancellationToken cancellationToken)
    {
        var world = TryWorld();
        if (world is null)
        {
            return _latencyMs;
        }

        var seq = Interlocked.Increment(ref _pingSeq);
        var key = $"ping:{seq}";
        var tcs = new TaskCompletionSource<CachedEntity>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        var payload = new byte[8];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), seq);
        _lastPingSentTick = Environment.TickCount64;
        try
        {
            await world.SendAsync(new Packet(Opcodes.CmsgPing, payload), cancellationToken).ConfigureAwait(false);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000, cancellationToken)).ConfigureAwait(false);
            if (completed == tcs.Task)
            {
                _latencyMs = (int)Math.Max(0, Environment.TickCount64 - _lastPingSentTick);
            }
        }
        catch
        {
            // keep last
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }

        return _latencyMs;
    }

    /// <summary>Scrape chat for |Hitem:/quest:/…| links and warm the cache.</summary>
    public void NoteChatText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (Match m in ChatLinkRx.Matches(text))
        {
            PrefetchLink(m.Groups["kind"].Value, m.Groups["payload"].Value);
        }

        foreach (Match m in BareLinkRx.Matches(text))
        {
            PrefetchLink(m.Groups["kind"].Value, m.Groups["payload"].Value);
        }
    }

    private void PersistEntity(CachedEntity ent, string source)
    {
        if (_catalog is null || ent.Id == 0 || !string.IsNullOrEmpty(ent.Error))
        {
            return;
        }

        try
        {
            _catalog.UpsertFromQueryDto(ent.Kind, ent.Id, ToDto(ent), source);
            if (ent.Found && ent.Kind is "item" or "quest" or "creature" or "gameobject")
            {
                _catalog.NoteInterest(ent.Kind, ent.Id, source);
            }
        }
        catch
        {
            // never break live query path
        }
    }

    private void QueueChatInterest(string kind, uint id)
    {
        _catalog?.NoteInterest(kind, id, "chat");
    }

    public async Task<object> GetItemAsync(uint id, CancellationToken cancellationToken)
    {
        if (_items.TryGetValue(id, out var hit) && hit.Fresh)
        {
            return ToDto(hit);
        }

        return ToDto(await QueryAsync(
            $"item:{id}",
            CmsgItemQuerySingle,
            U32(id),
            cancellationToken,
            () => _items.TryGetValue(id, out var c) ? c : null).ConfigureAwait(false));
    }

    public async Task<object> GetQuestAsync(uint id, CancellationToken cancellationToken)
    {
        if (_quests.TryGetValue(id, out var hit) && hit.Fresh)
        {
            return ToDto(hit);
        }

        return ToDto(await QueryAsync(
            $"quest:{id}",
            CmsgQuestQuery,
            U32(id),
            cancellationToken,
            () => _quests.TryGetValue(id, out var c) ? c : null).ConfigureAwait(false));
    }

    public async Task<object> GetCreatureAsync(uint entry, CancellationToken cancellationToken)
    {
        if (_creatures.TryGetValue(entry, out var hit) && hit.Fresh)
        {
            return ToDto(hit);
        }

        return ToDto(await QueryAsync(
            $"creature:{entry}",
            CmsgCreatureQuery,
            EntryGuid(entry, 0),
            cancellationToken,
            () => _creatures.TryGetValue(entry, out var c) ? c : null).ConfigureAwait(false));
    }

    public async Task<object> GetGameObjectAsync(uint entry, CancellationToken cancellationToken)
    {
        if (_gameObjects.TryGetValue(entry, out var hit) && hit.Fresh)
        {
            return ToDto(hit);
        }

        return ToDto(await QueryAsync(
            $"go:{entry}",
            CmsgGameobjectQuery,
            EntryGuid(entry, 0),
            cancellationToken,
            () => _gameObjects.TryGetValue(entry, out var c) ? c : null).ConfigureAwait(false));
    }

    public object? GetCachedItem(uint id) =>
        _items.TryGetValue(id, out var c) ? ToDto(c) : null;

    public object? GetCachedCreature(uint entry) =>
        _creatures.TryGetValue(entry, out var c) ? ToDto(c) : null;

    public object? GetCachedGameObject(uint entry) =>
        _gameObjects.TryGetValue(entry, out var c) ? ToDto(c) : null;

    public object GetPlayerDossier(string nameOrGuid, IReadOnlyList<WhoEntry> who)
    {
        nameOrGuid = (nameOrGuid ?? "").Trim();
        WhoEntry? whoHit = who.FirstOrDefault(w =>
            string.Equals(w.Name, nameOrGuid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w.Guid, nameOrGuid, StringComparison.OrdinalIgnoreCase));

        PlayerInfo? dir = null;
        if (_players.TryGetByName(nameOrGuid, out var byName))
        {
            dir = byName;
        }
        else if (_players.TryGetByGuid(nameOrGuid, out var byGuid))
        {
            dir = byGuid;
        }

        CachedEntity? nameCache = null;
        if (whoHit is not null && !string.IsNullOrWhiteSpace(whoHit.Guid)
            && _namesByGuid.TryGetValue(whoHit.Guid, out var nc))
        {
            nameCache = nc;
        }
        else if (_namesByGuid.TryGetValue(nameOrGuid, out var nc2))
        {
            nameCache = nc2;
        }

        var displayName = whoHit?.Name ?? dir?.Name ?? nameOrGuid;
        return new
        {
            ok = true,
            name = displayName,
            guid = whoHit?.Guid ?? dir?.Guid ?? nameCache?.Id.ToString("X16", CultureInfo.InvariantCulture) ?? "",
            level = whoHit?.Level ?? dir?.Level ?? -1,
            classId = whoHit?.ClassId ?? dir?.ClassId ?? nameCache?.ClassId ?? -1,
            race = whoHit?.Race ?? dir?.Race ?? nameCache?.Race ?? -1,
            gender = whoHit?.Gender ?? (byte)(dir?.Gender ?? 0),
            guild = whoHit?.Guild ?? dir?.Guild ?? "",
            zoneId = whoHit?.ZoneId ?? 0,
            zone = dir?.Zone ?? "",
            realm = dir?.Realm ?? nameCache?.Realm ?? "",
            messageCount = whoHit?.MessageCount ?? 0,
            nameQuery = nameCache is null ? null : ToDto(nameCache),
            self = string.Equals(displayName, _character, StringComparison.OrdinalIgnoreCase)
        };
    }

    public static IReadOnlyList<object> BuildWhisperThreads(IEnumerable<ChatLine> lines, string? selfName)
    {
        var threads = new Dictionary<string, WhisperThreadAccum>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.OrderBy(l => l.ReceivedAt))
        {
            if (!TryWhisperPeer(line, selfName, out var peer, out var direction))
            {
                continue;
            }

            if (!threads.TryGetValue(peer, out var t))
            {
                t = new WhisperThreadAccum(peer);
                threads[peer] = t;
            }

            t.Messages.Add(new
            {
                receivedAt = line.ReceivedAt,
                direction,
                sender = line.Sender,
                channel = line.Channel,
                message = line.Message,
                readableText = line.ReadableText
            });
            t.LastAt = line.ReceivedAt;
            t.Count++;
        }

        return threads.Values
            .OrderByDescending(t => t.LastAt)
            .Select(t => (object)new
            {
                peer = t.Peer,
                count = t.Count,
                lastAt = t.LastAt,
                preview = t.Messages.Count > 0
                    ? ((dynamic)t.Messages[^1]).message ?? ((dynamic)t.Messages[^1]).readableText
                    : "",
                messages = t.Messages
            })
            .ToList();
    }

    public static bool TryWhisperPeer(ChatLine line, string? selfName, out string peer, out string direction)
    {
        peer = "";
        direction = "";
        var ch = line.Channel ?? "";
        var type = line.Type;
        var isWhisper = type is 7 or 8 or 9
            || ch.Contains("WHISPER", StringComparison.OrdinalIgnoreCase)
            || ch.StartsWith("to:", StringComparison.OrdinalIgnoreCase)
            || line.Direction is "in" or "out";
        if (!isWhisper)
        {
            return false;
        }

        if (line.Direction == "out" || type == 9 || ch.StartsWith("to:", StringComparison.OrdinalIgnoreCase))
        {
            direction = "out";
            peer = ch.StartsWith("to:", StringComparison.OrdinalIgnoreCase)
                ? ch[3..].Trim()
                : (line.Sender ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peer) || string.Equals(peer, selfName, StringComparison.OrdinalIgnoreCase))
            {
                // WhisperInform sometimes puts target in ReadableText "You whisper to X: …"
                peer = ExtractWhisperTarget(line) ?? peer;
            }

            return peer.Length > 0;
        }

        direction = "in";
        peer = (line.Sender ?? "").Trim();
        return peer.Length > 0;
    }

    private static string? ExtractWhisperTarget(ChatLine line)
    {
        var text = line.ReadableText ?? line.Message ?? "";
        const string marker = "to ";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var rest = text[(idx + marker.Length)..];
        var colon = rest.IndexOf(':');
        if (colon > 0)
        {
            rest = rest[..colon];
        }

        return rest.Trim();
    }

    private void PrefetchLink(string kind, string payload)
    {
        var idPart = payload.Split(':')[0];
        if (!uint.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id == 0)
        {
            return;
        }

        var k = kind.ToLowerInvariant();
        if (k is "item" or "quest" or "creature" or "gameobject" or "npc")
        {
            if (k == "npc")
            {
                k = "creature";
            }

            QueueChatInterest(k, id);
        }

        _ = k switch
        {
            "item" => GetItemAsync(id, CancellationToken.None),
            "quest" => GetQuestAsync(id, CancellationToken.None),
            "creature" => GetCreatureAsync(id, CancellationToken.None),
            "gameobject" => GetGameObjectAsync(id, CancellationToken.None),
            _ => Task.CompletedTask
        };
    }

    private async Task<CachedEntity> QueryAsync(
        string key,
        uint opcode,
        byte[] body,
        CancellationToken cancellationToken,
        Func<CachedEntity?> tryCache)
    {
        var existing = tryCache();
        if (existing is { Fresh: true })
        {
            return existing;
        }

        var world = TryWorld();
        if (world is null)
        {
            return existing ?? CachedEntity.Missing(key, "not_in_world");
        }

        var tcs = new TaskCompletionSource<CachedEntity>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, tcs))
        {
            if (_pending.TryGetValue(key, out var wait))
            {
                try
                {
                    return await wait.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return tryCache() ?? CachedEntity.Missing(key, "timeout");
                }
            }
        }

        try
        {
            await world.SendAsync(new Packet(opcode, body), cancellationToken).ConfigureAwait(false);
            // Items often get SMSG_PATCH_ITEM first; Ascension can delay 0x0058 a few seconds.
            var waitMs = opcode == CmsgItemQuerySingle ? 5500 : 2500;
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(waitMs, cancellationToken)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                return tryCache() ?? CachedEntity.Missing(key, "timeout");
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CachedEntity.Missing(key, ex.Message);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private void OnPacket(Packet pkt)
    {
        try
        {
            HandlePacket(pkt);
        }
        catch
        {
            // never break world pump
        }
    }

    private void HandlePacket(Packet pkt)
    {
        var payload = pkt.Payload.Span;
        if (pkt.Opcode == Opcodes.SmsgPong && payload.Length >= 4)
        {
            var seq = BitConverter.ToUInt32(payload);
            CompletePending($"ping:{seq}", new CachedEntity
            {
                Kind = "pong",
                Id = seq,
                Name = "pong",
                UpdatedAt = DateTimeOffset.UtcNow,
                Raw = new { sequence = seq }
            });
            if (_lastPingSentTick > 0)
            {
                _latencyMs = (int)Math.Max(0, Environment.TickCount64 - _lastPingSentTick);
            }

            return;
        }

        if (pkt.Opcode == SmsgQueryTimeResponse)
        {
            var decoded = ProbePacketFormatter.TryDecode(pkt.Opcode, "SMSG_QUERY_TIME_RESPONSE", payload);
            if (decoded is not null)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(decoded));
                if (doc.RootElement.TryGetProperty("time", out var t))
                {
                    _serverTimeUnix = t.GetUInt32();
                    _serverTimeLocalTick = Environment.TickCount64;
                }
            }

            return;
        }

        if (pkt.Opcode == MsgQueryNextMailTime)
        {
            var decoded = ProbePacketFormatter.TryDecode(pkt.Opcode, "MSG_QUERY_NEXT_MAIL_TIME", payload)
                          ?? new { kind = "MSG_QUERY_NEXT_MAIL_TIME", hex = Convert.ToHexString(payload) };
            var ent = new CachedEntity
            {
                Kind = "mail",
                Name = "next_mail_time",
                UpdatedAt = DateTimeOffset.UtcNow,
                Raw = decoded,
                Hex = Convert.ToHexString(payload)
            };
            CompletePending("mail:next", ent);
            _mail = new MailStatusSnapshot(InferHasMail(ent), decoded, DateTimeOffset.UtcNow, "ok");
            return;
        }

        if (pkt.Opcode == Opcodes.SmsgNameQueryResponse)
        {
            var decoded = ProbePacketFormatter.TryDecode(pkt.Opcode, "SMSG_NAME_QUERY_RESPONSE", payload);
            if (decoded is null)
            {
                return;
            }

            var ent = FromDecoded("name", decoded);
            if (!string.IsNullOrWhiteSpace(ent.GuidHex))
            {
                _namesByGuid[ent.GuidHex] = ent;
                _players.Observe(
                    ent.GuidHex,
                    ent.Name,
                    classId: ent.ClassId,
                    race: ent.Race,
                    gender: ent.Gender,
                    realm: ent.Realm);
                if (ulong.TryParse(ent.GuidHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
                    && !string.IsNullOrWhiteSpace(ent.Name))
                {
                    _objects.ApplyIdentity(g, ent.Name);
                }
            }

            CompletePending($"name:{ent.GuidHex}", ent);
            return;
        }

        if (pkt.Opcode is SmsgItemQuerySingleResponse or SmsgPatchItem)
        {
            var decoded = ProbePacketFormatter.TryDecode(
                pkt.Opcode,
                pkt.Opcode == SmsgPatchItem ? "SMSG_PATCH_ITEM" : "SMSG_ITEM_QUERY_SINGLE_RESPONSE",
                payload);
            if (decoded is null)
            {
                return;
            }

            var ent = FromDecoded("item", decoded);
            if (ent.Id == 0)
            {
                return;
            }

            if (pkt.Opcode == SmsgPatchItem)
            {
                if (_items.TryGetValue(ent.Id, out var prev) && prev.Found && !string.IsNullOrWhiteSpace(prev.Name))
                {
                    // Keep a good classic tip; just attach the patch blob.
                    _items[ent.Id] = prev with { Patch = decoded, UpdatedAt = DateTimeOffset.UtcNow };
                    return;
                }

                var patched = ent with
                {
                    Patch = decoded,
                    // PATCH alone is not a classic tip — only mark found when we scraped a name.
                    Found = !string.IsNullOrWhiteSpace(ent.Name) || ent.Strings.Count > 0,
                    Name = !string.IsNullOrWhiteSpace(ent.Name)
                        ? ent.Name
                        : (ent.Strings.Count > 0 ? ent.Strings[0] : "")
                };
                _items[ent.Id] = patched;

                // Unblock waiters only when the patch carried something useful; else keep waiting for 0x0058.
                if (patched.Found)
                {
                    CompletePending($"item:{ent.Id}", patched);
                }

                return;
            }

            if (_items.TryGetValue(ent.Id, out var existing) && existing.Patch is not null)
            {
                ent = ent with { Patch = existing.Patch };
            }

            _items[ent.Id] = ent;
            CompletePending($"item:{ent.Id}", ent);
            PersistEntity(ent, "packet");
            if (!string.IsNullOrWhiteSpace(ent.Name))
            {
                _objects.UpsertStatic("item", ent.Id, ent.Name, typeId: 1);
            }

            return;
        }

        if (pkt.Opcode == SmsgQuestQueryResponse)
        {
            StoreDecoded("quest", pkt.Opcode, "SMSG_QUEST_QUERY_RESPONSE", payload, _quests, id => $"quest:{id}");
            return;
        }

        if (pkt.Opcode == SmsgCreatureQueryResponse)
        {
            StoreDecoded("creature", pkt.Opcode, "SMSG_CREATURE_QUERY_RESPONSE", payload, _creatures, id => $"creature:{id}");
            return;
        }

        if (pkt.Opcode == SmsgGameobjectQueryResponse)
        {
            StoreDecoded("gameobject", pkt.Opcode, "SMSG_GAMEOBJECT_QUERY_RESPONSE", payload, _gameObjects, id => $"go:{id}");
            return;
        }

        if (pkt.Opcode == SmsgNpcTextUpdate)
        {
            var decoded = ProbePacketFormatter.TryDecode(pkt.Opcode, "SMSG_NPC_TEXT_UPDATE", payload);
            if (decoded is not null)
            {
                var ent = FromDecoded("npctext", decoded);
                CompletePending($"npctext:{ent.Id}", ent);
            }
        }
    }

    private void StoreDecoded(
        string kind,
        uint opcode,
        string name,
        ReadOnlySpan<byte> payload,
        ConcurrentDictionary<uint, CachedEntity> map,
        Func<uint, string> pendingKey)
    {
        var decoded = ProbePacketFormatter.TryDecode(opcode, name, payload);
        if (decoded is null)
        {
            return;
        }

        var ent = FromDecoded(kind, decoded);
        if (ent.Id == 0)
        {
            return;
        }

        map[ent.Id] = ent;
        CompletePending(pendingKey(ent.Id), ent);
        PersistEntity(ent, "packet");
        if (!string.IsNullOrWhiteSpace(ent.Name))
        {
            _objects.UpsertStatic(kind, ent.Id, ent.Name);
        }
    }

    /// <summary>Resolve a cached name by GUID hex (Object Manager enrichment).</summary>
    public string? TryGetCachedName(string guidHex)
    {
        guidHex = (guidHex ?? "").Trim();
        if (guidHex.Length == 0)
        {
            return null;
        }

        if (_namesByGuid.TryGetValue(guidHex, out var n) && !string.IsNullOrWhiteSpace(n.Name))
        {
            return n.Name;
        }

        return null;
    }

    public string? TryGetStaticName(string kind, uint entry)
    {
        var map = kind.ToLowerInvariant() switch
        {
            "item" => _items,
            "quest" => _quests,
            "gameobject" or "go" => _gameObjects,
            _ => _creatures
        };
        return map.TryGetValue(entry, out var e) && !string.IsNullOrWhiteSpace(e.Name) ? e.Name : null;
    }

    private void CompletePending(string key, CachedEntity ent)
    {
        if (_pending.TryRemove(key, out var tcs))
        {
            tcs.TrySetResult(ent);
        }
    }

    private static CachedEntity FromDecoded(string kind, object decoded)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(decoded));
        var root = doc.RootElement;
        uint id = 0;
        if (root.TryGetProperty("itemId", out var itemId))
        {
            id = itemId.GetUInt32();
        }
        else if (root.TryGetProperty("questId", out var questId))
        {
            id = questId.GetUInt32();
        }
        else if (root.TryGetProperty("entry", out var entry))
        {
            id = entry.GetUInt32();
        }
        else if (root.TryGetProperty("textId", out var textId))
        {
            id = textId.GetUInt32();
        }
        else if (root.TryGetProperty("u32_0", out var u0) && kind is "item" or "mail")
        {
            id = u0.GetUInt32();
        }

        var name = "";
        if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            name = n.GetString() ?? "";
        }
        else if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
        {
            name = title.GetString() ?? "";
        }
        else if (root.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array
                 && names.GetArrayLength() > 0)
        {
            name = names[0].GetString() ?? "";
        }

        if (string.IsNullOrWhiteSpace(name) && root.TryGetProperty("strings", out var strings)
            && strings.ValueKind == JsonValueKind.Array && strings.GetArrayLength() > 0)
        {
            name = strings[0].GetString() ?? "";
        }

        var guidHex = "";
        if (root.TryGetProperty("guid", out var g) && g.ValueKind == JsonValueKind.String)
        {
            guidHex = PlayerDirectory.NormGuid(g.GetString());
        }

        int race = -1, gender = -1, classId = -1;
        if (root.TryGetProperty("race", out var r) && r.TryGetInt32(out var ri))
        {
            race = ri;
        }

        if (root.TryGetProperty("gender", out var ge) && ge.TryGetInt32(out var gi))
        {
            gender = gi;
        }

        if (root.TryGetProperty("classId", out var cl) && cl.TryGetInt32(out var ci))
        {
            classId = ci;
        }

        var realm = root.TryGetProperty("realm", out var realmEl) && realmEl.ValueKind == JsonValueKind.String
            ? realmEl.GetString() ?? ""
            : "";

        var found = true;
        if (root.TryGetProperty("found", out var foundEl) && foundEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            found = foundEl.GetBoolean();
        }

        if (root.TryGetProperty("notFound", out var nf) && nf.ValueKind == JsonValueKind.True)
        {
            found = false;
        }

        var stringsList = new List<string>();
        if (root.TryGetProperty("strings", out var strArr) && strArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in strArr.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.String)
                {
                    var v = s.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        stringsList.Add(v!);
                    }
                }
            }
        }

        return new CachedEntity
        {
            Kind = kind,
            Id = id,
            GuidHex = guidHex,
            Name = name,
            Realm = realm,
            Race = race,
            Gender = gender,
            ClassId = classId,
            Found = found,
            Strings = stringsList,
            UpdatedAt = DateTimeOffset.UtcNow,
            Raw = decoded
        };
    }

    private static bool InferHasMail(CachedEntity ent)
    {
        // WotLK: all-FF float usually means "no mail"; Ascension returned 00C0A8C7… in probe.
        if (string.IsNullOrWhiteSpace(ent.Hex) || ent.Hex.Length < 8)
        {
            return false;
        }

        return !ent.Hex.StartsWith("FFFFFFFF", StringComparison.OrdinalIgnoreCase)
               && !ent.Hex.StartsWith("00000000", StringComparison.OrdinalIgnoreCase);
    }

    private static object ToDto(CachedEntity e)
    {
        int quality = -1, itemLevel = -1, requiredLevel = -1, bonding = -1, inventoryType = -1, armor = 0;
        int buyPrice = 0, sellPrice = 0, delay = 0, flags = 0, flags2 = 0, displayId = 0, itemClass = 0, itemSubClass = 0;
        bool notFound = !e.Found;
        string? qualityColor = null, qualityName = null, bondingName = null, inventoryTypeName = null, description = null;
        string? packetKind = null;
        object? stats = null, damages = null;

        if (e.Raw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(e.Raw));
                var r = doc.RootElement;
                if (r.TryGetProperty("kind", out var pk) && pk.ValueKind == JsonValueKind.String)
                {
                    packetKind = pk.GetString();
                }

                if (r.TryGetProperty("quality", out var q) && q.TryGetInt32(out var qi))
                {
                    quality = qi;
                }

                if (r.TryGetProperty("itemLevel", out var il) && il.TryGetInt32(out var ili))
                {
                    itemLevel = ili;
                }

                if (r.TryGetProperty("requiredLevel", out var rl) && rl.TryGetInt32(out var rli))
                {
                    requiredLevel = rli;
                }

                if (r.TryGetProperty("bonding", out var b) && b.TryGetInt32(out var bi))
                {
                    bonding = bi;
                }

                if (r.TryGetProperty("inventoryType", out var it) && it.TryGetInt32(out var iti))
                {
                    inventoryType = iti;
                }

                if (r.TryGetProperty("armor", out var ar) && ar.TryGetInt32(out var ari))
                {
                    armor = ari;
                }

                if (r.TryGetProperty("buyPrice", out var bp) && bp.TryGetInt32(out var bpi))
                {
                    buyPrice = bpi;
                }

                if (r.TryGetProperty("sellPrice", out var sp) && sp.TryGetInt32(out var spi))
                {
                    sellPrice = spi;
                }

                if (r.TryGetProperty("delay", out var dy) && dy.TryGetInt32(out var dyi))
                {
                    delay = dyi;
                }

                if (r.TryGetProperty("flags", out var fl) && fl.TryGetInt32(out var fli))
                {
                    flags = fli;
                }

                if (r.TryGetProperty("flags2", out var fl2) && fl2.TryGetInt32(out var fl2i))
                {
                    flags2 = fl2i;
                }

                if (r.TryGetProperty("displayId", out var did) && did.TryGetInt32(out var didi))
                {
                    displayId = didi;
                }

                if (r.TryGetProperty("itemClass", out var ic) && ic.TryGetInt32(out var ici))
                {
                    itemClass = ici;
                }

                if (r.TryGetProperty("itemSubClass", out var isc) && isc.TryGetInt32(out var isci))
                {
                    itemSubClass = isci;
                }

                if (r.TryGetProperty("notFound", out var nf) && nf.ValueKind == JsonValueKind.True)
                {
                    notFound = true;
                }

                qualityColor = r.TryGetProperty("qualityColor", out var qc) && qc.ValueKind == JsonValueKind.String
                    ? qc.GetString()
                    : null;
                qualityName = r.TryGetProperty("qualityName", out var qn) && qn.ValueKind == JsonValueKind.String
                    ? qn.GetString()
                    : null;
                bondingName = r.TryGetProperty("bondingName", out var bn) && bn.ValueKind == JsonValueKind.String
                    ? bn.GetString()
                    : null;
                inventoryTypeName = r.TryGetProperty("inventoryTypeName", out var itn)
                    && itn.ValueKind == JsonValueKind.String
                    ? itn.GetString()
                    : null;
                description = r.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                    ? desc.GetString()
                    : null;
                if (r.TryGetProperty("stats", out var st))
                {
                    stats = JsonSerializer.Deserialize<object>(st.GetRawText());
                }

                if (r.TryGetProperty("damages", out var dm))
                {
                    damages = JsonSerializer.Deserialize<object>(dm.GetRawText());
                }
            }
            catch
            {
                // keep tip fields unset
            }
        }

        // Prefer packet kind label for items so SoftRealm shows SMSG_ITEM_QUERY_SINGLE_RESPONSE tips.
        var kindLabel = !string.IsNullOrWhiteSpace(packetKind)
            ? packetKind
            : e.Kind;

        return new
        {
            ok = e.Found,
            kind = kindLabel,
            entityKind = e.Kind,
            id = e.Id,
            itemId = e.Kind == "item" ? e.Id : 0u,
            guid = e.GuidHex,
            name = e.Name,
            realm = e.Realm,
            race = e.Race,
            gender = e.Gender,
            classId = e.ClassId,
            found = e.Found,
            notFound,
            strings = e.Strings,
            updatedAt = e.UpdatedAt,
            patch = e.Patch,
            detail = e.Raw,
            error = e.Error,
            quality,
            qualityName,
            qualityColor,
            itemLevel,
            requiredLevel,
            bonding,
            bondingName,
            inventoryType,
            inventoryTypeName,
            armor,
            buyPrice,
            sellPrice,
            delay,
            flags,
            flags2,
            displayId,
            itemClass,
            itemSubClass,
            description,
            stats,
            damages
        };
    }

    private IWorldClient? TryWorld()
    {
        lock (_gate)
        {
            return _world is { State: SessionState.InWorld } ? _world : null;
        }
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BitConverter.TryWriteBytes(b, v);
        return b;
    }

    private static byte[] EntryGuid(uint entry, ulong guid)
    {
        var b = new byte[12];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), entry);
        BitConverter.TryWriteBytes(b.AsSpan(4, 8), guid);
        return b;
    }

    private sealed class WhisperThreadAccum
    {
        public WhisperThreadAccum(string peer) => Peer = peer;
        public string Peer { get; }
        public int Count { get; set; }
        public DateTimeOffset LastAt { get; set; }
        public List<object> Messages { get; } = new();
    }
}

public sealed record MailStatusSnapshot(
    bool HasMail,
    object? Detail,
    DateTimeOffset? CheckedAt,
    string State);

public sealed record CachedEntity
{
    public string Kind { get; init; } = "";
    public uint Id { get; init; }
    public string GuidHex { get; init; } = "";
    public string Name { get; init; } = "";
    public string Realm { get; init; } = "";
    public int Race { get; init; } = -1;
    public int Gender { get; init; } = -1;
    public int ClassId { get; init; } = -1;
    public bool Found { get; init; } = true;
    public IReadOnlyList<string> Strings { get; init; } = Array.Empty<string>();
    public DateTimeOffset UpdatedAt { get; init; }
    public object? Raw { get; init; }
    public object? Patch { get; init; }
    public string Hex { get; init; } = "";
    public string? Error { get; init; }

    public bool Fresh => string.IsNullOrEmpty(Error)
                         && Found
                         && !string.IsNullOrWhiteSpace(Name)
                         && (DateTimeOffset.UtcNow - UpdatedAt) < TimeSpan.FromMinutes(10);

    public static CachedEntity Missing(string key, string error)
    {
        var kind = key;
        uint id = 0;
        var colon = key.IndexOf(':');
        if (colon > 0)
        {
            kind = key[..colon];
            _ = uint.TryParse(key[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }

        return new()
        {
            Kind = kind,
            Id = id,
            Found = false,
            Error = error,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
