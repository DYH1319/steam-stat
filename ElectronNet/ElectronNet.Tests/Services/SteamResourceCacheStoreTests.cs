using ElectronNet.Features.SteamCache.Persistence;
using ElectronNet.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using CacheEntry = SteamStat.Core.Steam.Cache.SteamResourceCacheEntry;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class SteamResourceCacheStoreTests
{
    private string _tempDirectory = null!;
    private string _databaseFile = null!;
    private TestDbContextFactory _dbContextFactory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "steam-stat-cache-tests", Guid.NewGuid().ToString("N"));
        _databaseFile = Path.Combine(_tempDirectory, "steam-stat.db");
        Directory.CreateDirectory(_tempDirectory);
        _dbContextFactory = new TestDbContextFactory(_databaseFile);
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true);
    }

    [Test]
    public async Task Upsert_RoundTripsAcrossStoreAndContextRestart()
    {
        var key = SteamCacheKey.Create("app-metadata", "public", "730", "schinese");
        var original = Entry(key, "{\"Name\":\"Counter-Strike 2\"}", 1_700_000_000);
        await CreateStore(_dbContextFactory).UpsertAsync(original);
        SqliteConnection.ClearAllPools();

        var restartedFactory = new TestDbContextFactory(_databaseFile);
        var result = await CreateStore(restartedFactory).GetAsync(key);

        result.Should().BeEquivalentTo(original);
    }

    [Test]
    public async Task Upsert_DuplicateKeyUpdatesAtomicallyWithoutCreatingAnotherRow()
    {
        var key = SteamCacheKey.Create("app-metadata", "public", "730");
        var store = CreateStore(_dbContextFactory);
        await store.UpsertAsync(Entry(key, "{\"Name\":\"Old\"}", 1_700_000_000));
        await store.UpsertAsync(Entry(key, "{\"Name\":\"New\"}", 1_700_000_100));

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        (await db.SteamResourceCacheTable.CountAsync()).Should().Be(1);
        (await store.GetAsync(key))!.Payload.Should().Contain("New");
    }

    [Test]
    public async Task RejectedOversizedWritePreservesPreviousSuccessfulValue()
    {
        var key = SteamCacheKey.Create("app-metadata", "public", "730");
        var store = CreateStore(_dbContextFactory);
        var original = Entry(key, "{\"Name\":\"Old\"}", 1_700_000_000);
        await store.UpsertAsync(original);
        var oversized = Entry(key, new string('x', 1024 * 1024 + 1), 1_700_000_100);

        var action = () => store.UpsertAsync(oversized);

        await action.Should().ThrowAsync<ArgumentException>();
        (await store.GetAsync(key)).Should().BeEquivalentTo(original);
    }

    [Test]
    public async Task KeysKeepLanguageVariantAndSchemaIsolated()
    {
        var store = CreateStore(_dbContextFactory);
        var first = SteamCacheKey.Create("app-metadata", "public", "730", "english", "default", 1);
        var second = SteamCacheKey.Create("app-metadata", "public", "730", "schinese", "default", 2);
        await store.UpsertAsync(Entry(first, "{\"Name\":\"English\"}", 1_700_000_000));
        await store.UpsertAsync(Entry(second, "{\"Name\":\"简体中文\"}", 1_700_000_000));

        (await store.GetAsync(first))!.Payload.Should().Contain("English");
        (await store.GetAsync(second))!.Payload.Should().Contain("简体中文");
    }

    [Test]
    public async Task GetByResourceKind_IsSchemaFilteredAndBounded()
    {
        var store = CreateStore(_dbContextFactory);
        await store.UpsertAsync(Entry(
            SteamCacheKey.Create("library-snapshot", "76561198000000000", "snapshot"),
            "{\"AccountName\":\"alice\"}",
            1_700_000_000));
        await store.UpsertAsync(Entry(
            SteamCacheKey.Create("library-snapshot", "76561198000000001", "snapshot"),
            "{\"AccountName\":\"bob\"}",
            1_700_000_001));
        await store.UpsertAsync(Entry(
            SteamCacheKey.Create("friends-snapshot", "76561198000000000", "snapshot"),
            "{\"AccountName\":\"alice\"}",
            1_700_000_002));

        var entries = await store.GetByResourceKindAsync("library-snapshot", 1, 1);

        entries.Should().ContainSingle();
        entries[0].Key.ResourceKind.Should().Be("library-snapshot");
        entries[0].Key.ScopeId.Should().Be("76561198000000001");
    }

    [Test]
    public async Task DeleteExpired_IsBoundedAndKeepsRetainedEntries()
    {
        var store = CreateStore(_dbContextFactory);
        var old = SteamCacheKey.Create("app-metadata", "public", "1");
        var anotherOld = SteamCacheKey.Create("app-metadata", "public", "2");
        var retained = SteamCacheKey.Create("app-metadata", "public", "3");
        await store.UpsertAsync(Entry(old, "{}", 1_600_000_000));
        await store.UpsertAsync(Entry(anotherOld, "{}", 1_600_000_001));
        await store.UpsertAsync(Entry(retained, "{}", 1_700_000_000));

        (await store.DeleteExpiredAsync(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), 1)).Should().Be(1);

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        (await db.SteamResourceCacheTable.CountAsync()).Should().Be(2);
        (await store.GetAsync(retained)).Should().NotBeNull();
    }

    private static EfSteamResourceCacheStore CreateStore(IDbContextFactory<AppDbContext> factory)
        => new(factory, NullLogger<EfSteamResourceCacheStore>.Instance);

    private static CacheEntry Entry(SteamCacheKey key, string payload, long fetchedAt)
    {
        var fetched = DateTimeOffset.FromUnixTimeSeconds(fetchedAt);
        return new CacheEntry(
            key,
            "json-v1",
            payload,
            SteamDataSource.Http,
            fetched,
            fetched.AddDays(7),
            fetched.AddDays(180),
            fetched,
            "etag",
            "hash");
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile, pooling: false))
            .Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
