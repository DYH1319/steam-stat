using SteamKit2;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Sessions;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal sealed class SteamKitPresenceFeed(ISteamSessionAccessor sessionAccessor) : ISteamPresenceFeed
{
    public IReadOnlyList<string> GetLoggedInAccounts() => sessionAccessor.GetLoggedInUsers();

    public bool TryGetSnapshot(
        string accountName,
        out SteamPersonaSnapshot currentUser,
        out IReadOnlyList<SteamPersonaSnapshot> friends)
    {
        if (!sessionAccessor.TryGetSession(accountName, out var session)
            || session.Client.GetHandler<SteamFriends>() is not { } steamFriends)
        {
            currentUser = null!;
            friends = [];
            return false;
        }
        currentUser = ToSnapshot(steamFriends, session.Client.SteamID ?? new SteamID(), true);
        var values = new List<SteamPersonaSnapshot>();
        for (var index = 0; index < steamFriends.GetFriendCount(); index++)
        {
            var id = steamFriends.GetFriendByIndex(index);
            if (steamFriends.GetFriendRelationship(id) == EFriendRelationship.Friend)
                values.Add(ToSnapshot(steamFriends, id));
        }
        friends = values;
        return true;
    }

    public IDisposable? Subscribe(string accountName, SteamPresenceFeedHandlers handlers)
    {
        if (!sessionAccessor.TryGetSession(accountName, out var session)) return null;
        var subscriptions = new List<IDisposable>
        {
            session.Callbacks.Subscribe<SteamFriends.PersonaStateCallback>(callback =>
                handlers.PersonaChanged(new SteamPersonaUpdate(
                    callback.FriendID.ConvertToUInt64(),
                    callback.Name,
                    (int)callback.State,
                    (int)callback.StateFlags,
                    callback.GameID.AppID,
                    ToAvatarHash(callback.AvatarHash),
                    new DateTimeOffset(callback.LastLogOff).ToUnixTimeSeconds(),
                    new DateTimeOffset(callback.LastLogOn).ToUnixTimeSeconds()))),
            session.Callbacks.Subscribe<RichPresenceInfoCallback>(callback =>
            {
                foreach (var entry in callback.Entries)
                    handlers.RichPresenceChanged(new SteamRichPresenceUpdate(
                        entry.SteamId,
                        null,
                        new Dictionary<string, string>(entry.KeyValues, StringComparer.OrdinalIgnoreCase),
                        true));
            }),
            session.Callbacks.Subscribe<PersonaStateRichPresenceCallback>(callback =>
                handlers.RichPresenceChanged(new SteamRichPresenceUpdate(
                    callback.SteamId,
                    callback.AppId,
                    new Dictionary<string, string>(callback.KeyValues, StringComparer.OrdinalIgnoreCase),
                    false))),
            session.Callbacks.Subscribe<FriendsSteamLevelsCallback>(callback =>
            {
                var levels = new Dictionary<ulong, int>();
                foreach (var (accountId, level) in callback.Levels)
                {
                    var steamId = FindSteamId(session.Client, accountId);
                    if (steamId != 0) levels[steamId] = level;
                }
                if (levels.Count > 0) handlers.LevelsChanged(levels);
            }),
            session.Callbacks.Subscribe<SteamFriends.FriendsListCallback>(_ => handlers.FriendsListChanged())
        };
        return new CompositeDisposable(subscriptions);
    }

    public void RequestRichPresence(string accountName, uint appId, IReadOnlyList<ulong> steamIds)
    {
        if (appId == 0 || steamIds.Count == 0
            || !sessionAccessor.TryGetSession(accountName, out var session)) return;
        session.Client.GetHandler<SteamRichPresenceHandler>()?.RequestRichPresence(appId, steamIds);
    }

    public void RequestLevels(string accountName, IReadOnlyList<ulong> steamIds)
    {
        if (steamIds.Count == 0 || !sessionAccessor.TryGetSession(accountName, out var session)) return;
        session.Client.GetHandler<SteamLevelsHandler>()?.RequestFriendLevels(
            steamIds.Select(id => new SteamID(id).AccountID));
    }

    public void RequestFriendInfo(string accountName, ulong steamId)
    {
        if (!sessionAccessor.TryGetSession(accountName, out var session)) return;
        session.Client.GetHandler<SteamFriends>()?.RequestFriendInfo(new SteamID(steamId));
    }

    private static SteamPersonaSnapshot ToSnapshot(SteamFriends friends, SteamID id, bool currentUser = false)
    {
        var gameId = friends.GetFriendGamePlayed(id);
        return new SteamPersonaSnapshot(
            id.ConvertToUInt64(),
            (currentUser ? friends.GetPersonaName() : friends.GetFriendPersonaName(id)) ?? string.Empty,
            (int)friends.GetFriendPersonaState(id),
            (int)friends.GetFriendRelationship(id),
            gameId.AppID,
            GetAvatarHash(friends, id));
    }

    private static string GetAvatarHash(SteamFriends friends, SteamID id)
    {
        try
        {
            return ToAvatarHash(friends.GetFriendAvatar(id));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ToAvatarHash(byte[]? hash)
        => hash is { Length: > 0 } ? Convert.ToHexStringLower(hash) : string.Empty;

    private static ulong FindSteamId(SteamClient client, uint accountId)
    {
        if (client.SteamID is { } current && current.AccountID == accountId)
            return current.ConvertToUInt64();
        var friends = client.GetHandler<SteamFriends>();
        if (friends == null) return 0;
        for (var index = 0; index < friends.GetFriendCount(); index++)
        {
            var id = friends.GetFriendByIndex(index);
            if (id.AccountID == accountId) return id.ConvertToUInt64();
        }
        return 0;
    }

    private sealed class CompositeDisposable(IReadOnlyList<IDisposable> subscriptions) : IDisposable
    {
        private IReadOnlyList<IDisposable>? _subscriptions = subscriptions;

        public void Dispose()
        {
            var values = Interlocked.Exchange(ref _subscriptions, null);
            if (values == null) return;
            foreach (var subscription in values) subscription.Dispose();
        }
    }
}
