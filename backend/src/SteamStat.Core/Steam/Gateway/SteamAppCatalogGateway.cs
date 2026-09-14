using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Steam.Gateway;

internal sealed class SteamAppCatalogGateway(
    ISteamResourceCacheStore cacheStore,
    ISteamAppMetadataSource source,
    SteamRequestCoalescer<SteamCacheKey> coalescer,
    TimeProvider timeProvider,
    ILogger<SteamAppCatalogGateway> logger) : ISteamAppCatalogGateway, IDisposable, IAsyncDisposable
{
    private const string ResourceKind = "app-metadata";
    private const string PublicScope = "public";
    private const string PayloadFormat = "json-v1";
    private readonly SteamCachePolicy _policy = SteamResourcePolicies.AppMetadata;
    private readonly ILogger<SteamAppCatalogGateway> _logger = logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundTasks = new();
    private int _disposed;

    public async Task<SteamGatewayResult<SteamAppMetadataSnapshot>> GetAppAsync(
        uint appId,
        string language,
        string? preferredAccountName = null,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (appId == 0)
            return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                SteamFailureKind.NotFound, "invalid_app_id");

        var key = SteamCacheKey.Create(ResourceKind, PublicScope, appId.ToString(), language);
        var cached = await ReadCacheAsync(key, appId, cancellationToken).ConfigureAwait(false);
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
        if (cached?.Snapshot != null && freshness == SteamFreshness.Stale && refreshMode == SteamRefreshMode.PreferCache)
        {
            TrackBackground(RefreshAsync(key, appId, key.Language, preferredAccountName, CancellationToken.None));
            return FromCache(cached, SteamFreshness.Stale);
        }
        if (refreshMode == SteamRefreshMode.CacheOnly)
            return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                SteamFailureKind.NotFound, "cache_miss");

        return await RefreshAsync(
            key, appId, key.Language, preferredAccountName, cancellationToken).ConfigureAwait(false);
    }

    private Task<SteamGatewayResult<SteamAppMetadataSnapshot>> RefreshAsync(
        SteamCacheKey key,
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken callerToken)
        => coalescer.RunAsync(
            key,
            token => TrackOperation(FetchAndCacheAsync(key, appId, language, preferredAccountName, token)),
            _lifetime.Token,
            callerToken);

    private async Task<SteamGatewayResult<SteamAppMetadataSnapshot>> FetchAndCacheAsync(
        SteamCacheKey key,
        uint appId,
        string language,
        string? preferredAccountName,
        CancellationToken cancellationToken)
    {
        var result = await source.GetAsync(
            appId, language, preferredAccountName, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null)
        {
            var fallback = await ReadCacheAsync(key, appId, cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            if (fallback?.Snapshot != null && _policy.ShouldRetain(fallback.Entry, now))
            {
                var freshness = _policy.GetFreshness(fallback.Entry, now);
                if (freshness == SteamFreshness.Fresh) freshness = SteamFreshness.Stale;
                if (freshness != SteamFreshness.Expired || _policy.AllowExpiredOnFailure)
                    return SteamGatewayResult<SteamAppMetadataSnapshot>.Succeeded(
                        fallback.Snapshot,
                        SteamDataSource.Sqlite,
                        freshness,
                        fallback.Entry.FetchedAt,
                        fallback.Entry.RefreshAfter,
                        result.Failure,
                        result.DiagnosticCode);
            }
            if (result.Failure is { } failure && _policy.CanNegativeCache(failure))
                await WriteNegativeCacheAsync(key, failure, result.DiagnosticCode, now, cancellationToken)
                    .ConfigureAwait(false);
            return result;
        }

        var fetchedAt = result.FetchedAt ?? timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new AppMetadataCachePayload(result.Value, null, null));
        if (Encoding.UTF8.GetByteCount(payload) > _policy.MaximumPayloadBytes)
            return SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
                SteamFailureKind.InvalidData, "cache_payload_too_large");
        var entry = _policy.CreateEntry(key, payload, result.Source ?? SteamDataSource.Http, fetchedAt, PayloadFormat);
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
            _logger.LogWarning(exception, "Failed to persist app metadata cache for {AppId}", appId);
        }
        return SteamGatewayResult<SteamAppMetadataSnapshot>.Succeeded(
            result.Value,
            result.Source ?? SteamDataSource.Http,
            SteamFreshness.Fresh,
            fetchedAt,
            entry.RefreshAfter);
    }

    private async Task WriteNegativeCacheAsync(
        SteamCacheKey key,
        SteamFailureKind failure,
        string? diagnosticCode,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        if (_policy.NegativeCacheInterval is not { } interval) return;
        var payload = JsonSerializer.Serialize(new AppMetadataCachePayload(null, failure, diagnosticCode));
        var entry = _policy.CreateEntry(key, payload, SteamDataSource.Http, fetchedAt, PayloadFormat) with
        {
            RefreshAfter = fetchedAt + interval,
            RetainUntil = fetchedAt + interval
        };
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
            _logger.LogWarning(exception, "Failed to persist negative app metadata cache");
        }
    }

    private static bool IsValid(SteamAppMetadataSnapshot? snapshot, uint expectedAppId)
        => snapshot != null && snapshot.AppId == expectedAppId && !string.IsNullOrWhiteSpace(snapshot.Name);

    private async Task<CachedApp?> ReadCacheAsync(
        SteamCacheKey key,
        uint expectedAppId,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await cacheStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (entry == null || entry.PayloadFormat != PayloadFormat
                || Encoding.UTF8.GetByteCount(entry.Payload) > _policy.MaximumPayloadBytes)
                return null;
            using var document = JsonDocument.Parse(entry.Payload);
            if (document.RootElement.TryGetProperty(nameof(AppMetadataCachePayload.Value), out _))
            {
                var payload = document.RootElement.Deserialize<AppMetadataCachePayload>();
                if (payload?.Failure is { } failure)
                    return _policy.CanNegativeCache(failure)
                        ? new CachedApp(entry, null, failure, payload.DiagnosticCode)
                        : null;
                return IsValid(payload?.Value, expectedAppId)
                    ? new CachedApp(entry, payload!.Value, null, null)
                    : null;
            }
            var snapshot = document.RootElement.Deserialize<SteamAppMetadataSnapshot>();
            return IsValid(snapshot, expectedAppId)
                ? new CachedApp(entry, snapshot, null, null)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogWarning(exception, "Ignored malformed app metadata cache for {AppId}", expectedAppId);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read app metadata cache for {AppId}", expectedAppId);
            return null;
        }
    }

    private static SteamGatewayResult<SteamAppMetadataSnapshot> FromCache(CachedApp cached, SteamFreshness freshness)
        => cached.Failure is { } failure
            ? new SteamGatewayResult<SteamAppMetadataSnapshot>(
                false,
                null,
                SteamDataSource.Sqlite,
                freshness,
                cached.Entry.FetchedAt,
                cached.Entry.RefreshAfter,
                failure,
                cached.DiagnosticCode)
            : SteamGatewayResult<SteamAppMetadataSnapshot>.Succeeded(
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
                var owner = (SteamAppCatalogGateway)state!;
                owner._backgroundTasks.TryRemove(completed, out _);
                if (completed.Exception != null)
                    owner._logger.LogWarning(completed.Exception, "Background app metadata refresh failed");
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
                _logger.LogWarning("Timed out draining background app metadata refreshes");
        }
        catch (AggregateException exception)
        {
            _logger.LogWarning(exception, "Background app metadata refresh drain failed");
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
            _logger.LogWarning(exception, "Background app metadata refresh drain failed");
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private sealed record AppMetadataCachePayload(
        SteamAppMetadataSnapshot? Value,
        SteamFailureKind? Failure,
        string? DiagnosticCode);

    private sealed record CachedApp(
        SteamResourceCacheEntry Entry,
        SteamAppMetadataSnapshot? Snapshot,
        SteamFailureKind? Failure,
        string? DiagnosticCode);
}
