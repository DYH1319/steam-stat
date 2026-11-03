using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;
using SteamKit2.WebUI.Internal;
using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Sessions;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal interface ISteamProfileSource
{
    Task<SteamGatewayResult<SteamProfileSnapshot>> GetAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken);

    Task<SteamGatewayResult<SteamFavoriteBadgeSnapshot>> GetFavoriteBadgeAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken);

    Task<SteamGatewayResult<SteamBadgeNameSnapshot>> GetBadgeNameAsync(
        string accountName,
        uint appId,
        string language,
        CancellationToken cancellationToken);
}

internal sealed class CmProfileSource(
    ISteamSessionAccessor sessionAccessor,
    ISteamCmOperationScheduler scheduler,
    SteamStat.Core.Steam.Session.SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<CmProfileSource> logger) : ISteamProfileSource
{
    public async Task<SteamGatewayResult<SteamProfileSnapshot>> GetAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<SteamProfileSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired, "profile_session_unavailable");
        var client = session.Client;
        var friends = client.GetHandler<SteamFriends>();
        if (friends == null)
            return SteamGatewayResult<SteamProfileSnapshot>.Failed(
                SteamFailureKind.Protocol, "profile_persona_unavailable");
        var id = new SteamID(steamId);
        var currentUser = client.SteamID?.ConvertToUInt64() == steamId;
        var personaName = currentUser ? friends.GetPersonaName() : friends.GetFriendPersonaName(id);
        var avatarHash = GetAvatarHash(friends, id);
        var level = await TryGetLevelAsync(accountName, session, id.AccountID, cancellationToken)
            .ConfigureAwait(false);
        var fetchedAt = timeProvider.GetUtcNow();
        return SteamGatewayResult<SteamProfileSnapshot>.Succeeded(
            new SteamProfileSnapshot(steamId, NormalizePersonaName(personaName), level, avatarHash),
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            fetchedAt,
            fetchedAt + TimeSpan.FromHours(6));
    }

    public async Task<SteamGatewayResult<SteamFavoriteBadgeSnapshot>> GetFavoriteBadgeAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<SteamFavoriteBadgeSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired, "profile_session_unavailable");
        var unifiedMessages = session.Client.GetHandler<SteamUnifiedMessages>();
        if (unifiedMessages == null)
            return SteamGatewayResult<SteamFavoriteBadgeSnapshot>.Failed(
                SteamFailureKind.Protocol, "profile_handler_unavailable");
        try
        {
            var player = unifiedMessages.CreateService<Player>();
            var response = await scheduler.RunAsync(
                accountName,
                "profile-favorite-badge",
                session.Generation,
                async _ => await player.GetFavoriteBadge(new CPlayer_GetFavoriteBadge_Request { steamid = steamId }),
                cancellationToken).ConfigureAwait(false);
            if (response.Result != EResult.OK)
                return SteamGatewayResult<SteamFavoriteBadgeSnapshot>.Failed(classifier.Classify(response.Result));
            var body = response.Body;
            var fetchedAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<SteamFavoriteBadgeSnapshot>.Succeeded(
                new SteamFavoriteBadgeSnapshot(
                    body?.has_favorite_badge == true,
                    body?.badgeid ?? 0,
                    body?.level ?? 0,
                    body?.appid ?? 0,
                    body?.communityitemid ?? 0,
                    body?.border_color ?? 0),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                fetchedAt,
                fetchedAt + TimeSpan.FromHours(6));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to fetch Steam favorite badge for generation {SessionGeneration} with {ExceptionType}",
                session.Generation, exception.GetType().Name);
            return SteamGatewayResult<SteamFavoriteBadgeSnapshot>.Failed(classifier.Classify(exception, cancellationToken));
        }
    }

    /// <summary>ECommunityItemClass.Badge</summary>
    private const int CommunityItemClassBadge = 1;

    public async Task<SteamGatewayResult<SteamBadgeNameSnapshot>> GetBadgeNameAsync(
        string accountName,
        uint appId,
        string language,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<SteamBadgeNameSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired, "profile_session_unavailable");
        var unifiedMessages = session.Client.GetHandler<SteamUnifiedMessages>();
        if (unifiedMessages == null)
            return SteamGatewayResult<SteamBadgeNameSnapshot>.Failed(
                SteamFailureKind.Protocol, "profile_handler_unavailable");

        string? badgeName = null;
        string? appName = null;
        try
        {
            // 活动 / 特卖徽章在积分商店有定义，item_title 即徽章名（按 language 本地化）
            var loyalty = unifiedMessages.CreateService<LoyaltyRewards>();
            var request = new CLoyaltyRewards_QueryRewardItems_Request { language = language, count = 20 };
            request.appids.Add(appId);
            request.community_item_classes.Add(CommunityItemClassBadge);
            var response = await scheduler.RunAsync(
                accountName,
                "profile-badge-definition",
                session.Generation,
                async _ => await loyalty.QueryRewardItems(request),
                cancellationToken).ConfigureAwait(false);
            if (response.Result == EResult.OK)
            {
                var definition = response.Body?.definitions?
                    .FirstOrDefault(item => item.appid == appId && item.community_item_class == CommunityItemClassBadge);
                badgeName = NormalizeText(definition?.community_item_data?.item_title)
                            ?? NormalizeText(definition?.community_item_data?.item_name);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                "Failed to query Steam badge definition for generation {SessionGeneration} with {ExceptionType}",
                session.Generation, exception.GetType().Name);
        }

        try
        {
            // 社区应用信息：商店查不到的活动应用（如季节性特卖）也有名字
            var community = unifiedMessages.CreateService<Community>();
            var request = new CCommunity_GetApps_Request { language = ToELanguage(language) };
            request.appids.Add((int)appId);
            var response = await scheduler.RunAsync(
                accountName,
                "profile-badge-app",
                session.Generation,
                async _ => await community.GetApps(request),
                cancellationToken).ConfigureAwait(false);
            if (response.Result == EResult.OK)
                appName = NormalizeText(response.Body?.apps?.FirstOrDefault(app => app.appid == appId)?.name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                "Failed to query Steam community app for generation {SessionGeneration} with {ExceptionType}",
                session.Generation, exception.GetType().Name);
        }

        if (badgeName == null && appName == null)
            return SteamGatewayResult<SteamBadgeNameSnapshot>.Failed(SteamFailureKind.NotFound, "badge_name_unavailable");
        var fetchedAt = timeProvider.GetUtcNow();
        return SteamGatewayResult<SteamBadgeNameSnapshot>.Succeeded(
            new SteamBadgeNameSnapshot(appId, badgeName, appName),
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            fetchedAt,
            fetchedAt + TimeSpan.FromDays(30));
    }

    /// <summary>Steam 语言代码 → ELanguage（Community.GetApps 只接受枚举值）。</summary>
    internal static uint ToELanguage(string language) => language switch
    {
        "schinese" => 6,
        "tchinese" => 7,
        _ => 0
    };

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<int?> TryGetLevelAsync(
        string accountName,
        ISteamSession session,
        uint accountId,
        CancellationToken cancellationToken)
    {
        var levels = session.Client.GetHandler<SteamLevelsHandler>();
        if (levels == null) return null;
        try
        {
            return await scheduler.RunAsync(
                accountName,
                "profile-level",
                session.Generation,
                async stoppingToken =>
                {
                    var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var subscription = session.Callbacks.Subscribe<FriendsSteamLevelsCallback>(callback =>
                    {
                        if (callback.Levels.TryGetValue(accountId, out var value)) completion.TrySetResult(value);
                    });
                    levels.RequestFriendLevels([accountId]);
                    return await completion.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to fetch Steam profile level for generation {SessionGeneration} with {ExceptionType}",
                session.Generation, exception.GetType().Name);
            return null;
        }
    }

    private static string? GetAvatarHash(SteamFriends friends, SteamID id)
    {
        try
        {
            var hash = friends.GetFriendAvatar(id);
            return hash is { Length: > 0 }
                ? Convert.ToHexStringLower(hash)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizePersonaName(string? value)
        => string.IsNullOrWhiteSpace(value) || value == "[unknown]" ? null : value;
}
