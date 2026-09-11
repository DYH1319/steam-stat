using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal interface ISteamLibrarySource
{
    Task<SteamGatewayResult<SteamLibrarySnapshot>> GetAsync(
        string accountName,
        bool includeFamilyShared,
        CancellationToken cancellationToken);
}

internal sealed class CmLibrarySource(
    ISteamSessionAccessor sessionAccessor,
    ILanguageProvider languageProvider,
    ISteamCmOperationScheduler scheduler,
    SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<CmLibrarySource> logger) : ISteamLibrarySource
{
    public async Task<SteamGatewayResult<SteamLibrarySnapshot>> GetAsync(
        string accountName,
        bool includeFamilyShared,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<SteamLibrarySnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired, "library_session_unavailable");
        var client = session.Client;
        var unifiedMessages = client.GetHandler<SteamUnifiedMessages>();
        var steamId = client.SteamID;
        if (unifiedMessages == null || steamId == null)
            return SteamGatewayResult<SteamLibrarySnapshot>.Failed(
                SteamFailureKind.Protocol, "library_handler_unavailable");
        try
        {
            var player = unifiedMessages.CreateService<Player>();
            var steamIdValue = steamId.ConvertToUInt64();
            var language = languageProvider.GetSteamLanguage();
            var ownedResult = await FetchOwnedGamesAsync(
                accountName, session.Generation, player, steamIdValue, language, cancellationToken)
                .ConfigureAwait(false);
            if (!ownedResult.IsSuccess || ownedResult.Value == null)
                return SteamGatewayResult<SteamLibrarySnapshot>.Failed(
                    ownedResult.Failure!.Value, ownedResult.DiagnosticCode!);
            var owned = ownedResult.Value;
            List<LibraryGameData> shared = [];
            Dictionary<uint, IReadOnlyList<string>> owners = [];
            if (includeFamilyShared)
            {
                (shared, owners) = await FetchFamilySharedGamesAsync(
                    accountName,
                    session.Generation,
                    unifiedMessages.CreateService<FamilyGroups>(),
                    player,
                    steamIdValue,
                    owned.Select(game => (uint)game.AppId).ToHashSet(),
                    language,
                    cancellationToken).ConfigureAwait(false);
            }
            foreach (var game in owned)
                if (owners.TryGetValue((uint)game.AppId, out var ownerIds))
                    game.OwnerSteamIds = ownerIds.ToList();
            await ApplyAchievementsProgressAsync(
                accountName,
                session.Generation,
                player,
                owned.Concat(shared).ToList(),
                steamIdValue,
                language,
                cancellationToken).ConfigureAwait(false);
            ResolveOwnerNames(client, owned.Concat(shared), steamIdValue);
            if (!sessionAccessor.TryGetSession(accountName, out var current)
                || current.Generation != session.Generation)
                return SteamGatewayResult<SteamLibrarySnapshot>.Failed(
                    SteamFailureKind.Transient, "stale_session_generation");
            var fetchedAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<SteamLibrarySnapshot>.Succeeded(
                new SteamLibrarySnapshot(
                    steamIdValue,
                    session.Generation,
                    owned.Select(ToSnapshot).ToArray(),
                    shared.Select(ToSnapshot).ToArray(),
                    owners),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                fetchedAt,
                fetchedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to fetch Steam library for generation {SessionGeneration}",
                session.Generation);
            return SteamGatewayResult<SteamLibrarySnapshot>.Failed(
                classifier.Classify(exception, cancellationToken));
        }
    }

    private async Task<SteamGatewayResult<List<LibraryGameData>>> FetchOwnedGamesAsync(
        string accountName,
        long generation,
        Player player,
        ulong steamId,
        string language,
        CancellationToken cancellationToken)
    {
        var response = await scheduler.RunAsync(
            accountName,
            "owned-games",
            generation,
            async _ => await player.GetOwnedGames(CreateOwnedGamesRequest(steamId)),
            cancellationToken).ConfigureAwait(false);
        if (response.Result != EResult.OK)
            return SteamGatewayResult<List<LibraryGameData>>.Failed(classifier.Classify(response.Result));
        var games = (response.Body?.games ?? []).Select(game => new LibraryGameData
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
        if (language != "english" && games.Count > 0)
        {
            try
            {
                var request = CreateOwnedGamesRequest(steamId);
                request.language = language;
                var localized = await scheduler.RunAsync(
                    accountName,
                    "owned-games-localized",
                    generation,
                    async _ => await player.GetOwnedGames(request),
                    cancellationToken).ConfigureAwait(false);
                if (localized.Result == EResult.OK)
                {
                    var names = (localized.Body?.games ?? [])
                        .Where(game => !string.IsNullOrEmpty(game.name))
                        .ToDictionary(game => (int)game.appid, game => game.name!);
                    foreach (var game in games)
                        if (names.TryGetValue(game.AppId, out var name)) game.NameLocalized = name;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to fetch localized Steam game names");
            }
        }
        var now = timeProvider.GetUtcNow();
        return SteamGatewayResult<List<LibraryGameData>>.Succeeded(
            games, SteamDataSource.Cm, SteamFreshness.Fresh, now, now);
    }

    private static CPlayer_GetOwnedGames_Request CreateOwnedGamesRequest(ulong steamId) => new()
    {
        steamid = steamId,
        include_appinfo = true,
        include_played_free_games = true,
        include_free_sub = false,
        skip_unvetted_apps = false
    };

    private async Task<(List<LibraryGameData>, Dictionary<uint, IReadOnlyList<string>>)> FetchFamilySharedGamesAsync(
        string accountName,
        long generation,
        FamilyGroups familyGroups,
        Player player,
        ulong steamId,
        HashSet<uint> ownedAppIds,
        string language,
        CancellationToken cancellationToken)
    {
        try
        {
            var group = await scheduler.RunAsync(
                accountName,
                "family-group",
                generation,
                async _ => await familyGroups.GetFamilyGroupForUser(new CFamilyGroups_GetFamilyGroupForUser_Request
                {
                    steamid = steamId,
                    include_family_group_response = false
                }),
                cancellationToken).ConfigureAwait(false);
            if (group.Result != EResult.OK || group.Body?.family_groupid is not { } groupId || groupId == 0)
                return ([], []);
            var shared = await scheduler.RunAsync(
                accountName,
                "shared-library-apps",
                generation,
                async _ => await familyGroups.GetSharedLibraryApps(new CFamilyGroups_GetSharedLibraryApps_Request
                {
                    family_groupid = groupId,
                    include_own = true,
                    include_excluded = false,
                    max_apps = 10000,
                    steamid = steamId,
                    language = language
                }),
                cancellationToken).ConfigureAwait(false);
            if (shared.Result != EResult.OK) return ([], []);
            var apps = shared.Body?.apps ?? [];
            var owners = apps.Where(app => app.owner_steamids is { Count: > 0 }).ToDictionary(
                app => app.appid,
                app => (IReadOnlyList<string>)app.owner_steamids.Select(id => id.ToString()).ToArray());
            var playtimes = await FetchLastPlayedTimesAsync(
                accountName, generation, player, cancellationToken).ConfigureAwait(false);
            var games = apps.Where(app => !ownedAppIds.Contains(app.appid)).Select(app =>
            {
                var (forever, twoWeeks, lastPlayed) = playtimes.GetValueOrDefault(
                    app.appid, (0, 0, (int)app.rt_last_played));
                return new LibraryGameData
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
            foreach (var game in games)
                if (owners.TryGetValue((uint)game.AppId, out var ownerIds))
                    game.OwnerSteamIds = ownerIds.ToList();
            return (games, owners);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to fetch Steam family-shared games");
            return ([], []);
        }
    }

    private async Task<Dictionary<uint, (int Forever, int TwoWeeks, int LastPlayed)>> FetchLastPlayedTimesAsync(
        string accountName,
        long generation,
        Player player,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await scheduler.RunAsync(
                accountName,
                "last-played-times",
                generation,
                async _ => await player.ClientGetLastPlayedTimes(
                    new CPlayer_GetLastPlayedTimes_Request { min_last_played = 0 }),
                cancellationToken).ConfigureAwait(false);
            return response.Result != EResult.OK
                ? []
                : (response.Body?.games ?? []).ToDictionary(
                    game => (uint)game.appid,
                    game => (game.playtime_forever, game.playtime_2weeks, (int)game.last_playtime));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to fetch Steam last-played times");
            return [];
        }
    }

    private async Task ApplyAchievementsProgressAsync(
        string accountName,
        long generation,
        Player player,
        List<LibraryGameData> games,
        ulong steamId,
        string language,
        CancellationToken cancellationToken)
    {
        try
        {
            var appIds = games.Select(game => (uint)game.AppId).ToList();
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
                var response = await scheduler.RunAsync(
                    accountName,
                    "achievements-progress",
                    generation,
                    async _ => await player.GetAchievementsProgress(request),
                    cancellationToken).ConfigureAwait(false);
                if (response.Result != EResult.OK) continue;
                foreach (var progress in response.Body?.achievement_progress ?? [])
                {
                    if (progress.total == 0 || !byId.TryGetValue((int)progress.appid, out var game)) continue;
                    game.AchievementTotal = (int)progress.total;
                    game.AchievementUnlocked = (int)progress.unlocked;
                    game.AchievementPercentage = progress.percentage;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to apply Steam achievement progress");
        }
    }

    private void ResolveOwnerNames(SteamClient client, IEnumerable<LibraryGameData> games, ulong currentSteamId)
    {
        try
        {
            var friends = client.GetHandler<SteamFriends>();
            if (friends == null) return;
            var names = new Dictionary<string, string>();
            foreach (var game in games.Where(game => game.OwnerSteamIds.Count > 0))
                game.OwnerNames = game.OwnerSteamIds.Select(ownerId =>
                {
                    if (names.TryGetValue(ownerId, out var cached)) return cached;
                    var value = ulong.TryParse(ownerId, out var id) && id == currentSteamId
                        ? friends.GetPersonaName()
                        : ulong.TryParse(ownerId, out id)
                            ? friends.GetFriendPersonaName(new SteamID(id))
                            : null;
                    var name = string.IsNullOrWhiteSpace(value) || value == "[unknown]" ? ownerId : value;
                    names[ownerId] = name;
                    return name;
                }).ToList();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to resolve Steam family owner names");
        }
    }

    private static SteamLibraryGameSnapshot ToSnapshot(LibraryGameData game) => new(
        game.AppId,
        game.Name,
        game.NameLocalized,
        game.PlaytimeForever,
        game.Playtime2Weeks,
        game.RtimeLastPlayed,
        game.ImgIconUrl,
        game.HasCommunityVisibleStats,
        game.ContentDescriptorIds,
        game.IsOwned,
        game.IsFamilyShared,
        game.OwnerSteamIds,
        game.OwnerNames,
        game.AchievementTotal,
        game.AchievementUnlocked,
        game.AchievementPercentage);

    private sealed class LibraryGameData
    {
        public int AppId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string NameLocalized { get; set; } = string.Empty;
        public int PlaytimeForever { get; init; }
        public int Playtime2Weeks { get; init; }
        public int RtimeLastPlayed { get; init; }
        public string ImgIconUrl { get; init; } = string.Empty;
        public bool HasCommunityVisibleStats { get; init; }
        public List<int> ContentDescriptorIds { get; init; } = [];
        public bool IsOwned { get; init; }
        public bool IsFamilyShared { get; init; }
        public List<string> OwnerSteamIds { get; set; } = [];
        public List<string> OwnerNames { get; set; } = [];
        public int AchievementTotal { get; set; }
        public int AchievementUnlocked { get; set; }
        public double AchievementPercentage { get; set; }
    }
}
