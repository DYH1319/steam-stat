using SteamKit2;

namespace SteamStat.Core.Sessions;

public interface ISteamSession
{
    SteamClient Client { get; }
    CallbackManager Callbacks { get; }
}

public interface ISteamSessionAccessor
{
    IReadOnlyList<string> GetLoggedInUsers();
    bool TryGetSession(string accountName, out ISteamSession session);
}
