using System.Text.Json;
using ElectronNet.Constants;
using Microsoft.EntityFrameworkCore;
using SteamKit2;
using SteamKit2.Internal;

namespace ElectronNet.Services;

/// <summary>
/// Steam 游戏库服务
/// 通过 SteamKit2 UnifiedMessages 调用 IPlayerService / IFamilyGroupsService 获取：
/// - 本账号拥有的游戏（含本地化名称）
/// - Steam 家庭共享库中的游戏（含各游戏的家庭拥有者）
/// - 愿望单（通过公开 Web API）
/// - 各游戏的成就进度
/// </summary>
public static class SteamLibraryService
{
    /// <summary>
    /// 每个用户（accountName）的游戏库缓存（包含已拥有、家庭共享与愿望单）
    /// </summary>
    private static readonly Dictionary<string, List<SteamOwnedGame>> _userLibraryCache = new();

    // 共享 HttpClient，避免端口耗尽，见 HttpClientProvider
    private static HttpClient _httpClient => Helpers.HttpClientProvider.SteamApi;

    /// <summary>
    /// 获取指定已登录用户的完整游戏库
    /// </summary>
    /// <param name="accountName">已登录的账号名</param>
    /// <param name="includeFamilyShared">是否包含 Steam 家庭共享库中的游戏</param>
    public static async Task<List<SteamOwnedGame>> GetLibraryForUserAsync(string accountName, IDbContextFactory<AppDbContext> dbContextFactory, bool includeFamilyShared = true)
    {
        try
        {
            var session = SteamLoginService.GetSessionByAccountName(accountName);
            if (session == null)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} user {accountName} not logged in");
                return [];
            }

            var (client, _, _) = session.Value;
            var unifiedMessages = client.GetHandler<SteamUnifiedMessages>();
            if (unifiedMessages == null)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} SteamUnifiedMessages handler not available");
                return [];
            }

            var steamId = client.SteamID;
            if (steamId == null)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} client has no SteamID yet");
                return [];
            }

            var playerService = unifiedMessages.CreateService<Player>();
            var steamIdUlong = steamId.ConvertToUInt64();
            var language = SteamRichPresenceService.GetSteamLanguage();

            // 1. 拉取本账号已拥有的游戏（英文名 + 本地化名）
            var ownedGames = await FetchOwnedGamesAsync(playerService, steamIdUlong, language);

            // 2. 家庭共享：拉取共享游戏列表 + 所有游戏（含自有游戏）的家庭拥有者信息
            List<SteamOwnedGame> familySharedGames = [];
            Dictionary<uint, List<string>> familyOwnersMap = [];
            if (includeFamilyShared)
            {
                var familyGroupsService = unifiedMessages.CreateService<FamilyGroups>();
                var ownedAppIds = ownedGames.Select(g => (uint)g.AppId).ToHashSet();
                (familySharedGames, familyOwnersMap) = await FetchFamilySharedGamesAsync(familyGroupsService, playerService, steamIdUlong, ownedAppIds, language);
            }

            // 自有游戏也补充家庭拥有者信息
            foreach (var game in ownedGames)
            {
                if (familyOwnersMap.TryGetValue((uint)game.AppId, out var owners))
                {
                    game.OwnerSteamIds = owners;
                }
            }

            // 3. 合并列表
            var merged = ownedGames.Concat(familySharedGames)
                .OrderByDescending(g => g.PlaytimeForever)
                .ToList();

            // 4. 标记愿望单，并把愿望单中未拥有的游戏也加入列表
            await ApplyWishlistAsync(merged, steamIdUlong, dbContextFactory);

            // 5. 批量获取成就进度（含家庭共享游戏）
            await ApplyAchievementsProgressAsync(playerService, merged, steamIdUlong, language);

            // 6. 解析家庭拥有者的昵称
            ResolveOwnerNames(client, merged);

            _userLibraryCache[accountName] = merged;

            // 异步将游戏名称缓存到本地 SteamApp 表，方便后续好友游戏名快速显示
            _ = Task.Run(async () =>
            {
                await SteamAppService.EnsureAppsCachedAsync(
                    merged.Where(g => !string.IsNullOrEmpty(g.Name))
                        .Select(g => ((uint)g.AppId, (string?)g.Name)),
                    dbContextFactory);
            });

            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} got {ownedGames.Count} owned + {familySharedGames.Count} family shared games for {accountName}");
            return merged;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} GetLibraryForUserAsync failed for {accountName}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 拉取账号已拥有的游戏。
    /// 默认返回英文名称；如果目标语言不是英文，额外用带 language 的请求获取本地化名称。
    /// </summary>
    private static async Task<List<SteamOwnedGame>> FetchOwnedGamesAsync(Player playerService, ulong steamId, string language)
    {
        var request = new CPlayer_GetOwnedGames_Request
        {
            steamid = steamId,
            include_appinfo = true,
            include_played_free_games = true,
            include_free_sub = false,
            skip_unvetted_apps = false
        };

        var serviceResponse = await playerService.GetOwnedGames(request);
        if (serviceResponse.Result != EResult.OK)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} GetOwnedGames failed with {serviceResponse.Result}");
            return [];
        }

        var games = serviceResponse.Body?.games ?? [];
        var result = games.Select(g => new SteamOwnedGame
        {
            AppId = (int)g.appid,
            Name = g.name ?? string.Empty,
            NameLocalized = g.name ?? string.Empty,
            PlaytimeForever = g.playtime_forever,
            Playtime2Weeks = g.playtime_2weeks,
            RtimeLastPlayed = (int)g.rtime_last_played,
            ImgIconUrl = g.img_icon_url ?? string.Empty,
            HasCommunityVisibleStats = g.has_community_visible_stats,
            ContentDescriptorIds = g.content_descriptorids?.Select(id => (int)id).ToList() ?? [],
            IsOwned = true,
            IsFamilyShared = false
        }).ToList();

        // 获取本地化名称（英文环境下无需再次请求）
        if (language != "english" && result.Count > 0)
        {
            try
            {
                var localizedRequest = new CPlayer_GetOwnedGames_Request
                {
                    steamid = steamId,
                    include_appinfo = true,
                    include_played_free_games = true,
                    include_free_sub = false,
                    skip_unvetted_apps = false,
                    language = language
                };

                var localizedResponse = await playerService.GetOwnedGames(localizedRequest);
                if (localizedResponse.Result == EResult.OK)
                {
                    var localizedNames = (localizedResponse.Body?.games ?? [])
                        .Where(g => !string.IsNullOrEmpty(g.name))
                        .ToDictionary(g => (int)g.appid, g => g.name!);
                    foreach (var game in result)
                    {
                        if (localizedNames.TryGetValue(game.AppId, out var localizedName))
                        {
                            game.NameLocalized = localizedName;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} fetch localized names failed: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// 拉取 Steam 家庭共享库中的游戏（排除已拥有的），并返回所有游戏（含自有游戏）的家庭拥有者映射。
    /// 组合 FamilyGroups.GetSharedLibraryApps 与 Player.ClientGetLastPlayedTimes 以获取游玩时间
    /// </summary>
    private static async Task<(List<SteamOwnedGame> SharedGames, Dictionary<uint, List<string>> OwnersMap)> FetchFamilySharedGamesAsync(
        FamilyGroups familyGroupsService,
        Player playerService,
        ulong steamId,
        HashSet<uint> ownedAppIds,
        string language)
    {
        try
        {
            // 先查询当前用户所属的家庭组
            var groupResponse = await familyGroupsService.GetFamilyGroupForUser(new CFamilyGroups_GetFamilyGroupForUser_Request
            {
                steamid = steamId,
                include_family_group_response = false
            });
            if (groupResponse.Result != EResult.OK)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} GetFamilyGroupForUser failed with {groupResponse.Result}");
                return ([], []);
            }

            var familyGroupId = groupResponse.Body?.family_groupid ?? 0;
            if (familyGroupId == 0)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} user is not a member of any family group");
                return ([], []);
            }

            // 调用 GetSharedLibraryApps 获取家庭库 App 列表（include_own=true 以便得到自有游戏的拥有者信息）
            var sharedRequest = new CFamilyGroups_GetSharedLibraryApps_Request
            {
                family_groupid = familyGroupId,
                include_own = true,
                include_excluded = false,
                max_apps = 10000,
                steamid = steamId,
                language = language
            };

            var sharedResponse = await familyGroupsService.GetSharedLibraryApps(sharedRequest);
            if (sharedResponse.Result != EResult.OK)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} GetSharedLibraryApps failed with {sharedResponse.Result}");
                return ([], []);
            }

            var apps = sharedResponse.Body?.apps ?? [];
            if (apps.Count == 0)
            {
                return ([], []);
            }

            // 所有游戏的家庭拥有者映射
            var ownersMap = apps
                .Where(a => a.owner_steamids is { Count: > 0 })
                .ToDictionary(
                    a => a.appid,
                    a => a.owner_steamids.Select(id => id.ToString()).ToList());

            // 查询本账号各游戏的游玩时长（包含家庭共享游戏的游玩时长）
            var playtimeMap = await FetchLastPlayedTimesAsync(playerService);

            var sharedGames = apps
                .Where(a => !ownedAppIds.Contains(a.appid))
                .Select(a =>
                {
                    var appId = (int)a.appid;
                    var (pForever, p2Weeks, rLastPlayed) = playtimeMap.GetValueOrDefault(a.appid, (0, 0, (int)a.rt_last_played));
                    return new SteamOwnedGame
                    {
                        AppId = appId,
                        Name = a.name ?? string.Empty,
                        NameLocalized = a.name ?? string.Empty,
                        PlaytimeForever = pForever,
                        Playtime2Weeks = p2Weeks,
                        RtimeLastPlayed = rLastPlayed,
                        ImgIconUrl = a.img_icon_hash ?? string.Empty,
                        HasCommunityVisibleStats = false,
                        ContentDescriptorIds = a.content_descriptors?.Select(id => (int)id).ToList() ?? [],
                        IsOwned = false,
                        IsFamilyShared = true,
                        OwnerSteamIds = a.owner_steamids?.Select(id => id.ToString()).ToList() ?? []
                    };
                })
                .ToList();

            return (sharedGames, ownersMap);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} FetchFamilySharedGamesAsync failed: {ex.Message}");
            return ([], []);
        }
    }

    /// <summary>
    /// 拉取当前登录账号各游戏的游玩时长，返回 appId -> (playtime_forever, playtime_2weeks, rtime_last_played) 的映射
    /// 该接口会包含家庭共享游戏的游玩时长
    /// </summary>
    private static async Task<Dictionary<uint, (int PlaytimeForever, int Playtime2Weeks, int RtimeLastPlayed)>> FetchLastPlayedTimesAsync(
        Player playerService)
    {
        try
        {
            var lastPlayedRequest = new CPlayer_GetLastPlayedTimes_Request
            {
                min_last_played = 0 // 0 代表不限制
            };

            var lastPlayedResponse = await playerService.ClientGetLastPlayedTimes(lastPlayedRequest);
            if (lastPlayedResponse.Result != EResult.OK)
            {
                return [];
            }

            var games = lastPlayedResponse.Body?.games ?? [];
            return games.ToDictionary(
                g => (uint)g.appid,
                g => (g.playtime_forever, g.playtime_2weeks, (int)g.last_playtime));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} FetchLastPlayedTimesAsync failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 获取愿望单并应用到游戏列表：
    /// - 已在列表中的游戏标记 IsInWishlist
    /// - 愿望单中未拥有的游戏追加为新条目（名称从本地缓存 / Store API 解析）
    /// </summary>
    private static async Task ApplyWishlistAsync(List<SteamOwnedGame> games, ulong steamId, IDbContextFactory<AppDbContext> dbContextFactory)
    {
        try
        {
            var wishlistAppIds = await FetchWishlistAppIdsAsync(steamId);
            if (wishlistAppIds.Count == 0)
            {
                return;
            }

            var gamesByAppId = games.ToDictionary(g => g.AppId);
            var missingAppIds = new List<int>();

            foreach (var appId in wishlistAppIds)
            {
                if (gamesByAppId.TryGetValue(appId, out var game))
                {
                    game.IsInWishlist = true;
                }
                else
                {
                    missingAppIds.Add(appId);
                }
            }

            // 愿望单中未拥有的游戏：解析名称后追加为新条目
            foreach (var appId in missingAppIds)
            {
                var name = await SteamAppService.GetAppNameByAppIdAsync((uint)appId, dbContextFactory) ?? string.Empty;
                games.Add(new SteamOwnedGame
                {
                    AppId = appId,
                    Name = name,
                    NameLocalized = name,
                    IsOwned = false,
                    IsFamilyShared = false,
                    IsInWishlist = true
                });
            }

            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} wishlist applied: {wishlistAppIds.Count} items ({missingAppIds.Count} not owned)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} ApplyWishlistAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 通过公开 Web API 获取愿望单中的 AppID 列表（个人资料需公开）
    /// </summary>
    private static async Task<List<int>> FetchWishlistAppIdsAsync(ulong steamId)
    {
        try
        {
            var url = $"https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid={steamId}";
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} GetWishlist failed with HTTP {(int)response.StatusCode}");
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("response", out var responseElement)
                || !responseElement.TryGetProperty("items", out var itemsElement)
                || itemsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return itemsElement.EnumerateArray()
                .Where(item => item.TryGetProperty("appid", out _))
                .Select(item => item.GetProperty("appid").GetInt32())
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} FetchWishlistAppIdsAsync failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 批量获取成就进度并应用到游戏列表（分块请求，每块 100 个 AppID）
    /// </summary>
    private static async Task ApplyAchievementsProgressAsync(Player playerService, List<SteamOwnedGame> games, ulong steamId, string language)
    {
        try
        {
            var appIds = games.Where(g => g.IsOwned || g.IsFamilyShared).Select(g => (uint)g.AppId).ToList();
            if (appIds.Count == 0)
            {
                return;
            }

            var gamesByAppId = games.ToDictionary(g => g.AppId);

            foreach (var chunk in appIds.Chunk(100))
            {
                var request = new CPlayer_GetAchievementsProgress_Request
                {
                    steamid = steamId,
                    language = language,
                    include_unvetted_apps = true
                };
                request.appids.AddRange(chunk);

                var response = await playerService.GetAchievementsProgress(request);
                if (response.Result != EResult.OK)
                {
                    Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} GetAchievementsProgress failed with {response.Result}");
                    continue;
                }

                foreach (var progress in response.Body?.achievement_progress ?? [])
                {
                    if (progress.total == 0 || !gamesByAppId.TryGetValue((int)progress.appid, out var game))
                    {
                        continue;
                    }

                    game.AchievementTotal = (int)progress.total;
                    game.AchievementUnlocked = (int)progress.unlocked;
                    game.AchievementPercentage = progress.percentage;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} ApplyAchievementsProgressAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 将游戏的家庭拥有者 SteamID 解析为昵称（家庭成员通常互为好友，可直接从 SteamFriends 缓存取名）
    /// </summary>
    private static void ResolveOwnerNames(SteamClient client, List<SteamOwnedGame> games)
    {
        try
        {
            var steamFriends = client.GetHandler<SteamFriends>();
            if (steamFriends == null)
            {
                return;
            }

            var currentUserSteamId = client.SteamID?.ConvertToUInt64().ToString();
            var nameCache = new Dictionary<string, string>();

            foreach (var game in games.Where(g => g.OwnerSteamIds.Count > 0))
            {
                game.OwnerNames = game.OwnerSteamIds.Select(ownerId =>
                {
                    if (nameCache.TryGetValue(ownerId, out var cached))
                    {
                        return cached;
                    }

                    string name;
                    if (ownerId == currentUserSteamId)
                    {
                        name = steamFriends.GetPersonaName() ?? ownerId;
                    }
                    else
                    {
                        var personaName = steamFriends.GetFriendPersonaName(new SteamID(ulong.Parse(ownerId)));
                        name = string.IsNullOrEmpty(personaName) || personaName == "[unknown]" ? ownerId : personaName;
                    }

                    nameCache[ownerId] = name;
                    return name;
                }).ToList();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LIBRARY} ResolveOwnerNames failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取所有已登录用户的游戏库（map accountName -> games）
    /// </summary>
    public static async Task<Dictionary<string, List<SteamOwnedGame>>> GetLibraryForAllUsersAsync(IDbContextFactory<AppDbContext> dbContextFactory, bool includeFamilyShared = true)
    {
        var loggedInUsers = SteamLoginService.GetLoggedInUsers();
        var tasks = loggedInUsers.Select(async user => (user, await GetLibraryForUserAsync(user, dbContextFactory, includeFamilyShared))).ToList();
        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.user, r => r.Item2);
    }

    /// <summary>
    /// 同步指定用户的游戏库（即强制重新拉取）；与 Get 同义，方便 IPC 命名
    /// </summary>
    public static async Task<bool> SyncLibraryForUserAsync(string accountName, IDbContextFactory<AppDbContext> dbContextFactory, bool includeFamilyShared = true)
    {
        var games = await GetLibraryForUserAsync(accountName, dbContextFactory, includeFamilyShared);
        return games.Count > 0;
    }

    /// <summary>
    /// 同步所有已登录用户的游戏库，返回每个用户的同步结果
    /// </summary>
    public static async Task<Dictionary<string, bool>> SyncLibraryForAllUsersAsync(IDbContextFactory<AppDbContext> dbContextFactory, bool includeFamilyShared = true)
    {
        var loggedInUsers = SteamLoginService.GetLoggedInUsers();
        var tasks = loggedInUsers.Select(async user => (user, await SyncLibraryForUserAsync(user, dbContextFactory, includeFamilyShared))).ToList();
        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.user, r => r.Item2);
    }

    /// <summary>
    /// 清理指定账号的游戏库缓存（登出时调用）
    /// </summary>
    public static void ClearLibraryForAccount(string accountName)
    {
        _userLibraryCache.Remove(accountName);
    }
}

/// <summary>
/// Steam 拥有的游戏信息
/// </summary>
public class SteamOwnedGame
{
    public int AppId { get; set; }
    /// <summary>
    /// 英文名称
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// 本地化名称（无本地化时与 Name 相同）
    /// </summary>
    public string NameLocalized { get; set; } = string.Empty;
    /// <summary>
    /// 总游玩时长（分钟）
    /// </summary>
    public int PlaytimeForever { get; set; }
    /// <summary>
    /// 最近两周游玩时长（分钟）
    /// </summary>
    public int Playtime2Weeks { get; set; }
    /// <summary>
    /// 最后游玩时间（Unix 秒）
    /// </summary>
    public int RtimeLastPlayed { get; set; }
    public string ImgIconUrl { get; set; } = string.Empty;
    public bool HasCommunityVisibleStats { get; set; }
    public List<int> ContentDescriptorIds { get; set; } = [];
    /// <summary>
    /// 是否被本账号直接拥有（在本账号的库中）
    /// </summary>
    public bool IsOwned { get; set; }
    /// <summary>
    /// 是否来自 Steam 家庭共享库（而非本账号直接拥有）
    /// </summary>
    public bool IsFamilyShared { get; set; }
    /// <summary>
    /// 是否在本账号的愿望单中
    /// </summary>
    public bool IsInWishlist { get; set; }
    /// <summary>
    /// 家庭中拥有此游戏的成员 SteamID（字符串形式，可能多个）
    /// </summary>
    public List<string> OwnerSteamIds { get; set; } = [];
    /// <summary>
    /// 家庭中拥有此游戏的成员昵称（与 OwnerSteamIds 一一对应）
    /// </summary>
    public List<string> OwnerNames { get; set; } = [];
    /// <summary>
    /// 成就总数（0 表示无成就或未获取到）
    /// </summary>
    public int AchievementTotal { get; set; }
    /// <summary>
    /// 已解锁成就数
    /// </summary>
    public int AchievementUnlocked { get; set; }
    /// <summary>
    /// 成就完成百分比（0-100）
    /// </summary>
    public double AchievementPercentage { get; set; }
}
