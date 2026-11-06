using ElectronNet.Models;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class AchievementSchemaWatcherTests
{
    private const uint AppId = 570;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private SqliteConnection _connection = null!;
    private TestDbContextFactory _factory = null!;
    private FakeSchemaGateway _schema = null!;
    private FakeProgressGateway _progress = null!;
    private FakeSessions _sessions = null!;
    private AchievementArchiveService _archive = null!;
    private AchievementSchemaWatcher _watcher = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _schema = new FakeSchemaGateway();
        _progress = new FakeProgressGateway();
        _sessions = new FakeSessions();
        var catalog = new FakeCatalog();
        var language = new FakeLanguage();
        var achievements = new SteamAchievementsService(
            new SteamAchievementOverviewQuery(catalog, _progress), catalog, _schema, _progress, language);
        _archive = new AchievementArchiveService(
            _factory, achievements, new FakeIdentity(), null!, TimeProvider.System, NullLogger<AchievementArchiveService>.Instance);
        _watcher = new AchievementSchemaWatcher(
            achievements, _schema, _archive, language, _sessions, TimeProvider.System, NullLogger<AchievementSchemaWatcher>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _watcher.Dispose();
        _archive.Dispose();
        _connection.Dispose();
    }

    private static SteamAchievementDefinition Definition(string name, bool archived = false)
        => new(null, name, name, "", "", "", false, null, 0, archived, SteamAchievementProgressType.None, null, null);

    private static SteamAchievementSchemaSnapshot Schema(uint hash, params SteamAchievementDefinition[] definitions)
        => new(AppId, "english", 1, hash, definitions, []);

    [Test]
    public async Task ASchemaChangeIsRecordedWithoutTheUserOpeningTheGame()
    {
        _schema.Current = Schema(1, Definition("A"));
        await _watcher.RunPassAsync("alice", CancellationToken.None);

        _schema.Current = Schema(2, Definition("A", archived: true));
        await _watcher.RunPassAsync("alice", CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        (await db.AchievementSchemaChangeTable.SingleAsync()).Kind.Should().Be("archived");
        (await db.AchievementUnlockArchiveTable.SingleAsync()).Archived.Should().BeTrue();
    }

    [Test]
    public async Task RowsWrittenBeforeSetsAreRecountedOnce()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.AchievementGameArchiveTable.Add(new AchievementGameArchive { AccountName = "alice", SteamId = "1", AppId = AppId, AppName = "Game", Total = 198, Unlocked = 1 });
            await db.SaveChangesAsync();
        }
        _schema.Current = Schema(1, Definition("A"));

        await _watcher.RunPassAsync("alice", CancellationToken.None);

        await using var check = _factory.CreateDbContext();
        var game = await check.AchievementGameArchiveTable.SingleAsync();
        (game.Total, game.AllTotal).Should().Be((1, 1));
        _archive.GetAppsNeedingRecount("alice").Should().BeEmpty();
    }

    [Test]
    public async Task PublicDataIsNeverComparedAgainstTheBaseline()
    {
        _schema.Current = Schema(1, Definition("A"));
        await _watcher.RunPassAsync("alice", CancellationToken.None);

        _schema.Current = Schema(2, Definition("A"), Definition("B"));
        _schema.Source = SteamDataSource.PublicData;
        await _watcher.RunPassAsync("alice", CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        (await db.AchievementSchemaChangeTable.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task StopsWhenTheSessionEnds()
    {
        _sessions.Connected = false;

        await _watcher.RunPassAsync("alice", CancellationToken.None);

        _schema.Calls.Should().Be(0);
    }

    private sealed class FakeSchemaGateway : ISteamAchievementSchemaGateway
    {
        public SteamAchievementSchemaSnapshot Current { get; set; } = null!;
        public SteamDataSource Source { get; set; } = SteamDataSource.Sqlite;
        public int Calls { get; private set; }

        public Task<SteamGatewayResult<SteamAchievementSchemaSnapshot>> GetSchemaAsync(
            uint appId, string language, string? preferredAccountName = null,
            SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
                Current, Source, SteamFreshness.Fresh, Now, Now));
        }
    }

    private sealed class FakeProgressGateway : ISteamAchievementProgressGateway
    {
        public Task<SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>> GetSummariesAsync(
            string accountName, IReadOnlyList<uint> appIds, SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
            CancellationToken cancellationToken = default)
            => Task.FromResult(SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>.Succeeded(
                [new SteamAchievementAppProgressSnapshot(AppId, 1, 1, 100)], SteamDataSource.Sqlite, SteamFreshness.Fresh, Now, Now));

        public Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetUnlocksAsync(
            string accountName, uint appId, SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
            CancellationToken cancellationToken = default)
            => Task.FromResult(SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(1, appId, 1, [new SteamAchievementUnlock(null, "A", true, Now)]),
                SteamDataSource.Sqlite, SteamFreshness.Fresh, Now, Now));
    }

    private sealed class FakeCatalog : IOwnedGameCatalog
    {
        public Task<IReadOnlyList<OwnedGameCatalogItem>> GetCachedAsync(string accountName, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnedGameCatalogItem>>([new OwnedGameCatalogItem(AppId, "Game", "Game", 60, 1)]);
    }

    private sealed class FakeLanguage : ILanguageProvider
    {
        public string GetSteamLanguage() => "english";
    }

    private sealed class FakeIdentity : ISteamAccountIdentityProvider
    {
        public bool TryGetSteamId(string accountName, out ulong steamId)
        {
            steamId = 76561198000000001;
            return true;
        }
    }

    private sealed class FakeSessions : ISteamSessionAccessor
    {
        public bool Connected { get; set; } = true;
        public IReadOnlyList<string> GetLoggedInUsers() => ["alice"];

        public bool TryGetSession(string accountName, out ISteamSession session)
        {
            session = null!;
            return Connected;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
