using System.Text.RegularExpressions;
using HeadlessClient.Domain.World;

namespace HeadlessClient.Infrastructure.Protocol;

/// <summary>Classifies LootCollector BBLC/KLCE/LC1 spam vs normal social chat.</summary>
public static class ChatTrafficClassifier
{
    static readonly Regex LcChannelRx = new(
        @"BBLC|KLCE|LOOTCOLLECTOR",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsLootCollector(ChatLine line)
    {
        if (line is null)
        {
            return false;
        }

        var msg = line.Message ?? "";
        if (msg.StartsWith("LC1:", StringComparison.Ordinal)
            || msg.StartsWith("LC2:", StringComparison.Ordinal)
            || msg.StartsWith("[LootCollector", StringComparison.Ordinal))
        {
            return true;
        }

        var ch = line.Channel ?? "";
        return LcChannelRx.IsMatch(ch);
    }
}
