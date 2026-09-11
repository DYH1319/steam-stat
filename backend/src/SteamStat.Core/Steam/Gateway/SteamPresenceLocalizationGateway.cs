using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Steam.Gateway;

internal sealed class SteamPresenceLocalizationGateway(
    ISteamResourceCacheStore cacheStore,
    ISteamPresenceLocalizationSource source,
    SteamRequestCoalescer<SteamCacheKey> coalescer,
    TimeProvider timeProvider,
    ILogger<SteamPresenceLocalizationGateway> logger) : ISteamPresenceLocalizationGateway, IAsyncDisposable
{
    private const string ResourceKind = "rich-presence-localization";
    private const string PayloadFormat = "json-v1";
    private const string PublicScope = "public";
    private readonly SteamCachePolicy _policy = SteamResourcePolicies.RichPresenceLocalization;
    private readonly CancellationTokenSource _lifetime = new();

    public async Task<SteamGatewayResult<IReadOnlyDictionary<string, string>>> GetLocalizationAsync(
        string accountName,
        uint appId,
        string language,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        cancellationToken.ThrowIfCancellationRequested();
        if (appId == 0)
            return SteamGatewayResult<IReadOnlyDictionary<string, string>>.Failed(
                SteamFailureKind.NotFound, "invalid_app_id");
        var key = SteamCacheKey.Create(ResourceKind, PublicScope, appId.ToString(), language);
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
            return SteamGatewayResult<IReadOnlyDictionary<string, string>>.Failed(
                SteamFailureKind.NotFound, "cache_miss");
        return await coalescer.RunAsync(
            key,
            token => FetchAndCacheAsync(key, accountName, appId, key.Language, cached, token),
            _lifetime.Token,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SteamGatewayResult<IReadOnlyDictionary<string, string>>> FetchAndCacheAsync(
        SteamCacheKey key,
        string accountName,
        uint appId,
        string language,
        CachedLocalization? cached,
        CancellationToken cancellationToken)
    {
        var result = await source.GetAsync(
            accountName, appId, language, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null)
        {
            var now = timeProvider.GetUtcNow();
            if (cached != null && _policy.ShouldRetain(cached.Entry, now))
            {
                var freshness = _policy.GetFreshness(cached.Entry, now);
                if (freshness == SteamFreshness.Fresh) freshness = SteamFreshness.Stale;
                return SteamGatewayResult<IReadOnlyDictionary<string, string>>.Succeeded(
                    cached.Tokens,
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
            return SteamGatewayResult<IReadOnlyDictionary<string, string>>.Failed(
                SteamFailureKind.InvalidData, "cache_payload_too_large");
        var entry = _policy.CreateEntry(key, payload, SteamDataSource.Cm, fetchedAt, PayloadFormat);
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
            logger.LogWarning(exception, "Failed to persist rich presence localization cache");
        }
        return SteamGatewayResult<IReadOnlyDictionary<string, string>>.Succeeded(
            result.Value,
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            fetchedAt,
            entry.RefreshAfter);
    }

    private async Task<CachedLocalization?> ReadCacheAsync(
        SteamCacheKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await cacheStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (entry == null || entry.PayloadFormat != PayloadFormat
                || Encoding.UTF8.GetByteCount(entry.Payload) > _policy.MaximumPayloadBytes) return null;
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(entry.Payload);
            return payload == null
                ? null
                : new CachedLocalization(
                    entry,
                    new Dictionary<string, string>(payload, StringComparer.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            logger.LogWarning(exception, "Ignored malformed rich presence localization cache");
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to read rich presence localization cache");
            return null;
        }
    }

    private static SteamGatewayResult<IReadOnlyDictionary<string, string>> FromCache(
        CachedLocalization cached,
        SteamFreshness freshness)
        => SteamGatewayResult<IReadOnlyDictionary<string, string>>.Succeeded(
            cached.Tokens,
            SteamDataSource.Sqlite,
            freshness,
            cached.Entry.FetchedAt,
            cached.Entry.RefreshAfter);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed record CachedLocalization(
        SteamResourceCacheEntry Entry,
        IReadOnlyDictionary<string, string> Tokens);
}
