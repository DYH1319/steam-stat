namespace SteamStat.Core.Events;

public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : notnull;
}

public interface IEventHandler<in TEvent>
{
    Task HandleAsync(TEvent message, CancellationToken cancellationToken);
}

public sealed record LoginUsersChanged;

public sealed record SteamLoginProgressChanged(string Type, object? Data = null);

public sealed record FriendsChanged(string AccountName, SteamFriendsSnapshot Data);

public sealed record SteamSessionDisconnected(string AccountName);

public sealed record SteamSessionReconnected(string AccountName);

public sealed record SteamSessionReady(string AccountName);

public sealed record SteamSessionEnded(string AccountName);

public sealed record SteamFriendsSnapshot(
    string AccountName,
    SteamFriendSnapshot CurrentUser,
    IReadOnlyList<SteamFriendSnapshot> Friends,
    int LastUpdateTime);

public sealed record SteamFriendSnapshot(
    string SteamId,
    string PersonaName,
    int PersonaState,
    int PersonaStateFlags,
    int Relationship,
    string GameName,
    string GameId,
    string AvatarHash,
    long LastLogOff,
    long LastLogOn,
    string RichPresence,
    int? Level);
