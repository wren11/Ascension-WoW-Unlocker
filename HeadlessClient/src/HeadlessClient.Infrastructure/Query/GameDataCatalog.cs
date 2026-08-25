using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HeadlessClient.Infrastructure.Query;

/// <summary>
/// Packet-derived SoftRealm game data catalog (items / quests / creatures / GOs).
/// Filled from live <c>SMSG_*_QUERY_RESPONSE</c> and chat |Hitem:| harvests; searchable via SQLite FTS-like LIKE.
/// </summary>
public sealed class GameDataCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<(string Kind, uint Id, string Source)> _probeQueue = new();
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private SqliteConnection? _conn;

    public GameDataCatalog(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeadlessClient",
                "game-data.db")
            : Path.GetFullPath(path);
        Open();
    }

    public string PathUsed => _path;

    public void NoteInterest(string kind, uint id, string source = "chat")
    {
        if (id == 0 || string.IsNullOrWhiteSpace(kind))
        {
            return;
        }

        kind = kind.Trim().ToLowerInvariant();
        var key = $"{kind}:{id}";
        if (!_queued.TryAdd(key, 1))
        {
            return;
        }

        _probeQueue.Enqueue((kind, id, source));
    }

    /// <summary>After a confirmed find, queue nearby template IDs (±1..3) for harvest.</summary>
    public void EnqueueNeighborhood(string kind, uint id, string source = "near")
    {
        if (id == 0 || string.IsNullOrWhiteSpace(kind))
        {
            return;
        }

        kind = kind.Trim().ToLowerInvariant();
        if (kind is not ("item" or "creature" or "quest" or "gameobject"))
        {
            return;
        }

        for (var d = 1u; d <= 3u; d++)
        {
            EnqueueNeighbor(kind, id + d, source);
            if (id > d)
            {
                EnqueueNeighbor(kind, id - d, source);
            }
        }
    }

    private void EnqueueNeighbor(string kind, uint id, string source)
    {
        var key = $"{kind}:{id}";
        if (!_queued.TryAdd(key, 1))
        {
            return;
        }

        _probeQueue.Enqueue((kind, id, source));
    }

    public bool TryDequeueProbe(out string kind, out uint id, out string source)
    {
        if (_probeQueue.TryDequeue(out var row))
        {
            kind = row.Kind;
            id = row.Id;
            source = row.Source;
            _queued.TryRemove($"{kind}:{id}", out _);
            return true;
        }

        kind = "";
        id = 0;
        source = "";
        return false;
    }

    public int PendingProbeCount => _probeQueue.Count;

    public void UpsertFromQueryDto(string kind, uint id, object dto, string source = "packet")
    {
        if (id == 0 || dto is null)
        {
            return;
        }

        kind = (kind ?? "item").Trim().ToLowerInvariant();
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, JsonOpts));
            var r = doc.RootElement;
            var found = !r.TryGetProperty("found", out var f) || f.ValueKind != JsonValueKind.False;
            if (r.TryGetProperty("notFound", out var nf) && nf.ValueKind == JsonValueKind.True)
            {
                found = false;
            }

            if (!found)
            {
                MarkMissing(kind, id, source);
                return;
            }

            var name = ReadString(r, "name") ?? "";
            if (string.IsNullOrWhiteSpace(name) && r.TryGetProperty("strings", out var strs)
                && strs.ValueKind == JsonValueKind.Array && strs.GetArrayLength() > 0)
            {
                name = strs[0].GetString() ?? "";
            }

            var quality = ReadInt(r, "quality", -1);
            var itemLevel = ReadInt(r, "itemLevel", -1);
            var requiredLevel = ReadInt(r, "requiredLevel", -1);
            var inventoryType = ReadInt(r, "inventoryType", -1);
            var bonding = ReadInt(r, "bonding", -1);
            var armor = ReadInt(r, "armor", 0);
            var buyPrice = ReadInt(r, "buyPrice", 0);
            var sellPrice = ReadInt(r, "sellPrice", 0);
            if (buyPrice == 0 && r.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.Object)
            {
                buyPrice = ReadInt(detail, "buyPrice", 0);
                sellPrice = ReadInt(detail, "sellPrice", sellPrice);
            }

            var qualityName = ReadString(r, "qualityName") ?? "";
            var qualityColor = ReadString(r, "qualityColor") ?? "";
            var bondingName = ReadString(r, "bondingName") ?? "";
            var inventoryTypeName = ReadString(r, "inventoryTypeName") ?? "";
            var description = ReadString(r, "description") ?? "";
            var json = JsonSerializer.Serialize(dto, JsonOpts);

            lock (_gate)
            {
                EnsureOpen();
                using var cmd = _conn!.CreateCommand();
                cmd.CommandText =
                    """
                    INSERT INTO entities(
                      kind, id, name, quality, quality_name, quality_color, item_level, required_level,
                      inventory_type, inventory_type_name, bonding, bonding_name, armor,
                      buy_price, sell_price, description, found, source, json, updated_at_utc)
                    VALUES(
                      $kind, $id, $name, $q, $qn, $qc, $ilvl, $rlvl,
                      $inv, $invn, $bond, $bondn, $armor,
                      $buy, $sell, $desc, 1, $src, $json, $at)
                    ON CONFLICT(kind, id) DO UPDATE SET
                      name=excluded.name,
                      quality=excluded.quality,
                      quality_name=excluded.quality_name,
                      quality_color=excluded.quality_color,
                      item_level=excluded.item_level,
                      required_level=excluded.required_level,
                      inventory_type=excluded.inventory_type,
                      inventory_type_name=excluded.inventory_type_name,
                      bonding=excluded.bonding,
                      bonding_name=excluded.bonding_name,
                      armor=excluded.armor,
                      buy_price=excluded.buy_price,
                      sell_price=excluded.sell_price,
                      description=excluded.description,
                      found=1,
                      source=excluded.source,
                      json=excluded.json,
                      updated_at_utc=excluded.updated_at_utc;
                    """;
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$id", (long)id);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$q", quality);
                cmd.Parameters.AddWithValue("$qn", qualityName);
                cmd.Parameters.AddWithValue("$qc", qualityColor);
                cmd.Parameters.AddWithValue("$ilvl", itemLevel);
                cmd.Parameters.AddWithValue("$rlvl", requiredLevel);
                cmd.Parameters.AddWithValue("$inv", inventoryType);
                cmd.Parameters.AddWithValue("$invn", inventoryTypeName);
                cmd.Parameters.AddWithValue("$bond", bonding);
                cmd.Parameters.AddWithValue("$bondn", bondingName);
                cmd.Parameters.AddWithValue("$armor", armor);
                cmd.Parameters.AddWithValue("$buy", buyPrice);
                cmd.Parameters.AddWithValue("$sell", sellPrice);
                cmd.Parameters.AddWithValue("$desc", description);
                cmd.Parameters.AddWithValue("$src", source ?? "packet");
                cmd.Parameters.AddWithValue("$json", json);
                cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
            }

            // Only expand ID neighborhood after a confirmed find.
            EnqueueNeighborhood(kind, id, source + "+near");
        }
        catch
        {
            // never break packet path
        }
    }

    public void MarkMissing(string kind, uint id, string source = "packet")
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (id == 0 || kind.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO entities(kind, id, name, found, source, json, updated_at_utc)
                VALUES($kind, $id, '', 0, $src, '{}', $at)
                ON CONFLICT(kind, id) DO UPDATE SET
                  found=0,
                  source=excluded.source,
                  updated_at_utc=excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$id", (long)id);
            cmd.Parameters.AddWithValue("$src", source ?? "packet");
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    public object? Get(string kind, uint id)
    {
        kind = (kind ?? "item").Trim().ToLowerInvariant();
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT json, found FROM entities WHERE kind=$kind AND id=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$id", (long)id);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return null;
            }

            var json = r.GetString(0);
            var found = r.GetInt32(1) != 0;
            if (string.IsNullOrWhiteSpace(json))
            {
                return new { ok = found, found, kind, id, source = "catalog" };
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    map[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
                }

                map["source"] = "catalog";
                map["catalog"] = true;
                map["ok"] = found;
                map["found"] = found;
                return map;
            }
            catch
            {
                return new { ok = found, found, kind, id, source = "catalog", raw = json };
            }
        }
    }

    public object Search(string? query, string? kind = null, int limit = 40)
    {
        limit = Math.Clamp(limit, 1, 200);
        query = (query ?? "").Trim();
        kind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();

        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            if (uint.TryParse(query, out var asId))
            {
                cmd.CommandText =
                    """
                    SELECT kind, id, name, quality, quality_color, item_level, found, source, updated_at_utc
                    FROM entities
                    WHERE id=$id AND ($kind IS NULL OR kind=$kind) AND found=1
                    ORDER BY kind
                    LIMIT $lim;
                    """;
                cmd.Parameters.AddWithValue("$id", (long)asId);
                cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            }
            else if (query.Length == 0)
            {
                cmd.CommandText =
                    """
                    SELECT kind, id, name, quality, quality_color, item_level, found, source, updated_at_utc
                    FROM entities
                    WHERE found=1 AND ($kind IS NULL OR kind=$kind)
                    ORDER BY updated_at_utc DESC
                    LIMIT $lim;
                    """;
                cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            }
            else
            {
                cmd.CommandText =
                    """
                    SELECT kind, id, name, quality, quality_color, item_level, found, source, updated_at_utc
                    FROM entities
                    WHERE found=1 AND ($kind IS NULL OR kind=$kind)
                      AND (name LIKE $q OR description LIKE $q OR CAST(id AS TEXT)= $exact)
                    ORDER BY
                      CASE WHEN name LIKE $qStart THEN 0 ELSE 1 END,
                      item_level DESC,
                      id
                    LIMIT $lim;
                    """;
                cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$q", "%" + query + "%");
                cmd.Parameters.AddWithValue("$qStart", query + "%");
                cmd.Parameters.AddWithValue("$exact", query);
            }

            cmd.Parameters.AddWithValue("$lim", limit);
            var items = new List<object>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                items.Add(new
                {
                    kind = r.GetString(0),
                    id = (uint)r.GetInt64(1),
                    name = r.IsDBNull(2) ? "" : r.GetString(2),
                    quality = r.IsDBNull(3) ? -1 : r.GetInt32(3),
                    qualityColor = r.IsDBNull(4) ? "" : r.GetString(4),
                    itemLevel = r.IsDBNull(5) ? -1 : r.GetInt32(5),
                    found = r.GetInt32(6) != 0,
                    source = r.IsDBNull(7) ? "" : r.GetString(7),
                    updatedAt = r.IsDBNull(8) ? "" : r.GetString(8)
                });
            }

            return new { ok = true, query, kind, count = items.Count, items, path = _path };
        }
    }

    public object GetStats()
    {
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                """
                SELECT kind, COUNT(*), SUM(CASE WHEN found=1 THEN 1 ELSE 0 END)
                FROM entities GROUP BY kind;
                """;
            var byKind = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            var found = 0;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var k = r.GetString(0);
                var c = r.GetInt32(1);
                var f = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));
                byKind[k] = new { total = c, found = f };
                total += c;
                found += f;
            }

            return new
            {
                ok = true,
                path = _path,
                total,
                found,
                pendingProbes = PendingProbeCount,
                byKind
            };
        }
    }

    private void Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS entities(
              kind TEXT NOT NULL,
              id INTEGER NOT NULL,
              name TEXT NOT NULL DEFAULT '',
              quality INTEGER NOT NULL DEFAULT -1,
              quality_name TEXT NOT NULL DEFAULT '',
              quality_color TEXT NOT NULL DEFAULT '',
              item_level INTEGER NOT NULL DEFAULT -1,
              required_level INTEGER NOT NULL DEFAULT -1,
              inventory_type INTEGER NOT NULL DEFAULT -1,
              inventory_type_name TEXT NOT NULL DEFAULT '',
              bonding INTEGER NOT NULL DEFAULT -1,
              bonding_name TEXT NOT NULL DEFAULT '',
              armor INTEGER NOT NULL DEFAULT 0,
              buy_price INTEGER NOT NULL DEFAULT 0,
              sell_price INTEGER NOT NULL DEFAULT 0,
              description TEXT NOT NULL DEFAULT '',
              found INTEGER NOT NULL DEFAULT 1,
              source TEXT NOT NULL DEFAULT 'packet',
              json TEXT NOT NULL DEFAULT '{}',
              updated_at_utc TEXT NOT NULL,
              PRIMARY KEY(kind, id)
            );
            CREATE INDEX IF NOT EXISTS ix_entities_name ON entities(name);
            CREATE INDEX IF NOT EXISTS ix_entities_kind_found ON entities(kind, found);
            CREATE INDEX IF NOT EXISTS ix_entities_updated ON entities(updated_at_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    private void EnsureOpen()
    {
        if (_conn is { State: System.Data.ConnectionState.Open })
        {
            return;
        }

        Open();
    }

    private static string? ReadString(JsonElement r, string name) =>
        r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int ReadInt(JsonElement r, string name, int fallback)
    {
        if (!r.TryGetProperty(name, out var p))
        {
            return fallback;
        }

        if (p.TryGetInt32(out var i))
        {
            return i;
        }

        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var l))
        {
            return (int)l;
        }

        return fallback;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _conn?.Dispose();
            _conn = null;
        }
    }
}
