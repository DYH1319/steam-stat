using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Steam.Cache;

public sealed record SteamResourceCacheEntry(
    SteamCacheKey Key,
    string PayloadFormat,
    string Payload,
    SteamDataSource Source,
    DateTimeOffset FetchedAt,
    DateTimeOffset RefreshAfter,
    DateTimeOffset? RetainUntil,
    DateTimeOffset LastAccessedAt,
    string? ETag = null,
    string? ContentHash = null);

public interface ISteamResourceCacheStore
{
    Task<SteamResourceCacheEntry?> GetAsync(SteamCacheKey key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SteamResourceCacheEntry>> GetByResourceKindAsync(
        string resourceKind,
        int schemaVersion,
        int limit,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(SteamResourceCacheEntry entry, CancellationToken cancellationToken = default);

    Task DeleteAsync(SteamCacheKey key, CancellationToken cancellationToken = default);

    Task<int> DeleteExpiredAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default);
}
