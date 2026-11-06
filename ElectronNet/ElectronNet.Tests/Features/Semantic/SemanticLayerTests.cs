using System.Globalization;
using ElectronNet;
using ElectronNet.Features.Semantic;
using ElectronNet.Features.UsageTracking;
using ElectronNet.Models;
using ElectronNet.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Features.Semantic;

[TestFixture]
public class SemanticLayerTests
{
    private const string SteamId = "76561198000000001";
    private string _directory = null!;
    private string _databaseFile = null!;

    [SetUp]
    public async Task SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "steam-stat-semantic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databaseFile = Path.Combine(_directory, "steam-stat.db");
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        db.SteamUserTable.Add(new SteamUser { SteamId = SteamId, AccountName = "secret-login", PersonaName = "Tester" });
        db.SteamLoginTokenTable.Add(new SteamLoginToken { AccountName = "secret-login", RefreshToken = "TOKEN-VALUE", CreatedAt = 1 });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private AppDbContext CreateContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(SqliteConnectionStrings.Create(_databaseFile, pooling: false)).Options);

    private async Task InstallAsync(AppSettings? settings = null)
    {
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(_databaseFile, pooling: false));
        await connection.OpenAsync();
        await SemanticSchema.InstallAsync(connection, settings ?? new AppSettings());
    }

    private async Task AddRecordsAsync(params UseAppRecord[] records)
    {
        await using var db = CreateContext();
        db.UseAppRecordTable.AddRange(records);
        await db.SaveChangesAsync();
    }

    private static UseAppRecord Record(int appId, int start, int duration, long idle = 0, string steamId = SteamId) => new()
    {
        AppId = appId,
        SteamId = steamId,
        StartTime = start,
        EndTime = start + duration,
        Duration = duration,
        IdleDuration = idle
    };

    private Task<SemanticQueryResult> QueryAsync(string sql, params string[] tiers)
        => SemanticQueryGateway.ExecuteAsync(_databaseFile, sql, (tiers.Length == 0 ? SemanticTier.All : tiers).ToHashSet());

    private async Task<List<object?[]>> RowsAsync(string sql)
    {
        var result = await QueryAsync(sql);
        result.Failure.Should().Be(SemanticQueryFailure.None, sql);
        return result.Rows.ToList();
    }

    #region 与现有计算路径一致

    [TestCase(0, 0)]
    [TestCase(0, 120)]
    [TestCase(300, 0)]
    [TestCase(300, 900)]
    public async Task Session_MatchesSessionCleanerOnRandomData(int minDuration, int mergeGap)
    {
        var random = new Random(minDuration * 31 + mergeGap);
        var records = new List<UseAppRecord>();
        var cursor = 1_700_000_000;
        for (var index = 0; index < 600; index++)
        {
            cursor += random.Next(1, 2000);
            var duration = random.Next(1, 1500);
            records.Add(Record(random.Next(1, 6), cursor, duration, random.Next(0, duration + 200), index % 7 == 0 ? "76561198000000002" : SteamId));
            if (index % 3 == 0) cursor += duration;
        }
        await AddRecordsAsync(records.ToArray());
        await InstallAsync(new AppSettings
        {
            SessionTracking = new SessionTrackingSettings { MinDurationSeconds = minDuration, MergeGapSeconds = mergeGap, ExcludedAppIds = [3] }
        });

        var expected = SessionCleaner.Apply(
                records.Select(record => new UseAppRecordDto
                {
                    AppId = record.AppId,
                    SteamId = record.SteamId,
                    StartTime = record.StartTime,
                    EndTime = record.EndTime!.Value,
                    Duration = record.Duration!.Value,
                    IdleDuration = (int)record.IdleDuration
                }),
                new SessionCleaningRules(minDuration, mergeGap, new HashSet<int> { 3 }))
            .OrderBy(session => session.SteamId, StringComparer.Ordinal).ThenBy(session => session.AppId).ThenBy(session => session.StartTime)
            .Select(session => (session.SteamId, session.AppId, session.StartTime, session.EndTime, session.Duration, Math.Clamp(session.IdleDuration, 0, session.Duration)))
            .ToList();

        var actual = (await SemanticQueryGateway.ExecuteAsync(
                _databaseFile,
                "SELECT steam_id, app_id, start_time, end_time, duration_seconds, idle_seconds FROM v_session ORDER BY steam_id, app_id, start_time",
                SemanticTier.All.ToHashSet(), maxRows: 10_000)).Rows
            .Select(row => ((string)row[0]!, (int)(long)row[1]!, (int)(long)row[2]!, (int)(long)row[3]!, (int)(long)row[4]!, (int)(long)row[5]!))
            .ToList();

        actual.Should().Equal(expected);
    }

    [Test]
    public async Task SessionDay_SplitsAtTheBoundaryLikeTheRenderer()
    {
        // 与渲染层 splitByDay 相同的切分方式，按本机时区计算
        var boundaryHour = 4;
        var localStart = new DateTime(2026, 3, 10, 2, 30, 0, DateTimeKind.Local);
        var start = (int)new DateTimeOffset(localStart).ToUnixTimeSeconds();
        var duration = 30 * 3600;
        await AddRecordsAsync(Record(10, start, duration, duration / 2));
        await InstallAsync(new AppSettings { Analytics = new AnalyticsSettings { DayBoundaryHour = boundaryHour } });

        var rows = await RowsAsync("SELECT logical_day, seconds, idle_seconds FROM v_session_day ORDER BY logical_day");

        var expected = SplitByDay(start, duration, boundaryHour);
        rows.Select(row => (string)row[0]!).Should().Equal(expected.Select(piece => piece.Day));
        rows.Select(row => (long)row[1]!).Should().Equal(expected.Select(piece => piece.Seconds));
        rows.Sum(row => (long)row[1]!).Should().Be(duration);
        rows.Sum(row => (double)row[2]!).Should().BeApproximately(duration / 2.0, 0.01);
    }

    private static List<(string Day, long Seconds)> SplitByDay(long start, long duration, int boundaryHour)
    {
        static DateTimeOffset Local(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
        var pieces = new List<(string, long)>();
        var cursor = start;
        var end = start + duration;
        while (cursor < end)
        {
            var day = Local(cursor - boundaryHour * 3600L).Date;
            var next = new DateTimeOffset(DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Local)).ToUnixTimeSeconds() + boundaryHour * 3600L;
            var stop = Math.Min(end, next);
            pieces.Add((day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), stop - cursor));
            cursor = stop;
        }
        return pieces;
    }

    [Test]
    public async Task HiddenAndExcludedGames_DisappearFromEveryPlaytimeView()
    {
        await AddRecordsAsync(Record(1, 1_700_000_000, 600), Record(2, 1_700_010_000, 600), Record(3, 1_700_020_000, 600));
        await using (var db = CreateContext())
        {
            db.SteamAppTable.AddRange(new SteamApp { AppId = 2, Name = "Embarrassing" }, new SteamApp { AppId = 1, Name = "Fine" });
            await db.SaveChangesAsync();
        }
        await InstallAsync(new AppSettings
        {
            Analytics = new AnalyticsSettings { HiddenAppIds = [2] },
            SessionTracking = new SessionTrackingSettings { ExcludedAppIds = [3] }
        });

        (await RowsAsync("SELECT app_id FROM v_session")).Select(row => (long)row[0]!).Should().Equal(1L);
        (await RowsAsync("SELECT app_id FROM v_app_playtime")).Select(row => (long)row[0]!).Should().Equal(1L);
        (await RowsAsync("SELECT SUM(seconds) FROM v_daily_playtime"))[0][0].Should().Be(600L);
        (await RowsAsync("SELECT name FROM v_app")).Select(row => (string)row[0]!).Should().Equal("Fine");
    }

    [Test]
    public async Task ChangingTheConfiguration_TakesEffectWithoutReinstalling()
    {
        await AddRecordsAsync(Record(1, 1_700_000_000, 600));
        await InstallAsync();
        (await RowsAsync("SELECT COUNT(*) FROM v_session"))[0][0].Should().Be(1L);

        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(_databaseFile, pooling: false));
        await connection.OpenAsync();
        await SemanticSchema.WriteConfigAsync(connection, new AppSettings { Analytics = new AnalyticsSettings { HiddenAppIds = [1] } });

        (await RowsAsync("SELECT COUNT(*) FROM v_session"))[0][0].Should().Be(0L);
    }

    #endregion

    #region 隔离

    private static readonly string[] Attacks =
    [
        "SELECT * FROM steam_login_token",
        "SELECT * FROM STEAM_LOGIN_TOKEN",
        "SELECT * FROM main.steam_login_token",
        "SELECT * FROM \"steam_login_token\"",
        "SELECT * FROM `steam_login_token`",
        "SELECT * FROM [steam_login_token]",
        "SELECT * FROM 'steam_login_token'",
        "SELECT * FROM/**/steam_login_token",
        "SELECT * FROM -- x\n steam_login_token",
        // authorizer 报告的最内层「视图」是 FROM 项的名字，可以用 CTE 伪造
        "WITH v_session AS (SELECT * FROM steam_login_token) SELECT * FROM v_session",
        "WITH x AS (SELECT * FROM steam_login_token) SELECT * FROM x",
        "SELECT (SELECT refresh_token FROM steam_login_token) FROM v_user",
        "SELECT * FROM v_user UNION ALL SELECT id, refresh_token FROM steam_login_token",
        "SELECT * FROM use_app_record",
        "SELECT * FROM steam_user",
        "SELECT * FROM semantic_app_filter",
        "SELECT * FROM sqlite_master",
        "SELECT * FROM sqlite_schema",
        "SELECT * FROM pragma_table_info('steam_login_token')",
        "WITH x AS (SELECT * FROM pragma_database_list) SELECT * FROM x",
        "PRAGMA table_info(steam_login_token)",
        "PRAGMA writable_schema = ON",
        "ATTACH DATABASE '/tmp/evil.db' AS evil",
        "INSERT INTO use_app_record (app_id) VALUES (1)",
        "UPDATE steam_user SET level = 1",
        "DELETE FROM use_app_record",
        "DROP VIEW v_session",
        "CREATE TABLE evil (x)",
        "CREATE TEMP VIEW evil AS SELECT 1",
        "SELECT load_extension('evil')"
    ];

    [TestCaseSource(nameof(Attacks))]
    public async Task Gateway_RejectsAnythingOutsideTheViews(string sql)
    {
        await InstallAsync();

        var result = await QueryAsync(sql);

        result.Failure.Should().NotBe(SemanticQueryFailure.None);
        result.Rows.Should().BeEmpty();
    }

    [Test]
    public async Task Authorizer_ProtectsCredentialsEvenIfTheNameCheckIsBypassed()
    {
        await InstallAsync();
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(_databaseFile, pooling: false, readOnly: true));
        await connection.OpenAsync();
        SQLitePCL.raw.sqlite3_set_authorizer(connection.Handle!, (_, action, first, second, _, _)
            => SemanticQueryGateway.Authorize(action, first, second, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "steam_login_token" }), null);

        var read = async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "WITH v_session AS (SELECT * FROM steam_login_token) SELECT * FROM v_session";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
        };

        await read.Should().ThrowAsync<SqliteException>();
    }

    [Test]
    public async Task Gateway_AllowsCteSubqueryAndJoinOverViews()
    {
        await AddRecordsAsync(Record(1, 1_700_000_000, 600));
        await InstallAsync();

        var result = await QueryAsync("""
            WITH totals AS (SELECT app_id, SUM(duration_seconds) AS total FROM v_session GROUP BY app_id)
            SELECT t.app_id, t.total, (SELECT COUNT(*) FROM v_daily_playtime) AS days
            FROM totals t LEFT JOIN v_app a ON a.app_id = t.app_id
            """);

        result.Failure.Should().Be(SemanticQueryFailure.None);
        result.Rows.Should().ContainSingle();
    }

    [Test]
    public async Task Gateway_DoesNotTreatCommentsAsTableNames()
    {
        await InstallAsync();

        var result = await QueryAsync("SELECT COUNT(*) FROM v_user -- steam_login_token");

        result.Failure.Should().Be(SemanticQueryFailure.None);
    }

    [Test]
    public async Task Tiers_LimitWhichViewsCanBeNamed()
    {
        await AddRecordsAsync(Record(1, 1_700_000_000, 600));
        await InstallAsync();

        (await QueryAsync("SELECT * FROM v_daily_playtime", SemanticTier.Aggregate)).Failure.Should().Be(SemanticQueryFailure.None);
        (await QueryAsync("SELECT * FROM v_session", SemanticTier.Aggregate)).Failure.Should().Be(SemanticQueryFailure.Denied);
        (await QueryAsync("SELECT * FROM v_friend_event", SemanticTier.Aggregate, SemanticTier.Games)).Failure.Should().Be(SemanticQueryFailure.Denied);
        (await QueryAsync("WITH v_daily_playtime AS (SELECT * FROM v_session) SELECT * FROM v_daily_playtime", SemanticTier.Aggregate)).Failure.Should().Be(SemanticQueryFailure.Denied);
    }

    [Test]
    public async Task Views_NeverExposeLoginNamesOrFilePaths()
    {
        await InstallAsync();
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(_databaseFile, pooling: false));
        await connection.OpenAsync();

        foreach (var view in SemanticSchema.Views)
        {
            var columns = await SqliteSchema.ColumnsAsync(connection, view.Name);
            columns.Should().NotContain(["account_name", "file_path", "thumbnail_path", "local_path", "refresh_token", "access_token"], view.Name);
        }
    }

    [Test]
    public async Task Gateway_StopsRunawayQueries()
    {
        await InstallAsync();

        var result = await SemanticQueryGateway.ExecuteAsync(
            _databaseFile,
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT COUNT(*) FROM n",
            SemanticTier.All.ToHashSet(),
            timeout: TimeSpan.FromMilliseconds(200));

        result.Failure.Should().Be(SemanticQueryFailure.Timeout);
    }

    [Test]
    public async Task Gateway_CapsTheRowCount()
    {
        await InstallAsync();

        var result = await SemanticQueryGateway.ExecuteAsync(
            _databaseFile,
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 100) SELECT i FROM n",
            SemanticTier.All.ToHashSet(),
            maxRows: 10);

        result.Rows.Should().HaveCount(10);
        result.Truncated.Should().BeTrue();
    }

    #endregion

    #region 字典

    [Test]
    public async Task Catalog_DescribesEveryViewAndColumn()
    {
        await InstallAsync();
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(_databaseFile, pooling: false));
        await connection.OpenAsync();

        foreach (var view in SemanticSchema.Views)
        {
            SemanticCatalog.ViewDescriptions.Should().ContainKey(view.Name);
            foreach (var column in await SqliteSchema.ColumnsAsync(connection, view.Name))
                SemanticCatalog.DescribeColumn(view.Name, column).Should().NotBeNull($"{view.Name}.{column} needs a description");
        }
    }

    [Test]
    public async Task Metrics_RunThroughTheGatewayAndNameTheirViews()
    {
        await AddRecordsAsync(Record(1, 1_700_000_000, 600, 60));
        await InstallAsync();

        MetricCatalog.Metrics.Select(metric => metric.Id).Should().OnlyHaveUniqueItems();
        foreach (var metric in MetricCatalog.Metrics)
        {
            var result = await QueryAsync(metric.Sql);
            result.Failure.Should().Be(SemanticQueryFailure.None, metric.Id);
            metric.Views.Should().OnlyContain(view => SemanticSchema.ByName.ContainsKey(view) && metric.Sql.Contains(view), metric.Id);
        }
    }

    [Test]
    public void UnderlyingTables_ExcludeCredentialsAndCaches()
    {
        var tables = SemanticSchema.UnderlyingTables();

        tables.Should().Contain(["use_app_record", "steam_app", "steam_user", "semantic_config", "rich_presence_record"]);
        tables.Should().NotContain(["steam_login_token", "steam_resource_cache", "global_status", "sync_control", "base", "piece", "v_session"]);
    }

    #endregion

    #region 迁移

    [Test]
    public async Task ViewsBlockTableRebuilds_SoTheMigratorMustDropThemFirst()
    {
        await InstallAsync();
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(_databaseFile, pooling: false));
        await connection.OpenAsync();

        // EF 重建表的步骤：DROP 旧表，再把临时表改名。视图还在时 RENAME 会失败
        async Task Rebuild()
        {
            await SqliteSchema.ExecuteAsync(connection, "CREATE TABLE ef_temp AS SELECT * FROM use_app_record");
            await SqliteSchema.ExecuteAsync(connection, "DROP TABLE use_app_record");
            await SqliteSchema.ExecuteAsync(connection, "ALTER TABLE ef_temp RENAME TO use_app_record");
        }

        await Rebuild().Awaiting(task => task).Should().ThrowAsync<SqliteException>();
        await SqliteSchema.ExecuteAsync(connection, "DROP TABLE IF EXISTS ef_temp");
        await SqliteSchema.ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS use_app_record (id INTEGER)");
    }

    [Test]
    public async Task DropViews_RemovesThemAllSoTablesCanBeRebuilt()
    {
        await InstallAsync();
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(_databaseFile, pooling: false));
        await connection.OpenAsync();

        await SemanticSchema.DropViewsAsync(connection);
        await SqliteSchema.ExecuteAsync(connection, "CREATE TABLE ef_temp AS SELECT * FROM use_app_record");
        await SqliteSchema.ExecuteAsync(connection, "DROP TABLE use_app_record");
        await SqliteSchema.ExecuteAsync(connection, "ALTER TABLE ef_temp RENAME TO use_app_record");

        (await SqliteSchema.ScalarLongAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view'")).Should().Be(0);
    }

    [Test]
    public async Task Install_IsIdempotent()
    {
        await InstallAsync();
        await InstallAsync();

        (await RowsAsync("SELECT COUNT(*) FROM v_session"))[0][0].Should().Be(0L);
    }

    #endregion
}
