using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.Session;
using HeadlessClient.Infrastructure.Chat;
using HeadlessClient.Infrastructure.Protocol;
using HeadlessClient.Infrastructure.Monitoring;

namespace HeadlessClient.Infrastructure.Query;

/// <summary>
/// Inspect players → persist profiles/builds → derive ladders for the chatroom.
/// </summary>
public sealed class PlayerProfileService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, PlayerProfile> _byGuid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _guidByName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ulong> _autoQueue = new();
    private readonly ConcurrentDictionary<ulong, long> _lastInspectTick = new();
    private readonly object _io = new();
    private readonly string _path;
    private readonly ChatMediator _mediator;
    private IWorldClient? _world;
    private int _saveScheduled;
    private int _pumpRunning;

    public PlayerProfileService(ChatMediator mediator, string? path = null)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeadlessClient",
                "player-profiles.json")
            : Path.GetFullPath(path);
        Load();
    }

    public event Action? ProfilesChanged;

    public string PathUsed => _path;

    public void Attach(IWorldClient world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            _world.PacketReceived -= OnPacket;
        }

        _world = world;
        _world.PacketReceived += OnPacket;
    }

    public void Detach(IWorldClient world)
    {
        if (_world != world)
        {
            return;
        }

        _world.PacketReceived -= OnPacket;
        _world = null;
    }

    public async Task InspectAsync(ulong guid, string? nameHint = null, string? sourceAccount = null, CancellationToken ct = default)
    {
        if (guid == 0)
        {
            throw new ArgumentException("guid required");
        }

        var world = RequireWorld();
        EnsureStub(guid, nameHint, sourceAccount);
        await world.SendAsync(InspectCodec.BuildSetSelection(guid), ct).ConfigureAwait(false);
        await Task.Delay(40, ct).ConfigureAwait(false);
        await world.SendAsync(InspectCodec.BuildInspect(guid), ct).ConfigureAwait(false);
        await Task.Delay(40, ct).ConfigureAwait(false);
        await world.SendAsync(InspectCodec.BuildInspectHonor(guid), ct).ConfigureAwait(false);
        await Task.Delay(40, ct).ConfigureAwait(false);
        await world.SendAsync(InspectCodec.BuildInspectArena(guid), ct).ConfigureAwait(false);
        await Task.Delay(40, ct).ConfigureAwait(false);
        await world.SendAsync(InspectCodec.BuildInspectAchievements(guid), ct).ConfigureAwait(false);
        _lastInspectTick[guid] = Environment.TickCount64;
    }

    public async Task InspectByNameAsync(string name, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("name required");
        }

        if (_guidByName.TryGetValue(name, out var gHex)
            && ulong.TryParse(gHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var known))
        {
            await InspectAsync(known, name, null, ct).ConfigureAwait(false);
            return;
        }

        var who = _mediator.GetPlayers()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (who is not null && !string.IsNullOrWhiteSpace(who.Guid)
            && ulong.TryParse(who.Guid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fromWho))
        {
            await InspectAsync(fromWho, name, null, ct).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException($"No GUID for '{name}' yet — open Object Manager or wait for WHO.");
    }

    public void NoteSeenPlayer(ulong guid, string? name = null, string? sourceAccount = null)
    {
        if (guid == 0 || WorldIntelService.InferTypeId(guid) != 4)
        {
            return;
        }

        EnsureStub(guid, name, sourceAccount);
        var now = Environment.TickCount64;
        if (_lastInspectTick.TryGetValue(guid, out var last) && now - last < 60_000)
        {
            return;
        }

        _autoQueue.Enqueue(guid);
        StartPump();
    }

    public IReadOnlyList<object> GetProfiles(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 2000);
        foreach (var p in _byGuid.Values)
        {
            if (!p.HasName || p.Level <= 0 || p.ClassId < 0)
            {
                EnrichFromWho(p);
            }
        }

        return _byGuid.Values
            .OrderByDescending(p => p.UpdatedAtUtc)
            .Take(limit)
            .Select(ToDto)
            .ToList();
    }

    public object? GetProfile(string nameOrGuid)
    {
        nameOrGuid = (nameOrGuid ?? "").Trim();
        if (nameOrGuid.Length == 0)
        {
            return null;
        }

        if (_byGuid.TryGetValue(NormGuid(nameOrGuid), out var byGuid))
        {
            return ToDto(byGuid);
        }

        if (_guidByName.TryGetValue(nameOrGuid, out var g) && _byGuid.TryGetValue(g, out var byName))
        {
            return ToDto(byName);
        }

        return _byGuid.Values
            .FirstOrDefault(p => p.Name.Equals(nameOrGuid, StringComparison.OrdinalIgnoreCase)) is { } hit
            ? ToDto(hit)
            : null;
    }

    public IReadOnlyList<object> GetBuilds(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return _byGuid.Values
            .Where(p => p.Talents.Count > 0 || p.ItemIds.Count > 0)
            .OrderByDescending(p => p.UpdatedAtUtc)
            .Take(limit)
            .Select(p => new
            {
                guid = p.GuidHex,
                name = p.Name,
                classId = p.ClassId,
                activeSpec = p.ActiveSpec,
                talentCount = p.Talents.Count,
                talents = p.Talents.Select(t => new { id = t.TalentId, rank = t.Rank, spec = t.SpecIndex }),
                glyphs = p.Glyphs,
                itemIds = p.ItemIds,
                summary = BuildSummary(p),
                updatedAt = p.UpdatedAtUtc,
                sourceAccount = p.SourceAccount
            })
            .Cast<object>()
            .ToList();
    }

    public object GetLadders()
    {
        var brackets = new[] { "2v2", "3v3", "5v5" };
        var ladders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var bracket in brackets)
        {
            var rows = _byGuid.Values
                .Select(p =>
                {
                    var a = p.Arena.FirstOrDefault(x =>
                        x.Bracket.Equals(bracket, StringComparison.OrdinalIgnoreCase));
                    return a is null
                        ? null
                        : new
                        {
                            rank = 0,
                            name = p.Name,
                            guid = p.GuidHex,
                            classId = p.ClassId,
                            personalRating = a.PersonalRating,
                            teamRating = a.TeamRating,
                            wins = a.WinsSeason,
                            games = a.GamesSeason,
                            teamId = a.TeamId,
                            sourceAccount = p.SourceAccount
                        };
                })
                .Where(x => x is not null && x.personalRating > 0)
                .OrderByDescending(x => x!.personalRating)
                .ThenByDescending(x => x!.wins)
                .Take(50)
                .Select((x, i) => new
                {
                    rank = i + 1,
                    x!.name,
                    x.guid,
                    x.classId,
                    x.personalRating,
                    x.teamRating,
                    x.wins,
                    x.games,
                    x.teamId,
                    x.sourceAccount
                })
                .ToList();
            ladders[bracket] = rows;
        }

        // Honor / HK ladder
        foreach (var p in _byGuid.Values)
        {
            EnrichFromWho(p);
        }

        ladders["honor"] = _byGuid.Values
            .Where(p => p.Honor is { LifetimeHonorableKills: > 0 })
            .OrderByDescending(p => p.Honor!.LifetimeHonorableKills)
            .Take(50)
            .Select((p, i) => new
            {
                rank = i + 1,
                name = p.Name,
                guid = p.GuidHex,
                lifetimeHk = p.Honor!.LifetimeHonorableKills,
                kills = p.Honor.Kills,
                honorToday = p.Honor.HonorToday,
                sourceAccount = p.SourceAccount
            })
            .ToList();

        // Activity / presence ladder from chat WHO
        ladders["activity"] = _mediator.GetPlayers()
            .OrderByDescending(p => p.MessageCount)
            .ThenByDescending(p => p.LastSeenUtc)
            .Take(50)
            .Select((p, i) => new
            {
                rank = i + 1,
                name = p.Name,
                guid = p.Guid,
                messageCount = p.MessageCount,
                presence = p.Presence,
                level = p.Level,
                classId = p.ClassId
            })
            .ToList();

        return new
        {
            ok = true,
            profileCount = _byGuid.Count,
            ladders,
            updatedAt = DateTimeOffset.UtcNow
        };
    }

    public object GetSummary() => new
    {
        ok = true,
        profiles = _byGuid.Count,
        builds = _byGuid.Values.Count(p => p.Talents.Count > 0 || p.ItemIds.Count > 0),
        withArena = _byGuid.Values.Count(p => p.Arena.Count > 0),
        path = _path
    };

    private void OnPacket(Packet packet)
    {
        var span = packet.Payload.Span;
        switch (packet.Opcode)
        {
            case InspectCodec.SmsgInspectTalent:
                if (InspectCodec.TryParseInspectTalent(span, out var talent))
                {
                    var p = EnsureStub(talent.Guid, null, null);
                    p.UnspentTalentPoints = talent.UnspentTalentPoints;
                    p.SpecCount = talent.SpecCount;
                    p.ActiveSpec = talent.ActiveSpec;
                    p.Talents = talent.Talents.ToList();
                    p.Glyphs = talent.Glyphs.ToList();
                    if (talent.ItemIds.Count > 0)
                    {
                        p.ItemIds = talent.ItemIds.ToList();
                    }

                    p.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    Touch(p);
                }

                break;
            case InspectCodec.MsgInspectHonorStats:
                if (InspectCodec.TryParseHonor(span, out var honor))
                {
                    var p = EnsureStub(honor.Guid, null, null);
                    p.Honor = honor;
                    p.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    Touch(p);
                }

                break;
            case InspectCodec.MsgInspectArenaTeams:
                if (InspectCodec.TryParseArenaTeam(span, out var arena))
                {
                    var p = EnsureStub(arena.Guid, null, null);
                    p.Arena.RemoveAll(a => a.Slot == arena.Slot);
                    p.Arena.Add(arena);
                    p.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    Touch(p);
                }

                break;
            case InspectCodec.SmsgRespondInspectAchievements:
                if (InspectCodec.TryParseAchievements(span, out var ach))
                {
                    var p = EnsureStub(ach.Guid, null, null);
                    p.AchievementIds = ach.AchievementIds.ToList();
                    p.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    Touch(p);
                }

                break;
        }
    }

    private void StartPump()
    {
        if (Interlocked.Exchange(ref _pumpRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (_autoQueue.TryDequeue(out var guid))
                {
                    try
                    {
                        await InspectAsync(guid, null, null, CancellationToken.None).ConfigureAwait(false);
                        await Task.Delay(650).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore per-target
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _pumpRunning, 0);
                if (!_autoQueue.IsEmpty)
                {
                    StartPump();
                }
            }
        });
    }

    private PlayerProfile EnsureStub(ulong guid, string? name, string? sourceAccount)
    {
        var hex = guid.ToString("X16", CultureInfo.InvariantCulture);
        var p = _byGuid.AddOrUpdate(
            hex,
            _ => new PlayerProfile
            {
                GuidHex = hex,
                Name = name?.Trim() ?? "",
                SourceAccount = sourceAccount ?? "",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                FirstSeenUtc = DateTimeOffset.UtcNow
            },
            (_, cur) =>
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    cur.Name = name.Trim();
                }

                if (!string.IsNullOrWhiteSpace(sourceAccount) && string.IsNullOrWhiteSpace(cur.SourceAccount))
                {
                    cur.SourceAccount = sourceAccount!;
                }

                return cur;
            });

        if (p.HasName)
        {
            _guidByName[p.Name] = hex;
        }

        EnrichFromWho(p);
        if (!p.HasName)
        {
            _mediator.RequestNameQueryPublic(hex);
        }

        return p;
    }

    private void EnrichFromWho(PlayerProfile p)
    {
        var who = _mediator.GetPlayers().FirstOrDefault(w =>
            (!string.IsNullOrWhiteSpace(w.Guid)
             && w.Guid.Equals(p.GuidHex, StringComparison.OrdinalIgnoreCase))
            || (p.HasName && w.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)));
        if (who is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(who.Name) && !p.HasName)
        {
            p.Name = who.Name.Trim();
            _guidByName[p.Name] = p.GuidHex;
        }

        if (who.ClassId >= 0)
        {
            p.ClassId = who.ClassId;
        }

        if (who.Race >= 0)
        {
            p.Race = who.Race;
        }

        if (who.Level > 0)
        {
            p.Level = who.Level;
        }

        if (!string.IsNullOrWhiteSpace(who.Guild))
        {
            p.Guild = who.Guild;
        }

        if (who.ZoneId > 0)
        {
            p.ZoneId = who.ZoneId;
        }
    }

    /// <summary>Push WHO/inspect dossier fields onto OM identity (name) and return whether useful data exists.</summary>
    public bool TryApplyToObjectManager(ulong guid, IObjectDirectory objects)
    {
        if (guid == 0 || objects is null)
        {
            return false;
        }

        var hex = guid.ToString("X16", CultureInfo.InvariantCulture);
        if (!_byGuid.TryGetValue(hex, out var p))
        {
            return false;
        }

        EnrichFromWho(p);
        if (p.HasName)
        {
            objects.ApplyIdentity(guid, p.Name);
        }

        return p.HasName || p.Level > 0 || p.ClassId >= 0 || p.ItemIds.Count > 0 || p.Honor is not null;
    }

    public bool TryGet(ulong guid, out PlayerProfile? profile)
    {
        profile = null;
        if (guid == 0)
        {
            return false;
        }

        var hex = guid.ToString("X16", CultureInfo.InvariantCulture);
        if (!_byGuid.TryGetValue(hex, out var p))
        {
            return false;
        }

        EnrichFromWho(p);
        profile = p;
        return true;
    }

    private void Touch(PlayerProfile p)
    {
        if (p.HasName)
        {
            _guidByName[p.Name] = p.GuidHex;
        }

        ScheduleSave();
        ProfilesChanged?.Invoke();
    }

    private IWorldClient RequireWorld()
    {
        if (_world is null || _world.State != SessionState.InWorld)
        {
            throw new InvalidOperationException("Not in world — cannot inspect.");
        }

        return _world;
    }

    private void ScheduleSave()
    {
        // Profiles stay in-memory for the session — only chat/roster/portal are persisted.
        return;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<ProfileFile>(json, JsonOpts);
            if (file?.Profiles is null)
            {
                return;
            }

            foreach (var p in file.Profiles)
            {
                if (string.IsNullOrWhiteSpace(p.GuidHex))
                {
                    continue;
                }

                p.GuidHex = NormGuid(p.GuidHex);
                _byGuid[p.GuidHex] = p;
                if (p.HasName)
                {
                    _guidByName[p.Name] = p.GuidHex;
                }
            }
        }
        catch
        {
            // ignore corrupt store
        }
    }

    private void Flush()
    {
        lock (_io)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var file = new ProfileFile
                {
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Profiles = _byGuid.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()
                };
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
                File.Copy(tmp, _path, overwrite: true);
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static string NormGuid(string g)
    {
        g = (g ?? "").Trim();
        if (g.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            g = g[2..];
        }

        return ulong.TryParse(g, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u)
            ? u.ToString("X16", CultureInfo.InvariantCulture)
            : g.ToUpperInvariant();
    }

    private static string BuildSummary(PlayerProfile p)
    {
        var talentPts = p.Talents.Sum(t => t.Rank);
        var arenaBest = p.Arena.Count == 0 ? 0 : p.Arena.Max(a => a.PersonalRating);
        return $"spec{p.ActiveSpec} · {talentPts} talent ranks · {p.ItemIds.Count} items · arena {arenaBest}";
    }

    private static object ToDto(PlayerProfile p) => new
    {
        guid = p.GuidHex,
        name = p.Name,
        level = p.Level,
        classId = p.ClassId,
        race = p.Race,
        guild = p.Guild,
        zoneId = p.ZoneId,
        activeSpec = p.ActiveSpec,
        specCount = p.SpecCount,
        unspentTalentPoints = p.UnspentTalentPoints,
        talents = p.Talents.Select(t => new { id = t.TalentId, rank = t.Rank, spec = t.SpecIndex }),
        glyphs = p.Glyphs,
        itemIds = p.ItemIds,
        honor = p.Honor is null ? null : new
        {
            p.Honor.HonorLevel,
            p.Honor.Kills,
            p.Honor.HonorToday,
            p.Honor.HonorYesterday,
            p.Honor.LifetimeHonorableKills
        },
        arena = p.Arena.Select(a => new
        {
            a.Bracket,
            a.Slot,
            a.TeamId,
            a.TeamRating,
            a.PersonalRating,
            a.GamesSeason,
            a.WinsSeason,
            a.TotalGames
        }),
        achievementIds = p.AchievementIds,
        buildSummary = BuildSummary(p),
        sourceAccount = p.SourceAccount,
        firstSeenUtc = p.FirstSeenUtc,
        updatedAtUtc = p.UpdatedAtUtc
    };

    private sealed class ProfileFile
    {
        public DateTimeOffset SavedAtUtc { get; set; }
        public List<PlayerProfile> Profiles { get; set; } = new();
    }
}

public sealed class PlayerProfile
{
    public string GuidHex { get; set; } = "";
    public string Name { get; set; } = "";
    public int Level { get; set; } = -1;
    public int ClassId { get; set; } = -1;
    public int Race { get; set; } = -1;
    public string Guild { get; set; } = "";
    public int ZoneId { get; set; }
    public uint UnspentTalentPoints { get; set; }
    public byte SpecCount { get; set; }
    public byte ActiveSpec { get; set; }
    public List<InspectTalentNode> Talents { get; set; } = new();
    public List<ushort> Glyphs { get; set; } = new();
    public List<uint> ItemIds { get; set; } = new();
    public InspectHonorSnapshot? Honor { get; set; }
    public List<InspectArenaSnapshot> Arena { get; set; } = new();
    public List<uint> AchievementIds { get; set; } = new();
    public string SourceAccount { get; set; } = "";
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public bool HasName => !string.IsNullOrWhiteSpace(Name);
}
