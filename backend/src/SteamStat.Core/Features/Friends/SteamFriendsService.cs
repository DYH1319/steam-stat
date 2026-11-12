using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Friends;

public sealed class SteamFriendsService(
    ISteamPresenceFeed presenceFeed,
    IAppNameResolver appNameResolver,
    RichPresenceCollector richPresenceCollector,
    IFriendStatusRecorder friendStatusRecorder,
    IEventBus eventBus,
    SteamFeatureSnapshotStore snapshotStore,
    TimeProvider timeProvider,
    ILogger<SteamFriendsService> logger,
    IRunningGameProbe? runningGameProbe = null,
    PresenceGroupCollector? groupCollector = null) : IEventHandler<SteamSessionReady>, IEventHandler<SteamSessionEnded>, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SteamFriendData> _userFriendsData = new();
    private readonly ConcurrentDictionary<string, object> _cacheLocks = new();
    private readonly ConcurrentDictionary<string, IDisposable> _subscriptions = new();
    private readonly ConcurrentDictionary<int, Task> _callbackWork = new();
    // 同一账号的拉取串行执行：登录就绪和好友列表回调会同时触发拉取，各自取一份快照、中间都要等昵称接口，
    // 不串行时先取到的旧快照（可能是空的）会在后面覆盖新的。
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchGates = new(StringComparer.OrdinalIgnoreCase);
    // 本次会话里已经收到过好友列表回调的账号。没收到之前的空列表只是「还没下发」，不是「没有好友」。
    private readonly ConcurrentDictionary<string, byte> _friendsListSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _emptyConfirmations = new(StringComparer.OrdinalIgnoreCase);
    // 本次会话里已经和落库记录对过账的好友（账号 → 好友 SteamID）
    private readonly ConcurrentDictionary<string, HashSet<string>> _reconciledFriends = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private int _nextWorkId;
    private int _disposed;

    public async Task<SteamFriendsResult> GetFriendsSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var accounts = await GetCachedFriendsDataAsync(cancellationToken).ConfigureAwait(false);
        var resources = await GetFriendsResourcesAsync(cancellationToken).ConfigureAwait(false);
        return new SteamFriendsResult(
            SteamFeatureResultClassifier.Classify(resources, accounts.Count > 0),
            accounts,
            resources);
    }

    public async Task<SteamFriendsResult> RefreshFriendsAsync(
        CancellationToken cancellationToken = default)
    {
        var accounts = await GetAllLoggedInUsersFriendsAsync(cancellationToken).ConfigureAwait(false);
        var resources = await GetFriendsResourcesAsync(cancellationToken).ConfigureAwait(false);
        return new SteamFriendsResult(
            SteamFeatureResultClassifier.Classify(resources, accounts.Count > 0),
            accounts,
            resources);
    }

    private async Task<IReadOnlyList<SteamResourceStatus>> GetFriendsResourcesAsync(
        CancellationToken cancellationToken)
        => (await snapshotStore.GetResourceStatusesAsync(cancellationToken).ConfigureAwait(false))
            .Where(status => status.ResourceKind == SteamFeatureSnapshotStore.FriendsResourceKind)
            .ToArray();

    /// <summary>好友列表回调迟迟不来时，等这么久就接受「确实没有好友」。</summary>
    internal TimeSpan EmptyListGrace { get; set; } = TimeSpan.FromSeconds(10);

    internal Task<SteamFriendData?> GetFriendsForUserAsync(
        string accountName,
        CancellationToken cancellationToken = default)
        => GetFriendsForUserAsync(accountName, acceptEmpty: false, cancellationToken);

    private async Task<SteamFriendData?> GetFriendsForUserAsync(
        string accountName,
        bool acceptEmpty,
        CancellationToken cancellationToken)
    {
        var gate = _fetchGates.GetOrAdd(accountName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FetchFriendsAsync(accountName, acceptEmpty, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SteamFriendData?> FetchFriendsAsync(
        string accountName,
        bool acceptEmpty,
        CancellationToken cancellationToken)
    {
        var cached = await snapshotStore.GetFriendsAsync(accountName, cancellationToken).ConfigureAwait(false);
        try
        {
            // 先订阅再取快照：反过来的话，两步之间到达的好友列表回调会丢
            var subscribed = EnsureSubscribed(accountName);
            if (!presenceFeed.TryGetSnapshot(accountName, out var current, out var friendSnapshots))
            {
                logger.LogWarning("Steam user {AccountName} is not logged in", accountName);
                return Fallback(accountName, cached, SteamFailureKind.AuthenticationRequired, "friends_session_unavailable");
            }
            if (subscribed) StartRichPresence(accountName, current.SteamId);
            if (friendSnapshots.Count == 0 && !acceptEmpty && !_friendsListSeen.ContainsKey(accountName))
            {
                // 好友列表还没下发。空结果不写进缓存和离线快照，先用已有的；回调到了会重新拉取，
                // 一直不来（确实没有好友，或回调早于订阅）就在宽限期后接受空列表。
                logger.LogDebug("Steam friends list for {AccountName} has not arrived yet", accountName);
                ScheduleEmptyConfirmation(accountName);
                lock (GetCacheLock(accountName))
                {
                    if (_userFriendsData.TryGetValue(accountName, out var known)) return Clone(known);
                }
                // 这不是失败：有离线快照就先当过期数据给出去，没有就什么都不报，等真正的结果
                if (cached == null) return null;
                snapshotStore.ReportResult(
                    SteamFeatureSnapshotStore.FriendsResourceKind,
                    accountName,
                    SteamDataSource.Sqlite,
                    SteamFreshness.Stale,
                    cached.LastSuccessfulUpdate,
                    diagnosticCode: "friends_list_pending");
                return Clone(cached.Value);
            }
            var currentUser = GetFriendInfo(current);
            var friends = friendSnapshots.Select(GetFriendInfo).ToList();
            var nicknames = await presenceFeed.GetNicknamesAsync(accountName, cancellationToken).ConfigureAwait(false);
            var result = new SteamFriendData
            {
                AccountName = accountName,
                CurrentUser = currentUser,
                Friends = friends,
                LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds()
            };
            SteamFriendData? baseline;
            lock (GetCacheLock(accountName))
            {
                _userFriendsData.TryGetValue(accountName, out var previous);
                baseline = previous == null ? cached?.Value : Clone(previous);
                _userFriendsData[accountName] = result;
                RestoreVolatileFields(result, previous);
                RestoreCachedLevels(result, previous ?? cached?.Value);
                if (nicknames != null) ApplyNicknames(result, nicknames);
                else RestoreCachedNicknames(result, previous ?? cached?.Value);
            }
            RecordFriendListChanges(accountName, baseline, result);
            ReconcileKnownFriends(accountName, friends);
            foreach (var friend in friends.Append(currentUser))
                if (uint.TryParse(friend.GameId, out var appId) && appId != 0)
                    richPresenceCollector.GameChanged(accountName, ulong.Parse(friend.SteamId), appId);
            richPresenceCollector.PollNow(accountName);
            presenceFeed.RequestLevels(
                accountName,
                friends.Select(friend => ulong.Parse(friend.SteamId))
                    .Append(ulong.Parse(currentUser.SteamId)).ToArray());
            var fetchedAt = timeProvider.GetUtcNow();
            snapshotStore.ReportResult(
                SteamFeatureSnapshotStore.FriendsResourceKind,
                accountName,
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                fetchedAt);
            try
            {
                await snapshotStore.SaveFriendsAsync(
                    Clone(result), SteamDataSource.Cm, fetchedAt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to persist Steam friends snapshot for {AccountName}", accountName);
            }
            logger.LogInformation("Got {Count} Steam friends for {AccountName}", friends.Count, accountName);
            lock (GetCacheLock(accountName)) return Clone(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to get Steam friends for {AccountName}", accountName);
            return Fallback(accountName, cached, SteamFailureKind.Unknown, "friends_refresh_failed");
        }
    }

    internal async Task<List<SteamFriendData>> GetAllLoggedInUsersFriendsAsync(
        CancellationToken cancellationToken = default)
    {
        var cached = await snapshotStore.GetFriendsAsync(cancellationToken).ConfigureAwait(false);
        var result = cached.ToDictionary(
            snapshot => snapshot.AccountName,
            snapshot => Clone(snapshot.Value),
            StringComparer.OrdinalIgnoreCase);
        var loggedInAccounts = presenceFeed.GetLoggedInAccounts();
        var loggedInSet = loggedInAccounts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in cached.Where(snapshot => !loggedInSet.Contains(snapshot.AccountName)))
            snapshotStore.ReportResult(
                SteamFeatureSnapshotStore.FriendsResourceKind,
                snapshot.AccountName,
                SteamDataSource.Sqlite,
                SteamFreshness.Stale,
                snapshot.LastSuccessfulUpdate,
                SteamFailureKind.AuthenticationRequired,
                "friends_session_unavailable");
        foreach (var accountName in loggedInAccounts)
        {
            var data = await GetFriendsForUserAsync(accountName, cancellationToken).ConfigureAwait(false);
            if (data != null) result[accountName] = data;
        }
        return result.Values.ToList();
    }

    internal async Task<List<SteamFriendData>> GetCachedFriendsDataAsync(
        CancellationToken cancellationToken = default)
    {
        var persisted = await snapshotStore.GetFriendsAsync(cancellationToken).ConfigureAwait(false);
        var result = persisted.ToDictionary(
            snapshot => snapshot.AccountName,
            snapshot => Clone(snapshot.Value),
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in _userFriendsData)
        {
            lock (GetCacheLock(item.Key)) result[item.Key] = Clone(item.Value);
        }
        return result.Values.ToList();
    }

    private SteamFriendData? Fallback(
        string accountName,
        SteamCachedSnapshot<SteamFriendData>? cached,
        SteamFailureKind failure,
        string diagnosticCode)
    {
        if (cached == null)
        {
            snapshotStore.ReportFailure(
                SteamFeatureSnapshotStore.FriendsResourceKind,
                accountName,
                failure,
                diagnosticCode);
            return null;
        }
        snapshotStore.ReportResult(
            SteamFeatureSnapshotStore.FriendsResourceKind,
            accountName,
            SteamDataSource.Sqlite,
            SteamFreshness.Stale,
            cached.LastSuccessfulUpdate,
            failure,
            diagnosticCode);
        return Clone(cached.Value);
    }

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

    // 快照只带基础字段。这些字段由推送累积，刷新好友列表时不能丢：
    // 之前每次刷新都把 Rich Presence 清空，下一条回复又被当成一次新变化
    private static void RestoreVolatileFields(SteamFriendData result, SteamFriendData? previous)
    {
        if (previous == null) return;
        var known = previous.Friends.Append(previous.CurrentUser)
            .GroupBy(friend => friend.SteamId)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var friend in result.Friends.Append(result.CurrentUser))
        {
            if (!known.TryGetValue(friend.SteamId, out var old)) continue;
            friend.PersonaStateFlags = old.PersonaStateFlags;
            friend.LastLogOff = old.LastLogOff;
            friend.LastLogOn = old.LastLogOn;
            if (friend.GameId == old.GameId) friend.RichPresence = old.RichPresence;
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

    private static bool ApplyNicknames(SteamFriendData data, IReadOnlyDictionary<ulong, string> nicknames)
    {
        var changed = false;
        foreach (var friend in data.Friends)
        {
            var nickname = ulong.TryParse(friend.SteamId, out var steamId)
                           && nicknames.TryGetValue(steamId, out var value) ? value : string.Empty;
            if (friend.Nickname == nickname) continue;
            friend.Nickname = nickname;
            changed = true;
        }
        return changed;
    }

    // 昵称列表读取失败时沿用已知的昵称，否则一次网络抖动会把全部昵称清空
    private static void RestoreCachedNicknames(SteamFriendData result, SteamFriendData? source)
    {
        if (source == null) return;
        var known = source.Friends.ToDictionary(friend => friend.SteamId, friend => friend.Nickname);
        foreach (var friend in result.Friends)
            if (known.TryGetValue(friend.SteamId, out var nickname)) friend.Nickname = nickname;
    }

    private async Task RefreshNicknamesAsync(string accountName)
    {
        var nicknames = await presenceFeed.GetNicknamesAsync(accountName, _stopping.Token).ConfigureAwait(false);
        if (nicknames == null || !_userFriendsData.TryGetValue(accountName, out var data)) return;
        lock (GetCacheLock(accountName))
        {
            if (!ApplyNicknames(data, nicknames)) return;
            data.LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
            SendFriendsUpdateEvent(eventBus, accountName, data);
        }
    }

    /// <summary>订阅这个账号的推送；返回这次是不是新订阅的。</summary>
    private bool EnsureSubscribed(string accountName)
    {
        if (_subscriptions.ContainsKey(accountName)) return false;
        var subscription = presenceFeed.Subscribe(accountName, new SteamPresenceFeedHandlers(
            update => OnPersonaChanged(accountName, update),
            update => richPresenceCollector.Observe(accountName, update),
            levels => OnLevelsChanged(accountName, levels),
            () => OnFriendsListChanged(accountName),
            () => TrackCallback(RefreshNicknamesAsync(accountName)),
            update => OnGroupChanged(accountName, update)));
        if (subscription == null) return false;
        if (!_subscriptions.TryAdd(accountName, subscription))
        {
            subscription.Dispose();
            return false;
        }
        return true;
    }

    private void StartRichPresence(string accountName, ulong selfSteamId)
        => richPresenceCollector.Start(accountName, new RichPresenceHost(
            selfSteamId,
            () => GetPlayers(accountName, selfSteamId),
            (steamId, appId, text) => ApplyRichPresenceText(accountName, steamId, appId, text),
            steamId => FindPersonaName(accountName, steamId)));

    private IReadOnlyList<(uint AppId, ulong SteamId)> GetPlayers(string accountName, ulong selfSteamId)
    {
        if (!_userFriendsData.TryGetValue(accountName, out var data)) return [];
        // 自己正在玩什么以本机记录为准，CM 不一定把自己的状态发给这条会话
        var localApp = runningGameProbe?.GetRunningAppId(selfSteamId.ToString());
        var players = new List<(uint AppId, ulong SteamId)>();
        lock (GetCacheLock(accountName))
        {
            foreach (var friend in data.Friends)
                if (uint.TryParse(friend.GameId, out var appId) && appId != 0 && ulong.TryParse(friend.SteamId, out var steamId))
                    players.Add((appId, steamId));
            uint.TryParse(data.CurrentUser.GameId, out var cmApp);
            var selfApp = localApp ?? cmApp;
            if (selfApp != 0) players.Add((selfApp, selfSteamId));
            richPresenceCollector.GameChanged(accountName, selfSteamId, selfApp);
        }
        return players;
    }

    private string? FindPersonaName(string accountName, ulong steamId)
    {
        if (!_userFriendsData.TryGetValue(accountName, out var data)) return null;
        var id = steamId.ToString();
        lock (GetCacheLock(accountName))
            return data.Friends.FirstOrDefault(friend => friend.SteamId == id)?.PersonaName;
    }

    private void ApplyRichPresenceText(string accountName, ulong steamId, uint appId, string? text)
    {
        if (text == null || !_userFriendsData.TryGetValue(accountName, out var data)) return;
        var id = steamId.ToString();
        lock (GetCacheLock(accountName))
        {
            var friend = data.CurrentUser.SteamId == id ? data.CurrentUser : data.Friends.FirstOrDefault(item => item.SteamId == id);
            if (friend == null || friend.RichPresence == text) return;
            // 账号本人的游戏来自本机记录，好友快照里可能还是 0
            if (friend != data.CurrentUser && friend.GameId != appId.ToString()) return;
            friend.RichPresence = text;
            data.LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
            SendFriendsUpdateEvent(eventBus, accountName, data);
        }
    }

    private void OnPersonaChanged(string accountName, SteamPersonaUpdate update)
    {
        // 不写昵称：好友的名字不该进日志（开发构建一天能写几百行）
        logger.LogDebug(
            "Persona state updated: {State}, app {AppId}",
            update.PersonaState, update.GameAppId);
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
            {
                // 会话重连后内存里的状态从「离线、没在玩」重新开始。第一次拿到这位好友的状态时不和它比，
                // 而是交给记录器和落库的最后一条比：没变就不写，变了才补一条（包括断线期间下线的）。
                var reconcileState = update.Fields.HasFlag(SteamPersonaUpdateFields.Presence) && MarkReconciled(accountName, friendId, "state");
                var reconcileGame = update.Fields.HasFlag(SteamPersonaUpdateFields.GameInfo) && MarkReconciled(accountName, friendId, "game");
                TryRecordFriendChanges(accountName, friend, oldState, oldGameId, oldGameName, oldName, reconcileState, reconcileGame);
            }
            data.LastUpdateTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
            SendFriendsUpdateEvent(eventBus, accountName, data);
            if (update.Fields.HasFlag(SteamPersonaUpdateFields.GameInfo))
            {
                richPresenceCollector.GameChanged(accountName, update.SteamId, update.GameAppId);
                if (update.GameAppId != 0 && oldGameId != friend.GameId)
                    presenceFeed.RequestRichPresence(accountName, update.GameAppId, [update.SteamId]);
            }
        }
    }

    private void OnGroupChanged(string accountName, SteamPersonaGroupUpdate update)
    {
        if (groupCollector == null || !_userFriendsData.TryGetValue(accountName, out var data)) return;
        string self;
        lock (GetCacheLock(accountName)) self = data.CurrentUser.SteamId;
        groupCollector.Observe(accountName, self, update);
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
        _friendsListSeen[accountName] = 0;
        CancelEmptyConfirmation(accountName);
        TrackCallback(FetchAndPublishAsync(accountName, acceptEmpty: false, _stopping.Token));
    }

    // 后台触发的拉取（好友列表回调、空列表确认）没有调用方等结果，页面要靠事件才知道
    private async Task FetchAndPublishAsync(string accountName, bool acceptEmpty, CancellationToken cancellationToken)
    {
        await GetFriendsForUserAsync(accountName, acceptEmpty, cancellationToken).ConfigureAwait(false);
        if (_userFriendsData.TryGetValue(accountName, out var current))
            SendFriendsUpdateEvent(eventBus, accountName, current);
    }

    private void ScheduleEmptyConfirmation(string accountName)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (!_emptyConfirmations.TryAdd(accountName, cancellation))
        {
            cancellation.Dispose();
            return;
        }
        TrackCallback(ConfirmEmptyFriendsAsync(accountName, cancellation));
    }

    private async Task ConfirmEmptyFriendsAsync(string accountName, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(EmptyListGrace, timeProvider, cancellation.Token).ConfigureAwait(false);
            _emptyConfirmations.TryRemove(new KeyValuePair<string, CancellationTokenSource>(accountName, cancellation));
            await FetchAndPublishAsync(accountName, acceptEmpty: true, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 好友列表到了，或者会话结束了
        }
        finally
        {
            _emptyConfirmations.TryRemove(new KeyValuePair<string, CancellationTokenSource>(accountName, cancellation));
            cancellation.Dispose();
        }
    }

    private void CancelEmptyConfirmation(string accountName)
    {
        if (!_emptyConfirmations.TryRemove(accountName, out var cancellation)) return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经跑完
        }
    }

    // 只更新本次推送实际携带的字段：Steam 常只推送部分字段（如仅 LastSeen），
    // 未携带的字段是默认值，按完整状态处理会把游戏 / 昵称误清空。
    private void UpdateFriendInfo(SteamFriendInfo friend, SteamPersonaUpdate update)
    {
        var fields = update.Fields;
        if (fields.HasFlag(SteamPersonaUpdateFields.PlayerName) && !string.IsNullOrEmpty(update.PersonaName))
            friend.PersonaName = update.PersonaName;
        if (fields.HasFlag(SteamPersonaUpdateFields.Presence))
        {
            friend.PersonaState = update.PersonaState;
            friend.PersonaStateFlags = update.PersonaStateFlags;
            if (!string.IsNullOrEmpty(update.AvatarHash)) friend.AvatarHash = update.AvatarHash;
        }
        if (fields.HasFlag(SteamPersonaUpdateFields.LastSeen))
        {
            friend.LastLogOff = update.LastLogOff;
            friend.LastLogOn = update.LastLogOn;
        }
        if (!fields.HasFlag(SteamPersonaUpdateFields.GameInfo)) return;
        var gameId = update.GameAppId.ToString();
        // 换游戏 / 退出游戏时旧的游戏内状态不再适用
        if (friend.GameId != gameId) friend.RichPresence = string.Empty;
        if (update.GameAppId != 0)
        {
            friend.GameId = gameId;
            friend.GameName = GetGameName(update.GameAppId);
        }
        else
        {
            friend.GameId = "0";
            friend.GameName = string.Empty;
        }
    }

    /// <summary>这位好友的这类状态在本次会话里是不是第一次对账。</summary>
    private bool MarkReconciled(string accountName, string friendSteamId, string changeType)
    {
        var reconciled = _reconciledFriends.GetOrAdd(accountName, _ => new HashSet<string>());
        lock (reconciled) return reconciled.Add($"{friendSteamId}:{changeType}");
    }

    // 拉取时 SteamKit 已经有资料的好友（名字不为空）直接对账：他们的状态推送可能早于这次拉取，之后不会再来。
    // 还没有资料的好友留给第一次状态推送，否则会把「还没收到」记成「离线」。
    private void ReconcileKnownFriends(string accountName, IReadOnlyList<SteamFriendInfo> friends)
    {
        foreach (var friend in friends)
        {
            if (string.IsNullOrEmpty(friend.PersonaName) || !friendStatusRecorder.IsTracked(accountName, friend.SteamId)) continue;
            var reconcileState = MarkReconciled(accountName, friend.SteamId, "state");
            var reconcileGame = MarkReconciled(accountName, friend.SteamId, "game");
            if (!reconcileState && !reconcileGame) continue;
            TryRecordFriendChanges(
                accountName, friend, friend.PersonaState, friend.GameId, friend.GameName, friend.PersonaName,
                reconcileState, reconcileGame);
        }
    }

    private void TryRecordFriendChanges(
        string accountName,
        SteamFriendInfo friend,
        int oldState,
        string oldGameId,
        string oldGameName,
        string oldName,
        bool reconcileState = false,
        bool reconcileGame = false)
    {
        if (oldState != friend.PersonaState || reconcileState)
            TrackCallback(friendStatusRecorder.RecordChangeAsync(
                accountName, friend.SteamId, friend.PersonaName, "state",
                new FriendStatusValue(PersonaState: oldState),
                new FriendStatusValue(PersonaState: friend.PersonaState),
                _stopping.Token));
        if (oldGameId != friend.GameId || reconcileGame)
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

    // 好友列表增删对所有好友记录（不依赖追踪名单），用于「谁删了我」。
    // 没有同一身份的基线或新列表为空（好友列表尚未下发）时跳过，避免误报整表增删。
    private void RecordFriendListChanges(string accountName, SteamFriendData? baseline, SteamFriendData current)
    {
        if (baseline == null
            || baseline.Friends.Count == 0
            || current.Friends.Count == 0
            || string.IsNullOrEmpty(current.CurrentUser.SteamId)
            || baseline.CurrentUser.SteamId != current.CurrentUser.SteamId)
            return;
        var before = baseline.Friends.ToDictionary(friend => friend.SteamId);
        var after = current.Friends.ToDictionary(friend => friend.SteamId);
        foreach (var friend in current.Friends.Where(friend => !before.ContainsKey(friend.SteamId)))
            TrackCallback(friendStatusRecorder.RecordChangeAsync(
                accountName, friend.SteamId, friend.PersonaName, RelationshipChangeType,
                new FriendStatusValue(Relationship: RelationshipNone),
                new FriendStatusValue(Relationship: RelationshipFriend),
                _stopping.Token));
        foreach (var friend in baseline.Friends.Where(friend => !after.ContainsKey(friend.SteamId)))
            TrackCallback(friendStatusRecorder.RecordChangeAsync(
                accountName, friend.SteamId, friend.PersonaName, RelationshipChangeType,
                new FriendStatusValue(Relationship: RelationshipFriend),
                new FriendStatusValue(Relationship: RelationshipNone),
                _stopping.Token));
    }

    public const string RelationshipChangeType = "relationship";
    private const int RelationshipNone = 0;
    private const int RelationshipFriend = 3;

    private void SendFriendsUpdateEvent(IEventBus targetEventBus, string accountName, SteamFriendData data)
    {
        SteamFriendsSnapshot snapshot;
        SteamFriendData persisted;
        lock (GetCacheLock(accountName))
        {
            snapshot = ToSnapshot(data);
            persisted = Clone(data);
        }
        TrackCallback(targetEventBus.PublishAsync(new FriendsChanged(accountName, snapshot), _stopping.Token));
        TrackCallback(PersistFriendsSnapshotAsync(persisted, _stopping.Token));
    }

    private async Task PersistFriendsSnapshotAsync(SteamFriendData data, CancellationToken cancellationToken)
    {
        var fetchedAt = DateTimeOffset.FromUnixTimeSeconds(data.LastUpdateTime);
        snapshotStore.ReportResult(
            SteamFeatureSnapshotStore.FriendsResourceKind,
            data.AccountName,
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            fetchedAt);
        await snapshotStore.SaveFriendsAsync(
            data, SteamDataSource.Cm, fetchedAt, cancellationToken).ConfigureAwait(false);
    }

    private static SteamFriendsSnapshot ToSnapshot(SteamFriendData data) => new(
        data.AccountName, ToSnapshot(data.CurrentUser), data.Friends.Select(ToSnapshot).ToArray(), data.LastUpdateTime);
    private static SteamFriendSnapshot ToSnapshot(SteamFriendInfo friend) => new(
        friend.SteamId, friend.PersonaName, friend.PersonaState, friend.PersonaStateFlags,
        friend.Relationship, friend.GameName, friend.GameId, friend.AvatarHash,
        friend.LastLogOff, friend.LastLogOn, friend.RichPresence, friend.Level, friend.Nickname);
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
        Level = friend.Level,
        Nickname = friend.Nickname
    };

    public void ClearUserFriendsData(string accountName)
    {
        _userFriendsData.TryRemove(accountName, out _);
        _cacheLocks.TryRemove(accountName, out _);
        if (_subscriptions.TryRemove(accountName, out var subscription)) subscription.Dispose();
        _friendsListSeen.TryRemove(accountName, out _);
        _reconciledFriends.TryRemove(accountName, out _);
        CancelEmptyConfirmation(accountName);
        TrackCallback(richPresenceCollector.StopAsync(accountName));
        groupCollector?.Forget(accountName);
        logger.LogDebug("Cleared Steam friends data for {AccountName}", accountName);
    }

    public async Task HandleAsync(SteamSessionReady message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_subscriptions.TryRemove(message.AccountName, out var subscription)) subscription.Dispose();
        // 新的会话代次：好友列表要重新等，好友状态要重新和落库记录对账
        _friendsListSeen.TryRemove(message.AccountName, out _);
        _reconciledFriends.TryRemove(message.AccountName, out _);
        CancelEmptyConfirmation(message.AccountName);
        await GetFriendsForUserAsync(message.AccountName, cancellationToken).ConfigureAwait(false);
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

    public RichPresenceCoverage? GetRichPresenceCoverage(string accountName) => richPresenceCollector.GetCoverage(accountName);

    public IReadOnlyList<string> GetRichPresenceAccounts() => richPresenceCollector.GetAccounts();

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
        foreach (var gate in _fetchGates.Values) gate.Dispose();
        _fetchGates.Clear();
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
    /// <summary>当前账号在 Steam 里为这位好友设置的昵称，未设置时为空</summary>
    public string Nickname { get; set; } = string.Empty;
}
