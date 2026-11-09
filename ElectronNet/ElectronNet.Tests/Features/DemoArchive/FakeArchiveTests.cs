using System.Diagnostics;
using ElectronNet;
using ElectronNet.Features.DemoArchive;
using ElectronNet.Features.Semantic;
using ElectronNet.Persistence;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Features.DemoArchive;

[TestFixture]
public class FakeArchiveTests
{
    private static readonly FakeArchiveProfile Small = FakeArchiveProfile.Demo with { Sessions = 400, FriendRecords = 200, Games = 12, Friends = 3 };
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "steam-stat-fake-tests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private async Task<string> GenerateAsync(string name = "a", FakeArchiveProfile? profile = null)
    {
        var directory = Path.Combine(_directory, name);
        await FakeArchiveGenerator.GenerateAsync(directory, profile ?? Small);
        return directory;
    }

    private static async Task<SemanticQueryResult> QueryAsync(string directory, string sql)
    {
        var database = Path.Combine(directory, "Database", "steam-stat.db");
        await using (var connection = new SqliteConnection(SqliteConnectionStrings.Create(database, pooling: false)))
        {
            await connection.OpenAsync();
            await SemanticSchema.InstallAsync(connection, new AppSettings());
        }
        return await SemanticQueryGateway.ExecuteAsync(database, sql, SemanticTier.All.ToHashSet(), maxRows: 100_000);
    }

    [Test]
    public async Task SameProfile_ProducesTheSameArchive()
    {
        const string sql = "SELECT app_id, start_time, duration_seconds, idle_seconds FROM v_session ORDER BY start_time, app_id";

        var first = await QueryAsync(await GenerateAsync("a"), sql);
        var second = await QueryAsync(await GenerateAsync("b"), sql);

        first.Rows.Should().NotBeEmpty();
        first.Rows.Should().BeEquivalentTo(second.Rows, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task Archive_HasTheRequestedScaleAndFillsEveryView()
    {
        var directory = await GenerateAsync();

        (await QueryAsync(directory, "SELECT COUNT(*) FROM v_session")).Rows[0][0].Should().Be((long)Small.Sessions);
        foreach (var view in SemanticSchema.Views.Where(view => view.Name != "v_user_name"))
        {
            var result = await QueryAsync(directory, $"SELECT COUNT(*) FROM {view.Name}");
            result.Failure.Should().Be(SemanticQueryFailure.None, view.Name);
            ((long)result.Rows[0][0]!).Should().BeGreaterThan(0, $"{view.Name} should have data");
        }
    }

    [Test]
    public async Task Archive_HasThePatternsTheChartsDependOn()
    {
        var directory = await GenerateAsync(profile: FakeArchiveProfile.Demo);

        var top = await QueryAsync(directory, "SELECT SUM(seconds) FROM v_app_playtime GROUP BY app_id ORDER BY 1 DESC");
        var total = top.Rows.Sum(row => (long)row[0]!);
        ((long)top.Rows[0][0]! * 100 / total).Should().BeGreaterThan(10, "时长应该集中在少数游戏");

        var idle = await QueryAsync(directory, "SELECT SUM(idle_seconds) * 1.0 / SUM(duration_seconds) FROM v_session");
        ((double)idle.Rows[0][0]!).Should().BeInRange(0.01, 0.3);
    }

    [Test]
    public async Task Archive_ContainsOnlyFictionalAppIds()
    {
        var directory = await GenerateAsync();

        (await QueryAsync(directory, "SELECT MIN(app_id) FROM v_app")).Rows[0][0].Should().Be(9_000_000L);
    }

    [Test]
    public async Task Settings_AreReadByTheRealSettingsStore()
    {
        var paths = new AppPaths(await GenerateAsync());

        var settings = new JsonSettingsStore(paths, new AppSettingsFactory(new AppEnvironment(false, "zh-CN", false, paths)), NullLogger<JsonSettingsStore>.Instance).GetSettings();

        settings.OnboardingCompleted.Should().BeTrue();
        settings.AutoUpdate.Should().BeFalse();
        settings.Analytics!.RendererMigrated.Should().BeTrue();
    }

    [Test]
    public async Task ExistingArchive_IsNeverOverwritten()
    {
        var directory = await GenerateAsync();

        var again = async () => await FakeArchiveGenerator.GenerateAsync(directory, Small);

        await again.Should().ThrowAsync<InvalidOperationException>();
    }
}

/// <summary>
/// 十年数据压测：10 年 / 5 万会话 / 100 万好友记录。手动运行：
/// <c>dotnet test --filter Category=Stress</c>。结果写到 STEAM_STAT_STRESS_REPORT 指定的文件。
/// </summary>
[TestFixture, Explicit, Category("Stress")]
public class DecadeStressTests
{
    [Test]
    public async Task MeasureTheCeiling()
    {
        var directory = Path.Combine(Path.GetTempPath(), "steam-stat-stress", Guid.NewGuid().ToString("N"));
        var lines = new List<string>();
        async Task<T> Measure<T>(string name, Func<Task<T>> action)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await action();
            lines.Add($"{name}\t{stopwatch.ElapsedMilliseconds}");
            TestContext.Progress.WriteLine($"{name}: {stopwatch.ElapsedMilliseconds} ms");
            return result;
        }

        try
        {
            await Measure("generate", () => FakeArchiveGenerator.GenerateAsync(directory, FakeArchiveProfile.Decade));
            var paths = new AppPaths(directory);
            var size = new FileInfo(paths.DatabaseFile).Length;
            lines.Add($"database_bytes\t{size}");

            var settings = new JsonSettingsStore(paths, new AppSettingsFactory(new AppEnvironment(false, "zh-CN", false, paths)), NullLogger<JsonSettingsStore>.Instance);
            var semantic = new SemanticLayerService(paths, settings, NullLogger<SemanticLayerService>.Instance);
            await Measure("install_views", async () => { await semantic.InstallAsync(); return 0; });

            foreach (var (name, sql) in new[]
            {
                ("v_session_count", "SELECT COUNT(*) FROM v_session"),
                ("v_session_day_sum", "SELECT SUM(seconds) FROM v_session_day"),
                ("v_daily_playtime_all", "SELECT * FROM v_daily_playtime"),
                ("v_app_playtime_all", "SELECT * FROM v_app_playtime"),
                ("v_friend_event_count", "SELECT COUNT(*) FROM v_friend_event"),
                ("v_friend_event_year", "SELECT change_type, COUNT(*) FROM v_friend_event WHERE logical_day BETWEEN '2025-01-01' AND '2025-12-31' GROUP BY change_type")
            })
            {
                var result = await Measure(name, () => SemanticQueryGateway.ExecuteAsync(paths.DatabaseFile, sql, SemanticTier.All.ToHashSet(), maxRows: 2_000_000, timeout: TimeSpan.FromMinutes(5)));
                result.Failure.Should().Be(SemanticQueryFailure.None, name);
            }

            await Measure("catalog_with_all_metrics", () => semantic.GetCatalogAsync());

            var factory = new Factory(paths.DatabaseFile);
            var records = new FriendStatusRecordService(factory, TimeProvider.System, NullLogger<FriendStatusRecordService>.Instance);
            await Measure("friend_records_all_pages", () =>
            {
                long? before = null;
                var total = 0;
                while (true)
                {
                    var page = records.GetRecords(new FriendStatusRecordsQueryRequest { BeforeId = (int?)before, Limit = 1000 });
                    if (page.Count == 0) break;
                    total += page.Count;
                    before = page[^1].Id;
                }
                return Task.FromResult(total);
            });

            await Measure("integrity_check", () => DatabaseSnapshotter.IsHealthyAsync(paths.DatabaseFile));
            await Measure("snapshot", async () =>
            {
                await new DatabaseSnapshotter(factory, paths, settings, NullLogger<DatabaseSnapshotter>.Instance).SnapshotAsync();
                return 0;
            });

            var report = string.Join('\n', lines);
            TestContext.Progress.WriteLine(report);
            if (Environment.GetEnvironmentVariable("STEAM_STAT_STRESS_REPORT") is { Length: > 0 } file) await File.WriteAllTextAsync(file, report);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class Factory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(SqliteConnectionStrings.Create(databaseFile, pooling: false)).Options);
    }
}
