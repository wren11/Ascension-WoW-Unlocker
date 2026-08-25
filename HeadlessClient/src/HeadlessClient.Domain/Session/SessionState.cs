namespace HeadlessClient.Domain.Session;

public enum SessionState
{
    Disconnected = 0,
    Authenticating = 1,
    RealmList = 2,
    WorldConnecting = 3,
    CharacterSelect = 4,
    InWorld = 5,
    Failed = 6
}
