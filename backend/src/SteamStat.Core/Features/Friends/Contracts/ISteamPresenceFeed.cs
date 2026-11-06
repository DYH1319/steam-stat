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
    long LastLogOn,
    SteamPersonaUpdateFields Fields = SteamPersonaUpdateFields.All);

/// <summary>
/// 一次好友状态推送实际携带的字段（对应 CMsgClientPersonaState.status_flags）。
/// Steam 经常只推送部分字段，未携带的字段为默认值，不能当作「已清空」。
/// </summary>
[Flags]
public enum SteamPersonaUpdateFields
{
    None = 0,
    PlayerName = 1,
    Presence = 2,
    GameInfo = 4,
    LastSeen = 8,
    All = PlayerName | Presence | GameInfo | LastSeen
}

public sealed record SteamRichPresenceUpdate(
    ulong SteamId,
    uint? AppId,
    IReadOnlyDictionary<string, string> Values,
    bool RecordChange);

public sealed record SteamPresenceFeedHandlers(
    Action<SteamPersonaUpdate> PersonaChanged,
    Action<SteamRichPresenceUpdate> RichPresenceChanged,
    Action<IReadOnlyDictionary<ulong, int>> LevelsChanged,
    Action FriendsListChanged,
    Action NicknamesChanged);

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

    /// <summary>当前账号在 Steam 里为好友设置的昵称（SteamID → 昵称）；读取失败返回 null，不代表没有昵称。</summary>
    Task<IReadOnlyDictionary<ulong, string>?> GetNicknamesAsync(
        string accountName,
        CancellationToken cancellationToken = default);
}
