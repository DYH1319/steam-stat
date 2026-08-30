namespace SteamStat.Contracts.Events;

public sealed record SteamLoginEventDto(string Type, object? Data);

public sealed record SteamFriendsUpdatedEventDto(string AccountName, SteamFriendsDataDto Data);

public sealed record SteamFriendsDataDto(
    string AccountName,
    SteamFriendDto CurrentUser,
    IReadOnlyList<SteamFriendDto> Friends,
    int LastUpdateTime);

public sealed record SteamFriendDto(
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

public sealed record UpdaterEventDto(string UpdaterEvent, object? Data);
