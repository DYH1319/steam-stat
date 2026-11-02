using ElectronNet.Models;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class ArchivePersistenceTests
{
    private SqliteConnection _connection = null!;
    private TestDbContextFactory _factory = null!;
    private AchievementArchiveService _achievements = null!;
    private LibraryArchiveService _library = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _achievements = new AchievementArchiveService(
            _factory, null!, new TestIdentityProvider(), null!, TimeProvider.System,
            NullLogger<AchievementArchiveService>.Instance);
        _library = new LibraryArchiveService(
            _factory, null!, null!, TimeProvider.System, NullLogger<LibraryArchiveService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _achievements.Dispose();
        _library.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task Capture_IsIdempotentAndRetainsUnlocksAfterSteamRelocksThem()
    {
        var definition = new SteamAchievementDefinition(
            1, "WIN", "First win", "", "", "", false, 1.5, null, false,
            SteamAchievementProgressType.None, null, null);
        var state = new SteamAchievementResourceState(SteamDataSource.Cm, SteamFreshness.Fresh,
            DateTimeOffset.UtcNow, null, null);
        var result = new SteamAchievementGameResult(
            570, "english", new SteamAchievementSchemaSnapshot(570, "english", 1, 1, [definition], []),
            [new SteamAchievementEntry(definition, true, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))],
            new SteamAchievementSummary(1, 1, 0, 100), state, state, []) { AppName = "Game" };

        await _achievements.CaptureAsync("alice", result);
        await _achievements.CaptureAsync("alice", result);
        await _achievements.CaptureAsync("alice", result with
        {
            Achievements = [],
            Summary = new SteamAchievementSummary(1, 0, 0, 0)
        });

        var rows = _achievements.GetUnlocks(new AchievementArchiveQueryRequest { AccountName = "alice" }).Items;
        rows.Should().ContainSingle().Which.UnlockTime.Should().Be(1_700_000_000);
        _achievements.GetUnlocks(new AchievementArchiveQueryRequest { AccountName = "bob" }).Items.Should().BeEmpty();
        _achievements.GetGames("alice").Should().ContainSingle().Which.SteamId.Should().Be("76561198000000001");
    }

    [Test]
    public async Task Targets_AreIdempotentAndAccountScoped()
    {
        var request = new AchievementTargetAddRequest
        {
            AccountName = "alice", AppId = 570, AppName = "Game",
            InternalName = "WIN", Name = "First win", Description = "", Icon = ""
        };
        var first = await _achievements.AddTargetAsync(request);
        var again = await _achievements.AddTargetAsync(request with { Note = "Next goal" });
        await _achievements.AddTargetAsync(request with { AccountName = "bob" });

        again.Id.Should().Be(first.Id);
        _achievements.GetTargets("alice").Should().ContainSingle().Which.Note.Should().Be("Next goal");
        await _achievements.RemoveTargetAsync(first.Id);
        _achievements.GetTargets("alice").Should().BeEmpty();
        _achievements.GetTargets("bob").Should().ContainSingle();
    }

    [Test]
    public async Task Metadata_UpsertsAndClearsOnlyTheRequestedAccount()
    {
        var request = new LibraryGameMetaUpdateRequest
        {
            AccountName = "alice",
            Items = [new LibraryGameMetaInput { AppId = 570, Price = 0, Currency = " usd ", Rating = 8 }]
        };
        await _library.UpdateGameMetaAsync(request);
        await _library.UpdateGameMetaAsync(request);
        await _library.UpdateGameMetaAsync(request with { AccountName = "bob" });

        var row = _library.GetGameMeta("alice").Should().ContainSingle().Subject;
        row.Price.Should().Be(0);
        row.Currency.Should().Be("USD");
        row.Rating.Should().Be(8);
        await _library.UpdateGameMetaAsync(request with { Items = [new LibraryGameMetaInput { AppId = 570 }] });
        _library.GetGameMeta("alice").Should().BeEmpty();
        _library.GetGameMeta("bob").Should().ContainSingle();
    }

    [Test]
    public void Acquisitions_PreferEarliestLicenseAndOtherwiseKeepObservedFallback()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.LibraryAcquisitionTable.AddRange(
                new LibraryAcquisition { AccountName = "alice", AppId = 10, PackageId = 0, AcquiredAt = 500 },
                new LibraryAcquisition { AccountName = "alice", AppId = 10, PackageId = 1, AcquiredAt = 200 },
                new LibraryAcquisition { AccountName = "alice", AppId = 10, PackageId = 2, AcquiredAt = 100 },
                new LibraryAcquisition { AccountName = "alice", AppId = 20, PackageId = 0, AcquiredAt = 600 },
                new LibraryAcquisition { AccountName = "bob", AppId = 30, PackageId = 1, AcquiredAt = 50 });
            db.SaveChanges();
        }
        var page = _library.GetAcquisitions(new LibraryAcquisitionPageRequest
        {
            AccountName = "alice", Offset = 0, Limit = 1
        });
        page.Total.Should().Be(2);
        page.NextOffset.Should().Be(1);
        page.Items.Should().ContainSingle().Which.AcquiredAt.Should().Be(100);
        page.Items[0].Source.Should().Be("license");
        var next = _library.GetAcquisitions(new LibraryAcquisitionPageRequest
        {
            AccountName = "alice", Offset = 1, Limit = 1
        });
        next.Items.Should().ContainSingle().Which.Source.Should().Be("observed");
        next.NextOffset.Should().BeNull();
    }

    private sealed class TestIdentityProvider : ISteamAccountIdentityProvider
    {
        public bool TryGetSteamId(string accountName, out ulong steamId)
        {
            steamId = 76561198000000001;
            return true;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
