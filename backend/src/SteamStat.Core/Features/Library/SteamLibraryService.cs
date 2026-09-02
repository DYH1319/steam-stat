using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;
using SteamStat.Core.Events;
using SteamStat.Core.Http;
using SteamStat.Core.Sessions;

namespace SteamStat.Core.Features.Library;

public sealed class SteamLibraryService(
    ISteamSessionAccessor sessionAccessor,
    IAppNameResolver appNameResolver,
    IAppMetadataWriter appMetadataWriter,
    ILanguageProvider languageProvider,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<SteamLibraryService> logger) : IEventHandler<SteamSessionEnded>, IDisposable
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<SteamOwnedGame>> _userLibraryCache = new();
    private int _disposed;
    private HttpClient HttpClient => httpClientFactory.CreateClient(SteamStatHttpClients.SteamApi);

    public async Task<List<SteamOwnedGame>> GetLibraryForUserAsync(string accountName, bool includeFamilyShared = true)
    {
        var refreshStartedAt = timeProvider.GetUtcNow();
        try
        {
            if (!sessionAccessor.TryGetSession(accountName, out var session))
            {
                logger.LogWarning("Steam user {AccountName} is not logged in", accountName);
                return [];
            }
            var client = session.Client;
            var unifiedMessages = client.GetHandler<SteamUnifiedMessages>();
            if (unifiedMessages == null)
            {
                logger.LogWarning("Steam UnifiedMessages handler is unavailable for {AccountName}", accountName);
                return [];
            }
            var steamId = client.SteamID;
            if (steamId == null)
            {
                logger.LogWarning("Steam client for {AccountName} has no SteamID", accountName);
                return [];
            }
            var playerService = unifiedMessages.CreateService<Player>();
            var steamIdValue = steamId.ConvertToUInt64();
            var language = languageProvider.GetSteamLanguage();
            var ownedGames = await FetchOwnedGamesAsync(playerService, steamIdValue, language);
            List<SteamOwnedGame> familySharedGames = [];
            Dictionary<uint, List<string>> familyOwnersMap = [];
            if (includeFamilyShared)
            {
                var familyGroups = unifiedMessages.CreateService<FamilyGroups>();
                (familySharedGames, familyOwnersMap) = await FetchFamilySharedGamesAsync(
                    familyGroups, playerService, steamIdValue,
                    ownedGames.Select(game => (uint)game.AppId).ToHashSet(), language);
            }
            foreach (var game in ownedGames)
                if (familyOwnersMap.TryGetValue((uint)game.AppId, out var owners)) game.OwnerSteamIds = owners;
            var merged = ownedGames.Concat(familySharedGames).OrderByDescending(game => game.PlaytimeForever).ToList();
            await ApplyWishlistAsync(merged, steamIdValue);
            await ApplyAchievementsProgressAsync(playerService, merged, steamIdValue, language);
            ResolveOwnerNames(client, merged);
            _userLibraryCache[accountName] = CloneGames(merged);
            await appMetadataWriter.EnsureCachedAsync(merged.Where(game => !string.IsNullOrEmpty(game.Name))
                .Select(game => new AppMetadata((uint)game.AppId, game.Name)));
            logger.LogInformation("Got {OwnedCount} owned and {SharedCount} family-shared games for {AccountName}",
                ownedGames.Count, familySharedGames.Count, accountName);
            logger.LogDebug("Refreshed Steam library for {AccountName} in {Elapsed}",
                accountName, timeProvider.GetUtcNow() - refreshStartedAt);
            return CloneGames(merged).ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to get Steam library for {AccountName}", accountName);
            return [];
        }
    }

    private async Task<List<SteamOwnedGame>> FetchOwnedGamesAsync(Player playerService, ulong steamId, string language)
    {
        var response = await playerService.GetOwnedGames(new CPlayer_GetOwnedGames_Request
        {
            steamid = steamId,
            include_appinfo = true,
            include_played_free_games = true,
            include_free_sub = false,
            skip_unvetted_apps = false
        });
        if (response.Result != EResult.OK)
        {
            logger.LogWarning("Steam GetOwnedGames failed: {Result}", response.Result);
            return [];
        }
        var result = (response.Body?.games ?? []).Select(game => new SteamOwnedGame
        {
            AppId = (int)game.appid,
            Name = game.name ?? string.Empty,
            NameLocalized = game.name ?? string.Empty,
            PlaytimeForever = game.playtime_forever,
            Playtime2Weeks = game.playtime_2weeks,
            RtimeLastPlayed = (int)game.rtime_last_played,
            ImgIconUrl = game.img_icon_url ?? string.Empty,
            HasCommunityVisibleStats = game.has_community_visible_stats,
            ContentDescriptorIds = game.content_descriptorids?.Select(id => (int)id).ToList() ?? [],
            IsOwned = true
        }).ToList();
        if (language != "english" && result.Count > 0)
        {
            try
            {
                var localized = await playerService.GetOwnedGames(new CPlayer_GetOwnedGames_Request
                {
                    steamid = steamId,
                    include_appinfo = true,
                    include_played_free_games = true,
                    include_free_sub = false,
                    skip_unvetted_apps = false,
                    language = language
                });
                if (localized.Result == EResult.OK)
                {
                    var names = (localized.Body?.games ?? []).Where(game => !string.IsNullOrEmpty(game.name))
                        .ToDictionary(game => (int)game.appid, game => game.name!);
                    foreach (var game in result)
                        if (names.TryGetValue(game.AppId, out var name)) game.NameLocalized = name;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to fetch localized Steam game names");
            }
        }
        return result;
    }

    private async Task<(List<SteamOwnedGame> SharedGames, Dictionary<uint, List<string>> OwnersMap)>
        FetchFamilySharedGamesAsync(FamilyGroups familyGroups, Player playerService, ulong steamId,
            HashSet<uint> ownedAppIds, string language)
    {
        try
        {
            var group = await familyGroups.GetFamilyGroupForUser(new CFamilyGroups_GetFamilyGroupForUser_Request
            {
                steamid = steamId,
                include_family_group_response = false
            });
            if (group.Result != EResult.OK)
            {
                logger.LogWarning("Steam GetFamilyGroupForUser failed: {Result}", group.Result);
                return ([], []);
            }
            var groupId = group.Body?.family_groupid ?? 0;
            if (groupId == 0)
            {
                logger.LogDebug("Steam user {SteamId} has no family group", steamId);
                return ([], []);
            }
            var shared = await familyGroups.GetSharedLibraryApps(new CFamilyGroups_GetSharedLibraryApps_Request
            {
                family_groupid = groupId,
                include_own = true,
                include_excluded = false,
                max_apps = 10000,
                steamid = steamId,
                language = language
            });
            if (shared.Result != EResult.OK)
            {
                logger.LogWarning("Steam GetSharedLibraryApps failed: {Result}", shared.Result);
                return ([], []);
            }
            var apps = shared.Body?.apps ?? [];
            if (apps.Count == 0) return ([], []);
            var owners = apps.Where(app => app.owner_steamids is { Count: > 0 }).ToDictionary(
                app => app.appid, app => app.owner_steamids.Select(id => id.ToString()).ToList());
            var playtimes = await FetchLastPlayedTimesAsync(playerService);
            var games = apps.Where(app => !ownedAppIds.Contains(app.appid)).Select(app =>
            {
                var (forever, twoWeeks, lastPlayed) = playtimes.GetValueOrDefault(
                    app.appid, (0, 0, (int)app.rt_last_played));
                return new SteamOwnedGame
                {
                    AppId = (int)app.appid,
                    Name = app.name ?? string.Empty,
                    NameLocalized = app.name ?? string.Empty,
                    PlaytimeForever = forever,
                    Playtime2Weeks = twoWeeks,
                    RtimeLastPlayed = lastPlayed,
                    ImgIconUrl = app.img_icon_hash ?? string.Empty,
                    ContentDescriptorIds = app.content_descriptors?.Select(id => (int)id).ToList() ?? [],
                    IsFamilyShared = true,
                    OwnerSteamIds = app.owner_steamids?.Select(id => id.ToString()).ToList() ?? []
                };
            }).ToList();
            return (games, owners);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to fetch Steam family-shared games");
            return ([], []);
        }
    }

    private async Task<Dictionary<uint, (int PlaytimeForever, int Playtime2Weeks, int RtimeLastPlayed)>>
        FetchLastPlayedTimesAsync(Player playerService)
    {
        try
        {
            var response = await playerService.ClientGetLastPlayedTimes(
                new CPlayer_GetLastPlayedTimes_Request { min_last_played = 0 });
            if (response.Result != EResult.OK) return [];
            return (response.Body?.games ?? []).ToDictionary(
                game => (uint)game.appid,
                game => (game.playtime_forever, game.playtime_2weeks, (int)game.last_playtime));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to fetch Steam last-played times");
            return [];
        }
    }

    private async Task ApplyWishlistAsync(List<SteamOwnedGame> games, ulong steamId)
    {
        try
        {
            var wishlist = await FetchWishlistAppIdsAsync(steamId);
            if (wishlist.Count == 0) return;
            var byId = games.ToDictionary(game => game.AppId);
            var missing = new List<int>();
            foreach (var appId in wishlist)
            {
                if (byId.TryGetValue(appId, out var game)) game.IsInWishlist = true;
                else missing.Add(appId);
            }
            foreach (var appId in missing)
            {
                var name = await appNameResolver.ResolveNameAsync((uint)appId) ?? string.Empty;
                games.Add(new SteamOwnedGame
                {
                    AppId = appId,
                    Name = name,
                    NameLocalized = name,
                    IsInWishlist = true
                });
            }
            logger.LogDebug("Applied {Count} Steam wishlist items ({MissingCount} not owned)", wishlist.Count, missing.Count);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to apply Steam wishlist");
        }
    }

    private async Task<List<int>> FetchWishlistAppIdsAsync(ulong steamId)
    {
        try
        {
            using var response = await HttpClient.GetAsync(
                $"https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid={steamId}");
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Steam GetWishlist failed with HTTP {StatusCode}", (int)response.StatusCode);
                return [];
            }
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            if (!document.RootElement.TryGetProperty("response", out var root)
                || !root.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array) return [];
            return items.EnumerateArray().Where(item => item.TryGetProperty("appid", out _))
                .Select(item => item.GetProperty("appid").GetInt32()).ToList();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to fetch Steam wishlist");
            return [];
        }
    }

    private async Task ApplyAchievementsProgressAsync(
        Player playerService, List<SteamOwnedGame> games, ulong steamId, string language)
    {
        try
        {
            var appIds = games.Where(game => game.IsOwned || game.IsFamilyShared).Select(game => (uint)game.AppId).ToList();
            if (appIds.Count == 0) return;
            var byId = games.ToDictionary(game => game.AppId);
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
                    logger.LogWarning("Steam GetAchievementsProgress failed: {Result}", response.Result);
                    continue;
                }
                foreach (var progress in response.Body?.achievement_progress ?? [])
                {
                    if (progress.total == 0 || !byId.TryGetValue((int)progress.appid, out var game)) continue;
                    game.AchievementTotal = (int)progress.total;
                    game.AchievementUnlocked = (int)progress.unlocked;
                    game.AchievementPercentage = progress.percentage;
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to apply Steam achievement progress");
        }
    }

    private void ResolveOwnerNames(SteamClient client, List<SteamOwnedGame> games)
    {
        try
        {
            var friends = client.GetHandler<SteamFriends>();
            if (friends == null) return;
            var currentId = client.SteamID?.ConvertToUInt64().ToString();
            var names = new Dictionary<string, string>();
            foreach (var game in games.Where(game => game.OwnerSteamIds.Count > 0))
            {
                game.OwnerNames = game.OwnerSteamIds.Select(ownerId =>
                {
                    if (names.TryGetValue(ownerId, out var cached)) return cached;
                    string name;
                    if (ownerId == currentId) name = friends.GetPersonaName() ?? ownerId;
                    else
                    {
                        var personaName = friends.GetFriendPersonaName(new SteamID(ulong.Parse(ownerId)));
                        name = string.IsNullOrEmpty(personaName) || personaName == "[unknown]" ? ownerId : personaName;
                    }
                    names[ownerId] = name;
                    return name;
                }).ToList();
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to resolve Steam family owner names");
        }
    }

    public async Task<Dictionary<string, List<SteamOwnedGame>>> GetLibraryForAllUsersAsync(bool includeFamilyShared = true)
    {
        var results = await Task.WhenAll(sessionAccessor.GetLoggedInUsers()
            .Select(async user => (user, Games: await GetLibraryForUserAsync(user, includeFamilyShared))));
        return results.ToDictionary(result => result.user, result => result.Games);
    }

    public async Task<bool> SyncLibraryForUserAsync(string accountName, bool includeFamilyShared = true)
        => (await GetLibraryForUserAsync(accountName, includeFamilyShared)).Count > 0;

    public async Task<Dictionary<string, bool>> SyncLibraryForAllUsersAsync(bool includeFamilyShared = true)
    {
        var results = await Task.WhenAll(sessionAccessor.GetLoggedInUsers()
            .Select(async user => (user, Result: await SyncLibraryForUserAsync(user, includeFamilyShared))));
        return results.ToDictionary(result => result.user, result => result.Result);
    }

    private static IReadOnlyList<SteamOwnedGame> CloneGames(IEnumerable<SteamOwnedGame> games)
        => games.Select(game => new SteamOwnedGame
        {
            AppId = game.AppId,
            Name = game.Name,
            NameLocalized = game.NameLocalized,
            PlaytimeForever = game.PlaytimeForever,
            Playtime2Weeks = game.Playtime2Weeks,
            RtimeLastPlayed = game.RtimeLastPlayed,
            ImgIconUrl = game.ImgIconUrl,
            HasCommunityVisibleStats = game.HasCommunityVisibleStats,
            ContentDescriptorIds = game.ContentDescriptorIds.ToList(),
            IsOwned = game.IsOwned,
            IsFamilyShared = game.IsFamilyShared,
            IsInWishlist = game.IsInWishlist,
            OwnerSteamIds = game.OwnerSteamIds.ToList(),
            OwnerNames = game.OwnerNames.ToList(),
            AchievementTotal = game.AchievementTotal,
            AchievementUnlocked = game.AchievementUnlocked,
            AchievementPercentage = game.AchievementPercentage
        }).ToArray();

    public void ClearLibraryForAccount(string accountName) => _userLibraryCache.TryRemove(accountName, out _);

    public Task HandleAsync(SteamSessionEnded message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearLibraryForAccount(message.AccountName);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _userLibraryCache.Clear();
    }
}

public sealed class SteamOwnedGame
{
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NameLocalized { get; set; } = string.Empty;
    public int PlaytimeForever { get; set; }
    public int Playtime2Weeks { get; set; }
    public int RtimeLastPlayed { get; set; }
    public string ImgIconUrl { get; set; } = string.Empty;
    public bool HasCommunityVisibleStats { get; set; }
    public List<int> ContentDescriptorIds { get; set; } = [];
    public bool IsOwned { get; set; }
    public bool IsFamilyShared { get; set; }
    public bool IsInWishlist { get; set; }
    public List<string> OwnerSteamIds { get; set; } = [];
    public List<string> OwnerNames { get; set; } = [];
    public int AchievementTotal { get; set; }
    public int AchievementUnlocked { get; set; }
    public double AchievementPercentage { get; set; }
}
