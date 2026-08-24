using ElectronNET.API;
using ElectronNet.Constants;
using SteamKit2;

namespace ElectronNet.Services;

public static class SteamFriendsService
{
    // 已登录用户的好友数据缓存
    private static readonly Dictionary<string, SteamFriendData> _userFriendsData = new();

    // 好友状态更新事件订阅
    private static readonly Dictionary<string, bool> _friendsCallbacksRegistered = new();

    /// <summary>
    /// 获取指定已登录用户的好友列表
    /// </summary>
    public static SteamFriendData? GetFriendsForUser(string accountName)
    {
        try
        {
            var session = SteamLoginService.GetSessionByAccountName(accountName);
            if (session == null)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} User {accountName} not logged in");
                return null;
            }

            (SteamClient client, CallbackManager manager, _) = session.Value;
            var steamFriends = client.GetHandler<SteamFriends>();
            if (steamFriends == null)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} SteamFriends handler not available");
                return null;
            }

            // 注册好友状态更新回调（如果还没注册）
            if (!_friendsCallbacksRegistered.ContainsKey(accountName))
            {
                RegisterFriendsCallbacks(accountName, manager);
                _friendsCallbacksRegistered[accountName] = true;
            }

            // 获取当前用户信息
            var currentUser = GetFriendInfo(steamFriends, client.SteamID ?? new SteamID(), client);

            // 获取好友列表
            var friendCount = steamFriends.GetFriendCount();
            var friends = new List<SteamFriendInfo>();

            for (var i = 0; i < friendCount; i++)
            {
                var friendSteamId = steamFriends.GetFriendByIndex(i);

                // 只保留双向好友，过滤掉单向好友（如对方申请添加但未同意的 RequestRecipient/RequestInitiator）
                if (steamFriends.GetFriendRelationship(friendSteamId) != EFriendRelationship.Friend)
                {
                    continue;
                }

                var friendInfo = GetFriendInfo(steamFriends, friendSteamId);
                friends.Add(friendInfo);
            }

            var result = new SteamFriendData
            {
                AccountName = accountName,
                CurrentUser = currentUser,
                Friends = friends,
                LastUpdateTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            // 缓存数据（保留旧缓存引用用于恢复等级等不常变化的信息）
            _userFriendsData.TryGetValue(accountName, out var previousData);
            _userFriendsData[accountName] = result;

            // 恢复上一份缓存中已获取到的等级信息（等级不常变化）
            RestoreCachedLevels(result, previousData);

            // 请求所有游戏中好友的 Rich Presence（按 AppID 分组路由）
            var richPresenceHandler = client.GetHandler<SteamRichPresenceHandler>();
            if (richPresenceHandler != null)
            {
                foreach (var group in friends
                             .Where(f => uint.TryParse(f.GameId, out var appId) && appId != 0)
                             .GroupBy(f => f.GameId))
                {
                    richPresenceHandler.RequestRichPresence(uint.Parse(group.Key), group.Select(f => ulong.Parse(f.SteamId)));
                }
            }

            // 批量请求当前用户与所有好友的 Steam 等级，结果通过 FriendsSteamLevelsCallback 返回
            var levelsHandler = client.GetHandler<SteamLevelsHandler>();
            levelsHandler?.RequestFriendLevels(
                friends.Select(f => new SteamID(ulong.Parse(f.SteamId)).AccountID)
                    .Append(new SteamID(ulong.Parse(currentUser.SteamId)).AccountID));

            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Got {friends.Count} friends for {accountName}");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Error getting friends for {accountName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取所有已登录用户的好友数据
    /// </summary>
    public static List<SteamFriendData> GetAllLoggedInUsersFriends()
    {
        var result = new List<SteamFriendData>();
        var loggedInUsers = SteamLoginService.GetLoggedInUsers();

        foreach (var accountName in loggedInUsers)
        {
            var friendsData = GetFriendsForUser(accountName);
            if (friendsData != null)
            {
                result.Add(friendsData);
            }
        }

        return result;
    }

    /// <summary>
    /// 获取缓存的好友数据（不重新请求）
    /// </summary>
    public static List<SteamFriendData> GetCachedFriendsData()
    {
        return _userFriendsData.Values.ToList();
    }

    /// <summary>
    /// 获取单个好友的详细信息
    /// </summary>
    private static SteamFriendInfo GetFriendInfo(SteamFriends steamFriends, SteamID friendSteamId, SteamClient? client = null)
    {
        var relationship = steamFriends.GetFriendRelationship(friendSteamId);
        var personaState = steamFriends.GetFriendPersonaState(friendSteamId);
        var personaName = steamFriends.GetFriendPersonaName(friendSteamId);
        var gameId = steamFriends.GetFriendGamePlayed(friendSteamId);
        var avatarHash = GetAvatarHash(steamFriends, friendSteamId);

        // 获取游戏名称
        var gameName = string.Empty;
        if (gameId.AppID != 0)
        {
            gameName = GetGameName(client, gameId.AppID);
        }

        return new SteamFriendInfo
        {
            SteamId = friendSteamId.ConvertToUInt64().ToString(),
            PersonaName = personaName!,
            PersonaState = (int)personaState,
            PersonaStateFlags = 0, // 初次获取时无法获得，需要通过回调更新
            Relationship = (int)relationship,
            GameName = gameName,
            GameId = gameId.AppID.ToString(),
            AvatarHash = avatarHash,
            LastLogOff = 0, // 初次获取时无法获得，需要通过回调更新
            LastLogOn = 0,
            RichPresence = string.Empty, // 初次获取时无法获得，需要通过回调更新
        };
    }

    /// <summary>
    /// 获取头像哈希
    /// </summary>
    private static string GetAvatarHash(SteamFriends steamFriends, SteamID steamId)
    {
        try
        {
            var avatarHash = steamFriends.GetFriendAvatar(steamId);
            if (avatarHash != null && avatarHash.Length > 0)
            {
                return BitConverter.ToString(avatarHash).Replace("-", "").ToLowerInvariant();
            }
        }
        catch
        {
            // 忽略获取头像哈希失败的情况
        }
        return "";
    }

    /// <summary>
    /// 根据 AppID 获取游戏名称（同步返回本地缓存；缺失时后台异步请求 Store API 并推送更新事件）
    /// </summary>
    private static string GetGameName(SteamClient? client, uint appId)
    {
        if (appId == 0)
        {
            return string.Empty;
        }

        try
        {
            // 首先尝试从本地数据库缓存获取
            var appName = SteamAppService.GetAppNameByAppId(appId);
            if (!string.IsNullOrEmpty(appName))
            {
                return appName;
            }

            // 本地缓存缺失，触发后台异步请求 Steam Store API 获取并缓存
            // 获取完成后推送所有用户的好友更新事件，前端会自动刷新显示
            _ = Task.Run(async () =>
            {
                var fetchedName = await SteamAppService.GetAppNameByAppIdAsync(appId);
                if (!string.IsNullOrEmpty(fetchedName))
                {
                    PropagateGameNameUpdate(appId, fetchedName);
                }
            });

            // 在名称到达前，临时展示 AppID
            return $"App {appId}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} GetGameName failed for AppID {appId}: {ex.Message}");
            return $"App {appId}";
        }
    }

    /// <summary>
    /// 当某个 AppID 的名称被异步获取到后，更新所有缓存中对应好友的 GameName，
    /// 并推送更新事件到前端，让前端实时看到最新的游戏名称。
    /// </summary>
    private static void PropagateGameNameUpdate(uint appId, string newGameName)
    {
        var appIdStr = appId.ToString();
        foreach (var (accountName, data) in _userFriendsData)
        {
            var changed = false;

            if (data.CurrentUser.GameId == appIdStr && data.CurrentUser.GameName != newGameName)
            {
                data.CurrentUser.GameName = newGameName;
                changed = true;
            }

            foreach (var friend in data.Friends.Where(f => f.GameId == appIdStr && f.GameName != newGameName))
            {
                friend.GameName = newGameName;
                changed = true;
            }

            if (changed)
            {
                data.LastUpdateTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SendFriendsUpdateEvent(accountName, data);
            }
        }
    }

    /// <summary>
    /// 用上一份缓存数据恢复新结果中的 Steam 等级（等级不常变化，避免每次刷新后闪烁为空）
    /// </summary>
    private static void RestoreCachedLevels(SteamFriendData result, SteamFriendData? previousData)
    {
        if (previousData == null)
        {
            return;
        }

        if (previousData.CurrentUser.SteamId == result.CurrentUser.SteamId)
        {
            result.CurrentUser.Level ??= previousData.CurrentUser.Level;
        }

        var previousLevels = previousData.Friends
            .Where(f => f.Level.HasValue)
            .ToDictionary(f => f.SteamId, f => f.Level);
        foreach (var friend in result.Friends)
        {
            if (friend.Level == null && previousLevels.TryGetValue(friend.SteamId, out var level))
            {
                friend.Level = level;
            }
        }
    }

    /// <summary>
    /// 从回调更新好友信息
    /// </summary>
    private static void UpdateFriendInfoFromCallback(SteamFriendInfo friendInfo, SteamFriends.PersonaStateCallback callback, SteamClient? client, SteamFriends? steamFriends = null)
    {
        friendInfo.PersonaName = callback.Name;
        friendInfo.PersonaState = (int)callback.State;
        friendInfo.PersonaStateFlags = (int)callback.StateFlags;

        // 更新最后登录/登出时间（转换为 Unix 时间戳）
        friendInfo.LastLogOff = new DateTimeOffset(callback.LastLogOff).ToUnixTimeSeconds();
        friendInfo.LastLogOn = new DateTimeOffset(callback.LastLogOn).ToUnixTimeSeconds();

        // 更新游戏信息
        if (callback.GameID.AppID != 0)
        {
            friendInfo.GameId = callback.GameID.AppID.ToString();
            friendInfo.GameName = GetGameName(client, callback.GameID.AppID);
        }
        else
        {
            friendInfo.GameId = "0";
            friendInfo.GameName = string.Empty;

            // 不在游戏中时清空 Rich Presence（在游戏中时由 RichPresenceInfoCallback 异步更新）
            friendInfo.RichPresence = string.Empty;
        }

        // 更新头像
        if (callback.AvatarHash != null && callback.AvatarHash.Length > 0)
        {
            friendInfo.AvatarHash = BitConverter.ToString(callback.AvatarHash).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// 注册好友状态更新回调
    /// </summary>
    private static void RegisterFriendsCallbacks(string accountName, CallbackManager manager)
    {
        // 好友状态变化
        manager.Subscribe<SteamFriends.PersonaStateCallback>(callback =>
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} PersonaState updated for {callback.Name}: State={callback.State}, GameAppID={callback.GameID.AppID}");

            // 更新缓存中的好友数据
            if (_userFriendsData.TryGetValue(accountName, out var data))
            {
                var friendSteamId = callback.FriendID.ConvertToUInt64().ToString();

                // 获取会话以便获取 SteamFriends 和 SteamClient
                var session = SteamLoginService.GetSessionByAccountName(accountName);
                var steamFriends = session?.client.GetHandler<SteamFriends>();

                // 检查是否是当前用户
                if (data.CurrentUser.SteamId == friendSteamId)
                {
                    UpdateFriendInfoFromCallback(data.CurrentUser, callback, session?.client, steamFriends);
                }
                else
                {
                    // 更新好友信息（先对比旧值，再更新，以便记录变化）
                    var friend = data.Friends.FirstOrDefault(f => f.SteamId == friendSteamId);
                    if (friend != null)
                    {
                        // 保存旧值用于对比
                        var oldPersonaState = friend.PersonaState;
                        var oldGameId = friend.GameId;
                        var oldGameName = friend.GameName;
                        var oldPersonaName = friend.PersonaName;

                        UpdateFriendInfoFromCallback(friend, callback, session?.client, steamFriends);

                        // 如果好友被追踪，记录变化到数据库
                        if (FriendStatusRecordService.IsTracked(accountName, friendSteamId))
                        {
                            TryRecordFriendChanges(accountName, friend, oldPersonaState, oldGameId, oldGameName, oldPersonaName);
                        }
                    }
                }

                data.LastUpdateTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // 通知前端
                SendFriendsUpdateEvent(accountName, data);

                // 在游戏中时请求 Rich Presence（比分等富文本状态），结果通过 RichPresenceInfoCallback 返回
                if (callback.GameID.AppID != 0)
                {
                    session?.client.GetHandler<SteamRichPresenceHandler>()
                        ?.RequestRichPresence(callback.GameID.AppID, [callback.FriendID.ConvertToUInt64()]);
                }
            }
        });

        // Rich Presence 信息回调（解析后更新好友的富文本状态）
        manager.Subscribe<RichPresenceInfoCallback>(callback =>
        {
            if (!_userFriendsData.TryGetValue(accountName, out var data))
            {
                return;
            }

            var session = SteamLoginService.GetSessionByAccountName(accountName);
            if (session == null)
            {
                return;
            }
            var client = session.Value.client;

            foreach (var entry in callback.Entries)
            {
                var friendSteamId = entry.SteamId.ToString();
                var friend = data.Friends.FirstOrDefault(f => f.SteamId == friendSteamId);
                if (friend == null)
                {
                    continue;
                }

                if (!uint.TryParse(friend.GameId, out var appId))
                {
                    appId = 0;
                }

                var keyValues = entry.KeyValues;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var resolved = await SteamRichPresenceService.ResolveAsync(client, appId, keyValues);
                        if (friend.RichPresence == resolved)
                        {
                            return;
                        }

                        var oldRichPresence = friend.RichPresence;
                        friend.RichPresence = resolved;
                        Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} RichPresence updated for {friend.PersonaName}: {resolved}");

                        // 如果好友被追踪，记录富文本状态变化
                        if (FriendStatusRecordService.IsTracked(accountName, friendSteamId))
                        {
                            _ = FriendStatusRecordService.RecordChangeAsync(
                                accountName,
                                friendSteamId,
                                friend.PersonaName,
                                "richPresence",
                                new { richPresence = oldRichPresence },
                                new { richPresence = resolved });
                        }

                        data.LastUpdateTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        SendFriendsUpdateEvent(accountName, data);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Failed to resolve rich presence for {friend.PersonaName}: {ex.Message}");
                    }
                });
            }
        });

        // 从 ClientPersonaState 报文直接读取到的 Rich Presence 回调
        manager.Subscribe<PersonaStateRichPresenceCallback>(callback =>
        {
            if (!_userFriendsData.TryGetValue(accountName, out var data))
            {
                return;
            }

            var friendSteamId = callback.SteamId.ToString();
            var friend = data.Friends.FirstOrDefault(f => f.SteamId == friendSteamId);
            if (friend == null && data.CurrentUser.SteamId == friendSteamId)
            {
                friend = data.CurrentUser;
            }

            if (friend == null)
            {
                return;
            }

            var session = SteamLoginService.GetSessionByAccountName(accountName);
            if (session == null)
            {
                // 会话已断开（例如正在重连），此时无法解析富文本状态
                return;
            }

            var client = session.Value.client;

            _ = Task.Run(async () =>
            {
                try
                {
                    var resolved = await SteamRichPresenceService.ResolveAsync(client, callback.AppId, callback.KeyValues);
                    if (friend.RichPresence == resolved)
                    {
                        return;
                    }

                    friend.RichPresence = resolved;
                    data.LastUpdateTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    SendFriendsUpdateEvent(accountName, data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Failed to resolve rich presence from PersonaState for {friend.PersonaName}: {ex.Message}");
                }
            });
        });

        // 好友 Steam 等级回调（批量更新缓存中的等级）
        manager.Subscribe<FriendsSteamLevelsCallback>(callback =>
        {
            if (!_userFriendsData.TryGetValue(accountName, out var data) || callback.Levels.Count == 0)
            {
                return;
            }

            var changed = false;
            foreach (var friend in data.Friends.Append(data.CurrentUser))
            {
                if (!ulong.TryParse(friend.SteamId, out var steamId))
                {
                    continue;
                }

                var accountId = new SteamID(steamId).AccountID;
                if (callback.Levels.TryGetValue(accountId, out var level) && friend.Level != level)
                {
                    friend.Level = level;
                    changed = true;
                }
            }

            if (changed)
            {
                data.LastUpdateTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SendFriendsUpdateEvent(accountName, data);
            }
        });

        // 好友列表变化（添加/删除好友）
        manager.Subscribe<SteamFriends.FriendsListCallback>(_ =>
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Friends list changed for {accountName}");

            // 重新获取好友列表
            GetFriendsForUser(accountName);
        });

        Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Registered callbacks for {accountName}");
    }

    /// <summary>
    /// 尝试记录好友的变化（仅在被追踪时才调用）
    /// 对比新旧值，有哪些字段发生变化就记录哪些
    /// </summary>
    private static void TryRecordFriendChanges(
        string accountName,
        SteamFriendInfo friend,
        int oldPersonaState,
        string oldGameId,
        string oldGameName,
        string oldPersonaName)
    {
        // 状态变化
        if (oldPersonaState != friend.PersonaState)
        {
            _ = FriendStatusRecordService.RecordChangeAsync(
                accountName,
                friend.SteamId,
                friend.PersonaName,
                "state",
                new { personaState = oldPersonaState },
                new { personaState = friend.PersonaState });
        }

        // 游戏变化（开始/结束/切换游戏）
        if (oldGameId != friend.GameId)
        {
            _ = FriendStatusRecordService.RecordChangeAsync(
                accountName,
                friend.SteamId,
                friend.PersonaName,
                "game",
                new { gameId = oldGameId, gameName = oldGameName },
                new { gameId = friend.GameId, gameName = friend.GameName });
        }

        // 昵称变化
        if (!string.IsNullOrEmpty(oldPersonaName) && oldPersonaName != friend.PersonaName)
        {
            _ = FriendStatusRecordService.RecordChangeAsync(
                accountName,
                friend.SteamId,
                friend.PersonaName,
                "personaName",
                new { personaName = oldPersonaName },
                new { personaName = friend.PersonaName });
        }
    }

    /// <summary>
    /// 向前端发送好友更新事件
    /// </summary>
    private static void SendFriendsUpdateEvent(string accountName, SteamFriendData data)
    {
        try
        {
            var mainWindow = Program.ElectronMainWindow;
            if (mainWindow == null) return;

            Electron.IpcMain.Send(mainWindow, "steamFriends:update", new { accountName, data });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Failed to send update event: {ex.Message}");
        }
    }

    /// <summary>
    /// 清理指定用户的好友数据（用户退出登录时调用）
    /// </summary>
    public static void ClearUserFriendsData(string accountName)
    {
        _userFriendsData.Remove(accountName);
        _friendsCallbacksRegistered.Remove(accountName);
        FriendStatusRecordService.ClearTrackingForAccount(accountName);
        Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Cleared friends data for {accountName}");
    }

    /// <summary>
    /// 请求好友信息（用于获取完整的个人资料）
    /// </summary>
    public static void RequestFriendInfo(string accountName, string friendSteamId)
    {
        try
        {
            var session = SteamLoginService.GetSessionByAccountName(accountName);
            if (session == null) return;

            var steamFriends = session.Value.client.GetHandler<SteamFriends>();
            steamFriends?.RequestFriendInfo(new SteamID(ulong.Parse(friendSteamId)));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} RequestFriendInfo failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Steam 好友数据（包含当前用户和好友列表）
/// </summary>
public class SteamFriendData
{
    public string AccountName { get; set; } = string.Empty;
    public SteamFriendInfo CurrentUser { get; set; } = new();
    public List<SteamFriendInfo> Friends { get; set; } = [];
    public int LastUpdateTime { get; set; }
}

/// <summary>
/// Steam 好友信息
/// </summary>
public class SteamFriendInfo
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
    /// <summary>
    /// Steam 等级（null 表示尚未获取到）
    /// </summary>
    public int? Level { get; set; }
}
