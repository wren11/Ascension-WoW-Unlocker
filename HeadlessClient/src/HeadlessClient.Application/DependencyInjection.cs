using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Auth;
using HeadlessClient.Domain.World;
using Microsoft.Extensions.DependencyInjection;

namespace HeadlessClient.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddHeadlessApplication(this IServiceCollection services)
    {
        services.AddSingleton<LoginAndEnterWorldUseCase>();
        services.AddSingleton<MonitorQueryService>();
        services.AddSingleton<RunAddonsUseCase>();
        return services;
    }
}

public sealed class LoginAndEnterWorldUseCase
{
    private readonly IAuthClient _auth;
    private readonly IWorldClient _world;
    private readonly ICredentialStore _credentials;
    private readonly IHeadlessOptions _options;

    public LoginAndEnterWorldUseCase(
        IAuthClient auth,
        IWorldClient world,
        ICredentialStore credentials,
        IHeadlessOptions options)
    {
        _auth = auth;
        _world = world;
        _credentials = credentials;
        _options = options;
    }

    public async Task<EnterWorldResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var creds = _credentials.GetCredentials();
        Console.WriteLine($"[login] auth begin account={creds.Account}");
        var login = await _auth.LoginAndListRealmsAsync(creds, cancellationToken);
        Console.WriteLine($"[login] auth ok realms={login.Realms.Count} key={Convert.ToHexString(login.SessionKey.AsSpan(0, 4))}...");
        var realm = SelectRealm(login.Realms);
        if (realm is null)
        {
            return EnterWorldResult.Fail("No matching realm");
        }

        Console.WriteLine($"[login] world connect {realm.Name} {realm.Address} id={realm.Id}");
        await _world.ConnectAsync(realm, login.SessionKey, login.AuthProofTail, cancellationToken);
        Console.WriteLine("[login] world auth ok — CharacterSelect");
        var characters = await _world.EnumerateCharactersAsync(cancellationToken);
        Console.WriteLine($"[login] char enum count={characters.Count}");
        var character = SelectCharacter(characters);
        if (character is null)
        {
            return EnterWorldResult.Fail("No matching character");
        }

        Console.WriteLine($"[login] enter world {character.Name} guid={character.Guid:X16}");
        await _world.EnterWorldAsync(character.Guid, cancellationToken);
        Console.WriteLine("[login] enter world ok");
        return EnterWorldResult.Ok(realm, character, characters);
    }

    private RealmInfo? SelectRealm(IReadOnlyList<RealmInfo> realms)
    {
        if (realms.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_options.PreferredRealm))
        {
            return realms.FirstOrDefault(r =>
                r.Name.Equals(_options.PreferredRealm, StringComparison.OrdinalIgnoreCase)) ?? realms[0];
        }

        return realms[0];
    }

    private CharacterInfo? SelectCharacter(IReadOnlyList<CharacterInfo> characters)
    {
        if (characters.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_options.PreferredCharacter))
        {
            // Strict: when a character name is configured, never fall back to another toon.
            return characters.FirstOrDefault(c =>
                c.Name.Equals(_options.PreferredCharacter, StringComparison.OrdinalIgnoreCase));
        }

        return characters[0];
    }
}

public sealed class EnterWorldResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public RealmInfo? Realm { get; init; }
    public CharacterInfo? Character { get; init; }
    public IReadOnlyList<CharacterInfo> Characters { get; init; } = Array.Empty<CharacterInfo>();

    public static EnterWorldResult Ok(RealmInfo realm, CharacterInfo character, IReadOnlyList<CharacterInfo>? all = null) =>
        new()
        {
            Success = true,
            Realm = realm,
            Character = character,
            Characters = all ?? new[] { character }
        };

    public static EnterWorldResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed class MonitorQueryService
{
    private readonly IChatLog _chat;
    private readonly IChatHistory? _history;
    private readonly IObjectDirectory _objects;

    public MonitorQueryService(IChatLog chat, IObjectDirectory objects, IChatHistory? history = null)
    {
        _chat = chat;
        _objects = objects;
        _history = history;
    }

    /// <summary>Social / in-game chat only (excludes LootCollector BBLC/LC1 flood).</summary>
    public IReadOnlyList<ChatLine> GetChat(int max = 100) => _chat.Recent(max);

    /// <summary>Decoded LootCollector discoveries (BBLC/KLCE/LC1).</summary>
    public IReadOnlyList<ChatLine> GetLoot(int max = 100) => _chat.RecentLoot(max);

    public ChatPage GetChatPage(
        int limit = 25,
        long? beforeId = null,
        string? channel = null,
        string? sender = null,
        string? text = null,
        bool loot = false,
        string? scope = null,
        string? ownerUserId = null)
    {
        channel = ChatChannelFilter.Normalize(channel);
        if (_history is not null)
        {
            return _history.Query(limit, beforeId, channel, sender, text, loot, scope, ownerUserId);
        }

        var recent = loot ? _chat.RecentLoot(limit) : _chat.Recent(limit);
        long? oldest = recent.Count > 0 && recent[0].Id > 0 ? recent[0].Id : null;
        long? newest = recent.Count > 0 && recent[^1].Id > 0 ? recent[^1].Id : null;
        return new ChatPage(recent, oldest, newest, HasMore: false, Limit: limit);
    }

    public Task<ChatPage> GetChatPageAsync(
        int limit = 25,
        long? beforeId = null,
        string? channel = null,
        string? sender = null,
        string? text = null,
        bool loot = false,
        string? scope = null,
        string? ownerUserId = null,
        CancellationToken cancellationToken = default)
    {
        channel = ChatChannelFilter.Normalize(channel);
        if (_history is not null)
        {
            return _history.QueryAsync(limit, beforeId, channel, sender, text, loot, scope, ownerUserId, cancellationToken);
        }

        return Task.FromResult(GetChatPage(limit, beforeId, channel, sender, text, loot, scope, ownerUserId));
    }

    public object GetStatus() => new
    {
        ok = true,
        socialLines = _chat.SocialCount,
        lootLines = _chat.LootCount,
        latestSocial = _chat.Recent(1).Count > 0 ? _chat.Recent(1)[^1] : null,
        latestLoot = _chat.RecentLoot(1).Count > 0 ? _chat.RecentLoot(1)[^1] : null,
        db = _history?.GetStats()
    };

    public IReadOnlyCollection<WorldObject> GetObjects() => _objects.Snapshot();
}

public sealed class RunAddonsUseCase
{
    private readonly IAddonHost _host;

    public RunAddonsUseCase(IAddonHost host) => _host = host;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await _host.LoadConfiguredAddonsAsync(cancellationToken);
        await _host.FireEventAsync("ADDON_LOADED", cancellationToken);
        await _host.FireEventAsync("PLAYER_LOGIN", cancellationToken);
        await _host.FireEventAsync("PLAYER_ENTERING_WORLD", cancellationToken);
    }
}
