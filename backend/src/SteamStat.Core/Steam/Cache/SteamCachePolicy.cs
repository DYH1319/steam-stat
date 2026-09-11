using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Steam.Cache;

public sealed record SteamCachePolicy
{
    public SteamCachePolicy(
        TimeSpan refreshInterval,
        TimeSpan staleInterval,
        TimeSpan retentionInterval,
        int maximumPayloadBytes,
        bool allowExpiredOnFailure,
        bool allowNegativeCache,
        TimeSpan? negativeCacheInterval = null)
    {
        if (refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        if (staleInterval < refreshInterval) throw new ArgumentOutOfRangeException(nameof(staleInterval));
        if (retentionInterval < staleInterval) throw new ArgumentOutOfRangeException(nameof(retentionInterval));
        if (maximumPayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        if (negativeCacheInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(negativeCacheInterval));
        RefreshInterval = refreshInterval;
        StaleInterval = staleInterval;
        RetentionInterval = retentionInterval;
        MaximumPayloadBytes = maximumPayloadBytes;
        AllowExpiredOnFailure = allowExpiredOnFailure;
        AllowNegativeCache = allowNegativeCache;
        NegativeCacheInterval = negativeCacheInterval;
    }

    public TimeSpan RefreshInterval { get; }
    public TimeSpan StaleInterval { get; }
    public TimeSpan RetentionInterval { get; }
    public int MaximumPayloadBytes { get; }
    public bool AllowExpiredOnFailure { get; }
    public bool AllowNegativeCache { get; }
    public TimeSpan? NegativeCacheInterval { get; }

    public SteamFreshness GetFreshness(SteamResourceCacheEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (now < entry.RefreshAfter) return SteamFreshness.Fresh;
        return now < entry.FetchedAt + StaleInterval ? SteamFreshness.Stale : SteamFreshness.Expired;
    }

    public bool ShouldRetain(SteamResourceCacheEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.RetainUntil == null || now < entry.RetainUntil;
    }

    public bool CanNegativeCache(SteamFailureKind failure)
        => AllowNegativeCache && failure == SteamFailureKind.NotFound;

    public SteamResourceCacheEntry CreateEntry(
        SteamCacheKey key,
        string payload,
        SteamDataSource source,
        DateTimeOffset fetchedAt,
        string payloadFormat = "json-v1",
        string? etag = null,
        string? contentHash = null)
        => new(
            key,
            payloadFormat,
            payload,
            source,
            fetchedAt,
            fetchedAt + RefreshInterval,
            fetchedAt + RetentionInterval,
            fetchedAt,
            etag,
            contentHash);
}

public static class SteamResourcePolicies
{
    public static SteamCachePolicy AppMetadata { get; } = new(
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(30),
        TimeSpan.FromDays(180),
        64 * 1024,
        true,
        true,
        TimeSpan.FromHours(1));
}
