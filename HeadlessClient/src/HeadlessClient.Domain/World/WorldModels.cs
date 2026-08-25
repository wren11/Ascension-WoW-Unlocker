namespace HeadlessClient.Domain.World;

public sealed class PlayerInfo
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Realm { get; set; } = "";
    public int Race { get; set; } = -1;
    public int Gender { get; set; } = -1;
    public int ClassId { get; set; } = -1;
    public int Level { get; set; } = -1;
    public string Guild { get; set; } = "";
    public string Zone { get; set; } = "";
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastChatUtc { get; set; }
    public int MessageCount { get; set; }

    public bool HasName => !string.IsNullOrWhiteSpace(Name);
    public bool HasDossier => Level > 0 || ClassId > 0 || Race > 0;
}

public sealed record CharacterInfo(
    ulong Guid,
    string Name,
    byte Race,
    byte Class,
    byte Gender,
    byte Level,
    uint Zone,
    uint Map,
    float X,
    float Y,
    float Z);

/// <summary>
/// Single shared Object Manager row — live sightings + identity + optional static template fields.
/// </summary>
public sealed record WorldObject(
    ulong Guid,
    string? Name,
    uint Entry,
    float X,
    float Y,
    float Z,
    float Orientation,
    uint Health,
    uint MaxHealth,
    byte TypeId,
    DateTimeOffset LastSeenUtc = default,
    IReadOnlyList<string>? SeenBy = null,
    string? StaticName = null,
    bool Alive = true,
    uint MapId = 0,
    /// <summary>live | static | cache</summary>
    string Source = "live",
    DateTimeOffset FirstSeenUtc = default);

public sealed record ChatLine(
    DateTimeOffset ReceivedAt,
    byte Type,
    string Language,
    string Sender,
    string Channel,
    string Message,
    string SenderGuid = "",
    string ReadableText = "",
    int Level = -1,
    int ClassId = -1,
    int Race = -1,
    string Guild = "",
    string Zone = "",
    string TargetGuid = "",
    string Direction = "", // "in" | "out" | ""
    long Id = 0,
    /// <summary>shared = consolidated realm chat; member = private to OwnerUserId.</summary>
    string Scope = "shared",
    string OwnerUserId = "",
    string ObserverAccount = "",
    string SeenBy = "");
