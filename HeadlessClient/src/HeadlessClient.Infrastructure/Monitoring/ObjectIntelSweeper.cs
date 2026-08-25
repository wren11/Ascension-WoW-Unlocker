using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Infrastructure.Chat;
using HeadlessClient.Infrastructure.Fleet;
using HeadlessClient.Infrastructure.Protocol;
using HeadlessClient.Infrastructure.Query;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeadlessClient.Infrastructure.Monitoring;

/// <summary>
/// Background WHO / NameQuery / Creature+GO query / Inspect enrichment. Runs off the chat path —
/// never blocks inbound UPDATE_OBJECT or chat streaming.
/// </summary>
public sealed class ObjectIntelSweeper : BackgroundService
{
    private readonly InMemoryObjectDirectory _objects;
    private readonly PlayerProfileService _profiles;
    private readonly ChatMediator _mediator;
    private readonly AccountFleetService _fleet;
    private readonly QueryCache _queries;
    private readonly ILogger<ObjectIntelSweeper>? _log;
    private long _cycles;
    private int _sweepBusy;

    public ObjectIntelSweeper(
        InMemoryObjectDirectory objects,
        PlayerProfileService profiles,
        ChatMediator mediator,
        AccountFleetService fleet,
        QueryCache queries,
        ILogger<ObjectIntelSweeper>? log = null)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(12_000, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (Interlocked.CompareExchange(ref _sweepBusy, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        _log?.LogDebug(ex, "Object intel sweep failed");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _sweepBusy, 0);
                    }
                }, stoppingToken);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async Task<object> SweepOnceAsync(CancellationToken ct = default)
    {
        var cycle = Interlocked.Increment(ref _cycles);
        var snap = _objects.Snapshot().Where(o => o.Alive).OrderByDescending(o => o.LastSeenUtc).ToList();
        var players = snap
            .Where(o => o.TypeId == 4 || WorldIntelService.InferTypeId(o.Guid) == 4)
            .Take(24)
            .ToList();

        var world = PickWorld();
        var whoBlank = false;
        var nameQueries = 0;
        var whoNamed = 0;
        var inspectQueued = 0;
        var enriched = 0;
        var creatureQueries = 0;
        var goQueries = 0;

        if (cycle % 5 == 1 && world is not null)
        {
            try
            {
                await _mediator.RefreshWhoAsync(null, ct).ConfigureAwait(false);
                whoBlank = true;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        var nameBudget = 6;
        var whoBudget = 3;
        foreach (var obj in players)
        {
            ct.ThrowIfCancellationRequested();
            var name = obj.Name;

            if (inspectQueued < 6)
            {
                _profiles.NoteSeenPlayer(obj.Guid, name, sourceAccount: "om-sweep");
                inspectQueued++;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                if (nameBudget-- > 0 && world is not null)
                {
                    try
                    {
                        await world.SendAsync(NameQueryCodec.BuildRequest(obj.Guid), ct).ConfigureAwait(false);
                        nameQueries++;
                        await Task.Delay(120, ct).ConfigureAwait(false);
                    }
                    catch { /* ignore */ }
                }

                continue;
            }

            if (whoBudget-- > 0 && world is not null)
            {
                try
                {
                    await _mediator.RefreshWhoAsync(name, ct).ConfigureAwait(false);
                    whoNamed++;
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }

            if (_profiles.TryApplyToObjectManager(obj.Guid, _objects))
            {
                enriched++;
            }
        }

        var queryBudget = 4;
        foreach (var obj in snap.Take(80))
        {
            if (queryBudget <= 0)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            var typeId = obj.TypeId != 0 ? obj.TypeId : WorldIntelService.InferTypeId(obj.Guid);
            var entry = obj.Entry != 0 ? obj.Entry : GuidIntel.EntryFromGuid(obj.Guid);
            if (entry == 0)
            {
                continue;
            }

            if (typeId == 3)
            {
                if (_queries.GetCachedCreature(entry) is not null && !string.IsNullOrWhiteSpace(obj.Name))
                {
                    continue;
                }

                try
                {
                    var live = await _queries.GetCreatureAsync(entry, ct).ConfigureAwait(false);
                    ApplyQueryName(obj.Guid, entry, live);
                    creatureQueries++;
                    queryBudget--;
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }
            else if (typeId == 5)
            {
                if (_queries.GetCachedGameObject(entry) is not null && !string.IsNullOrWhiteSpace(obj.Name))
                {
                    continue;
                }

                try
                {
                    var live = await _queries.GetGameObjectAsync(entry, ct).ConfigureAwait(false);
                    ApplyQueryName(obj.Guid, entry, live);
                    goQueries++;
                    queryBudget--;
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }
        }

        return new
        {
            ok = true,
            cycle,
            players = players.Count,
            whoBlank,
            whoNamed,
            nameQueries,
            creatureQueries,
            goQueries,
            inspectQueued,
            enriched
        };
    }

    private void ApplyQueryName(ulong guid, uint entry, object live)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(live);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            string? name = null;
            if (doc.RootElement.TryGetProperty("name", out var n))
            {
                name = n.GetString();
            }
            else if (doc.RootElement.TryGetProperty("Name", out var n2))
            {
                name = n2.GetString();
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                _objects.ApplyIdentity(guid, name, entry);
            }
            else
            {
                _objects.ApplyIdentity(guid, entry: entry);
            }
        }
        catch
        {
            _objects.ApplyIdentity(guid, entry: entry);
        }
    }

    private IWorldClient? PickWorld()
    {
        foreach (var r in _fleet.Runners)
        {
            if (r.IsInWorld && r.WorldClient is not null)
            {
                return r.WorldClient;
            }
        }

        return null;
    }
}
