using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Steam.Gateway;

internal sealed class SteamAchievementSchemaGateway(
    ISteamResourceCacheStore cacheStore,
    IAchievementSchemaSource source,
    SteamRequestCoalescer<SteamCacheKey> coalescer,
    TimeProvider timeProvider,
    ILogger<SteamAchievementSchemaGateway> logger) : ISteamAchievementSchemaGateway, IDisposable, IAsyncDisposable
{
    private const string PayloadFormat = "json-v1";
    private readonly SteamCachePolicy _policy = SteamResourcePolicies.AchievementSchema;
    private readonly ILogger<SteamAchievementSchemaGateway> _logger = logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundTasks = new();
    private int _disposed;

    public async Task<SteamGatewayResult<SteamAchievementSchemaSnapshot>> GetSchemaAsync(
        uint appId,
        string language,
        string? preferredAccountName = null,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (appId == 0)
            return SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(
                SteamFailureKind.NotFound, SteamAchievementDiagnosticCodes.SchemaInvalidPayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var normalizedLanguage = language.Trim().ToLowerInvariant();

        var key = SteamAchievementCacheKeys.Schema(appId, normalizedLanguage);
        var cached = await ReadCacheAsync(key, appId, normalizedLanguage, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        SteamFreshness? freshness = cached == null ? null : _policy.GetFreshness(cached.Entry, now);
        if (cached != null && !_policy.ShouldRetain(cached.Entry, now))
        {
            cached = null;
            freshness = null;
        }

        if (cached != null && refreshMode == SteamRefreshMode.CacheOnly)
            return FromCache(cached, freshness!.Value);
        if (cached != null && freshness == SteamFreshness.Fresh && refreshMode == SteamRefreshMode.PreferCache)
            return FromCache(cached, SteamFreshness.Fresh);
        if (refreshMode == SteamRefreshMode.CacheOnly)
            return SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(
                SteamFailureKind.NotFound, SteamAchievementDiagnosticCodes.SchemaCacheMiss);

        return await coalescer.RunAsync(
            key,
            token => TrackOperation(
                FetchAndCacheAsync(key, appId, normalizedLanguage, preferredAccountName, token)),
            _lifetime.Token,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SteamGatewayResult<SteamAchievementSchemaSnapshot>> FetchAndCacheAsync(
        SteamCacheKey key,
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken cancellationToken)
    {
        var cached = await ReadCacheAsync(key, appId, language, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (cached?.Snapshot != null && _policy.ShouldRetain(cached.Entry, now))
        {
            var hashResult = await source.GetHashAsync(
                appId, language, preferredAccountName, cancellationToken).ConfigureAwait(false);
            if (!hashResult.IsSuccess)
                return await HandleFailureAsync(
                    key, appId, language,
                    hashResult.Failure ?? SteamFailureKind.Unknown,
                    hashResult.DiagnosticCode, cancellationToken).ConfigureAwait(false);
            if (hashResult.Value == cached.Snapshot.SchemaHash)
            {
                var touched = _policy.CreateEntry(
                    key,
                    cached.Entry.Payload,
                    cached.Entry.Source,
                    timeProvider.GetUtcNow(),
                    PayloadFormat,
                    cached.Entry.ETag,
                    cached.Entry.ContentHash);
                await TryUpsertAsync(touched, cancellationToken).ConfigureAwait(false);
                return SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
                    cached.Snapshot,
                    SteamDataSource.Sqlite,
                    SteamFreshness.Fresh,
                    touched.FetchedAt,
                    touched.RefreshAfter);
            }
        }

        var result = await source.GetFullAsync(
            appId, language, preferredAccountName, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null)
            return await HandleFailureAsync(
                key, appId, language,
                result.Failure ?? SteamFailureKind.InvalidData,
                result.DiagnosticCode ?? SteamAchievementDiagnosticCodes.SchemaInvalidPayload,
                cancellationToken).ConfigureAwait(false);

        var snapshot = result.Value;
        if (!IsValidSnapshot(snapshot, appId, language))
            return await HandleFailureAsync(
                key, appId, language,
                SteamFailureKind.InvalidData,
                SteamAchievementDiagnosticCodes.SchemaInvalidPayload,
                cancellationToken).ConfigureAwait(false);
        var fetchedAt = result.FetchedAt ?? timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new AchievementSchemaCachePayload(snapshot, null, null));
        if (Encoding.UTF8.GetByteCount(payload) > _policy.MaximumPayloadBytes)
            return await HandleFailureAsync(
                key, appId, language,
                SteamFailureKind.InvalidData,
                SteamAchievementDiagnosticCodes.SchemaPayloadTooLarge,
                cancellationToken).ConfigureAwait(false);
        var entry = _policy.CreateEntry(
            key,
            payload,
            SteamDataSource.Cm,
            fetchedAt,
            PayloadFormat,
            contentHash: snapshot.SchemaHash.ToString(CultureInfo.InvariantCulture));
        await TryUpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        return SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
            snapshot,
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            fetchedAt,
            entry.RefreshAfter);
    }

    private async Task<SteamGatewayResult<SteamAchievementSchemaSnapshot>> HandleFailureAsync(
        SteamCacheKey key,
        uint appId,
        string language,
        SteamFailureKind failure,
        string? diagnosticCode,
        CancellationToken cancellationToken)
    {
        var fallback = await ReadCacheAsync(key, appId, language, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (fallback?.Snapshot != null && _policy.ShouldRetain(fallback.Entry, now))
        {
            var freshness = _policy.GetFreshness(fallback.Entry, now);
            if (freshness == SteamFreshness.Fresh) freshness = SteamFreshness.Stale;
            if (freshness != SteamFreshness.Expired || _policy.AllowExpiredOnFailure)
                return SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
                    fallback.Snapshot,
                    SteamDataSource.Sqlite,
                    freshness,
                    fallback.Entry.FetchedAt,
                    fallback.Entry.RefreshAfter,
                    failure,
                    diagnosticCode);
        }
        if (_policy.CanNegativeCache(failure))
            await WriteNegativeCacheAsync(key, failure, diagnosticCode, now, cancellationToken)
                .ConfigureAwait(false);
        return SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(
            failure, diagnosticCode ?? SteamAchievementDiagnosticCodes.SchemaInvalidPayload);
    }

    private async Task WriteNegativeCacheAsync(
        SteamCacheKey key,
        SteamFailureKind failure,
        string? diagnosticCode,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        if (_policy.NegativeCacheInterval is not { } interval) return;
        var payload = JsonSerializer.Serialize(new AchievementSchemaCachePayload(null, failure, diagnosticCode));
        var entry = _policy.CreateEntry(key, payload, SteamDataSource.Cm, fetchedAt, PayloadFormat) with
        {
            RefreshAfter = fetchedAt + interval,
            RetainUntil = fetchedAt + interval
        };
        await TryUpsertAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsValidSnapshot(
        SteamAchievementSchemaSnapshot snapshot,
        uint expectedAppId,
        string expectedLanguage)
    {
        if (snapshot.AppId != expectedAppId
            || !string.Equals(snapshot.Language, expectedLanguage, StringComparison.Ordinal)
            || snapshot.Definitions == null
            || snapshot.Groups == null)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var internalKeys = new HashSet<uint>();
        foreach (var definition in snapshot.Definitions)
        {
            if (string.IsNullOrEmpty(definition.InternalName) || !names.Add(definition.InternalName))
                return false;
            if (definition.InternalKey is { } internalKey && !internalKeys.Add(internalKey))
                return false;
        }
        var groupIds = new HashSet<uint>();
        foreach (var group in snapshot.Groups)
            if (!groupIds.Add(group.GroupId))
                return false;
        return true;
    }

    private static bool IsValid(
        SteamAchievementSchemaSnapshot snapshot,
        uint expectedAppId,
        string expectedLanguage,
        SteamResourceCacheEntry entry)
        => IsValidSnapshot(snapshot, expectedAppId, expectedLanguage)
            && string.Equals(
                entry.ContentHash,
                snapshot.SchemaHash.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

    private async Task<CachedSchema?> ReadCacheAsync(
        SteamCacheKey key,
        uint expectedAppId,
        string expectedLanguage,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await cacheStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (entry == null || entry.PayloadFormat != PayloadFormat
                || Encoding.UTF8.GetByteCount(entry.Payload) > _policy.MaximumPayloadBytes)
                return null;
            var payload = JsonSerializer.Deserialize<AchievementSchemaCachePayload>(entry.Payload);
            if (payload?.Value is { } snapshot)
                return IsValid(snapshot, expectedAppId, expectedLanguage, entry)
                    ? new CachedSchema(entry, snapshot, null, null)
                    : null;
            if (payload?.Failure is { } failure)
                return _policy.CanNegativeCache(failure)
                    ? new CachedSchema(entry, null, failure, payload.DiagnosticCode)
                    : null;
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogWarning(exception, "Ignored malformed achievement schema cache");
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read achievement schema cache");
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
            _logger.LogWarning(exception, "Failed to persist achievement schema cache");
        }
    }

    private static SteamGatewayResult<SteamAchievementSchemaSnapshot> FromCache(
        CachedSchema cached, SteamFreshness freshness)
        => cached.Failure is { } failure
            ? new SteamGatewayResult<SteamAchievementSchemaSnapshot>(
                false,
                null,
                SteamDataSource.Sqlite,
                freshness,
                cached.Entry.FetchedAt,
                cached.Entry.RefreshAfter,
                failure,
                cached.DiagnosticCode)
            : SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
                cached.Snapshot,
                SteamDataSource.Sqlite,
                freshness,
                cached.Entry.FetchedAt,
                cached.Entry.RefreshAfter);

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
                var owner = (SteamAchievementSchemaGateway)state!;
                owner._backgroundTasks.TryRemove(completed, out _);
                if (completed.Exception != null)
                    owner._logger.LogWarning(completed.Exception, "Background achievement schema refresh failed");
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
                _logger.LogWarning("Timed out draining background achievement schema refreshes");
        }
        catch (AggregateException exception)
        {
            _logger.LogWarning(exception, "Background achievement schema refresh drain failed");
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
            _logger.LogWarning(exception, "Background achievement schema refresh drain failed");
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private sealed record AchievementSchemaCachePayload(
        SteamAchievementSchemaSnapshot? Value,
        SteamFailureKind? Failure,
        string? DiagnosticCode);

    private sealed record CachedSchema(
        SteamResourceCacheEntry Entry,
        SteamAchievementSchemaSnapshot? Snapshot,
        SteamFailureKind? Failure,
        string? DiagnosticCode);
}
