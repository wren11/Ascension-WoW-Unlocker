using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace AscensionNetTool;

/// <summary>
/// Async background SQLite logger for decoded chat / system messages + player dossiers.
/// Never runs on the game thread — ExtProxy only copies bytes to the ring;
/// GmToolbox drains, decodes, and enqueues here.
/// </summary>
sealed class ChatLogService : IDisposable
{
    readonly Channel<ChatDecoder.DecodedChat> _q = Channel.CreateBounded<ChatDecoder.DecodedChat>(
        new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    readonly Channel<PlayerDirectory.PlayerInfo> _players = Channel.CreateBounded<PlayerDirectory.PlayerInfo>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    readonly Channel<PacketStringRow> _strings = Channel.CreateBounded<PacketStringRow>(
        new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public sealed class PacketStringRow
    {
        public int InstanceId;
        public uint Opcode;
        public string Dir = "";
        public int Offset;
        public string Text = "";
        public string RawHexHead = "";
    }

    readonly CancellationTokenSource _cts = new();
    readonly Task _worker;
    readonly string _dbPath;
    long _written;
    long _dropped;
    long _playersWritten;

    public ChatLogService()
    {
        Directory.CreateDirectory(Path.Combine(Paths.AppRoot, "Config"));
        _dbPath = Path.Combine(Paths.AppRoot, "Config", "chat-log.db");
        EnsureSchema();
        _worker = Task.Run(WorkerLoop);
    }

    public string DbPath => _dbPath;
    public long Written => Interlocked.Read(ref _written);
    public long QueueDrops => Interlocked.Read(ref _dropped);
    public long PlayersWritten => Interlocked.Read(ref _playersWritten);

    /// <summary>Fired after a chat line is accepted into the queue (wire or Lua).</summary>
    public Action<ChatDecoder.DecodedChat>? OnChat { get; set; }

    public void Enqueue(ChatDecoder.DecodedChat chat, int instanceId)
    {
        string guid = PlayerDirectory.NormGuid(chat.SenderGuid);
        string name = chat.SenderName ?? "";
        if (string.IsNullOrWhiteSpace(name) && guid.Length > 0)
            name = PlayerDirectory.ResolveName(guid);
        if (guid.Length > 0)
            PlayerDirectory.NoteChat(guid, name);

        chat = new ChatDecoder.DecodedChat
        {
            Opcode = chat.Opcode,
            Kind = chat.Kind,
            ChatType = chat.ChatType,
            ChatTypeName = chat.ChatTypeName,
            Language = chat.Language,
            SenderGuid = guid,
            SenderName = name,
            TargetGuid = PlayerDirectory.NormGuid(chat.TargetGuid),
            Channel = chat.Channel,
            Message = chat.Message,
            ChatTag = chat.ChatTag,
            RawHex = chat.RawHex,
            InstanceId = instanceId,
        };
        if (!_q.Writer.TryWrite(chat))
            Interlocked.Increment(ref _dropped);
        else
        {
            try { OnChat?.Invoke(chat); } catch { }
        }
    }

    public void EnqueuePlayer(PlayerDirectory.PlayerInfo info)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.Guid)) return;
        _players.Writer.TryWrite(ClonePlayer(info));
    }

    public void EnqueuePacketStrings(PacketStringRow row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Text)) return;
        _strings.Writer.TryWrite(row);
    }

    /// <summary>Authoritative Lua chat (UnitName / CHAT_MSG_* text) — preferred over wire decode.</summary>
    public void EnqueueLuaChat(string channel, string sender, string message, string guid, int instanceId, string? extra = null)
    {
        guid = PlayerDirectory.NormGuid(guid);
        sender = (sender ?? "").Trim();
        message = (message ?? "").Trim();
        if (message.Length == 0 && sender.Length == 0) return;
        if (guid.Length > 0 && sender.Length > 0)
            PlayerDirectory.ObserveName(guid, sender);
        else if (guid.Length > 0)
            sender = PlayerDirectory.ResolveName(guid, sender);
        Enqueue(new ChatDecoder.DecodedChat
        {
            Opcode = 0,
            Kind = "lua_chat",
            ChatTypeName = string.IsNullOrWhiteSpace(channel) ? "CHAT" : channel,
            Channel = channel ?? "",
            SenderGuid = guid,
            SenderName = sender,
            Message = message,
            RawHex = extra ?? "",
            InstanceId = instanceId,
        }, instanceId);
    }

    static PlayerDirectory.PlayerInfo ClonePlayer(PlayerDirectory.PlayerInfo p) => new()
    {
        Guid = p.Guid,
        Name = p.Name,
        Realm = p.Realm,
        Race = p.Race,
        Gender = p.Gender,
        Class = p.Class,
        Level = p.Level,
        Faction = p.Faction,
        Hp = p.Hp,
        MaxHp = p.MaxHp,
        X = p.X,
        Y = p.Y,
        Z = p.Z,
        MapId = p.MapId,
        LastSeenUtc = p.LastSeenUtc,
        LastChatUtc = p.LastChatUtc,
        MessageCount = p.MessageCount,
    };

    void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS channels (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL UNIQUE,
              first_seen TEXT NOT NULL,
              last_seen TEXT NOT NULL,
              message_count INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS users (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              guid TEXT NOT NULL DEFAULT '',
              name TEXT NOT NULL DEFAULT '',
              first_seen TEXT NOT NULL,
              last_seen TEXT NOT NULL,
              message_count INTEGER NOT NULL DEFAULT 0,
              UNIQUE(guid, name)
            );
            CREATE TABLE IF NOT EXISTS players (
              guid TEXT PRIMARY KEY,
              name TEXT NOT NULL DEFAULT '',
              realm TEXT NOT NULL DEFAULT '',
              race INTEGER NOT NULL DEFAULT -1,
              gender INTEGER NOT NULL DEFAULT -1,
              class INTEGER NOT NULL DEFAULT -1,
              level INTEGER NOT NULL DEFAULT -1,
              faction INTEGER NOT NULL DEFAULT -1,
              hp INTEGER NOT NULL DEFAULT -1,
              max_hp INTEGER NOT NULL DEFAULT -1,
              x REAL NOT NULL DEFAULT 0,
              y REAL NOT NULL DEFAULT 0,
              z REAL NOT NULL DEFAULT 0,
              map_id INTEGER NOT NULL DEFAULT -1,
              first_seen TEXT NOT NULL,
              last_seen TEXT NOT NULL,
              last_chat TEXT,
              message_count INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_players_name ON players(name);
            CREATE TABLE IF NOT EXISTS messages (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              ts_utc TEXT NOT NULL,
              instance_id INTEGER NOT NULL DEFAULT 0,
              opcode INTEGER NOT NULL,
              kind TEXT NOT NULL,
              chat_type INTEGER,
              chat_type_name TEXT,
              language INTEGER,
              channel_id INTEGER,
              sender_user_id INTEGER,
              sender_guid TEXT,
              sender_name TEXT,
              target_guid TEXT,
              channel_name TEXT,
              message TEXT,
              chat_tag INTEGER,
              raw_hex TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_messages_ts ON messages(ts_utc);
            CREATE INDEX IF NOT EXISTS idx_messages_channel ON messages(channel_id);
            CREATE INDEX IF NOT EXISTS idx_messages_sender ON messages(sender_user_id);
            CREATE INDEX IF NOT EXISTS idx_messages_kind ON messages(kind);
            CREATE INDEX IF NOT EXISTS idx_messages_sguid ON messages(sender_guid);
            CREATE TABLE IF NOT EXISTS packet_strings (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              ts_utc TEXT NOT NULL,
              instance_id INTEGER NOT NULL DEFAULT 0,
              opcode INTEGER NOT NULL,
              dir TEXT NOT NULL DEFAULT '',
              str_offset INTEGER NOT NULL DEFAULT 0,
              text TEXT NOT NULL,
              raw_hex_head TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_packet_strings_ts ON packet_strings(ts_utc);
            CREATE INDEX IF NOT EXISTS idx_packet_strings_op ON packet_strings(opcode);
            """;
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        conn.Open();
        return conn;
    }

    async Task WorkerLoop()
    {
        var batch = new List<ChatDecoder.DecodedChat>(64);
        var pbatch = new List<PlayerDirectory.PlayerInfo>(32);
        var sbatch = new List<PacketStringRow>(64);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var chatWait = _q.Reader.WaitToReadAsync(_cts.Token).AsTask();
                var playerWait = _players.Reader.WaitToReadAsync(_cts.Token).AsTask();
                var strWait = _strings.Reader.WaitToReadAsync(_cts.Token).AsTask();
                var done = await Task.WhenAny(chatWait, playerWait, strWait).ConfigureAwait(false);
                if (done.IsCanceled) break;

                batch.Clear();
                while (batch.Count < 64 && _q.Reader.TryRead(out var item))
                    batch.Add(item);
                pbatch.Clear();
                while (pbatch.Count < 64 && _players.Reader.TryRead(out var p))
                    pbatch.Add(p);
                sbatch.Clear();
                while (sbatch.Count < 64 && _strings.Reader.TryRead(out var s))
                    sbatch.Add(s);

                if (batch.Count == 0 && pbatch.Count == 0 && sbatch.Count == 0)
                {
                    await Task.Delay(15, _cts.Token).ConfigureAwait(false);
                    continue;
                }
                try { FlushBatch(batch, pbatch, sbatch); }
                catch { /* keep worker alive */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    void FlushBatch(List<ChatDecoder.DecodedChat> batch, List<PlayerDirectory.PlayerInfo> players, List<PacketStringRow> strings)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var p in players)
            UpsertPlayerRow(conn, p);
        foreach (var c in batch)
            InsertOne(conn, c);
        foreach (var s in strings)
            InsertPacketString(conn, s);
        tx.Commit();
        if (batch.Count > 0) Interlocked.Add(ref _written, batch.Count);
        if (players.Count > 0) Interlocked.Add(ref _playersWritten, players.Count);
    }

    static void InsertPacketString(SqliteConnection conn, PacketStringRow s)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO packet_strings(ts_utc, instance_id, opcode, dir, str_offset, text, raw_hex_head)
            VALUES($ts, $inst, $op, $dir, $off, $txt, $hex);
            """;
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$inst", s.InstanceId);
        cmd.Parameters.AddWithValue("$op", (int)s.Opcode);
        cmd.Parameters.AddWithValue("$dir", s.Dir ?? "");
        cmd.Parameters.AddWithValue("$off", s.Offset);
        cmd.Parameters.AddWithValue("$txt", s.Text ?? "");
        cmd.Parameters.AddWithValue("$hex", s.RawHexHead ?? "");
        cmd.ExecuteNonQuery();
    }

    void InsertOne(SqliteConnection conn, ChatDecoder.DecodedChat c)
    {
        string ts = DateTime.UtcNow.ToString("o");
        string channelName = string.IsNullOrWhiteSpace(c.Channel)
            ? (string.IsNullOrWhiteSpace(c.ChatTypeName) ? c.Kind : c.ChatTypeName)
            : c.Channel;
        string guid = PlayerDirectory.NormGuid(c.SenderGuid);
        string name = c.SenderName ?? "";
        if (string.IsNullOrWhiteSpace(name) && guid.Length > 0)
            name = ResolveStoredName(conn, guid);

        long? channelId = UpsertChannel(conn, channelName, ts);
        long? userId = UpsertUser(conn, guid, name, ts);
        if (guid.Length > 0)
            TouchPlayerFromChat(conn, guid, name, ts);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages(
              ts_utc, instance_id, opcode, kind, chat_type, chat_type_name, language,
              channel_id, sender_user_id, sender_guid, sender_name, target_guid,
              channel_name, message, chat_tag, raw_hex)
            VALUES(
              $ts, $inst, $op, $kind, $ctype, $ctname, $lang,
              $chid, $uid, $sguid, $sname, $tguid,
              $chname, $msg, $tag, $hex);
            """;
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$inst", c.InstanceId);
        cmd.Parameters.AddWithValue("$op", (int)c.Opcode);
        cmd.Parameters.AddWithValue("$kind", c.Kind);
        cmd.Parameters.AddWithValue("$ctype", c.ChatType);
        cmd.Parameters.AddWithValue("$ctname", c.ChatTypeName ?? "");
        cmd.Parameters.AddWithValue("$lang", (int)c.Language);
        cmd.Parameters.AddWithValue("$chid", (object?)channelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uid", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sguid", guid);
        cmd.Parameters.AddWithValue("$sname", name);
        cmd.Parameters.AddWithValue("$tguid", c.TargetGuid ?? "");
        cmd.Parameters.AddWithValue("$chname", channelName);
        cmd.Parameters.AddWithValue("$msg", c.Message ?? "");
        cmd.Parameters.AddWithValue("$tag", (int)c.ChatTag);
        cmd.Parameters.AddWithValue("$hex", TruncHex(c.RawHex));
        cmd.ExecuteNonQuery();
    }

    static string ResolveStoredName(SqliteConnection conn, string guid)
    {
        string mem = PlayerDirectory.ResolveName(guid);
        if (!string.IsNullOrWhiteSpace(mem)) return mem;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM players WHERE guid=$g AND name<>'' LIMIT 1";
        cmd.Parameters.AddWithValue("$g", guid);
        var v = cmd.ExecuteScalar()?.ToString();
        return v ?? "";
    }

    static void TouchPlayerFromChat(SqliteConnection conn, string guid, string name, string ts)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO players(guid, name, first_seen, last_seen, last_chat, message_count)
            VALUES($g, $n, $t, $t, $t, 1)
            ON CONFLICT(guid) DO UPDATE SET
              name = CASE WHEN excluded.name <> '' THEN excluded.name ELSE players.name END,
              last_seen = excluded.last_seen,
              last_chat = excluded.last_chat,
              message_count = players.message_count + 1;
            """;
        cmd.Parameters.AddWithValue("$g", guid);
        cmd.Parameters.AddWithValue("$n", name ?? "");
        cmd.Parameters.AddWithValue("$t", ts);
        cmd.ExecuteNonQuery();
    }

    static void UpsertPlayerRow(SqliteConnection conn, PlayerDirectory.PlayerInfo p)
    {
        string guid = PlayerDirectory.NormGuid(p.Guid);
        if (guid.Length == 0) return;
        string ts = (p.LastSeenUtc == default ? DateTime.UtcNow : p.LastSeenUtc).ToString("o");
        string name = p.Name ?? "";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO players(
              guid, name, realm, race, gender, class, level, faction, hp, max_hp,
              x, y, z, map_id, first_seen, last_seen, last_chat, message_count)
            VALUES(
              $g, $n, $realm, $race, $gender, $class, $level, $faction, $hp, $maxhp,
              $x, $y, $z, $map, $t, $t, $lchat, $mc)
            ON CONFLICT(guid) DO UPDATE SET
              name = CASE WHEN excluded.name <> '' THEN excluded.name ELSE players.name END,
              realm = CASE WHEN excluded.realm <> '' THEN excluded.realm ELSE players.realm END,
              race = CASE WHEN excluded.race >= 0 THEN excluded.race ELSE players.race END,
              gender = CASE WHEN excluded.gender >= 0 THEN excluded.gender ELSE players.gender END,
              class = CASE WHEN excluded.class >= 0 THEN excluded.class ELSE players.class END,
              level = CASE WHEN excluded.level > 0 THEN excluded.level ELSE players.level END,
              faction = CASE WHEN excluded.faction != 0 THEN excluded.faction ELSE players.faction END,
              hp = CASE WHEN excluded.max_hp > 0 THEN excluded.hp ELSE players.hp END,
              max_hp = CASE WHEN excluded.max_hp > 0 THEN excluded.max_hp ELSE players.max_hp END,
              x = excluded.x, y = excluded.y, z = excluded.z,
              map_id = CASE WHEN excluded.map_id >= 0 THEN excluded.map_id ELSE players.map_id END,
              last_seen = excluded.last_seen,
              last_chat = COALESCE(excluded.last_chat, players.last_chat),
              message_count = MAX(players.message_count, excluded.message_count);
            """;
        cmd.Parameters.AddWithValue("$g", guid);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$realm", p.Realm ?? "");
        cmd.Parameters.AddWithValue("$race", p.Race);
        cmd.Parameters.AddWithValue("$gender", p.Gender);
        cmd.Parameters.AddWithValue("$class", p.Class);
        cmd.Parameters.AddWithValue("$level", p.Level);
        cmd.Parameters.AddWithValue("$faction", p.Faction);
        cmd.Parameters.AddWithValue("$hp", p.Hp);
        cmd.Parameters.AddWithValue("$maxhp", p.MaxHp);
        cmd.Parameters.AddWithValue("$x", p.X);
        cmd.Parameters.AddWithValue("$y", p.Y);
        cmd.Parameters.AddWithValue("$z", p.Z);
        cmd.Parameters.AddWithValue("$map", p.MapId);
        cmd.Parameters.AddWithValue("$t", ts);
        cmd.Parameters.AddWithValue("$lchat", (object?)p.LastChatUtc?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mc", p.MessageCount);
        cmd.ExecuteNonQuery();

        if (!string.IsNullOrWhiteSpace(name))
            BackfillSenderName(conn, guid, name);
    }

    static void BackfillSenderName(SqliteConnection conn, string guid, string name)
    {
        using (var m = conn.CreateCommand())
        {
            m.CommandText = """
                UPDATE messages SET sender_name=$n
                WHERE sender_guid=$g AND (sender_name IS NULL OR sender_name='');
                """;
            m.Parameters.AddWithValue("$n", name);
            m.Parameters.AddWithValue("$g", guid);
            m.ExecuteNonQuery();
        }
        using (var u = conn.CreateCommand())
        {
            u.CommandText = """
                UPDATE users SET name=$n
                WHERE guid=$g AND (name IS NULL OR name='');
                """;
            u.Parameters.AddWithValue("$n", name);
            u.Parameters.AddWithValue("$g", guid);
            u.ExecuteNonQuery();
        }
    }

    static string TruncHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return "";
        return hex.Length <= 2048 ? hex : hex[..2048];
    }

    static long UpsertChannel(SqliteConnection conn, string name, string ts)
    {
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT id FROM channels WHERE name=$n";
            sel.Parameters.AddWithValue("$n", name);
            var v = sel.ExecuteScalar();
            if (v is long id || (v is not null && long.TryParse(v.ToString(), out id)))
            {
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE channels SET last_seen=$t, message_count=message_count+1 WHERE id=$id";
                upd.Parameters.AddWithValue("$t", ts);
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
                return id;
            }
        }
        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO channels(name, first_seen, last_seen, message_count)
            VALUES($n, $t, $t, 1);
            SELECT last_insert_rowid();
            """;
        ins.Parameters.AddWithValue("$n", name);
        ins.Parameters.AddWithValue("$t", ts);
        return (long)(ins.ExecuteScalar() ?? 0L);
    }

    static long? UpsertUser(SqliteConnection conn, string guid, string name, string ts)
    {
        if (string.IsNullOrEmpty(guid) && string.IsNullOrEmpty(name))
            return null;

        // Prefer match by GUID (canonical identity), then fall back to name-only rows.
        if (!string.IsNullOrEmpty(guid))
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT id, name FROM users WHERE guid=$g ORDER BY CASE WHEN name<>'' THEN 0 ELSE 1 END, id LIMIT 1";
            sel.Parameters.AddWithValue("$g", guid);
            using var r = sel.ExecuteReader();
            if (r.Read())
            {
                long id = r.GetInt64(0);
                string existing = r.IsDBNull(1) ? "" : r.GetString(1);
                r.Close();
                using var upd = conn.CreateCommand();
                upd.CommandText = """
                    UPDATE users SET last_seen=$t, message_count=message_count+1,
                      name = CASE WHEN $n <> '' THEN $n ELSE name END
                    WHERE id=$id;
                    """;
                upd.Parameters.AddWithValue("$t", ts);
                upd.Parameters.AddWithValue("$n", name ?? "");
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
                _ = existing;
                return id;
            }
        }

        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO users(guid, name, first_seen, last_seen, message_count)
            VALUES($g, $n, $t, $t, 1);
            SELECT last_insert_rowid();
            """;
        ins.Parameters.AddWithValue("$g", guid ?? "");
        ins.Parameters.AddWithValue("$n", name ?? "");
        ins.Parameters.AddWithValue("$t", ts);
        return (long)(ins.ExecuteScalar() ?? 0L);
    }

    public object RecentMessages(int limit = 10, string? channel = null)
    {
        limit = Math.Clamp(limit, 1, 1000);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        bool filter = !string.IsNullOrWhiteSpace(channel) && !channel.Equals("all", StringComparison.OrdinalIgnoreCase);
        cmd.CommandText = filter
            ? """
            SELECT m.id, m.ts_utc, m.instance_id, m.kind, m.chat_type_name, m.channel_name,
                   COALESCE(NULLIF(m.sender_name, ''), p.name, '') AS sender,
                   m.sender_guid, m.message
            FROM messages m
            LEFT JOIN players p ON p.guid = m.sender_guid
            WHERE m.channel_name LIKE $ch
            ORDER BY m.id DESC LIMIT $lim
            """
            : """
            SELECT m.id, m.ts_utc, m.instance_id, m.kind, m.chat_type_name, m.channel_name,
                   COALESCE(NULLIF(m.sender_name, ''), p.name, '') AS sender,
                   m.sender_guid, m.message
            FROM messages m
            LEFT JOIN players p ON p.guid = m.sender_guid
            ORDER BY m.id DESC LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        if (filter)
            cmd.Parameters.AddWithValue("$ch", "%" + channel!.Trim() + "%");
        var rows = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string guid = r.IsDBNull(7) ? "" : r.GetString(7);
            string sender = r.IsDBNull(6) ? "" : r.GetString(6);
            if (string.IsNullOrWhiteSpace(sender) && guid.Length > 0)
                sender = PlayerDirectory.ResolveName(guid);
            rows.Add(new
            {
                id = r.GetInt64(0),
                ts = r.GetString(1),
                instanceId = r.GetInt32(2),
                kind = r.GetString(3),
                chatType = r.IsDBNull(4) ? "" : r.GetString(4),
                channel = r.IsDBNull(5) ? "" : r.GetString(5),
                sender,
                senderGuid = guid,
                message = r.IsDBNull(8) ? "" : r.GetString(8),
            });
        }
        return rows;
    }

    public object RecentPacketStrings(int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 500);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts_utc, instance_id, opcode, dir, str_offset, text
            FROM packet_strings ORDER BY id DESC LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        var rows = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new
            {
                id = r.GetInt64(0),
                ts = r.GetString(1),
                instanceId = r.GetInt32(2),
                opcode = r.GetInt32(3),
                dir = r.IsDBNull(4) ? "" : r.GetString(4),
                offset = r.GetInt32(5),
                text = r.IsDBNull(6) ? "" : r.GetString(6),
            });
        }
        return rows;
    }

    public object Channels()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, first_seen, last_seen, message_count FROM channels ORDER BY message_count DESC";
        var rows = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new
            {
                id = r.GetInt64(0),
                name = r.GetString(1),
                firstSeen = r.GetString(2),
                lastSeen = r.GetString(3),
                messageCount = r.GetInt64(4),
            });
        }
        return rows;
    }

    public object Users()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // Prefer players dossier; fall back to users for GUIDs not yet in players.
        cmd.CommandText = """
            SELECT guid, name, message_count, last_seen, first_seen, level, class, race, faction, realm
            FROM (
              SELECT guid, name, message_count, last_seen, first_seen,
                     level, class, race, faction, realm
              FROM players
              UNION ALL
              SELECT u.guid, u.name, u.message_count, u.last_seen, u.first_seen,
                     -1, -1, -1, -1, ''
              FROM users u
              WHERE NOT EXISTS (SELECT 1 FROM players p WHERE p.guid = u.guid)
            )
            ORDER BY message_count DESC
            LIMIT 500
            """;
        var rows = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string guid = r.IsDBNull(0) ? "" : r.GetString(0);
            string name = r.IsDBNull(1) ? "" : r.GetString(1);
            if (string.IsNullOrWhiteSpace(name) && guid.Length > 0)
                name = PlayerDirectory.ResolveName(guid);
            rows.Add(new
            {
                guid,
                name,
                display = string.IsNullOrWhiteSpace(name) ? guid : name,
                messageCount = r.IsDBNull(2) ? 0L : r.GetInt64(2),
                lastSeen = r.IsDBNull(3) ? "" : r.GetString(3),
                firstSeen = r.IsDBNull(4) ? "" : r.GetString(4),
                level = r.IsDBNull(5) ? -1 : r.GetInt32(5),
                classId = r.IsDBNull(6) ? -1 : r.GetInt32(6),
                race = r.IsDBNull(7) ? -1 : r.GetInt32(7),
                faction = r.IsDBNull(8) ? -1 : r.GetInt32(8),
                realm = r.IsDBNull(9) ? "" : r.GetString(9),
            });
        }
        return rows;
    }

    /// <summary>Every distinct player/user name in chat-log.db (no cap).</summary>
    public List<string> AllUserNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT name FROM (
                  SELECT name FROM players
                  UNION
                  SELECT name FROM users
                )
                WHERE name IS NOT NULL AND TRIM(name) != ''
                ORDER BY name COLLATE NOCASE
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0)) continue;
                var name = r.GetString(0).Trim();
                if (name.Length < 2 || name.Length > 48) continue;
                if (!seen.Add(name)) continue;
                names.Add(name);
            }
        }
        catch
        {
            /* schema not ready */
        }
        return names;
    }

    public object Players(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 2000);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT guid, name, realm, race, gender, class, level, faction, hp, max_hp,
                   x, y, z, map_id, first_seen, last_seen, last_chat, message_count
            FROM players
            ORDER BY last_seen DESC
            LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        var rows = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string guid = r.GetString(0);
            string name = r.IsDBNull(1) ? "" : r.GetString(1);
            if (string.IsNullOrWhiteSpace(name))
                name = PlayerDirectory.ResolveName(guid);
            rows.Add(new
            {
                guid,
                name,
                display = string.IsNullOrWhiteSpace(name) ? guid : name,
                realm = r.IsDBNull(2) ? "" : r.GetString(2),
                race = r.GetInt32(3),
                gender = r.GetInt32(4),
                classId = r.GetInt32(5),
                level = r.GetInt32(6),
                faction = r.GetInt32(7),
                hp = r.GetInt32(8),
                maxHp = r.GetInt32(9),
                x = r.GetFloat(10),
                y = r.GetFloat(11),
                z = r.GetFloat(12),
                mapId = r.GetInt32(13),
                firstSeen = r.GetString(14),
                lastSeen = r.GetString(15),
                lastChat = r.IsDBNull(16) ? "" : r.GetString(16),
                messageCount = r.GetInt64(17),
            });
        }
        return rows;
    }

    public object Stats() => new
    {
        db = _dbPath,
        written = Written,
        playersWritten = PlayersWritten,
        queueDrops = QueueDrops,
        channels = Count("channels"),
        users = Count("users"),
        players = Count("players"),
        messages = Count("messages"),
        packetStrings = Count("packet_strings"),
    };

    long Count(string table)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
        catch { return 0; }
    }

    public int PruneOlderThanDays(int days)
    {
        if (days <= 0) return 0;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM messages WHERE ts_utc < datetime('now', @off); " +
                "DELETE FROM packet_strings WHERE ts_utc < datetime('now', @off);";
            cmd.Parameters.AddWithValue("@off", $"-{days} days");
            return cmd.ExecuteNonQuery();
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        int days = SettingsStore.Current.ChatRetentionDays;
        if (days > 0)
            PruneOlderThanDays(days);
        _cts.Cancel();
        _q.Writer.TryComplete();
        _players.Writer.TryComplete();
        _strings.Writer.TryComplete();
        try { _worker.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
