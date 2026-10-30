using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal sealed class CmFriendProfileSource(
    ISteamSessionAccessor sessionAccessor,
    ILanguageProvider languageProvider,
    ISteamCmOperationScheduler scheduler,
    SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<CmFriendProfileSource> logger) : ISteamFriendProfileGateway
{
    private const string SessionUnavailable = "friend_profile_session_unavailable";
    private const string HandlerUnavailable = "friend_profile_handler_unavailable";
    private const string LibraryPrivate = "friend_profile_library_private";

    public async Task<SteamGatewayResult<SteamFriendLibrarySnapshot>> GetLibraryAsync(
        string accountName,
        ulong friendSteamId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (friendSteamId == 0 || !sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<SteamFriendLibrarySnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired, SessionUnavailable);
        var unifiedMessages = session.Client.GetHandler<SteamUnifiedMessages>();
        if (unifiedMessages == null)
            return SteamGatewayResult<SteamFriendLibrarySnapshot>.Failed(SteamFailureKind.Protocol, HandlerUnavailable);
        try
        {
            var player = unifiedMessages.CreateService<Player>();
            var response = await scheduler.RunAsync(
                accountName,
                "friend-owned-games",
                session.Generation,
                async _ => await player.GetOwnedGames(new CPlayer_GetOwnedGames_Request
                {
                    steamid = friendSteamId,
                    include_appinfo = true,
                    include_played_free_games = true,
                    include_free_sub = false,
                    skip_unvetted_apps = false,
                    language = languageProvider.GetSteamLanguage()
                }),
                cancellationToken).ConfigureAwait(false);
            if (response.Result != EResult.OK)
                return SteamGatewayResult<SteamFriendLibrarySnapshot>.Failed(classifier.Classify(response.Result));
            var games = response.Body?.games ?? [];
            // 好友库设为私密时 Steam 返回 OK 但不含任何游戏
            if (games.Count == 0 && response.Body?.game_count == 0)
                return SteamGatewayResult<SteamFriendLibrarySnapshot>.Failed(SteamFailureKind.Forbidden, LibraryPrivate);
            var progress = await FetchProgressAsync(
                accountName,
                session.Generation,
                player,
                friendSteamId,
                games.Where(game => game.has_community_visible_stats).Select(game => (uint)game.appid).ToList(),
                cancellationToken).ConfigureAwait(false);
            var fetchedAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<SteamFriendLibrarySnapshot>.Succeeded(
                new SteamFriendLibrarySnapshot(
                    friendSteamId,
                    games.Select(game =>
                    {
                        progress.TryGetValue((uint)game.appid, out var summary);
                        return new SteamFriendOwnedGame(
                            (int)game.appid,
                            game.name ?? string.Empty,
                            game.playtime_forever,
                            game.playtime_2weeks,
                            (int)game.rtime_last_played,
                            game.has_community_visible_stats,
                            summary?.Total ?? 0,
                            summary?.Unlocked ?? 0,
                            summary?.Percentage ?? 0);
                    }).ToArray()),
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
            logger.LogWarning(exception, "Failed to fetch friend library for generation {SessionGeneration}", session.Generation);
            return SteamGatewayResult<SteamFriendLibrarySnapshot>.Failed(classifier.Classify(exception, cancellationToken));
        }
    }

    public async Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetAchievementUnlocksAsync(
        string accountName,
        ulong friendSteamId,
        uint appId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (appId == 0)
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.InvalidData, SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        if (friendSteamId == 0 || !sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired, SessionUnavailable);
        var handler = session.Client.GetHandler<AchievementUserStatsProtocolHandler>();
        if (handler == null)
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(SteamFailureKind.Protocol, HandlerUnavailable);
        try
        {
            var callback = await scheduler.RunAsync(
                accountName,
                "friend-achievement-unlocks",
                session.Generation,
                async _ => await handler.GetUserStats(appId, friendSteamId),
                cancellationToken).ConfigureAwait(false);
            if (callback.Result != EResult.OK)
                return callback.Result == EResult.Fail
                    ? SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                        SteamFailureKind.Forbidden, SteamAchievementDiagnosticCodes.UserStatsFailed)
                    : SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(classifier.Classify(callback.Result));
            var body = callback.Body;
            if (body == null || (body.game_id != 0 && body.game_id != appId))
                return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                    SteamFailureKind.InvalidData, SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
            var mapped = AchievementProtocolMapper.MapUserStats(body);
            var fetchedAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(friendSteamId, appId, session.Generation, mapped.Entries),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                fetchedAt,
                fetchedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.InvalidData, SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to fetch friend achievement unlocks for generation {SessionGeneration} with {ExceptionType}",
                session.Generation, exception.GetType().Name);
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(classifier.Classify(exception, cancellationToken));
        }
    }

    // 成就进度只是对比的附加信息：失败时返回空，不影响游戏库本身
    private async Task<Dictionary<uint, SteamAchievementAppProgressSnapshot>> FetchProgressAsync(
        string accountName,
        long generation,
        Player player,
        ulong friendSteamId,
        IReadOnlyList<uint> appIds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CmAchievementProgressSource.FetchProgressForUserAsync(
                scheduler, player, accountName, generation, friendSteamId,
                languageProvider.GetSteamLanguage(), appIds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Skipped friend achievement progress");
            return [];
        }
    }
}
