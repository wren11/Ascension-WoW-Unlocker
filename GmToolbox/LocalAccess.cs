namespace AscensionNetTool;

/// <summary>Local free toolbox — no account, license, or store.</summary>
static class LocalAccess
{
    public const string DiscordInviteUrl = "https://discord.gg/3K24chnRKm";
    public const string DiscordInviteLabel = "discord.gg/3K24chnRKm";

    public static (bool Allowed, string Message) Evaluate() =>
        (true, "local");

    public static object StatusDto() => new
    {
        allowed = true,
        kind = "Ok",
        message = "local — no login",
        machineId = "",
        graceHoursLeft = 0,
        allowedNames = Array.Empty<string>(),
        allowedAddons = Array.Empty<string>(),
        maxInstances = GmtLimits.MaxInstances,
        graceHours = 0,
        account = new
        {
            loggedIn = true,
            email = "",
            discordUsername = "",
            displayName = "local",
            tokens = 0,
            trialActive = false,
            hasCore = true,
            remainingDays = 0,
            softRealmUrl = "",
            maxInstances = GmtLimits.MaxInstances,
        },
        pendingLogin = new
        {
            active = false,
            code = "",
            verifyUrl = "",
        },
        mode = "local",
        loggedIn = true,
        hasCore = true,
        valid = true,
        displayName = "local",
        addons = Array.Empty<string>(),
        softRealmUrl = "",
        active = false,
        code = "",
        verifyUrl = "",
        discordUrl = DiscordInviteUrl,
        discordLabel = DiscordInviteLabel,
    };
}
