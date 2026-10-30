using SteamKit2;

namespace SteamStat.Core.Sessions;

public interface ISteamSession
{
    long Generation { get; }
    SteamClient Client { get; }
    CallbackManager Callbacks { get; }
}

public interface ISteamSessionAccessor
{
    IReadOnlyList<string> GetLoggedInUsers();
    bool TryGetSession(string accountName, out ISteamSession session);
}

/// <summary>
/// 把已登录账号名解析为 64 位 Steam ID（供宿主层关联会话数据，不暴露 SteamKit 类型）。
/// </summary>
public interface ISteamAccountIdentityProvider
{
    bool TryGetSteamId(string accountName, out ulong steamId);
}
