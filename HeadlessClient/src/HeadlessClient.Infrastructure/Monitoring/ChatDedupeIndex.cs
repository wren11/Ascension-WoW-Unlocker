using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using HeadlessClient.Domain.World;

namespace HeadlessClient.Infrastructure.Monitoring;

/// <summary>
/// Deduplicate the same world chat line heard by multiple fleet accounts within a short window.
/// </summary>
public sealed class ChatDedupeIndex
{
    private readonly ConcurrentDictionary<string, DedupeEntry> _recent = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;

    public ChatDedupeIndex(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// Returns true if this is a new unique line (should persist + broadcast).
    /// False if duplicate — merges observer into SeenBy on the prior entry.
    /// </summary>
    public bool TryAccept(ChatLine line, out string fingerprint, out string mergedSeenBy)
    {
        fingerprint = Fingerprint(line);
        mergedSeenBy = MergeSeen(line.SeenBy, line.ObserverAccount);
        var now = DateTimeOffset.UtcNow;

        while (true)
        {
            if (_recent.TryGetValue(fingerprint, out var existing))
            {
                if (now - existing.FirstSeenUtc <= _window)
                {
                    var nextSeen = MergeSeen(existing.SeenBy, line.ObserverAccount, line.SeenBy);
                    var updated = existing with { SeenBy = nextSeen, LastSeenUtc = now, HitCount = existing.HitCount + 1 };
                    if (_recent.TryUpdate(fingerprint, updated, existing))
                    {
                        mergedSeenBy = nextSeen;
                        return false;
                    }

                    continue;
                }
            }

            var entry = new DedupeEntry(now, now, mergedSeenBy, 1);
            if (_recent.TryAdd(fingerprint, entry))
            {
                Prune(now);
                return true;
            }
        }
    }

    public static string Fingerprint(ChatLine line)
    {
        // Scope member lines are never cross-account duplicates of shared chat.
        var scope = string.IsNullOrWhiteSpace(line.Scope) ? "shared" : line.Scope.Trim().ToLowerInvariant();
        if (scope == "member")
        {
            return "member:" + (line.OwnerUserId ?? "") + ":" + Guid.NewGuid().ToString("N");
        }

        var raw = string.Join('\u001f',
            scope,
            line.Type.ToString(),
            (line.Channel ?? "").Trim().ToLowerInvariant(),
            (line.SenderGuid ?? "").Trim().ToUpperInvariant(),
            (line.Sender ?? "").Trim().ToLowerInvariant(),
            (line.TargetGuid ?? "").Trim().ToUpperInvariant(),
            (line.Direction ?? "").Trim().ToLowerInvariant(),
            NormalizeMessage(line.Message));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "";
        }

        var sb = new StringBuilder(message.Length);
        var prevSpace = false;
        foreach (var ch in message.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                }

                prevSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevSpace = false;
            }
        }

        return sb.ToString();
    }

    private static string MergeSeen(params string?[] parts)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            foreach (var bit in part.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (bit.Length > 0)
                {
                    set.Add(bit);
                }
            }
        }

        return string.Join(',', set);
    }

    private void Prune(DateTimeOffset now)
    {
        if (_recent.Count < 4000)
        {
            return;
        }

        foreach (var kv in _recent)
        {
            if (now - kv.Value.LastSeenUtc > _window + TimeSpan.FromSeconds(30))
            {
                _recent.TryRemove(kv.Key, out _);
            }
        }
    }

    private sealed record DedupeEntry(
        DateTimeOffset FirstSeenUtc,
        DateTimeOffset LastSeenUtc,
        string SeenBy,
        int HitCount);
}
