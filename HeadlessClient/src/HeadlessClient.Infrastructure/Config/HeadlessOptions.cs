using HeadlessClient.Domain.Abstractions;

namespace HeadlessClient.Infrastructure.Config;

public sealed class HeadlessOptions : IHeadlessOptions
{
    public string AuthHost { get; set; } = string.Empty;
    public int AuthPort { get; set; } = 3724;
    public int ClientBuild { get; set; } = 12344;

    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? PreferredRealm { get; set; }
    public string? PreferredCharacter { get; set; }

    public List<AccountEntry> Accounts { get; set; } = new();

    public string AddonsRoot { get; set; } = "";
    public List<string> EnabledAddons { get; set; } = new();
    public int MonitorPort { get; set; } = 5100;
    public string AuthMode { get; set; } = "Tcp";
    public string AscensionChallengePath { get; set; } = "";
    public string SessionKeyOverridePath { get; set; } = "";
    public string RuntimeInstDir { get; set; } = "";
    public string TrainingDataDir { get; set; } = "";
    public string PlayerRosterPath { get; set; } = "";
    public string ChannelRosterPath { get; set; } = "";
    public string ChatDbPath { get; set; } = "";
    public string GameDataDbPath { get; set; } = "";

    public FleetOptions Fleet { get; set; } = new();
    public PacketLogOptions PacketLog { get; set; } = new();

    IReadOnlyList<string> IHeadlessOptions.EnabledAddons => EnabledAddons;

    public IReadOnlyList<AccountEntry> ResolveAccounts()
    {
        if (Accounts.Count > 0)
        {
            return Accounts
                .Where(a => !string.IsNullOrWhiteSpace(a.Account) && !string.IsNullOrWhiteSpace(a.Password))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(Account) && !string.IsNullOrWhiteSpace(Password))
        {
            return new[]
            {
                new AccountEntry
                {
                    Account = Account,
                    Password = Password,
                    Realm = PreferredRealm,
                    Character = PreferredCharacter,
                    SessionKeyOverridePath = SessionKeyOverridePath
                }
            };
        }

        return Array.Empty<AccountEntry>();
    }
}
