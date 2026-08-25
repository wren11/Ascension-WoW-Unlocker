using System.Collections.Concurrent;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Protocol;

namespace HeadlessClient.Infrastructure.Monitoring;

public sealed class InMemoryChatLog : IObservableChatLog
{
    private readonly ConcurrentQueue<ChatLine> _social = new();
    private readonly ConcurrentQueue<ChatLine> _loot = new();
    private readonly object _rewriteGate = new();
    private readonly int _capacity;

    public InMemoryChatLog(int capacity = 2000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public int SocialCount => _social.Count;
    public int LootCount => _loot.Count;

    public event Action<ChatLine>? LineAppended;

    public void Append(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _social.Enqueue(line);
        Trim(_social);
        try { LineAppended?.Invoke(line); } catch { /* never throw to callers */ }
    }

    public void AppendLoot(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _loot.Enqueue(line);
        Trim(_loot);
    }

    /// <summary>Back-fill sender names once NAME_QUERY resolves a GUID.</summary>
    public int ApplySenderName(string guidHex, string name)
    {
        if (string.IsNullOrWhiteSpace(guidHex) || string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        lock (_rewriteGate)
        {
            var updated = 0;
            var items = _social.ToArray();
            if (items.Length == 0)
            {
                return 0;
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

            return updated;
        }
    }

    public IReadOnlyList<ChatLine> Recent(int max) => TakeRecent(_social, max);

    public IReadOnlyList<ChatLine> RecentLoot(int max) => TakeRecent(_loot, max);

    void Trim(ConcurrentQueue<ChatLine> q)
    {
        while (q.Count > _capacity && q.TryDequeue(out _))
        {
        }
    }

    static IReadOnlyList<ChatLine> TakeRecent(ConcurrentQueue<ChatLine> q, int max)
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
}
