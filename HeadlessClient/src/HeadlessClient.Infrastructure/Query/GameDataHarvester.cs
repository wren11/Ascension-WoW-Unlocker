using HeadlessClient.Domain.Session;
using HeadlessClient.Infrastructure.Fleet;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeadlessClient.Infrastructure.Query;

/// <summary>
/// Drains <see cref="GameDataCatalog"/> probe queue: live CMSG queries for chat-linked /
/// neighborhood IDs, then persists decoded SMSG bodies into the SQLite catalog.
/// </summary>
public sealed class GameDataHarvester : BackgroundService
{
    private readonly GameDataCatalog _catalog;
    private readonly QueryCache _queries;
    private readonly AccountFleetService _fleet;
    private readonly ILogger<GameDataHarvester>? _log;
    private long _cycles;

    public GameDataHarvester(
        GameDataCatalog catalog,
        QueryCache queries,
        AccountFleetService fleet,
        ILogger<GameDataHarvester>? log = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(12_000, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "Game data harvest tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var cycle = Interlocked.Increment(ref _cycles);
        if (!_fleet.Runners.Any(s => s.IsInWorld))
        {
            return;
        }

        var budget = 8;
        var probed = 0;
        while (budget-- > 0 && _catalog.TryDequeueProbe(out var kind, out var id, out var source))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                object dto = kind switch
                {
                    "item" => await _queries.GetItemAsync(id, ct).ConfigureAwait(false),
                    "quest" => await _queries.GetQuestAsync(id, ct).ConfigureAwait(false),
                    "creature" => await _queries.GetCreatureAsync(id, ct).ConfigureAwait(false),
                    "gameobject" => await _queries.GetGameObjectAsync(id, ct).ConfigureAwait(false),
                    _ => new { ok = false, found = false, kind, id }
                };

                // Never persist timeouts / transport errors as "missing" catalog rows.
                var json = System.Text.Json.JsonSerializer.Serialize(dto);
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var err = doc.RootElement.TryGetProperty("error", out var e)
                              && e.ValueKind == System.Text.Json.JsonValueKind.String
                        ? e.GetString()
                        : null;
                    if (!string.IsNullOrEmpty(err))
                    {
                        continue;
                    }
                }

                _catalog.UpsertFromQueryDto(kind, id, dto, source);
                probed++;
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "Probe {Kind}:{Id} failed", kind, id);
            }

            await Task.Delay(180, ct).ConfigureAwait(false);
        }

        // Periodic sparse ID sweep for items — fills holes around known catalog entries.
        if (cycle % 15 == 0)
        {
            await SparseItemSweepAsync(ct).ConfigureAwait(false);
        }

        if (probed > 0 && cycle % 5 == 0)
        {
            Console.WriteLine($"[game-data] probed={probed} pending={_catalog.PendingProbeCount}");
        }
    }

    private async Task SparseItemSweepAsync(CancellationToken ct)
    {
        // Pick a few unknown IDs near the top of the recent search list.
        var snap = _catalog.Search("", "item", limit: 20);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(snap));
            if (!doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return;
            }

            var seeds = new List<uint>();
            foreach (var it in items.EnumerateArray())
            {
                if (it.TryGetProperty("id", out var idEl) && idEl.TryGetUInt32(out var id) && id > 0)
                {
                    seeds.Add(id);
                }
            }

            foreach (var seed in seeds.Take(5))
            {
                // Wider neighborhood for established seeds (still gated by queue dedupe).
                for (var d = 4u; d <= 12u; d += 4u)
                {
                    _catalog.NoteInterest("item", seed + d, "sweep");
                    if (seed > d)
                    {
                        _catalog.NoteInterest("item", seed - d, "sweep");
                    }
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore
        }
    }
}
