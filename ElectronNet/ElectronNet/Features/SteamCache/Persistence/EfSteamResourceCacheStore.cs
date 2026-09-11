using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using CacheEntry = SteamStat.Core.Steam.Cache.SteamResourceCacheEntry;

namespace ElectronNet.Features.SteamCache.Persistence;

internal sealed class EfSteamResourceCacheStore(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<EfSteamResourceCacheStore> logger) : ISteamResourceCacheStore
{
    private const int MaximumPayloadBytes = 1024 * 1024;

    public async Task<CacheEntry?> GetAsync(SteamCacheKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.SteamResourceCacheTable.AsNoTracking()
            .FirstOrDefaultAsync(entry =>
                entry.ResourceKind == key.ResourceKind
                && entry.ScopeId == key.ScopeId
                && entry.ResourceId == key.ResourceId
                && entry.Language == key.Language
                && entry.Variant == key.Variant
                && entry.SchemaVersion == key.SchemaVersion,
                cancellationToken)
            .ConfigureAwait(false);
        if (entity == null) return null;
        if (Encoding.UTF8.GetByteCount(entity.Payload) > MaximumPayloadBytes)
        {
            logger.LogWarning(
                "Ignored oversized Steam cache payload for {ResourceKind} schema {SchemaVersion}",
                key.ResourceKind,
                key.SchemaVersion);
            return null;
        }
        if (!Enum.TryParse<SteamDataSource>(entity.Source, out var source))
        {
            logger.LogWarning(
                "Ignored Steam cache entry with invalid source for {ResourceKind} schema {SchemaVersion}",
                key.ResourceKind,
                key.SchemaVersion);
            return null;
        }
        return new CacheEntry(
            key,
            entity.PayloadFormat,
            entity.Payload,
            source,
            DateTimeOffset.FromUnixTimeSeconds(entity.FetchedAt),
            DateTimeOffset.FromUnixTimeSeconds(entity.RefreshAfter),
            entity.RetainUntil is { } retainUntil ? DateTimeOffset.FromUnixTimeSeconds(retainUntil) : null,
            DateTimeOffset.FromUnixTimeSeconds(entity.LastAccessedAt),
            entity.ETag,
            entity.ContentHash);
    }

    public async Task UpsertAsync(CacheEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.PayloadFormat != "json-v1") throw new ArgumentException("Unsupported cache payload format.", nameof(entry));
        if (Encoding.UTF8.GetByteCount(entry.Payload) > MaximumPayloadBytes)
            throw new ArgumentException("Cache payload exceeds the maximum size.", nameof(entry));

        var key = entry.Key;
        var retainUntil = entry.RetainUntil?.ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO steam_resource_cache
                (resource_kind, scope_id, resource_id, language, variant, schema_version,
                 payload_format, payload, source, etag, content_hash, fetched_at, refresh_after,
                 retain_until, last_accessed_at)
            VALUES
                ({{key.ResourceKind}}, {{key.ScopeId}}, {{key.ResourceId}}, {{key.Language}}, {{key.Variant}}, {{key.SchemaVersion}},
                 {{entry.PayloadFormat}}, {{entry.Payload}}, {{entry.Source.ToString()}}, {{entry.ETag}}, {{entry.ContentHash}},
                 {{entry.FetchedAt.ToUnixTimeSeconds()}}, {{entry.RefreshAfter.ToUnixTimeSeconds()}},
                 {{retainUntil}}, {{entry.LastAccessedAt.ToUnixTimeSeconds()}})
            ON CONFLICT(resource_kind, scope_id, resource_id, language, variant, schema_version)
            DO UPDATE SET
                payload_format = excluded.payload_format,
                payload = excluded.payload,
                source = excluded.source,
                etag = excluded.etag,
                content_hash = excluded.content_hash,
                fetched_at = excluded.fetched_at,
                refresh_after = excluded.refresh_after,
                retain_until = excluded.retain_until,
                last_accessed_at = excluded.last_accessed_at;
            """, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(SteamCacheKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            DELETE FROM steam_resource_cache
            WHERE resource_kind = {{key.ResourceKind}}
              AND scope_id = {{key.ScopeId}}
              AND resource_id = {{key.ResourceId}}
              AND language = {{key.Language}}
              AND variant = {{key.Variant}}
              AND schema_version = {{key.SchemaVersion}};
            """, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteExpiredAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Database.ExecuteSqlInterpolatedAsync($$"""
            DELETE FROM steam_resource_cache
            WHERE id IN (
                SELECT id FROM steam_resource_cache
                WHERE retain_until IS NOT NULL AND retain_until <= {{now.ToUnixTimeSeconds()}}
                ORDER BY retain_until, id
                LIMIT {{batchSize}}
            );
            """, cancellationToken).ConfigureAwait(false);
    }
}
