using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Auth;
using HeadlessClient.Infrastructure.Config;

namespace HeadlessClient.Infrastructure.Config;

/// <summary>Per-account view of global options + credential store.</summary>
public sealed class AccountSessionOptions : IHeadlessOptions, ICredentialStore
{
    private readonly HeadlessOptions _global;
    private readonly AccountEntry _account;

    public AccountSessionOptions(HeadlessOptions global, AccountEntry account)
    {
        _global = global ?? throw new ArgumentNullException(nameof(global));
        _account = account ?? throw new ArgumentNullException(nameof(account));
    }

    public AccountEntry Entry => _account;

    public string AuthHost => _global.AuthHost;
    public int AuthPort => _global.AuthPort;
    public int ClientBuild => _global.ClientBuild;
    public string? PreferredRealm =>
        !string.IsNullOrWhiteSpace(_account.Realm) ? _account.Realm : _global.PreferredRealm;
    public string? PreferredCharacter =>
        !string.IsNullOrWhiteSpace(_account.Character) ? _account.Character : _global.PreferredCharacter;
    public string AddonsRoot => _global.AddonsRoot;
    public IReadOnlyList<string> EnabledAddons => _global.EnabledAddons;
    public int MonitorPort => _global.MonitorPort;
    public string AscensionChallengePath =>
        !string.IsNullOrWhiteSpace(_account.AscensionChallengePath)
            ? _account.AscensionChallengePath!
            : _global.AscensionChallengePath;
    public string SessionKeyOverridePath =>
        !string.IsNullOrWhiteSpace(_account.SessionKeyOverridePath)
            ? _account.SessionKeyOverridePath!
            : _global.SessionKeyOverridePath;
    public string RuntimeInstDir => _global.RuntimeInstDir;

    public Credentials GetCredentials() => new(_account.Account, _account.Password);

    public string InitialLogTag
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_account.LogTag))
            {
                return _account.LogTag!;
            }

            if (!string.IsNullOrWhiteSpace(_account.Character))
            {
                return _account.Character!;
            }

            var acct = _account.Account;
            var at = acct.IndexOf('@');
            return at > 0 ? acct[..at] : acct;
        }
    }
}
