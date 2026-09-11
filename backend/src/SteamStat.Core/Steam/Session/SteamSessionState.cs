namespace SteamStat.Core.Steam.Session;

public enum SteamSessionState
{
    Disconnected,
    Connecting,
    Authenticating,
    Ready,
    ReconnectWaiting,
    Reconnecting,
    ReauthenticationRequired,
    Failed,
    Stopping
}

internal sealed class SteamSessionStateMachine
{
    private static readonly IReadOnlyDictionary<SteamSessionState, SteamSessionState[]> AllowedTransitions =
        new Dictionary<SteamSessionState, SteamSessionState[]>
        {
            [SteamSessionState.Disconnected] = [SteamSessionState.Connecting, SteamSessionState.ReconnectWaiting, SteamSessionState.Stopping],
            [SteamSessionState.Connecting] = [SteamSessionState.Authenticating, SteamSessionState.Ready, SteamSessionState.Disconnected, SteamSessionState.Failed, SteamSessionState.Stopping],
            [SteamSessionState.Authenticating] = [SteamSessionState.Ready, SteamSessionState.Disconnected, SteamSessionState.ReauthenticationRequired, SteamSessionState.Failed, SteamSessionState.Stopping],
            [SteamSessionState.Ready] = [SteamSessionState.Connecting, SteamSessionState.Disconnected, SteamSessionState.Stopping],
            [SteamSessionState.ReconnectWaiting] = [SteamSessionState.Connecting, SteamSessionState.Reconnecting, SteamSessionState.ReauthenticationRequired, SteamSessionState.Failed, SteamSessionState.Stopping],
            [SteamSessionState.Reconnecting] = [SteamSessionState.Connecting, SteamSessionState.Ready, SteamSessionState.ReconnectWaiting, SteamSessionState.ReauthenticationRequired, SteamSessionState.Failed, SteamSessionState.Stopping],
            [SteamSessionState.ReauthenticationRequired] = [SteamSessionState.Connecting, SteamSessionState.Stopping],
            [SteamSessionState.Failed] = [SteamSessionState.Connecting, SteamSessionState.Stopping],
            [SteamSessionState.Stopping] = [SteamSessionState.Disconnected]
        };

    public SteamSessionState State { get; private set; } = SteamSessionState.Disconnected;

    public bool CanTransitionTo(SteamSessionState next)
        => next == State || AllowedTransitions[State].Contains(next);

    public void TransitionTo(SteamSessionState next)
    {
        if (!CanTransitionTo(next))
            throw new InvalidOperationException($"Illegal Steam session transition: {State} -> {next}.");
        State = next;
    }
}
