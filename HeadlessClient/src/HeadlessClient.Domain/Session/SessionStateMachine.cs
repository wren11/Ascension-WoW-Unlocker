namespace HeadlessClient.Domain.Session;

public sealed class SessionStateMachine
{
    public SessionState State { get; private set; } = SessionState.Disconnected;

    public void TransitionTo(SessionState next)
    {
        if (!IsAllowed(State, next))
        {
            throw new InvalidOperationException($"Illegal transition {State} -> {next}");
        }

        State = next;
    }

    public void Reset() => State = SessionState.Disconnected;

    private static bool IsAllowed(SessionState from, SessionState to) => (from, to) switch
    {
        (_, SessionState.Failed) => true,
        (SessionState.Disconnected, SessionState.Authenticating) => true,
        (SessionState.Authenticating, SessionState.RealmList) => true,
        (SessionState.RealmList, SessionState.WorldConnecting) => true,
        (SessionState.WorldConnecting, SessionState.CharacterSelect) => true,
        (SessionState.CharacterSelect, SessionState.InWorld) => true,
        (SessionState.Failed, SessionState.Disconnected) => true,
        (SessionState.InWorld, SessionState.Disconnected) => true,
        _ => false
    };
}
