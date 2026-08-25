using HeadlessClient.Domain.Auth;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.Session;
using HeadlessClient.Domain.World;

namespace HeadlessClient.Domain.Abstractions;

public interface IAuthClient
{
    Task<AuthLoginResult> LoginAndListRealmsAsync(Credentials credentials, CancellationToken cancellationToken);
}

public interface IWorldClient
{
    Task ConnectAsync(RealmInfo realm, byte[] sessionKey, CancellationToken cancellationToken);
    Task ConnectAsync(RealmInfo realm, byte[] sessionKey, ReadOnlyMemory<byte> authProofTail, CancellationToken cancellationToken);
    Task<IReadOnlyList<CharacterInfo>> EnumerateCharactersAsync(CancellationToken cancellationToken);
    Task EnterWorldAsync(ulong characterGuid, CancellationToken cancellationToken);
    SessionState State { get; }
    event Action<Packet>? PacketReceived;
    Task SendAsync(Packet packet, CancellationToken cancellationToken);
}

/// <summary>
/// Global shared Object Manager — all sessions aggregate into one GUID-keyed store.
/// </summary>
public interface IObjectDirectory
{
    IReadOnlyCollection<WorldObject> Snapshot();
    void Upsert(WorldObject obj);
    /// <summary>Merge a live sighting from a fleet session (preserves Name/Entry/HP).</summary>
    void Observe(WorldObject patch, string? seenBy = null);
    void ApplyIdentity(ulong guid, string? name = null, uint entry = 0, string? staticName = null);
    void UpsertStatic(string kind, uint entry, string name, byte typeId = 0);
    void Remove(ulong guid);
    /// <summary>Soft: drop session visibility; never wipe the global aggregate.</summary>
    void Clear();
    void SoftClearSession(string? sessionTag);
}

public interface IChatLog
{
    void Append(ChatLine line);
    void AppendLoot(ChatLine line);
    IReadOnlyList<ChatLine> Recent(int max);
    IReadOnlyList<ChatLine> RecentLoot(int max);
    int SocialCount { get; }
    int LootCount { get; }
}

/// <summary>Discord-style cursor pagination over persisted chat.</summary>
public interface IChatHistory
{
    /// <summary>
    /// Latest page (no before) or older page with id &lt; before, ascending for display.
    /// </summary>
    ChatPage Query(
        int limit = 25,
        long? beforeId = null,
        string? channel = null,
        string? sender = null,
        string? text = null,
        bool loot = false,
        string? scope = null,
        string? ownerUserId = null);

    Task<ChatPage> QueryAsync(
        int limit = 25,
        long? beforeId = null,
        string? channel = null,
        string? sender = null,
        string? text = null,
        bool loot = false,
        string? scope = null,
        string? ownerUserId = null,
        CancellationToken cancellationToken = default);

    object GetStats();
}

/// <summary>Normalize UI channel filters (CHANNEL:Name → Name) for SQLite matching.</summary>
public static class ChatChannelFilter
{
    public static string? Normalize(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return null;
        }

        channel = channel.Trim();
        if (channel is "*" or "all")
        {
            return null;
        }

        if (channel.StartsWith("CHANNEL:", StringComparison.OrdinalIgnoreCase))
        {
            channel = channel[8..].Trim();
        }
        else if (channel.StartsWith("#"))
        {
            channel = channel[1..].Trim();
        }

        return string.IsNullOrWhiteSpace(channel) ? null : channel;
    }
}

public sealed record ChatPage(
    IReadOnlyList<ChatLine> Messages,
    long? OldestId,
    long? NewestId,
    bool HasMore,
    int Limit);

public interface IObservableChatLog : IChatLog
{
    event Action<ChatLine>? LineAppended;
    int ApplySenderName(string guidHex, string name);
}

public interface IWorldActions
{
    Task SelectAsync(ulong guid, CancellationToken cancellationToken);
    Task LootAsync(ulong guid, CancellationToken cancellationToken);
    Task UseGameObjectAsync(ulong guid, CancellationToken cancellationToken);
    Task MoveFallLandAsync(float x, float y, float z, float o, CancellationToken cancellationToken);
    Task MoveHeartbeatAsync(float x, float y, float z, float o, CancellationToken cancellationToken);
}

public interface IAddonHost
{
    Task LoadConfiguredAddonsAsync(CancellationToken cancellationToken);
    Task FireEventAsync(string eventName, CancellationToken cancellationToken);
    IReadOnlyList<string> LoadedAddons { get; }
}

public interface ICredentialStore
{
    Credentials GetCredentials();
}

public interface IHeadlessOptions
{
    string AuthHost { get; }
    int AuthPort { get; }
    int ClientBuild { get; }
    string? PreferredRealm { get; }
    string? PreferredCharacter { get; }
    string AddonsRoot { get; }
    IReadOnlyList<string> EnabledAddons { get; }
    int MonitorPort { get; }
    string AscensionChallengePath { get; }
    /// <summary>Optional 40-byte session key override (ExtProxy dump). Ascension K is not stock SRP.</summary>
    string SessionKeyOverridePath { get; }
    string RuntimeInstDir { get; }
}
