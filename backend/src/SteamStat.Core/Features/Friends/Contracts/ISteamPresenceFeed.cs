namespace SteamStat.Core.Features.Friends.Contracts;

public sealed record SteamPersonaSnapshot(
    ulong SteamId,
    string PersonaName,
    int PersonaState,
    int Relationship,
    uint GameAppId,
    string AvatarHash);

public sealed record SteamPersonaUpdate(
    ulong SteamId,
    string PersonaName,
    int PersonaState,
    int PersonaStateFlags,
    uint GameAppId,
    string AvatarHash,
    long LastLogOff,
    long LastLogOn);

public sealed record SteamRichPresenceUpdate(
    ulong SteamId,
    uint? AppId,
    IReadOnlyDictionary<string, string> Values,
    bool RecordChange);

public sealed record SteamPresenceFeedHandlers(
    Action<SteamPersonaUpdate> PersonaChanged,
    Action<SteamRichPresenceUpdate> RichPresenceChanged,
    Action<IReadOnlyDictionary<ulong, int>> LevelsChanged,
    Action FriendsListChanged);

public interface ISteamPresenceFeed
{
    IReadOnlyList<string> GetLoggedInAccounts();
    bool TryGetSnapshot(
        string accountName,
        out SteamPersonaSnapshot currentUser,
        out IReadOnlyList<SteamPersonaSnapshot> friends);
    IDisposable? Subscribe(string accountName, SteamPresenceFeedHandlers handlers);
    void RequestRichPresence(string accountName, uint appId, IReadOnlyList<ulong> steamIds);
    void RequestLevels(string accountName, IReadOnlyList<ulong> steamIds);
    void RequestFriendInfo(string accountName, ulong steamId);
}
