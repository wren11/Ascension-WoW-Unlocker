using System.Collections.Concurrent;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Persistence;
using HeadlessClient.Infrastructure.Protocol;

namespace HeadlessClient.Infrastructure.Monitoring;

/// <summary>
/// Hot in-memory ring + SQLite persistence. Live SSE reads memory; scroll-up uses SQLite keyset pagination.
/// Shared realm chat is deduplicated across fleet accounts; member-scoped lines stay private.
/// </summary>
public sealed class PersistentChatLog : IObservableChatLog, IChatHistory, IDisposable
{
    private readonly SqliteChatStore _store;
    private readonly ConcurrentQueue<ChatLine> _social = new();
    private readonly ConcurrentQueue<ChatLine> _loot = new();
    private readonly ChatDedupeIndex _dedupe = new();
    private readonly object _rewriteGate = new();
    private readonly int _capacity;

    public PersistentChatLog(SqliteChatStore store, int capacity = 2000)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        WarmRecent();
    }

    public string DbPath => _store.PathUsed;
    public int SocialCount => _social.Count;
    public int LootCount => _loot.Count;
    public event Action<ChatLine>? LineAppended;

    public void Append(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        // Drop WIM / addon handshake noise that leaks as whispers (e.g. !NEGOTIATE:3.3.7:0).
        if (IsAddonHandshakeNoise(line.Message))
        {
            return;
        }

        // Login / join plumbing — keep SoftRealm chat for real player talk only.
        if (IsChannelPlumbingNoise(line))
        {
            return;
        }

        var scope = string.IsNullOrWhiteSpace(line.Scope) ? "shared" : line.Scope.Trim().ToLowerInvariant();
        line = line with { Scope = scope };

        if (scope == "shared")
        {
            if (!_dedupe.TryAccept(line, out _, out var seenBy))
            {
                // Another account already logged this — consolidated, not duplicated.
                return;
            }

            line = line with { SeenBy = seenBy };
        }

        var id = _store.Insert(line, loot: false);
        var stored = line with { Id = id };
        if (scope == "shared" || string.IsNullOrEmpty(line.OwnerUserId))
        {
            _social.Enqueue(stored);
            Trim(_social);
        }

        try { LineAppended?.Invoke(stored); } catch { /* never throw to callers */ }
    }

    public void AppendLoot(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!_dedupe.TryAccept(line with { Scope = "shared" }, out _, out var seenBy))
        {
            return;
        }

        line = line with { Scope = "shared", SeenBy = seenBy };
        var id = _store.Insert(line, loot: true);
        var stored = line with { Id = id };
        _loot.Enqueue(stored);
        Trim(_loot);
    }

    public IReadOnlyList<ChatLine> Recent(int max) =>
        TakeRecent(_social, max).Where(l =>
            string.IsNullOrEmpty(l.Scope) || l.Scope.Equals("shared", StringComparison.OrdinalIgnoreCase)).ToList();

    public IReadOnlyList<ChatLine> RecentLoot(int max) => TakeRecent(_loot, max);

    public ChatPage Query(
        int limit = 50,
        long? beforeId = null,
        string? channel = null,
        string? sender = null,
        string? text = null,
        bool loot = false,
        string? scope = null,
        string? ownerUserId = null) =>
        _store.Query(limit, beforeId, channel, sender, text, loot, scope, ownerUserId);

    public Task<ChatPage> QueryAsync(
        int limit = 50,
        long? beforeId = null,
        string? channel = null,
        string? sender = null,
        string? text = null,
        bool loot = false,
        string? scope = null,
        string? ownerUserId = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => _store.Query(limit, beforeId, channel, sender, text, loot, scope, ownerUserId),
            cancellationToken);

    public object GetStats() => _store.GetStats();

    public int ApplySenderName(string guidHex, string name)
    {
        if (string.IsNullOrWhiteSpace(guidHex) || string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        var dbUpdated = _store.ApplySenderName(guidHex, name);
        lock (_rewriteGate)
        {
            var updated = 0;
            var items = _social.ToArray();
            if (items.Length == 0)
            {
                return dbUpdated;
            }

            while (_social.TryDequeue(out _))
            {
            }

            foreach (var line in items)
            {
                if (string.IsNullOrWhiteSpace(line.Sender)
                    && string.Equals(line.SenderGuid, guidHex, StringComparison.OrdinalIgnoreCase))
                {
                    var next = line with
                    {
                        Sender = name,
                        ReadableText = $"{name}: {line.Message}"
                    };
                    _social.Enqueue(next);
                    updated++;
                }
                else if (line.Type == ChatTypes.WhisperInform
                         && line.Channel is "WHISPER_OUT" or "WHISPER_INFORM"
                         && string.Equals(line.TargetGuid, guidHex, StringComparison.OrdinalIgnoreCase))
                {
                    _social.Enqueue(line with { Channel = $"to:{name}" });
                    updated++;
                }
                else
                {
                    _social.Enqueue(line);
                }
            }

            return Math.Max(updated, dbUpdated);
        }
    }

    private void WarmRecent()
    {
        try
        {
            var page = _store.Query(limit: Math.Min(_capacity, 500), scope: "shared");
            foreach (var line in page.Messages)
            {
                _social.Enqueue(line);
            }
        }
        catch
        {
            // empty / corrupt — start fresh
        }
    }

    private void Trim(ConcurrentQueue<ChatLine> q)
    {
        while (q.Count > _capacity && q.TryDequeue(out _))
        {
        }
    }

    private static IReadOnlyList<ChatLine> TakeRecent(ConcurrentQueue<ChatLine> q, int max)
    {
        if (max <= 0)
        {
            return Array.Empty<ChatLine>();
        }

        var all = q.ToArray();
        if (all.Length <= max)
        {
            return all;
        }

        return all.AsSpan(all.Length - max).ToArray();
    }

    /// <summary>
    /// WIM and similar addons whisper version probes that look like chat to SoftRealm.
    /// </summary>
    internal static bool IsAddonHandshakeNoise(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var m = message.Trim();
        // WIM Negotiate / W2W version probes (sometimes prefixed with "WIM\t")
        if (m.StartsWith("!NEGOTIATE:", StringComparison.OrdinalIgnoreCase)
            || m.Contains("!NEGOTIATE:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (m.StartsWith("!WIM", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("WIM\t", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("WIM ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // pfQuest / pfqe version probes on guild/channels
        if (m.StartsWith("pfQuest", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("pfqe", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("CleanerChat", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("ATR\t", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("ATR ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (m.Contains('\t') && m.Contains("VERSION:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Guild addon binary blobs (LootCollector / DTLS / LRS)
        if (m.StartsWith("DTLS", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("LRS", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("LC1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>Channel-join failures + login essence/uptime spam — not SoftRealm chat.</summary>
    internal static bool IsChannelPlumbingNoise(ChatLine line)
    {
        var ch = (line.Channel ?? "").Trim();
        var msg = (line.Message ?? "").Trim();
        if (ch.Equals("CHANNEL_JOIN", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ch.Equals("Misc", StringComparison.OrdinalIgnoreCase)
            && msg.StartsWith("Invalid channel", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (msg.StartsWith("Invalid channel name grouped", StringComparison.OrdinalIgnoreCase)
            || msg.StartsWith("Invalid channel →", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (msg.StartsWith("Server uptime:", StringComparison.OrdinalIgnoreCase)
            || msg.StartsWith("You have ", StringComparison.OrdinalIgnoreCase)
               && msg.Contains("unspent", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public void Dispose() => _store.Dispose();
}
