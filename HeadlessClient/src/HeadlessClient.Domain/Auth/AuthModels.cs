namespace HeadlessClient.Domain.Auth;

public sealed record Credentials(string Account, string Password);

public sealed record RealmInfo(
    byte Type,
    byte Flags,
    string Name,
    string Address,
    float Population,
    byte CharacterCount,
    byte Timezone,
    byte Id);

public sealed record AuthChallenge(uint ServerSeed, byte[] Generator, byte[] LargeSafePrime, byte[] Salt, byte[] ServerPublicEphemeral);

public sealed record AuthProofResult(bool Success, byte[] SessionKey, string? Error);

public sealed record AuthLoginResult(IReadOnlyList<RealmInfo> Realms, byte[] SessionKey, byte[] AuthProofTail);
