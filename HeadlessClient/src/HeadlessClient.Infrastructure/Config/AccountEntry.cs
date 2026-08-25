namespace HeadlessClient.Infrastructure.Config;

/// <summary>One bot account in the Tcp fleet.</summary>
public sealed class AccountEntry
{
    /// <summary>Login email / account name.</summary>
    public string Account { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Optional realm name; falls back to global PreferredRealm.</summary>
    public string? Realm { get; set; }

    /// <summary>Optional character to select. If set and missing, login fails for this account.</summary>
    public string? Character { get; set; }

    /// <summary>Optional per-account 40-byte seal K override path.</summary>
    public string? SessionKeyOverridePath { get; set; }

    /// <summary>Optional per-account sealed challenge blob (falls back to global AscensionChallengePath).</summary>
    public string? AscensionChallengePath { get; set; }

    /// <summary>Short tag for logs when character name is not yet known (defaults to account local-part).</summary>
    public string? LogTag { get; set; }

    /// <summary>True for appsettings/system fleet accounts (default listening backbone).</summary>
    public bool IsSystemDefault { get; set; } = true;

    /// <summary>Portal user id when this entry was started from a user login.</summary>
    public string? OwnerUserId { get; set; }
}
