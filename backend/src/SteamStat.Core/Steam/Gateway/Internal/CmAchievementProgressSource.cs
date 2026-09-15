using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal sealed record AchievementProgressSessionIdentity(ulong SteamId, long SessionGeneration);

internal sealed record AchievementProgressBatchSnapshot(
    ulong SteamId,
    long SessionGeneration,
    IReadOnlyList<uint> RequestedAppIds,
    IReadOnlyList<uint> CoveredAppIds,
    IReadOnlyList<SteamAchievementAppProgressSnapshot> Summaries);

internal interface IAchievementProgressSource
{
    bool TryGetIdentity(string accountName, out AchievementProgressSessionIdentity identity);
    Task<SteamGatewayResult<AchievementProgressBatchSnapshot>> GetSummariesAsync(
        string accountName,
        IReadOnlyList<uint> appIds,
        CancellationToken cancellationToken);
    Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetUnlocksAsync(
        string accountName,
        uint appId,
        CancellationToken cancellationToken);
}

internal sealed class CmAchievementProgressSource(
    ISteamSessionAccessor sessionAccessor,
    ILanguageProvider languageProvider,
    ISteamCmOperationScheduler scheduler,
    SteamResultClassifier classifier,
    TimeProvider timeProvider,
    ILogger<CmAchievementProgressSource> logger) : IAchievementProgressSource
{
    private const int ProgressChunkSize = 100;

    public bool TryGetIdentity(string accountName, out AchievementProgressSessionIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(accountName)
            || !sessionAccessor.TryGetSession(accountName, out var session)
            || session.Client?.SteamID is not { } steamId)
            return false;
        var steamId64 = steamId.ConvertToUInt64();
        if (steamId64 == 0) return false;
        identity = new AchievementProgressSessionIdentity(steamId64, session.Generation);
        return true;
    }

    public async Task<SteamGatewayResult<AchievementProgressBatchSnapshot>> GetSummariesAsync(
        string accountName,
        IReadOnlyList<uint> appIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(appIds);
        List<uint[]> chunks;
        try
        {
            chunks = CreateProgressChunks(appIds);
        }
        catch (InvalidDataException)
        {
            return SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                SteamFailureKind.InvalidData,
                SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        }
        if (!TryGetIdentity(accountName, out var identity))
            return SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired,
                SteamAchievementDiagnosticCodes.ProgressSessionUnavailable);
        var requestedAppIds = chunks.SelectMany(chunk => chunk).ToArray();
        if (chunks.Count == 0)
        {
            var emptyNow = timeProvider.GetUtcNow();
            return SteamGatewayResult<AchievementProgressBatchSnapshot>.Succeeded(
                new AchievementProgressBatchSnapshot(
                    identity.SteamId,
                    identity.SessionGeneration,
                    [],
                    [],
                    []),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                emptyNow,
                emptyNow);
        }
        if (!sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired,
                SteamAchievementDiagnosticCodes.ProgressSessionUnavailable);
        var unifiedMessages = session.Client.GetHandler<SteamUnifiedMessages>();
        if (unifiedMessages == null)
            return SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                SteamFailureKind.Protocol,
                SteamAchievementDiagnosticCodes.SchemaHandlerUnavailable);
        var player = unifiedMessages.CreateService<Player>();
        var language = languageProvider.GetSteamLanguage();
        var covered = new List<uint>();
        var summaries = new List<SteamAchievementAppProgressSnapshot>();
        var seenSummaryAppIds = new HashSet<uint>();
        SteamFailure? firstFailure = null;
        foreach (var chunk in chunks)
        {
            SteamFailure? batchFailure = null;
            try
            {
                var request = new CPlayer_GetAchievementsProgress_Request
                {
                    steamid = identity.SteamId,
                    language = language,
                    include_unvetted_apps = true
                };
                request.appids.AddRange(chunk);
                var response = await scheduler.RunAsync(
                    accountName,
                    "achievements-progress",
                    identity.SessionGeneration,
                    async _ => await player.GetAchievementsProgress(request),
                    cancellationToken).ConfigureAwait(false);
                if (!IsCurrent(accountName, identity))
                    return SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                        SteamFailureKind.Transient,
                        SteamAchievementDiagnosticCodes.ProgressStaleSessionGeneration);
                if (response.Result != EResult.OK)
                {
                    batchFailure = classifier.Classify(response.Result);
                }
                else if (response.Body == null)
                {
                    batchFailure = new SteamFailure(
                        SteamFailureKind.InvalidData,
                        SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
                }
                else
                {
                    try
                    {
                        var mapped = AchievementProtocolMapper.MapProgressSummaries(
                            response.Body.achievement_progress);
                        var chunkIds = new HashSet<uint>(chunk);
                        if (mapped.Any(summary => !chunkIds.Contains(summary.AppId))
                            || mapped.Any(summary => seenSummaryAppIds.Contains(summary.AppId)))
                            throw new InvalidDataException(
                                "Achievement progress response contains out-of-batch app ids.");
                        summaries.AddRange(mapped);
                        foreach (var summary in mapped) seenSummaryAppIds.Add(summary.AppId);
                        covered.AddRange(chunk);
                    }
                    catch (InvalidDataException)
                    {
                        batchFailure = new SteamFailure(
                            SteamFailureKind.InvalidData,
                            SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                batchFailure = classifier.Classify(exception, cancellationToken);
            }
            firstFailure ??= batchFailure;
        }
        if (!IsCurrent(accountName, identity))
            return SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                SteamFailureKind.Transient,
                SteamAchievementDiagnosticCodes.ProgressStaleSessionGeneration);
        if (covered.Count == 0)
            return SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                firstFailure?.Kind ?? SteamFailureKind.Unknown,
                firstFailure?.DiagnosticCode
                    ?? SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        var fetchedAt = timeProvider.GetUtcNow();
        return SteamGatewayResult<AchievementProgressBatchSnapshot>.Succeeded(
            new AchievementProgressBatchSnapshot(
                identity.SteamId,
                identity.SessionGeneration,
                requestedAppIds,
                covered,
                summaries),
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            fetchedAt,
            fetchedAt + SteamResourcePolicies.AchievementProgressSummary.RefreshInterval,
            firstFailure?.Kind,
            firstFailure == null
                ? null
                : SteamAchievementDiagnosticCodes.ProgressPartial);
    }

    public async Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetUnlocksAsync(
        string accountName,
        uint appId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (appId == 0)
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.InvalidData,
                SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        if (!TryGetIdentity(accountName, out var identity)
            || !sessionAccessor.TryGetSession(accountName, out var session))
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired,
                SteamAchievementDiagnosticCodes.ProgressSessionUnavailable);
        var handler = session.Client.GetHandler<AchievementUserStatsProtocolHandler>();
        if (handler == null)
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.Protocol,
                SteamAchievementDiagnosticCodes.SchemaHandlerUnavailable);
        try
        {
            var callback = await scheduler.RunAsync(
                accountName,
                "achievement-unlocks",
                identity.SessionGeneration,
                async _ => await handler.GetUserStats(appId, identity.SteamId),
                cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(accountName, identity))
                return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                    SteamFailureKind.Transient,
                    SteamAchievementDiagnosticCodes.ProgressStaleSessionGeneration);
            if (callback.Result != EResult.OK)
            {
                if (callback.Result == EResult.Fail)
                    return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                        SteamFailureKind.Unknown,
                        SteamAchievementDiagnosticCodes.UserStatsFailed);
                return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                    classifier.Classify(callback.Result));
            }
            var body = callback.Body;
            if (body == null || (body.game_id != 0 && body.game_id != appId))
                return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                    SteamFailureKind.InvalidData,
                    SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
            AchievementUserStatsMapResult mapped;
            try
            {
                mapped = AchievementProtocolMapper.MapUserStats(body);
            }
            catch (InvalidDataException)
            {
                return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                    SteamFailureKind.InvalidData,
                    SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
            }
            if (!IsCurrent(accountName, identity))
                return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                    SteamFailureKind.Transient,
                    SteamAchievementDiagnosticCodes.ProgressStaleSessionGeneration);
            var fetchedAt = timeProvider.GetUtcNow();
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(
                    identity.SteamId,
                    appId,
                    identity.SessionGeneration,
                    mapped.Entries),
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                fetchedAt,
                fetchedAt + SteamResourcePolicies.AchievementUnlocks.RefreshInterval,
                null,
                mapped.UnmatchedUnlockedCoordinates.Count > 0
                    ? SteamAchievementDiagnosticCodes.UnlocksUnmatched
                    : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to fetch achievement unlocks for generation {SessionGeneration} with {ExceptionType}",
                identity.SessionGeneration, exception.GetType().Name);
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                classifier.Classify(exception, cancellationToken));
        }
    }

    internal static List<uint[]> CreateProgressChunks(IReadOnlyList<uint> appIds)
    {
        var seen = new HashSet<uint>();
        var deduped = new List<uint>(appIds.Count);
        foreach (var appId in appIds)
        {
            if (appId == 0)
                throw new InvalidDataException("Achievement progress app id must not be zero.");
            if (seen.Add(appId)) deduped.Add(appId);
        }
        return deduped.Chunk(ProgressChunkSize).ToList();
    }

    private bool IsCurrent(string accountName, AchievementProgressSessionIdentity identity)
        => TryGetIdentity(accountName, out var current)
            && current.SteamId == identity.SteamId
            && current.SessionGeneration == identity.SessionGeneration;
}
