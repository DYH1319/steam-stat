using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamStat.Core.Events;
using SteamStat.Core.Sessions;

namespace SteamStat.Core.Features.Friends;

public sealed class SteamFriendsService(
    ISteamSessionAccessor sessionAccessor,
    IAppNameResolver appNameResolver,
    IRichPresenceResolver richPresenceResolver,
    IFriendStatusRecorder friendStatusRecorder,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<SteamFriendsService> logger) : IEventHandler<SteamSessionReady>, IEventHandler<SteamSessionEnded>, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SteamFriendData> _userFriendsData = new();
    private readonly ConcurrentDictionary<string, object> _cacheLocks = new();
    private readonly ConcurrentDictionary<string, byte> _friendsCallbacksRegistered = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<IDisposable>> _subscriptions = new();
    private readonly ConcurrentDictionary<int, Task> _callbackWork = new();
    private readonly CancellationTokenSource _stopping = new();
    private int _nextWorkId;
    private int _disposed;

    public SteamFriendData? GetFriendsForUser(string accountName)
    {
        try
        {
            if (!sessionAccessor.TryGetSession(accountName, out var session))
            {
                logger.LogWarning("Steam user {AccountName} is not logged in", accountName);
                return null;
            }
            var client = session.Client;
            var steamFriends = client.GetHandler<SteamFriends>();
            if (steamFriends == null)
            {
                logger.LogWarning("SteamFriends handler is unavailable for {AccountName}", accountName);
                return null;
            }
            if (_friendsCallbacksRegistered.TryAdd(accountName, 0))
                RegisterFriendsCallbacks(accountName, session.Callbacks);
            var currentUser = GetFriendInfo(steamFriends, client.SteamID ?? new SteamID());
            var friends = new List<SteamFriendInfo>();
            for (var i = 0; i < steamFriends.GetFriendCount(); i++)
            {
                var id = steamFriends.GetFriendByIndex(i);
                if (steamFriends.GetFriendRelationship(id) == EFriendRelationship.Friend)
                    friends.Add(GetFriendInfo(steamFriends, id));
            }
            var result = new SteamFriendData
            {
                AccountName = accountName,
                CurrentUser = currentUser,
                Friends = friends,
                LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds()
            };
            lock (GetCacheLock(accountName))
            {
                _userFriendsData.TryGetValue(accountName, out var previous);
                _userFriendsData[accountName] = result;
                RestoreCachedLevels(result, previous);
            }
            var richPresence = client.GetHandler<SteamRichPresenceHandler>();
            if (richPresence != null)
            {
                foreach (var group in friends.Where(friend => uint.TryParse(friend.GameId, out var appId) && appId != 0)
                             .GroupBy(friend => friend.GameId))
                    richPresence.RequestRichPresence(uint.Parse(group.Key), group.Select(friend => ulong.Parse(friend.SteamId)));
            }
            client.GetHandler<SteamLevelsHandler>()?.RequestFriendLevels(
                friends.Select(friend => new SteamID(ulong.Parse(friend.SteamId)).AccountID)
                    .Append(new SteamID(ulong.Parse(currentUser.SteamId)).AccountID));
            logger.LogInformation("Got {Count} Steam friends for {AccountName}", friends.Count, accountName);
            lock (GetCacheLock(accountName)) return Clone(result);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to get Steam friends for {AccountName}", accountName);
            return null;
        }
    }

    public List<SteamFriendData> GetAllLoggedInUsersFriends()
    {
        var result = new List<SteamFriendData>();
        foreach (var accountName in sessionAccessor.GetLoggedInUsers())
        {
            var data = GetFriendsForUser(accountName);
            if (data != null) result.Add(data);
        }
        return result;
    }

    public List<SteamFriendData> GetCachedFriendsData() => _userFriendsData.Select(item =>
    {
        lock (GetCacheLock(item.Key)) return Clone(item.Value);
    }).ToList();

    private SteamFriendInfo GetFriendInfo(SteamFriends steamFriends, SteamID id)
    {
        var gameId = steamFriends.GetFriendGamePlayed(id);
        return new SteamFriendInfo
        {
            SteamId = id.ConvertToUInt64().ToString(),
            PersonaName = steamFriends.GetFriendPersonaName(id)!,
            PersonaState = (int)steamFriends.GetFriendPersonaState(id),
            Relationship = (int)steamFriends.GetFriendRelationship(id),
            GameName = gameId.AppID == 0 ? string.Empty : GetGameName(gameId.AppID),
            GameId = gameId.AppID.ToString(),
            AvatarHash = GetAvatarHash(steamFriends, id)
        };
    }

    private static string GetAvatarHash(SteamFriends steamFriends, SteamID id)
    {
        try
        {
            var hash = steamFriends.GetFriendAvatar(id);
            return hash is { Length: > 0 } ? BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() : string.Empty;
        }
        catch { return string.Empty; }
    }

    private string GetGameName(uint appId)
    {
        if (appId == 0) return string.Empty;
        try
        {
            var name = appNameResolver.GetCachedName(appId);
            if (!string.IsNullOrEmpty(name)) return name;
            TrackCallback(ResolveAndPropagateGameNameAsync(appId, _stopping.Token));
            return $"App {appId}";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to resolve app name for {AppId}", appId);
            return $"App {appId}";
        }
    }

    private async Task ResolveAndPropagateGameNameAsync(uint appId, CancellationToken cancellationToken)
    {
        var name = await appNameResolver.ResolveNameAsync(appId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(name)) PropagateGameNameUpdate(appId, name);
    }

    private void PropagateGameNameUpdate(uint appId, string newName)
    {
        var id = appId.ToString();
        foreach (var (accountName, data) in _userFriendsData)
        {
            lock (GetCacheLock(accountName))
            {
                var changed = false;
                if (data.CurrentUser.GameId == id && data.CurrentUser.GameName != newName)
                {
                    data.CurrentUser.GameName = newName;
                    changed = true;
                }
                foreach (var friend in data.Friends.Where(friend => friend.GameId == id && friend.GameName != newName))
                {
                    friend.GameName = newName;
                    changed = true;
                }
                if (changed)
                {
                    data.LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
                    SendFriendsUpdateEvent(eventBus, accountName, data);
                }
            }
        }
    }

    private static void RestoreCachedLevels(SteamFriendData result, SteamFriendData? previous)
    {
        if (previous == null) return;
        if (previous.CurrentUser.SteamId == result.CurrentUser.SteamId)
            result.CurrentUser.Level ??= previous.CurrentUser.Level;
        var levels = previous.Friends.Where(friend => friend.Level.HasValue)
            .ToDictionary(friend => friend.SteamId, friend => friend.Level);
        foreach (var friend in result.Friends)
            if (friend.Level == null && levels.TryGetValue(friend.SteamId, out var level)) friend.Level = level;
    }

    private void UpdateFriendInfoFromCallback(SteamFriendInfo friend, SteamFriends.PersonaStateCallback callback)
    {
        friend.PersonaName = callback.Name;
        friend.PersonaState = (int)callback.State;
        friend.PersonaStateFlags = (int)callback.StateFlags;
        friend.LastLogOff = new DateTimeOffset(callback.LastLogOff).ToUnixTimeSeconds();
        friend.LastLogOn = new DateTimeOffset(callback.LastLogOn).ToUnixTimeSeconds();
        if (callback.GameID.AppID != 0)
        {
            friend.GameId = callback.GameID.AppID.ToString();
            friend.GameName = GetGameName(callback.GameID.AppID);
        }
        else
        {
            friend.GameId = "0";
            friend.GameName = string.Empty;
            friend.RichPresence = string.Empty;
        }
        if (callback.AvatarHash is { Length: > 0 })
            friend.AvatarHash = BitConverter.ToString(callback.AvatarHash).Replace("-", "").ToLowerInvariant();
    }

    private void RegisterFriendsCallbacks(string accountName, CallbackManager manager)
    {
        var subscriptions = _subscriptions.GetOrAdd(accountName, _ => new ConcurrentBag<IDisposable>());
        subscriptions.Add(manager.Subscribe<SteamFriends.PersonaStateCallback>(callback =>
        {
            logger.LogDebug("Persona state updated for {Name}: {State}, app {AppId}", callback.Name, callback.State, callback.GameID.AppID);
            if (!_userFriendsData.TryGetValue(accountName, out var data)) return;
            lock (GetCacheLock(accountName))
            {
                var friendId = callback.FriendID.ConvertToUInt64().ToString();
                sessionAccessor.TryGetSession(accountName, out var session);
                if (data.CurrentUser.SteamId == friendId) UpdateFriendInfoFromCallback(data.CurrentUser, callback);
                else
                {
                    var friend = data.Friends.FirstOrDefault(item => item.SteamId == friendId);
                    if (friend != null)
                    {
                        var oldState = friend.PersonaState;
                        var oldGameId = friend.GameId;
                        var oldGameName = friend.GameName;
                        var oldName = friend.PersonaName;
                        UpdateFriendInfoFromCallback(friend, callback);
                        if (friendStatusRecorder.IsTracked(accountName, friendId))
                            TryRecordFriendChanges(accountName, friend, oldState, oldGameId, oldGameName, oldName);
                    }
                }
                data.LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
                SendFriendsUpdateEvent(eventBus, accountName, data);
                if (callback.GameID.AppID != 0)
                    session?.Client.GetHandler<SteamRichPresenceHandler>()
                        ?.RequestRichPresence(callback.GameID.AppID, [callback.FriendID.ConvertToUInt64()]);
            }
        }));
        subscriptions.Add(manager.Subscribe<RichPresenceInfoCallback>(callback =>
        {
            if (!sessionAccessor.TryGetSession(accountName, out var session)) return;
            foreach (var entry in callback.Entries)
            {
                var friendId = entry.SteamId.ToString();
                uint appId;
                if (!_userFriendsData.TryGetValue(accountName, out var data)) continue;
                lock (GetCacheLock(accountName))
                {
                    var friend = data.Friends.FirstOrDefault(item => item.SteamId == friendId);
                    if (friend == null) continue;
                    if (!uint.TryParse(friend.GameId, out appId)) appId = 0;
                }
                TrackCallback(ResolveRichPresenceAsync(accountName, friendId, appId,
                    new Dictionary<string, string>(entry.KeyValues, StringComparer.OrdinalIgnoreCase),
                    session.Client, true, _stopping.Token));
            }
        }));
        subscriptions.Add(manager.Subscribe<PersonaStateRichPresenceCallback>(callback =>
        {
            if (!sessionAccessor.TryGetSession(accountName, out var session)) return;
            TrackCallback(ResolveRichPresenceAsync(accountName, callback.SteamId.ToString(), callback.AppId,
                new Dictionary<string, string>(callback.KeyValues, StringComparer.OrdinalIgnoreCase),
                session.Client, false, _stopping.Token));
        }));
        subscriptions.Add(manager.Subscribe<FriendsSteamLevelsCallback>(callback =>
        {
            if (!_userFriendsData.TryGetValue(accountName, out var data) || callback.Levels.Count == 0) return;
            lock (GetCacheLock(accountName))
            {
                var changed = false;
                foreach (var friend in data.Friends.Append(data.CurrentUser))
                {
                    if (!ulong.TryParse(friend.SteamId, out var steamId)) continue;
                    if (callback.Levels.TryGetValue(new SteamID(steamId).AccountID, out var level) && friend.Level != level)
                    {
                        friend.Level = level;
                        changed = true;
                    }
                }
                if (changed)
                {
                    data.LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
                    SendFriendsUpdateEvent(eventBus, accountName, data);
                }
            }
        }));
        subscriptions.Add(manager.Subscribe<SteamFriends.FriendsListCallback>(_ =>
        {
            logger.LogDebug("Steam friends list changed for {AccountName}", accountName);
            GetFriendsForUser(accountName);
        }));
        logger.LogDebug("Registered Steam friends callbacks for {AccountName}", accountName);
    }

    private async Task ResolveRichPresenceAsync(
        string accountName, string friendSteamId, uint appId, IReadOnlyDictionary<string, string> values,
        SteamClient client, bool recordChange, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await richPresenceResolver.ResolveAsync(client, appId, values, cancellationToken).ConfigureAwait(false);
            if (!_userFriendsData.TryGetValue(accountName, out var data)) return;
            string? name = null;
            string? previous = null;
            lock (GetCacheLock(accountName))
            {
                var friend = data.Friends.FirstOrDefault(item => item.SteamId == friendSteamId);
                if (friend == null && data.CurrentUser.SteamId == friendSteamId) friend = data.CurrentUser;
                if (friend == null || friend.RichPresence == resolved) return;
                name = friend.PersonaName;
                previous = friend.RichPresence;
                friend.RichPresence = resolved;
                data.LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
                SendFriendsUpdateEvent(eventBus, accountName, data);
            }
            if (recordChange && friendStatusRecorder.IsTracked(accountName, friendSteamId))
                await friendStatusRecorder.RecordChangeAsync(accountName, friendSteamId, name!, "richPresence",
                    new FriendStatusValue(RichPresence: previous), new FriendStatusValue(RichPresence: resolved), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to resolve rich presence for {FriendSteamId}", friendSteamId);
        }
    }

    private void TryRecordFriendChanges(
        string accountName, SteamFriendInfo friend, int oldState, string oldGameId, string oldGameName, string oldName)
    {
        if (oldState != friend.PersonaState)
            TrackCallback(friendStatusRecorder.RecordChangeAsync(accountName, friend.SteamId, friend.PersonaName, "state",
                new FriendStatusValue(PersonaState: oldState), new FriendStatusValue(PersonaState: friend.PersonaState), _stopping.Token));
        if (oldGameId != friend.GameId)
            TrackCallback(friendStatusRecorder.RecordChangeAsync(accountName, friend.SteamId, friend.PersonaName, "game",
                new FriendStatusValue(GameId: oldGameId, GameName: oldGameName),
                new FriendStatusValue(GameId: friend.GameId, GameName: friend.GameName), _stopping.Token));
        if (!string.IsNullOrEmpty(oldName) && oldName != friend.PersonaName)
            TrackCallback(friendStatusRecorder.RecordChangeAsync(accountName, friend.SteamId, friend.PersonaName, "personaName",
                new FriendStatusValue(PersonaName: oldName), new FriendStatusValue(PersonaName: friend.PersonaName), _stopping.Token));
    }

    private void SendFriendsUpdateEvent(IEventBus targetEventBus, string accountName, SteamFriendData data)
    {
        SteamFriendsSnapshot snapshot;
        lock (GetCacheLock(accountName)) snapshot = ToSnapshot(data);
        TrackCallback(targetEventBus.PublishAsync(new FriendsChanged(accountName, snapshot), _stopping.Token));
    }

    private static SteamFriendsSnapshot ToSnapshot(SteamFriendData data) => new(
        data.AccountName, ToSnapshot(data.CurrentUser), data.Friends.Select(ToSnapshot).ToArray(), data.LastUpdateTime);
    private static SteamFriendSnapshot ToSnapshot(SteamFriendInfo friend) => new(
        friend.SteamId, friend.PersonaName, friend.PersonaState, friend.PersonaStateFlags,
        friend.Relationship, friend.GameName, friend.GameId, friend.AvatarHash,
        friend.LastLogOff, friend.LastLogOn, friend.RichPresence, friend.Level);
    private static SteamFriendData Clone(SteamFriendData data) => new()
    {
        AccountName = data.AccountName,
        CurrentUser = Clone(data.CurrentUser),
        Friends = data.Friends.Select(Clone).ToList(),
        LastUpdateTime = data.LastUpdateTime
    };
    private static SteamFriendInfo Clone(SteamFriendInfo friend) => new()
    {
        SteamId = friend.SteamId,
        PersonaName = friend.PersonaName,
        PersonaState = friend.PersonaState,
        PersonaStateFlags = friend.PersonaStateFlags,
        Relationship = friend.Relationship,
        GameName = friend.GameName,
        GameId = friend.GameId,
        AvatarHash = friend.AvatarHash,
        LastLogOff = friend.LastLogOff,
        LastLogOn = friend.LastLogOn,
        RichPresence = friend.RichPresence,
        Level = friend.Level
    };

    public void ClearUserFriendsData(string accountName)
    {
        _userFriendsData.TryRemove(accountName, out _);
        _cacheLocks.TryRemove(accountName, out _);
        _friendsCallbacksRegistered.TryRemove(accountName, out _);
        if (_subscriptions.TryRemove(accountName, out var subscriptions))
            foreach (var subscription in subscriptions) subscription.Dispose();
        friendStatusRecorder.ClearTrackingForAccount(accountName);
        logger.LogDebug("Cleared Steam friends data for {AccountName}", accountName);
    }

    public void RequestFriendInfo(string accountName, string friendSteamId)
    {
        try
        {
            if (!sessionAccessor.TryGetSession(accountName, out var session)) return;
            session.Client.GetHandler<SteamFriends>()?.RequestFriendInfo(new SteamID(ulong.Parse(friendSteamId)));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to request Steam friend information");
        }
    }

    public Task HandleAsync(SteamSessionReady message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetFriendsForUser(message.AccountName);
        return Task.CompletedTask;
    }

    public async Task HandleAsync(SteamSessionEnded message, CancellationToken cancellationToken)
    {
        ClearUserFriendsData(message.AccountName);
        await DrainCallbackWorkAsync(cancellationToken).ConfigureAwait(false);
    }

    private object GetCacheLock(string accountName) => _cacheLocks.GetOrAdd(accountName, _ => new object());

    private void TrackCallback(Task task)
    {
        var id = Interlocked.Increment(ref _nextWorkId);
        _callbackWork[id] = task;
        _ = task.ContinueWith(completed =>
        {
            _callbackWork.TryRemove(id, out _);
            if (completed.IsFaulted) logger.LogError(completed.Exception, "Tracked Steam friends callback work failed");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task DrainCallbackWorkAsync(CancellationToken cancellationToken)
    {
        while (!_callbackWork.IsEmpty)
        {
            var work = _callbackWork.Values.ToArray();
            if (work.Length == 0) break;
            try { await Task.WhenAll(work).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Failed while draining Steam friends callback work"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var subscriptions in _subscriptions.Values)
            foreach (var subscription in subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        await _stopping.CancelAsync();
        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await DrainCallbackWorkAsync(drainTimeout.Token).ConfigureAwait(false);
        _stopping.Dispose();
        _userFriendsData.Clear();
        _friendsCallbacksRegistered.Clear();
        _cacheLocks.Clear();
    }
}

public sealed class SteamFriendData
{
    public string AccountName { get; set; } = string.Empty;
    public SteamFriendInfo CurrentUser { get; set; } = new();
    public List<SteamFriendInfo> Friends { get; set; } = [];
    public int LastUpdateTime { get; set; }
}

public sealed class SteamFriendInfo
{
    public string SteamId { get; set; } = string.Empty;
    public string PersonaName { get; set; } = string.Empty;
    public int PersonaState { get; set; }
    public int PersonaStateFlags { get; set; }
    public int Relationship { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string AvatarHash { get; set; } = string.Empty;
    public long LastLogOff { get; set; }
    public long LastLogOn { get; set; }
    public string RichPresence { get; set; } = string.Empty;
    public int? Level { get; set; }
}
