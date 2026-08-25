using System.Data;
using System.Globalization;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.World;
using Microsoft.Data.Sqlite;

namespace HeadlessClient.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed chat history optimized for Discord-style upward pagination
/// (keyset / cursor on AUTOINCREMENT id — O(log n) seeks, no OFFSET).
/// </summary>
public sealed class SqliteChatStore : IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private SqliteConnection? _conn;

    public SqliteChatStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeadlessClient",
                "headless.db")
            : Path.GetFullPath(path);
        Open();
    }

    public string PathUsed => _path;

    public long Insert(ChatLine line, bool loot)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO chat_messages(
                  received_at_utc, type, language, sender, channel, message,
                  sender_guid, readable_text, level, class_id, race, guild, zone,
                  target_guid, direction, is_loot, scope, owner_user_id, observer_account, seen_by)
                VALUES(
                  $at, $type, $lang, $sender, $channel, $message,
                  $sg, $readable, $level, $class, $race, $guild, $zone,
                  $tg, $dir, $loot, $scope, $owner, $observer, $seen);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$at", line.ReceivedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$type", (int)line.Type);
            cmd.Parameters.AddWithValue("$lang", line.Language ?? "");
            cmd.Parameters.AddWithValue("$sender", line.Sender ?? "");
            cmd.Parameters.AddWithValue("$channel", line.Channel ?? "");
            cmd.Parameters.AddWithValue("$message", line.Message ?? "");
            cmd.Parameters.AddWithValue("$sg", line.SenderGuid ?? "");
            cmd.Parameters.AddWithValue("$readable", line.ReadableText ?? "");
            cmd.Parameters.AddWithValue("$level", line.Level);
            cmd.Parameters.AddWithValue("$class", line.ClassId);
            cmd.Parameters.AddWithValue("$race", line.Race);
            cmd.Parameters.AddWithValue("$guild", line.Guild ?? "");
            cmd.Parameters.AddWithValue("$zone", line.Zone ?? "");
            cmd.Parameters.AddWithValue("$tg", line.TargetGuid ?? "");
            cmd.Parameters.AddWithValue("$dir", line.Direction ?? "");
            cmd.Parameters.AddWithValue("$loot", loot ? 1 : 0);
            cmd.Parameters.AddWithValue("$scope", string.IsNullOrWhiteSpace(line.Scope) ? "shared" : line.Scope);
            cmd.Parameters.AddWithValue("$owner", line.OwnerUserId ?? "");
            cmd.Parameters.AddWithValue("$observer", line.ObserverAccount ?? "");
            cmd.Parameters.AddWithValue("$seen", line.SeenBy ?? "");
            var id = (long)(cmd.ExecuteScalar() ?? 0L);
            return id;
        }
    }

    public ChatPage Query(
        int limit = 50,
        long? beforeId = null,
        string? channel = null,
        string? sender = null,
        string? text = null,
        bool loot = false,
        string? scope = null,
        string? ownerUserId = null)
    {
        limit = Math.Clamp(limit, 1, 500);
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            var sql =
                """
                SELECT id, received_at_utc, type, language, sender, channel, message,
                       sender_guid, readable_text, level, class_id, race, guild, zone,
                       target_guid, direction, scope, owner_user_id, observer_account, seen_by
                FROM chat_messages
                WHERE is_loot = $loot
                """;
            cmd.Parameters.AddWithValue("$loot", loot ? 1 : 0);

            var scopeNorm = string.IsNullOrWhiteSpace(scope) ? "shared" : scope.Trim().ToLowerInvariant();
            if (scopeNorm == "member")
            {
                sql += " AND scope = 'member' AND owner_user_id = $owner";
                cmd.Parameters.AddWithValue("$owner", ownerUserId ?? "");
            }
            else if (scopeNorm == "all")
            {
                // no scope filter
            }
            else
            {
                sql += " AND (scope = 'shared' OR scope IS NULL OR scope = '')";
            }

            if (beforeId is > 0)
            {
                sql += " AND id < $before";
                cmd.Parameters.AddWithValue("$before", beforeId.Value);
            }

            var channelNorm = ChatChannelFilter.Normalize(channel);
            if (!string.IsNullOrWhiteSpace(channelNorm))
            {
                sql += " AND (channel = $ch COLLATE NOCASE OR channel LIKE $chPrefix COLLATE NOCASE OR ($chWhisper = 1 AND (type = 7 OR type = 8 OR channel LIKE 'to:%' COLLATE NOCASE OR channel LIKE 'WHISPER%' COLLATE NOCASE)))";
                cmd.Parameters.AddWithValue("$ch", channelNorm);
                cmd.Parameters.AddWithValue("$chPrefix", channelNorm + "%");
                cmd.Parameters.AddWithValue("$chWhisper",
                    channelNorm.StartsWith("WHISPER", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            }

            if (!string.IsNullOrWhiteSpace(sender))
            {
                sql += " AND sender = $sender COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$sender", sender.Trim());
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                sql += " AND (message LIKE $q OR readable_text LIKE $q OR sender LIKE $q)";
                cmd.Parameters.AddWithValue("$q", "%" + text.Trim() + "%");
            }

            sql += " ORDER BY id DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit + 1);
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var rows = new List<ChatLine>(limit + 1);
            while (reader.Read())
            {
                rows.Add(ReadLine(reader));
            }

            var hasMore = rows.Count > limit;
            if (hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            rows.Reverse();
            long? oldest = rows.Count > 0 ? rows[0].Id : null;
            long? newest = rows.Count > 0 ? rows[^1].Id : null;
            return new ChatPage(rows, oldest, newest, hasMore, limit);
        }
    }

    public int ApplySenderName(string guidHex, string name)
    {
        if (string.IsNullOrWhiteSpace(guidHex) || string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                """
                UPDATE chat_messages
                SET sender = $name,
                    readable_text = $name || ': ' || message
                WHERE sender_guid = $guid
                  AND (sender IS NULL OR sender = '');
                """;
            cmd.Parameters.AddWithValue("$name", name.Trim());
            cmd.Parameters.AddWithValue("$guid", guidHex.Trim());
            return cmd.ExecuteNonQuery();
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
                SELECT
                  (SELECT COUNT(*) FROM chat_messages WHERE is_loot = 0) AS social,
                  (SELECT COUNT(*) FROM chat_messages WHERE is_loot = 1) AS loot,
                  (SELECT MAX(id) FROM chat_messages) AS max_id;
                """;
            using var r = cmd.ExecuteReader();
            r.Read();
            return new
            {
                ok = true,
                path = _path,
                social = r.IsDBNull(0) ? 0 : r.GetInt64(0),
                loot = r.IsDBNull(1) ? 0 : r.GetInt64(1),
                maxId = r.IsDBNull(2) ? 0 : r.GetInt64(2)
            };
        }
    }

    private static ChatLine ReadLine(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var atRaw = reader.GetString(1);
        _ = DateTimeOffset.TryParse(atRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
        if (at == default)
        {
            at = DateTimeOffset.UtcNow;
        }

        return new ChatLine(
            at,
            (byte)reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            id,
            SafeString(reader, 16, "shared"),
            SafeString(reader, 17, ""),
            SafeString(reader, 18, ""),
            SafeString(reader, 19, ""));
    }

    private static string SafeString(SqliteDataReader reader, int ordinal, string fallback)
    {
        try
        {
            if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
            {
                return fallback;
            }

            return reader.GetString(ordinal);
        }
        catch
        {
            return fallback;
        }
    }

    private void Open()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        _conn = new SqliteConnection(cs);
        _conn.Open();
        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText =
                """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                PRAGMA busy_timeout=5000;
                """;
            pragma.ExecuteNonQuery();
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS chat_messages (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              received_at_utc TEXT NOT NULL,
              type INTEGER NOT NULL,
              language TEXT NOT NULL DEFAULT '',
              sender TEXT NOT NULL DEFAULT '',
              channel TEXT NOT NULL DEFAULT '',
              message TEXT NOT NULL DEFAULT '',
              sender_guid TEXT NOT NULL DEFAULT '',
              readable_text TEXT NOT NULL DEFAULT '',
              level INTEGER NOT NULL DEFAULT -1,
              class_id INTEGER NOT NULL DEFAULT -1,
              race INTEGER NOT NULL DEFAULT -1,
              guild TEXT NOT NULL DEFAULT '',
              zone TEXT NOT NULL DEFAULT '',
              target_guid TEXT NOT NULL DEFAULT '',
              direction TEXT NOT NULL DEFAULT '',
              is_loot INTEGER NOT NULL DEFAULT 0,
              scope TEXT NOT NULL DEFAULT 'shared',
              owner_user_id TEXT NOT NULL DEFAULT '',
              observer_account TEXT NOT NULL DEFAULT '',
              seen_by TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_chat_id_loot ON chat_messages(is_loot, id DESC);
            CREATE INDEX IF NOT EXISTS ix_chat_channel_id ON chat_messages(channel, id DESC);
            CREATE INDEX IF NOT EXISTS ix_chat_sender_id ON chat_messages(sender, id DESC);
            CREATE INDEX IF NOT EXISTS ix_chat_guid ON chat_messages(sender_guid);
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn("scope", "TEXT NOT NULL DEFAULT 'shared'");
        EnsureColumn("owner_user_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("observer_account", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("seen_by", "TEXT NOT NULL DEFAULT ''");
        using (var idx = _conn.CreateCommand())
        {
            idx.CommandText =
                "CREATE INDEX IF NOT EXISTS ix_chat_scope_owner ON chat_messages(scope, owner_user_id, id DESC);";
            idx.ExecuteNonQuery();
        }
    }

    private void EnsureColumn(string name, string decl)
    {
        try
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = $"ALTER TABLE chat_messages ADD COLUMN {name} {decl};";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // column already exists
        }
    }

    private void EnsureOpen()
    {
        if (_conn is null || _conn.State != ConnectionState.Open)
        {
            Open();
        }
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
