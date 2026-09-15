using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Steam.Gateway;

internal sealed record AchievementProgressSummaryCachePayload(
    ulong SteamId,
    IReadOnlyList<uint> CoveredAppIds,
    IReadOnlyList<SteamAchievementAppProgressSnapshot> Values);

internal sealed record AchievementUnlockCachePayload(
    ulong SteamId,
    uint AppId,
    IReadOnlyList<SteamAchievementUnlock> Entries);

internal sealed class SteamAchievementProgressGateway(
    ISteamResourceCacheStore cacheStore,
    IAchievementProgressSource source,
    SteamRequestCoalescer<SteamCacheKey> coalescer,
    TimeProvider timeProvider,
    ILogger<SteamAchievementProgressGateway> logger) : ISteamAchievementProgressGateway, IDisposable, IAsyncDisposable
{
    private const string PayloadFormat = "json-v1";
    private readonly SteamCachePolicy _summaryPolicy = SteamResourcePolicies.AchievementProgressSummary;
    private readonly SteamCachePolicy _unlockPolicy = SteamResourcePolicies.AchievementUnlocks;
    private readonly ILogger<SteamAchievementProgressGateway> _logger = logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundTasks = new();
    private int _disposed;

    public async Task<SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>> GetSummariesAsync(
        string accountName,
        IReadOnlyList<uint> appIds,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentNullException.ThrowIfNull(appIds);
        if (!source.TryGetIdentity(accountName, out var identity))
            return SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>.Failed(
                SteamFailureKind.AuthenticationRequired,
                SteamAchievementDiagnosticCodes.ProgressSessionUnavailable);
        var requested = DedupRequested(appIds);
        if (requested.Any(appId => appId == 0))
            return SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>.Failed(
                SteamFailureKind.InvalidData,
                SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        if (requested.Count == 0)
        {
            var emptyNow = timeProvider.GetUtcNow();
            return SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>.Succeeded(
                [],
                SteamDataSource.Memory,
                SteamFreshness.Fresh,
                emptyNow,
                emptyNow);
        }

        var key = SteamAchievementCacheKeys.ProgressSummary(identity.SteamId);
        var cached = await ReadSummaryCacheAsync(key, identity.SteamId, cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        SteamFreshness? freshness = cached == null
            ? null
            : _summaryPolicy.GetFreshness(cached.Entry, now);
        if (cached != null && !_summaryPolicy.ShouldRetain(cached.Entry, now))
        {
            cached = null;
            freshness = null;
        }

        if (cached != null && refreshMode == SteamRefreshMode.CacheOnly)
            return CoversAll(cached, requested)
                ? SummaryFromCache(cached, requested, freshness!.Value)
                : SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>.Failed(
                    SteamFailureKind.NotFound,
                    SteamAchievementDiagnosticCodes.ProgressCacheMiss);
        if (cached != null
            && freshness == SteamFreshness.Fresh
            && refreshMode == SteamRefreshMode.PreferCache
            && CoversAll(cached, requested))
            return SummaryFromCache(cached, requested, SteamFreshness.Fresh);
        if (refreshMode == SteamRefreshMode.CacheOnly)
            return SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>.Failed(
                SteamFailureKind.NotFound,
                SteamAchievementDiagnosticCodes.ProgressCacheMiss);

        return await RefreshSummariesAsync(
            key, accountName, identity, requested, cached, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetUnlocksAsync(
        string accountName,
        uint appId,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        if (appId == 0)
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.InvalidData,
                SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        if (!source.TryGetIdentity(accountName, out var identity))
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired,
                SteamAchievementDiagnosticCodes.ProgressSessionUnavailable);

        var key = SteamAchievementCacheKeys.Unlocks(identity.SteamId, appId);
        var cached = await ReadUnlockCacheAsync(key, identity.SteamId, appId, cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        SteamFreshness? freshness = cached == null
            ? null
            : _unlockPolicy.GetFreshness(cached.Entry, now);
        if (cached != null && !_unlockPolicy.ShouldRetain(cached.Entry, now))
        {
            cached = null;
            freshness = null;
        }

        if (cached != null && refreshMode == SteamRefreshMode.CacheOnly)
            return UnlockFromCache(cached, identity, appId, freshness!.Value);
        if (cached != null && freshness == SteamFreshness.Fresh && refreshMode == SteamRefreshMode.PreferCache)
            return UnlockFromCache(cached, identity, appId, SteamFreshness.Fresh);
        if (refreshMode == SteamRefreshMode.CacheOnly)
            return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.NotFound,
                SteamAchievementDiagnosticCodes.UnlocksCacheMiss);

        var result = await coalescer.RunAsync(
            key,
            token => TrackOperation(
                FetchAndCacheUnlocksAsync(key, accountName, appId, identity, token)),
            _lifetime.Token,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess
            && result.Value != null
            && result.Value.SessionGeneration != identity.SessionGeneration)
            return result with
            {
                Value = result.Value with { SessionGeneration = identity.SessionGeneration }
            };
        return result;
    }

    private async Task<SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>> RefreshSummariesAsync(
        SteamCacheKey key,
        string accountName,
        AchievementProgressSessionIdentity identity,
        List<uint> requested,
        CachedSummaries? initialCached,
        CancellationToken cancellationToken)
    {
        var fetches = new List<FetchAttempt>();
        var latestCache = initialCached;
        var pending = requested;
        for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
        {
            var pendingIds = pending;
            var fetch = await coalescer.RunAsync(
                key,
                token => TrackOperation(
                    FetchAndMergeSummariesAsync(key, accountName, identity, pendingIds, token)),
                _lifetime.Token,
                cancellationToken).ConfigureAwait(false);
            var owned = fetch.IsSuccess
                && fetch.Value != null
                && fetch.Value.RequestedAppIds.SequenceEqual(pendingIds);
            fetches.Add(new FetchAttempt(fetch, pendingIds, owned));
            latestCache = await ReadSummaryCacheAsync(key, identity.SteamId, cancellationToken)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            if (latestCache != null && !_summaryPolicy.ShouldRetain(latestCache.Entry, now))
                latestCache = null;
            var covered = latestCache == null
                ? new HashSet<uint>()
                : new HashSet<uint>(latestCache.CoveredAppIds);
            if (fetch.IsSuccess && fetch.Value != null)
                foreach (var appId in fetch.Value.CoveredAppIds) covered.Add(appId);
            pending = requested.Where(appId => !covered.Contains(appId)).ToList();
            if (!fetch.IsSuccess || owned) break;
        }

        var freshCovered = new HashSet<uint>();
        var freshByAppId = new Dictionary<uint, SteamAchievementAppProgressSnapshot>();
        SteamGatewayResult<AchievementProgressBatchSnapshot>? firstFailure = null;
        var ownedPartial = false;
        foreach (var attempt in fetches)
        {
            var fetch = attempt.Result;
            if (fetch.IsSuccess && fetch.Value != null)
            {
                foreach (var appId in fetch.Value.CoveredAppIds) freshCovered.Add(appId);
                foreach (var summary in fetch.Value.Summaries) freshByAppId[summary.AppId] = summary;
                if (attempt.Owned && fetch.Failure != null) ownedPartial = true;
            }
            if (fetch.Failure != null) firstFailure ??= fetch;
        }

        var nowFinal = timeProvider.GetUtcNow();
        var retainedByAppId = latestCache == null
            ? new Dictionary<uint, SteamAchievementAppProgressSnapshot>()
            : latestCache.Values.ToDictionary(summary => summary.AppId);
        var output = new List<SteamAchievementAppProgressSnapshot>(requested.Count);
        var usedRetained = false;
        foreach (var appId in requested)
        {
            if (freshCovered.Contains(appId))
            {
                if (freshByAppId.TryGetValue(appId, out var summary)) output.Add(summary);
            }
            else if (retainedByAppId.TryGetValue(appId, out var retained))
            {
                output.Add(retained);
                usedRetained = true;
            }
        }

        if (firstFailure != null && !fetches.Any(attempt => attempt.Result.IsSuccess))
        {
            if (latestCache != null && CoversAll(latestCache, requested))
            {
                var fallbackFreshness = _summaryPolicy.GetFreshness(latestCache.Entry, nowFinal);
                if (fallbackFreshness == SteamFreshness.Fresh) fallbackFreshness = SteamFreshness.Stale;
                if (fallbackFreshness != SteamFreshness.Expired || _summaryPolicy.AllowExpiredOnFailure)
                    return SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>.Succeeded(
                        output,
                        SteamDataSource.Sqlite,
                        fallbackFreshness,
                        latestCache.Entry.FetchedAt,
                        latestCache.Entry.RefreshAfter,
                        firstFailure.Failure,
                        firstFailure.DiagnosticCode);
            }
            return SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>.Failed(
                firstFailure.Failure ?? SteamFailureKind.Unknown,
                firstFailure.DiagnosticCode
                    ?? SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        }

        var freshnessResult = SteamFreshness.Fresh;
        if (usedRetained && latestCache != null)
        {
            freshnessResult = _summaryPolicy.GetFreshness(latestCache.Entry, nowFinal);
            if (freshnessResult == SteamFreshness.Fresh) freshnessResult = SteamFreshness.Stale;
        }
        var anyRequestedNotFresh = requested.Any(appId => !freshCovered.Contains(appId));
        var isPartial = anyRequestedNotFresh || ownedPartial;
        var fetchedAtFinal = fetches
            .Where(attempt => attempt.Result.IsSuccess)
            .Select(attempt => attempt.Result.FetchedAt)
            .LastOrDefault() ?? nowFinal;
        return SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>.Succeeded(
            output,
            usedRetained ? SteamDataSource.Sqlite : SteamDataSource.Cm,
            freshnessResult,
            fetchedAtFinal,
            latestCache?.Entry.RefreshAfter ?? fetchedAtFinal,
            isPartial ? firstFailure?.Failure : null,
            isPartial
                ? SteamAchievementDiagnosticCodes.ProgressPartial
                : null);
    }

    private async Task<SteamGatewayResult<AchievementProgressBatchSnapshot>> FetchAndMergeSummariesAsync(
        SteamCacheKey key,
        string accountName,
        AchievementProgressSessionIdentity identity,
        IReadOnlyList<uint> appIds,
        CancellationToken cancellationToken)
    {
        var fetch = await source.GetSummariesAsync(accountName, appIds, cancellationToken)
            .ConfigureAwait(false);
        if (!fetch.IsSuccess || fetch.Value == null)
            return fetch;
        var snapshot = fetch.Value;
        if (!IsValidBatchSnapshot(snapshot, identity, appIds))
            return SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                SteamFailureKind.InvalidData,
                SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        if (!source.TryGetIdentity(accountName, out var current)
            || current.SteamId != identity.SteamId
            || current.SessionGeneration != identity.SessionGeneration)
            return SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                SteamFailureKind.Transient,
                SteamAchievementDiagnosticCodes.ProgressStaleSessionGeneration);
        var cached = await ReadSummaryCacheAsync(key, identity.SteamId, cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (cached != null && !_summaryPolicy.ShouldRetain(cached.Entry, now)) cached = null;
        var coveredIds = cached == null
            ? new HashSet<uint>()
            : new HashSet<uint>(cached.CoveredAppIds);
        var valuesByAppId = cached == null
            ? new Dictionary<uint, SteamAchievementAppProgressSnapshot>()
            : cached.Values.ToDictionary(summary => summary.AppId);
        foreach (var appId in snapshot.CoveredAppIds)
        {
            coveredIds.Add(appId);
            valuesByAppId.Remove(appId);
        }
        foreach (var summary in snapshot.Summaries)
            if (coveredIds.Contains(summary.AppId)) valuesByAppId[summary.AppId] = summary;
        var payload = JsonSerializer.Serialize(new AchievementProgressSummaryCachePayload(
            identity.SteamId,
            coveredIds.OrderBy(appId => appId).ToArray(),
            valuesByAppId.Values.OrderBy(summary => summary.AppId).ToArray()));
        if (Encoding.UTF8.GetByteCount(payload) > _summaryPolicy.MaximumPayloadBytes)
            return SteamGatewayResult<AchievementProgressBatchSnapshot>.Failed(
                SteamFailureKind.InvalidData,
                SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
        var fetchedAt = fetch.FetchedAt ?? now;
        var entry = _summaryPolicy.CreateEntry(
            key,
            payload,
            SteamDataSource.Cm,
            fetchedAt,
            PayloadFormat);
        await TryUpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        return fetch;
    }

    private async Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> FetchAndCacheUnlocksAsync(
        SteamCacheKey key,
        string accountName,
        uint appId,
        AchievementProgressSessionIdentity identity,
        CancellationToken cancellationToken)
    {
        var fetch = await source.GetUnlocksAsync(accountName, appId, cancellationToken)
            .ConfigureAwait(false);
        if (!fetch.IsSuccess || fetch.Value == null)
            return await HandleUnlockFailureAsync(
                key,
                identity,
                appId,
                fetch.Failure ?? SteamFailureKind.InvalidData,
                fetch.DiagnosticCode ?? SteamAchievementDiagnosticCodes.ProgressInvalidPayload,
                cancellationToken).ConfigureAwait(false);
        var snapshot = fetch.Value;
        if (snapshot.SteamId != identity.SteamId
            || snapshot.AppId != appId
            || snapshot.SessionGeneration != identity.SessionGeneration
            || !source.TryGetIdentity(accountName, out var current)
            || current.SteamId != identity.SteamId
            || current.SessionGeneration != identity.SessionGeneration)
            return await HandleUnlockFailureAsync(
                key,
                identity,
                appId,
                SteamFailureKind.Transient,
                SteamAchievementDiagnosticCodes.ProgressStaleSessionGeneration,
                cancellationToken).ConfigureAwait(false);
        if (!IsValidUnlockEntries(snapshot.Entries))
            return await HandleUnlockFailureAsync(
                key,
                identity,
                appId,
                SteamFailureKind.InvalidData,
                SteamAchievementDiagnosticCodes.ProgressInvalidPayload,
                cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new AchievementUnlockCachePayload(
            identity.SteamId,
            appId,
            snapshot.Entries));
        if (Encoding.UTF8.GetByteCount(payload) > _unlockPolicy.MaximumPayloadBytes)
            return await HandleUnlockFailureAsync(
                key,
                identity,
                appId,
                SteamFailureKind.InvalidData,
                SteamAchievementDiagnosticCodes.ProgressInvalidPayload,
                cancellationToken).ConfigureAwait(false);
        var fetchedAt = fetch.FetchedAt ?? timeProvider.GetUtcNow();
        var entry = _unlockPolicy.CreateEntry(
            key,
            payload,
            SteamDataSource.Cm,
            fetchedAt,
            PayloadFormat);
        await TryUpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
            snapshot,
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            fetchedAt,
            entry.RefreshAfter,
            fetch.Failure,
            fetch.DiagnosticCode);
    }

    private async Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> HandleUnlockFailureAsync(
        SteamCacheKey key,
        AchievementProgressSessionIdentity identity,
        uint appId,
        SteamFailureKind failure,
        string? diagnosticCode,
        CancellationToken cancellationToken)
    {
        var fallback = await ReadUnlockCacheAsync(key, identity.SteamId, appId, cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (fallback != null && _unlockPolicy.ShouldRetain(fallback.Entry, now))
        {
            var freshness = _unlockPolicy.GetFreshness(fallback.Entry, now);
            if (freshness == SteamFreshness.Fresh) freshness = SteamFreshness.Stale;
            if (freshness != SteamFreshness.Expired || _unlockPolicy.AllowExpiredOnFailure)
                return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                    new SteamAchievementUnlockSnapshot(
                        identity.SteamId,
                        appId,
                        identity.SessionGeneration,
                        fallback.Entries),
                    SteamDataSource.Sqlite,
                    freshness,
                    fallback.Entry.FetchedAt,
                    fallback.Entry.RefreshAfter,
                    failure,
                    diagnosticCode);
        }
        return SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
            failure,
            diagnosticCode ?? SteamAchievementDiagnosticCodes.ProgressInvalidPayload);
    }

    private static List<uint> DedupRequested(IReadOnlyList<uint> appIds)
    {
        var seen = new HashSet<uint>();
        var requested = new List<uint>(appIds.Count);
        foreach (var appId in appIds)
            if (seen.Add(appId)) requested.Add(appId);
        return requested;
    }

    private static bool IsValidBatchSnapshot(
        AchievementProgressBatchSnapshot snapshot,
        AchievementProgressSessionIdentity identity,
        IReadOnlyList<uint> expectedRequested)
    {
        if (snapshot.SteamId != identity.SteamId
            || snapshot.SessionGeneration != identity.SessionGeneration
            || snapshot.RequestedAppIds == null
            || snapshot.CoveredAppIds == null
            || snapshot.Summaries == null
            || !snapshot.RequestedAppIds.SequenceEqual(expectedRequested))
            return false;
        var requestedSet = new HashSet<uint>();
        foreach (var appId in snapshot.RequestedAppIds)
            if (appId == 0 || !requestedSet.Add(appId)) return false;
        var coveredSet = new HashSet<uint>();
        foreach (var appId in snapshot.CoveredAppIds)
            if (appId == 0 || !requestedSet.Contains(appId) || !coveredSet.Add(appId))
                return false;
        var summaryIds = new HashSet<uint>();
        foreach (var summary in snapshot.Summaries)
        {
            if (summary == null
                || summary.AppId == 0
                || !coveredSet.Contains(summary.AppId)
                || !summaryIds.Add(summary.AppId)
                || summary.Total < 0
                || summary.Unlocked < 0
                || summary.Unlocked > summary.Total
                || double.IsNaN(summary.Percentage)
                || double.IsInfinity(summary.Percentage)
                || summary.Percentage < 0
                || summary.Percentage > 100)
                return false;
        }
        return true;
    }

    private static bool CoversAll(CachedSummaries cached, List<uint> requested)
    {
        var covered = new HashSet<uint>(cached.CoveredAppIds);
        return requested.All(covered.Contains);
    }

    private static bool IsValidUnlockEntries(IReadOnlyList<SteamAchievementUnlock>? entries)
    {
        if (entries == null) return false;
        var keys = new HashSet<uint>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.InternalKey is 0) return false;
            var hasKey = entry.InternalKey is > 0;
            var hasName = !string.IsNullOrWhiteSpace(entry.InternalName);
            if (!hasKey && !hasName) return false;
            if (hasKey && !keys.Add(entry.InternalKey!.Value)) return false;
            if (hasName && !names.Add(entry.InternalName)) return false;
            if (entry.UnlockTimeUtc is { } time && time < DateTimeOffset.UnixEpoch) return false;
        }
        return true;
    }

    private static bool IsValidSummaryPayload(
        AchievementProgressSummaryCachePayload payload, ulong expectedSteamId)
    {
        if (payload.SteamId != expectedSteamId
            || payload.CoveredAppIds == null
            || payload.Values == null)
            return false;
        var covered = new HashSet<uint>();
        foreach (var appId in payload.CoveredAppIds)
            if (appId == 0 || !covered.Add(appId)) return false;
        var valueIds = new HashSet<uint>();
        foreach (var summary in payload.Values)
        {
            if (summary == null
                || summary.AppId == 0
                || !covered.Contains(summary.AppId)
                || !valueIds.Add(summary.AppId)
                || summary.Total < 0
                || summary.Unlocked < 0
                || summary.Unlocked > summary.Total
                || double.IsNaN(summary.Percentage)
                || double.IsInfinity(summary.Percentage)
                || summary.Percentage < 0
                || summary.Percentage > 100)
                return false;
        }
        return true;
    }

    private static SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>> SummaryFromCache(
        CachedSummaries cached,
        List<uint> requested,
        SteamFreshness freshness)
    {
        var covered = new HashSet<uint>(cached.CoveredAppIds);
        var byAppId = cached.Values.ToDictionary(summary => summary.AppId);
        var output = new List<SteamAchievementAppProgressSnapshot>(requested.Count);
        foreach (var appId in requested)
            if (covered.Contains(appId) && byAppId.TryGetValue(appId, out var summary))
                output.Add(summary);
        return SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>.Succeeded(
            output,
            SteamDataSource.Sqlite,
            freshness,
            cached.Entry.FetchedAt,
            cached.Entry.RefreshAfter);
    }

    private static SteamGatewayResult<SteamAchievementUnlockSnapshot> UnlockFromCache(
        CachedUnlocks cached,
        AchievementProgressSessionIdentity identity,
        uint appId,
        SteamFreshness freshness)
        => SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
            new SteamAchievementUnlockSnapshot(
                identity.SteamId,
                appId,
                identity.SessionGeneration,
                cached.Entries),
            SteamDataSource.Sqlite,
            freshness,
            cached.Entry.FetchedAt,
            cached.Entry.RefreshAfter);

    private async Task<CachedSummaries?> ReadSummaryCacheAsync(
        SteamCacheKey key,
        ulong expectedSteamId,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await cacheStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (entry == null
                || entry.PayloadFormat != PayloadFormat
                || Encoding.UTF8.GetByteCount(entry.Payload) > _summaryPolicy.MaximumPayloadBytes)
                return null;
            var payload = JsonSerializer.Deserialize<AchievementProgressSummaryCachePayload>(entry.Payload);
            return payload != null && IsValidSummaryPayload(payload, expectedSteamId)
                ? new CachedSummaries(entry, payload.CoveredAppIds, payload.Values)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogWarning(exception, "Ignored malformed achievement progress summary cache");
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read achievement progress summary cache");
            return null;
        }
    }

    private async Task<CachedUnlocks?> ReadUnlockCacheAsync(
        SteamCacheKey key,
        ulong expectedSteamId,
        uint expectedAppId,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await cacheStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (entry == null
                || entry.PayloadFormat != PayloadFormat
                || Encoding.UTF8.GetByteCount(entry.Payload) > _unlockPolicy.MaximumPayloadBytes)
                return null;
            var payload = JsonSerializer.Deserialize<AchievementUnlockCachePayload>(entry.Payload);
            return payload != null
                && payload.SteamId == expectedSteamId
                && payload.AppId == expectedAppId
                && IsValidUnlockEntries(payload.Entries)
                    ? new CachedUnlocks(entry, payload.Entries)
                    : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogWarning(exception, "Ignored malformed achievement unlock cache");
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read achievement unlock cache");
            return null;
        }
    }

    private async Task TryUpsertAsync(SteamResourceCacheEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await cacheStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist achievement progress cache");
        }
    }

    private Task<T> TrackOperation<T>(Task<T> task)
    {
        TrackBackground(task);
        return task;
    }

    private void TrackBackground(Task task)
    {
        _backgroundTasks.TryAdd(task, 0);
        _ = task.ContinueWith(
            (completed, state) =>
            {
                var owner = (SteamAchievementProgressGateway)state!;
                owner._backgroundTasks.TryRemove(completed, out _);
                if (completed.Exception != null)
                    owner._logger.LogWarning(completed.Exception, "Background achievement progress refresh failed");
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try
        {
            if (!Task.WhenAll(_backgroundTasks.Keys).Wait(TimeSpan.FromSeconds(5)))
                _logger.LogWarning("Timed out draining background achievement progress refreshes");
        }
        catch (AggregateException exception)
        {
            _logger.LogWarning(exception, "Background achievement progress refresh drain failed");
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_backgroundTasks.Keys).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Background achievement progress refresh drain failed");
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private sealed record FetchAttempt(
        SteamGatewayResult<AchievementProgressBatchSnapshot> Result,
        IReadOnlyList<uint> RequestedAppIds,
        bool Owned);

    private sealed record CachedSummaries(
        SteamResourceCacheEntry Entry,
        IReadOnlyList<uint> CoveredAppIds,
        IReadOnlyList<SteamAchievementAppProgressSnapshot> Values);

    private sealed record CachedUnlocks(
        SteamResourceCacheEntry Entry,
        IReadOnlyList<SteamAchievementUnlock> Entries);
}
