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

    private static SteamAchievementDefinition Definition(string name, uint group = 0, bool archived = false, string displayName = "")
        => new(null, name, displayName.Length > 0 ? displayName : name, "", "", "", false, null, group, archived,
            SteamAchievementProgressType.None, null, null);

    private static SteamAchievementSchemaSnapshot Schema(
        uint hash,
        string language,
        SteamAchievementGroup[] groups,
        params SteamAchievementDefinition[] definitions)
        => new(570, language, 1, hash, definitions, groups);

    private static SteamGatewayResult<SteamAchievementSchemaSnapshot> Success(SteamAchievementSchemaSnapshot schema)
        => SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
            schema, SteamDataSource.Cm, SteamFreshness.Fresh, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static SteamAchievementGameResult Compose(SteamAchievementSchemaSnapshot schema, params (string Name, long Time)[] unlocks)
        => SteamAchievementMerge.Compose(
            570, schema.Language, Success(schema),
            SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(1, 570, 1,
                    unlocks.Select(item => new SteamAchievementUnlock(null, item.Name, true, DateTimeOffset.FromUnixTimeSeconds(item.Time))).ToArray()),
                SteamDataSource.Cm, SteamFreshness.Fresh, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
            with { AppName = "Game" };

    private static readonly SteamAchievementGroup[] BaseAndHalloween =
    [
        new(0, "", null, false, false, true, 0),
        new(1, "Halloween", null, false, false, true, 0),
        new(2, "Replay", null, false, false, true, 0)
    ];

    [Test]
    public async Task Capture_RecordsSetsAndKeepsACompletedSetCompleteAfterAchievementsAreAdded()
    {
        var first = Schema(10, "english", BaseAndHalloween, Definition("A"), Definition("H1", 1));
        var done = await _achievements.CaptureAsync("alice", Compose(first, ("H1", 1_700_000_000)));
        done.Sets.Single(set => set.GroupId == 1).Completed.Should().BeTrue();

        var second = Schema(11, "english", BaseAndHalloween, Definition("A"), Definition("H1", 1), Definition("H2", 1));
        var later = await _achievements.CaptureAsync("alice", Compose(second, ("H1", 1_700_000_000)));

        var halloween = later.Sets.Single(set => set.GroupId == 1);
        halloween.Unlocked.Should().Be(1);
        halloween.Total.Should().Be(2);
        halloween.Completed.Should().BeTrue();
        halloween.CompletedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

        var rows = _achievements.GetSets(new AchievementArchiveQueryRequest { AccountName = "alice" }).Items;
        rows.Should().HaveCount(2);
        rows.Single(row => row.GroupId == 1).CompletedAt.Should().Be(1_700_000_000);
        _achievements.GetGames("alice").Single().AllTotal.Should().Be(3);
    }

    [Test]
    public async Task ObserveSchema_FirstSightIsABaselineThenEveryChangeIsRecorded()
    {
        var baseline = Schema(1, "english", BaseAndHalloween,
            Definition("KEEP"), Definition("RETIRE"), Definition("MOVE"), Definition("RENAME", displayName: "Old name"), Definition("GONE"));
        (await _achievements.ObserveSchemaAsync(baseline)).Should().BeFalse();

        var next = Schema(2, "english", BaseAndHalloween,
            Definition("KEEP"), Definition("RETIRE", archived: true), Definition("MOVE", 1),
            Definition("RENAME", displayName: "New name"), Definition("NEW", 2));
        (await _achievements.ObserveSchemaAsync(next)).Should().BeTrue();

        var changes = _factory.CreateDbContext().AchievementSchemaChangeTable.ToList();
        changes.Select(change => (change.InternalName, change.Kind)).Should().BeEquivalentTo(new[]
        {
            ("RETIRE", "archived"), ("MOVE", "moved"), ("RENAME", "renamed"), ("NEW", "added"), ("GONE", "removed")
        });
        var moved = changes.Single(change => change.Kind == "moved");
        moved.OldValue.Should().Be("");
        moved.NewValue.Should().Be("Halloween");
        var renamed = changes.Single(change => change.Kind == "renamed");
        (renamed.OldValue, renamed.NewValue).Should().Be(("Old name", "New name"));
        changes.Should().OnlyContain(change => change.SchemaHash == 2);
    }

    [Test]
    public async Task ObserveSchema_SwitchingLanguageIsNotARename()
    {
        await _achievements.ObserveSchemaAsync(Schema(1, "english", BaseAndHalloween, Definition("A", displayName: "Win")));

        (await _achievements.ObserveSchemaAsync(Schema(2, "schinese", BaseAndHalloween, Definition("A", displayName: "胜利"))))
            .Should().BeFalse();

        _factory.CreateDbContext().AchievementSchemaChangeTable.Should().BeEmpty();
    }

    [Test]
    public async Task ObserveSchema_SameHashDoesNothing()
    {
        await _achievements.ObserveSchemaAsync(Schema(1, "english", BaseAndHalloween, Definition("A")));

        (await _achievements.ObserveSchemaAsync(Schema(1, "english", BaseAndHalloween, Definition("A", archived: true))))
            .Should().BeFalse();
    }

    [Test]
    public async Task ObserveSchema_ArchivingMarksUnlocksOfEveryAccountWithoutANewSync()
    {
        var before = Schema(1, "english", BaseAndHalloween, Definition("LIMITED", 1));
        await _achievements.CaptureAsync("alice", Compose(before, ("LIMITED", 1_700_000_000)));
        _achievements.GetUnlocks(new AchievementArchiveQueryRequest { AccountName = "alice" }).Items.Single().Archived.Should().BeFalse();

        await _achievements.ObserveSchemaAsync(Schema(2, "english", BaseAndHalloween, Definition("LIMITED", 1, archived: true)));

        var unlock = _achievements.GetUnlocks(new AchievementArchiveQueryRequest { AccountName = "alice" }).Items.Single();
        unlock.Archived.Should().BeTrue();
        unlock.GroupId.Should().Be(1);
    }

    [Test]
    public async Task GetChanges_OnlyListsGamesOfTheAccount()
    {
        var before = Schema(1, "english", BaseAndHalloween, Definition("A"));
        await _achievements.CaptureAsync("alice", Compose(before, ("A", 1_700_000_000)));
        await _achievements.ObserveSchemaAsync(Schema(2, "english", BaseAndHalloween, Definition("A"), Definition("B")));

        _achievements.GetChanges(new AchievementArchiveQueryRequest { AccountName = "alice" }).Items
            .Should().ContainSingle().Which.Kind.Should().Be("added");
        _achievements.GetChanges(new AchievementArchiveQueryRequest { AccountName = "bob" }).Items.Should().BeEmpty();
    }

    [Test]
    public async Task GetAppsNeedingRecount_ListsRowsWrittenBeforeSets()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.AchievementGameArchiveTable.Add(new AchievementGameArchive { AccountName = "alice", SteamId = "1", AppId = 570, AppName = "Old", Total = 198, Unlocked = 56 });
            await db.SaveChangesAsync();
        }
        _achievements.GetAppsNeedingRecount("alice").Should().BeEquivalentTo([570u]);

        await _achievements.CaptureAsync("alice", Compose(Schema(1, "english", BaseAndHalloween, Definition("A")), ("A", 1_700_000_000)));

        _achievements.GetAppsNeedingRecount("alice").Should().BeEmpty();
    }

    [Test]
    public async Task Ledger_MergesAccountsAndPagesByApp()
    {
        var schema = Schema(1, "english", BaseAndHalloween, Definition("A"), Definition("B"), Definition("H1", 1));
        await _achievements.CaptureAsync("alice", Compose(schema, ("A", 1_700_000_100)));
        await _achievements.CaptureAsync("bob", Compose(schema, ("A", 1_700_000_000), ("B", 1_700_000_050), ("H1", 1_700_000_200)));
        await using (var db = _factory.CreateDbContext())
        {
            db.AchievementGameArchiveTable.Add(new AchievementGameArchive { AccountName = "alice", SteamId = "1", AppId = 10, AppName = "Other", Total = 4, Unlocked = 1 });
            db.AchievementGameArchiveTable.Add(new AchievementGameArchive { AccountName = "carol", SteamId = "3", AppId = 20, AppName = "Not mine", Total = 4, Unlocked = 4 });
            await db.SaveChangesAsync();
        }

        var first = _achievements.GetLedger(new AchievementLedgerRequest { AccountNames = ["alice", "bob"], Limit = 1 });
        first.Items.Should().ContainSingle().Which.AppId.Should().Be(10);
        first.NextOffset.Should().Be(1);

        var second = _achievements.GetLedger(new AchievementLedgerRequest { AccountNames = ["alice", "bob"], Offset = 1, Limit = 1 });
        second.NextOffset.Should().BeNull("carol 不在范围里，她的游戏不能出现");
        var game = second.Items.Should().ContainSingle().Subject;
        game.AppId.Should().Be(570);
        (game.Unlocked, game.Total).Should().Be((3, 3));
        game.Completed.Should().BeTrue();
        game.Synthetic.Should().BeFalse("bob 自己就全成就了");
        game.Accounts.Select(account => (account.AccountName, account.Completed)).Should().Equal(("alice", false), ("bob", true));
        game.Sets.Should().HaveCount(2);

        _achievements.GetLedger(new AchievementLedgerRequest { AccountNames = ["alice", "bob"], AppIds = [570] }).Items
            .Should().ContainSingle().Which.AppId.Should().Be(570);
        _achievements.GetLedger(new AchievementLedgerRequest { AccountNames = [] }).Items.Should().BeEmpty();
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
