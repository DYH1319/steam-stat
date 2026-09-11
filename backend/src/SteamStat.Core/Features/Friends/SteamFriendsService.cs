using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Friends.Contracts;

namespace SteamStat.Core.Features.Friends;

public sealed class SteamFriendsService(
    ISteamPresenceFeed presenceFeed,
    IAppNameResolver appNameResolver,
    IRichPresenceResolver richPresenceResolver,
    IFriendStatusRecorder friendStatusRecorder,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<SteamFriendsService> logger) : IEventHandler<SteamSessionReady>, IEventHandler<SteamSessionEnded>, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SteamFriendData> _userFriendsData = new();
    private readonly ConcurrentDictionary<string, object> _cacheLocks = new();
    private readonly ConcurrentDictionary<string, IDisposable> _subscriptions = new();
    private readonly ConcurrentDictionary<int, Task> _callbackWork = new();
    private readonly CancellationTokenSource _stopping = new();
    private int _nextWorkId;
    private int _disposed;

    public SteamFriendData? GetFriendsForUser(string accountName)
    {
        try
        {
            if (!presenceFeed.TryGetSnapshot(accountName, out var current, out var friendSnapshots))
            {
                logger.LogWarning("Steam user {AccountName} is not logged in", accountName);
                return null;
            }
            EnsureSubscribed(accountName);
            var currentUser = GetFriendInfo(current);
            var friends = friendSnapshots.Select(GetFriendInfo).ToList();
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
            foreach (var group in friends
                         .Where(friend => uint.TryParse(friend.GameId, out var appId) && appId != 0)
                         .GroupBy(friend => friend.GameId))
                presenceFeed.RequestRichPresence(
                    accountName,
                    uint.Parse(group.Key),
                    group.Select(friend => ulong.Parse(friend.SteamId)).ToArray());
            presenceFeed.RequestLevels(
                accountName,
                friends.Select(friend => ulong.Parse(friend.SteamId))
                    .Append(ulong.Parse(currentUser.SteamId)).ToArray());
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
        foreach (var accountName in presenceFeed.GetLoggedInAccounts())
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

    private SteamFriendInfo GetFriendInfo(SteamPersonaSnapshot persona) => new()
    {
        SteamId = persona.SteamId.ToString(),
        PersonaName = persona.PersonaName,
        PersonaState = persona.PersonaState,
        Relationship = persona.Relationship,
        GameName = persona.GameAppId == 0 ? string.Empty : GetGameName(persona.GameAppId),
        GameId = persona.GameAppId.ToString(),
        AvatarHash = persona.AvatarHash
    };

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

    private void EnsureSubscribed(string accountName)
    {
        if (_subscriptions.ContainsKey(accountName)) return;
        var subscription = presenceFeed.Subscribe(accountName, new SteamPresenceFeedHandlers(
            update => OnPersonaChanged(accountName, update),
            update => OnRichPresenceChanged(accountName, update),
            levels => OnLevelsChanged(accountName, levels),
            () => OnFriendsListChanged(accountName)));
        if (subscription != null && !_subscriptions.TryAdd(accountName, subscription)) subscription.Dispose();
    }

    private void OnPersonaChanged(string accountName, SteamPersonaUpdate update)
    {
        logger.LogDebug(
            "Persona state updated for {Name}: {State}, app {AppId}",
            update.PersonaName, update.PersonaState, update.GameAppId);
        if (!_userFriendsData.TryGetValue(accountName, out var data)) return;
        lock (GetCacheLock(accountName))
        {
            var friendId = update.SteamId.ToString();
            var friend = data.CurrentUser.SteamId == friendId
                ? data.CurrentUser
                : data.Friends.FirstOrDefault(item => item.SteamId == friendId);
            if (friend == null) return;
            var oldState = friend.PersonaState;
            var oldGameId = friend.GameId;
            var oldGameName = friend.GameName;
            var oldName = friend.PersonaName;
            UpdateFriendInfo(friend, update);
            if (friend != data.CurrentUser && friendStatusRecorder.IsTracked(accountName, friendId))
                TryRecordFriendChanges(accountName, friend, oldState, oldGameId, oldGameName, oldName);
            data.LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
            SendFriendsUpdateEvent(eventBus, accountName, data);
            if (update.GameAppId != 0)
                presenceFeed.RequestRichPresence(accountName, update.GameAppId, [update.SteamId]);
        }
    }

    private void OnRichPresenceChanged(string accountName, SteamRichPresenceUpdate update)
    {
        uint appId;
        if (update.AppId.HasValue) appId = update.AppId.Value;
        else
        {
            if (!_userFriendsData.TryGetValue(accountName, out var data)) return;
            lock (GetCacheLock(accountName))
            {
                var friend = data.Friends.FirstOrDefault(item => item.SteamId == update.SteamId.ToString());
                if (friend == null || !uint.TryParse(friend.GameId, out appId)) appId = 0;
            }
        }
        TrackCallback(ResolveRichPresenceAsync(
            accountName,
            update.SteamId.ToString(),
            appId,
            update.Values,
            update.RecordChange,
            _stopping.Token));
    }

    private void OnLevelsChanged(string accountName, IReadOnlyDictionary<ulong, int> levels)
    {
        if (!_userFriendsData.TryGetValue(accountName, out var data) || levels.Count == 0) return;
        lock (GetCacheLock(accountName))
        {
            var changed = false;
            foreach (var friend in data.Friends.Append(data.CurrentUser))
                if (ulong.TryParse(friend.SteamId, out var steamId)
                    && levels.TryGetValue(steamId, out var level)
                    && friend.Level != level)
                {
                    friend.Level = level;
                    changed = true;
                }
            if (changed)
            {
                data.LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
                SendFriendsUpdateEvent(eventBus, accountName, data);
            }
        }
    }

    private void OnFriendsListChanged(string accountName)
    {
        logger.LogDebug("Steam friends list changed for {AccountName}", accountName);
        GetFriendsForUser(accountName);
    }

    private void UpdateFriendInfo(SteamFriendInfo friend, SteamPersonaUpdate update)
    {
        friend.PersonaName = update.PersonaName;
        friend.PersonaState = update.PersonaState;
        friend.PersonaStateFlags = update.PersonaStateFlags;
        friend.LastLogOff = update.LastLogOff;
        friend.LastLogOn = update.LastLogOn;
        if (update.GameAppId != 0)
        {
            friend.GameId = update.GameAppId.ToString();
            friend.GameName = GetGameName(update.GameAppId);
        }
        else
        {
            friend.GameId = "0";
            friend.GameName = string.Empty;
            friend.RichPresence = string.Empty;
        }
        if (!string.IsNullOrEmpty(update.AvatarHash)) friend.AvatarHash = update.AvatarHash;
    }

    private async Task ResolveRichPresenceAsync(
        string accountName,
        string friendSteamId,
        uint appId,
        IReadOnlyDictionary<string, string> values,
        bool recordChange,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await richPresenceResolver.ResolveAsync(
                accountName, appId, values, cancellationToken).ConfigureAwait(false);
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
                await friendStatusRecorder.RecordChangeAsync(
                    accountName,
                    friendSteamId,
                    name!,
                    "richPresence",
                    new FriendStatusValue(RichPresence: previous),
                    new FriendStatusValue(RichPresence: resolved),
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to resolve rich presence for {FriendSteamId}", friendSteamId);
        }
    }

    private void TryRecordFriendChanges(
        string accountName,
        SteamFriendInfo friend,
        int oldState,
        string oldGameId,
        string oldGameName,
        string oldName)
    {
        if (oldState != friend.PersonaState)
            TrackCallback(friendStatusRecorder.RecordChangeAsync(
                accountName, friend.SteamId, friend.PersonaName, "state",
                new FriendStatusValue(PersonaState: oldState),
                new FriendStatusValue(PersonaState: friend.PersonaState),
                _stopping.Token));
        if (oldGameId != friend.GameId)
            TrackCallback(friendStatusRecorder.RecordChangeAsync(
                accountName, friend.SteamId, friend.PersonaName, "game",
                new FriendStatusValue(GameId: oldGameId, GameName: oldGameName),
                new FriendStatusValue(GameId: friend.GameId, GameName: friend.GameName),
                _stopping.Token));
        if (!string.IsNullOrEmpty(oldName) && oldName != friend.PersonaName)
            TrackCallback(friendStatusRecorder.RecordChangeAsync(
                accountName, friend.SteamId, friend.PersonaName, "personaName",
                new FriendStatusValue(PersonaName: oldName),
                new FriendStatusValue(PersonaName: friend.PersonaName),
                _stopping.Token));
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
        if (_subscriptions.TryRemove(accountName, out var subscription)) subscription.Dispose();
        friendStatusRecorder.ClearTrackingForAccount(accountName);
        logger.LogDebug("Cleared Steam friends data for {AccountName}", accountName);
    }

    public void RequestFriendInfo(string accountName, string friendSteamId)
    {
        try
        {
            if (ulong.TryParse(friendSteamId, out var steamId))
                presenceFeed.RequestFriendInfo(accountName, steamId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to request Steam friend information");
        }
    }

    public Task HandleAsync(SteamSessionReady message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_subscriptions.TryRemove(message.AccountName, out var subscription)) subscription.Dispose();
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
            try
            {
                await Task.WhenAll(work).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed while draining Steam friends callback work");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var subscription in _subscriptions.Values) subscription.Dispose();
        _subscriptions.Clear();
        await _stopping.CancelAsync();
        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await DrainCallbackWorkAsync(drainTimeout.Token).ConfigureAwait(false);
        _stopping.Dispose();
        _userFriendsData.Clear();
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
