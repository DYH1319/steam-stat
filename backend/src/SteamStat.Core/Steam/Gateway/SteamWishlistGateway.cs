using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Steam.Gateway;

internal sealed class SteamWishlistGateway(
    ISteamResourceCacheStore cacheStore,
    ISteamWishlistSource source,
    SteamRequestCoalescer<SteamCacheKey> coalescer,
    TimeProvider timeProvider,
    ILogger<SteamWishlistGateway> logger) : ISteamWishlistGateway, IAsyncDisposable
{
    private const string ResourceKind = "wishlist";
    private const string PayloadFormat = "json-v1";
    private readonly SteamCachePolicy _policy = SteamResourcePolicies.Wishlist;
    private readonly CancellationTokenSource _lifetime = new();

    public async Task<SteamGatewayResult<IReadOnlyList<uint>>> GetWishlistAsync(
        ulong steamId,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (steamId == 0)
            return SteamGatewayResult<IReadOnlyList<uint>>.Failed(
                SteamFailureKind.NotFound, "invalid_steam_id");
        var key = SteamCacheKey.Create(ResourceKind, steamId.ToString(), "items");
        var cached = await ReadCacheAsync(key, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (cached != null && !_policy.ShouldRetain(cached.Entry, now)) cached = null;
        if (cached != null)
        {
            var freshness = _policy.GetFreshness(cached.Entry, now);
            if (refreshMode == SteamRefreshMode.CacheOnly
                || refreshMode == SteamRefreshMode.PreferCache && freshness == SteamFreshness.Fresh)
                return FromCache(cached, freshness);
        }
        if (refreshMode == SteamRefreshMode.CacheOnly)
            return SteamGatewayResult<IReadOnlyList<uint>>.Failed(
                SteamFailureKind.NotFound, "cache_miss");
        return await coalescer.RunAsync(
            key,
            token => FetchAndCacheAsync(key, steamId, cached, token),
            _lifetime.Token,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SteamGatewayResult<IReadOnlyList<uint>>> FetchAndCacheAsync(
        SteamCacheKey key,
        ulong steamId,
        CachedWishlist? cached,
        CancellationToken cancellationToken)
    {
        var result = await source.GetAsync(steamId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null)
        {
            var now = timeProvider.GetUtcNow();
            if (cached != null && _policy.ShouldRetain(cached.Entry, now))
            {
                var freshness = _policy.GetFreshness(cached.Entry, now);
                if (freshness == SteamFreshness.Fresh) freshness = SteamFreshness.Stale;
                return SteamGatewayResult<IReadOnlyList<uint>>.Succeeded(
                    cached.AppIds,
                    SteamDataSource.Sqlite,
                    freshness,
                    cached.Entry.FetchedAt,
                    cached.Entry.RefreshAfter,
                    result.Failure,
                    result.DiagnosticCode);
            }
            return result;
        }
        var fetchedAt = result.FetchedAt ?? timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(result.Value);
        if (Encoding.UTF8.GetByteCount(payload) > _policy.MaximumPayloadBytes)
            return SteamGatewayResult<IReadOnlyList<uint>>.Failed(
                SteamFailureKind.InvalidData, "cache_payload_too_large");
        var entry = _policy.CreateEntry(key, payload, SteamDataSource.Http, fetchedAt, PayloadFormat);
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
            logger.LogWarning(exception, "Failed to persist Steam wishlist cache");
        }
        return SteamGatewayResult<IReadOnlyList<uint>>.Succeeded(
            result.Value,
            SteamDataSource.Http,
            SteamFreshness.Fresh,
            fetchedAt,
            entry.RefreshAfter);
    }

    private async Task<CachedWishlist?> ReadCacheAsync(
        SteamCacheKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await cacheStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (entry == null || entry.PayloadFormat != PayloadFormat
                || Encoding.UTF8.GetByteCount(entry.Payload) > _policy.MaximumPayloadBytes) return null;
            var appIds = JsonSerializer.Deserialize<uint[]>(entry.Payload);
            return appIds == null ? null : new CachedWishlist(entry, appIds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            logger.LogWarning(exception, "Ignored malformed Steam wishlist cache");
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to read Steam wishlist cache");
            return null;
        }
    }

    private static SteamGatewayResult<IReadOnlyList<uint>> FromCache(
        CachedWishlist cached,
        SteamFreshness freshness)
        => SteamGatewayResult<IReadOnlyList<uint>>.Succeeded(
            cached.AppIds,
            SteamDataSource.Sqlite,
            freshness,
            cached.Entry.FetchedAt,
            cached.Entry.RefreshAfter);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed record CachedWishlist(
        SteamResourceCacheEntry Entry,
        IReadOnlyList<uint> AppIds);
}
