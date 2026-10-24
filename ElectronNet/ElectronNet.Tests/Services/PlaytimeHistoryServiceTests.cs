using ElectronNet;
using ElectronNet.Features.History;
using ElectronNet.Models;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Platform;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class PlaytimeHistoryServiceTests
{
    private string _tempDir = null!;
    private string _databaseFile = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private PlaytimeHistoryService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _databaseFile = Path.Combine(_tempDir, "test.db");
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Persistence.SqliteConnectionStrings.Create(_databaseFile))
            .Options;
        using var context = new AppDbContext(_options);
        context.Database.Migrate();
        _service = new PlaytimeHistoryService(
            new TestDbContextFactory(_options),
            new StubSteamInstallLocator(),
            null!,
            null!,
            TimeProvider.System,
            NullLogger<PlaytimeHistoryService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private List<PlaytimeSnapshot> GetSnapshots()
    {
        using var context = new AppDbContext(_options);
        return context.PlaytimeSnapshotTable.AsNoTracking()
            .OrderBy(s => s.CapturedAt).ThenBy(s => s.Id)
            .ToList();
    }

    [Test]
    public async Task ApplyObservations_FirstRowBecomesBaseline()
    {
        var result = await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 120, 1700000000, 1000)], "localconfig");

        result.Inserted.Should().Be(1);
        result.BaselinesCreated.Should().Be(1);
        var rows = GetSnapshots();
        rows.Should().ContainSingle();
        rows[0].IsBaseline.Should().BeTrue();
        rows[0].PlaytimeMinutes.Should().Be(120);
        rows[0].CapturedAt.Should().Be(1000);
        rows[0].LastSeenAt.Should().Be(1000);
        rows[0].LastPlayed.Should().Be(1700000000);
        rows[0].Source.Should().Be("localconfig");
    }

    [Test]
    public async Task ApplyObservations_SameValueUpdatesLastSeenOnly()
    {
        await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 120, null, 1000)], "localconfig");

        var result = await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 120, 1700000000, 2000)], "localconfig");

        result.Updated.Should().Be(1);
        result.Inserted.Should().Be(0);
        var rows = GetSnapshots();
        rows.Should().ContainSingle();
        rows[0].CapturedAt.Should().Be(1000);
        rows[0].LastSeenAt.Should().Be(2000);
        rows[0].LastPlayed.Should().Be(1700000000);
        rows[0].IsBaseline.Should().BeTrue();
    }

    [Test]
    public async Task ApplyObservations_ChangedValueInsertsNewRow()
    {
        await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 120, null, 1000)], "localconfig");

        var result = await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 180, null, 2000)], "localconfig");

        result.Inserted.Should().Be(1);
        result.BaselinesCreated.Should().Be(0);
        var rows = GetSnapshots();
        rows.Should().HaveCount(2);
        rows[0].PlaytimeMinutes.Should().Be(120);
        rows[0].IsBaseline.Should().BeTrue();
        rows[1].PlaytimeMinutes.Should().Be(180);
        rows[1].IsBaseline.Should().BeFalse();
        rows[1].CapturedAt.Should().Be(2000);
    }

    [Test]
    public async Task ApplyObservations_StaleObservationIsIgnored()
    {
        await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 120, null, 2000)], "localconfig");
        // 值变化后 CapturedAt=3000/LastSeenAt=3000
        await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 180, null, 3000)], "localconfig");

        // 同一值、观测时间 ≤ LastSeenAt → stale
        var stale = await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 180, null, 3000)], "localconfig");
        stale.Stale.Should().Be(1);
        stale.Updated.Should().Be(0);
        stale.Inserted.Should().Be(0);

        // 不同值但观测时间仍 ≤ LastSeenAt → 同样 stale
        var staleChanged = await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 200, null, 2500)], "localconfig");
        staleChanged.Stale.Should().Be(1);

        GetSnapshots().Should().HaveCount(2);
    }

    [Test]
    public async Task ApplyObservations_UsesObservedAtForTimestamps()
    {
        // 模拟 localconfig 场景：文件修改时间即为观测时间
        var result = await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 120, null, 1500)], "localconfig");
        var row = GetSnapshots()[0];
        row.CapturedAt.Should().Be(1500);
        row.LastSeenAt.Should().Be(1500);

        // 同一值、更新的观测时间 → LastSeenAt 跟随 ObservedAt
        var updated = await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 120, null, 2500)], "localconfig");
        updated.Updated.Should().Be(1);
        row = GetSnapshots()[0];
        row.CapturedAt.Should().Be(1500);
        row.LastSeenAt.Should().Be(2500);
    }

    [Test]
    public async Task ApplyObservations_SkipsPairsWithOpenSession()
    {
        using (var context = new AppDbContext(_options))
        {
            context.UseAppRecordTable.Add(new UseAppRecord
            {
                AppId = 570,
                SteamId = "s1",
                StartTime = 500,
                EndTime = null,
                Source = "auto"
            });
            context.SaveChanges();
        }

        var result = await _service.ApplyObservationsAsync(
            [
                new PlaytimeObservation("s1", 570, 120, null, 1000),
                new PlaytimeObservation("s1", 730, 60, null, 1000)
            ],
            "localconfig");

        result.SkippedRunning.Should().Be(1);
        result.Inserted.Should().Be(1);
        GetSnapshots().Should().ContainSingle().Which.AppId.Should().Be(730);
    }

    [Test]
    public async Task ResetBaseline_MovesBaselineToLatestRow()
    {
        await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 120, null, 1000)], "localconfig");
        await _service.ApplyObservationsAsync(
            [new PlaytimeObservation("s1", 570, 180, null, 2000)], "localconfig");

        var count = await _service.ResetBaselineAsync(new SteamStat.Contracts.Ipc.PlaytimeBaselineResetRequest
        {
            SteamId = "s1",
            AppIds = [570]
        });

        count.Should().Be(1);
        var rows = GetSnapshots();
        rows[0].IsBaseline.Should().BeFalse();
        rows[1].IsBaseline.Should().BeTrue();
    }

    private sealed class StubSteamInstallLocator : ISteamInstallLocator
    {
        public SteamRegistrySnapshot ReadSteamRegistry() => new();
        public SteamActiveProcessSnapshot ReadActiveProcess() => new(0, 0, "", "", "");
        public IReadOnlyDictionary<int, SteamAppRegistrySnapshot> ReadAppRegistry() => new Dictionary<int, SteamAppRegistrySnapshot>();
        public bool HasRegistryKey(SteamRegistryKey key) => false;
        public void SetAutoLoginUser(string accountName, bool rememberPassword) { }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(options));
    }
}

