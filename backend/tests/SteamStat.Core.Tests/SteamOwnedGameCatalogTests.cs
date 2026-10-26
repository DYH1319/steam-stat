using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamOwnedGameCatalogTests
{
    [Test]
    public async Task GetCachedAsync_ReturnsOwnedAndFamilySharedGames()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var snapshotStore = new SteamFeatureSnapshotStore(
            cache, time, NullLogger<SteamFeatureSnapshotStore>.Instance);
        await snapshotStore.SaveLibraryAsync(
            "acct",
            100000001UL,
            [
                new SteamOwnedGame
                {
                    AppId = 10,
                    Name = "Alpha",
                    NameLocalized = "Alpha Localized",
                    PlaytimeForever = 120,
                    RtimeLastPlayed = 1699999999,
                    IsOwned = true
                },
                new SteamOwnedGame
                {
                    AppId = 20,
                    Name = "Beta",
                    NameLocalized = "Beta Localized",
                    PlaytimeForever = 30,
                    RtimeLastPlayed = 1699999900,
                    IsFamilyShared = true
                },
                new SteamOwnedGame
                {
                    AppId = 30,
                    Name = "Wishlist Only",
                    IsInWishlist = true
                }
            ],
            SteamDataSource.Cm,
            time.GetUtcNow());
        var catalog = new SteamOwnedGameCatalog(snapshotStore);
        var upserts = cache.UpsertCount;

        var games = await catalog.GetCachedAsync("ACCT");

        games.Should().HaveCount(2);
        var alpha = games.Should().ContainSingle(game => game.AppId == 10u).Subject;
        alpha.Name.Should().Be("Alpha");
        alpha.LocalizedName.Should().Be("Alpha Localized");
        alpha.PlaytimeForever.Should().Be(120);
        alpha.LastPlayedAt.Should().Be(1699999999L);
        games.Should().ContainSingle(game => game.AppId == 20u);
        games.Should().NotContain(game => game.AppId == 30u);
        cache.UpsertCount.Should().Be(upserts);
    }

    [Test]
    public async Task GetCachedAsync_UnknownAccountReturnsEmpty()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var catalog = new SteamOwnedGameCatalog(
            new SteamFeatureSnapshotStore(cache, time, NullLogger<SteamFeatureSnapshotStore>.Instance));

        var games = await catalog.GetCachedAsync("nobody");

        games.Should().BeEmpty();
    }

    [Test]
    public async Task GetCachedAsync_RejectsBlankAccountNames()
    {
        var catalog = new SteamOwnedGameCatalog(
            new SteamFeatureSnapshotStore(
                new MemoryCacheStore(),
                new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)),
                NullLogger<SteamFeatureSnapshotStore>.Instance));

        await ((Func<Task>)(() => catalog.GetCachedAsync("  ")))
            .Should().ThrowAsync<ArgumentException>();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class MemoryCacheStore : ISteamResourceCacheStore
    {
        private readonly Dictionary<SteamCacheKey, SteamResourceCacheEntry> _entries = [];
        public int GetCount { get; private set; }
        public int UpsertCount { get; private set; }

        public Task<SteamResourceCacheEntry?> GetAsync(
            SteamCacheKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCount++;
            return Task.FromResult(_entries.GetValueOrDefault(key));
        }

        public Task<IReadOnlyList<SteamResourceCacheEntry>> GetByResourceKindAsync(
            string resourceKind,
            int schemaVersion,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCount++;
            IReadOnlyList<SteamResourceCacheEntry> result = _entries.Values
                .Where(entry => entry.Key.ResourceKind == resourceKind && entry.Key.SchemaVersion == schemaVersion)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task UpsertAsync(
            SteamResourceCacheEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpsertCount++;
            _entries[entry.Key] = entry;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(SteamCacheKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.Remove(key);
            return Task.CompletedTask;
        }

        public Task<int> DeleteExpiredAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }
    }
}
