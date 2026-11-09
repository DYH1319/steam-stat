using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElectronNET.Serialization;
using ElectronNet.Infrastructure;
using ElectronNet;
using ElectronNet.Features.Reliability;
using ElectronNet.Features.Semantic;
using ElectronNet.Hosting;
using ElectronNet.Models;
using ElectronNet.Persistence;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Features.Semantic;

[TestFixture]
public class ArchiveHealthRulesTests
{
    private const long Now = 1_800_000_000;
    private const long Day = 86400;

    private static DatabaseSnapshotDto Snapshot(long age) => new()
    {
        Name = "steam-stat-x.bak", CreatedAt = Now - age, SizeBytes = 1, Kind = "startup"
    };

    [Test]
    public void Backup_CountsManualBackupsAndSyncButWarnsWhenBothAreOld()
    {
        ArchiveHealthRules.Backup(null, null, Now).Status.Should().Be("warning");
        ArchiveHealthRules.Backup(Now - 5 * Day, null, Now).Status.Should().Be("ok");
        ArchiveHealthRules.Backup(Now - 90 * Day, Now - 2 * Day, Now).Status.Should().Be("ok");
        ArchiveHealthRules.Backup(Now - 40 * Day, null, Now).Status.Should().Be("warning");
        ArchiveHealthRules.Backup(Now - 200 * Day, null, Now).Status.Should().Be("error");
    }

    [Test]
    public void Snapshots_WarnWhenMissingOrStale()
    {
        ArchiveHealthRules.Snapshots([], Now).Status.Should().Be("warning");
        ArchiveHealthRules.Snapshots([Snapshot(Day)], Now).Status.Should().Be("ok");
        ArchiveHealthRules.Snapshots([Snapshot(30 * Day)], Now).Status.Should().Be("warning");
    }

    [Test]
    public void Gaps_MergeAdjacentDaysAndIgnoreRoundingNoise()
    {
        var utc = TimeZoneInfo.Utc;
        var day = (int)new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var gaps = ArchiveHealthRules.MergeGaps(
        [
            new PlaytimeGap(day, day + 3600, 3000),
            new PlaytimeGap(day + 86400, day + 86400 + 3600, 3000),
            new PlaytimeGap(day + 30 * 86400, day + 30 * 86400 + 60, 120)
        ], 4, utc);

        gaps.Should().ContainSingle();
        gaps[0].FromDay.Should().Be("2026-03-10");
        gaps[0].ToDay.Should().Be("2026-03-11");
        gaps[0].Days.Should().Be(2);
        gaps[0].UntrackedSeconds.Should().Be(6000);
    }

    [Test]
    public void Gaps_UseTheLogicalDay()
    {
        var early = (int)new DateTimeOffset(2026, 3, 10, 2, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var gaps = ArchiveHealthRules.MergeGaps([new PlaytimeGap(early, early + 600, 3600)], 4, TimeZoneInfo.Utc);

        gaps[0].FromDay.Should().Be("2026-03-09");
    }

    [Test]
    public void RichPresence_WarnsOnlyWithEnoughDataAndMostChangesComingFromThePoll()
    {
        static RichPresenceCoverage Coverage(long changes, long caught) => new(0, 0, changes, caught, 0, 0, null, null);

        ArchiveHealthRules.RichPresence([]).Status.Should().Be("unknown");
        ArchiveHealthRules.RichPresence([Coverage(5, 5)]).Status.Should().Be("ok");
        ArchiveHealthRules.RichPresence([Coverage(100, 10)]).Status.Should().Be("ok");
        ArchiveHealthRules.RichPresence([Coverage(100, 60)]).Status.Should().Be("warning");
    }

    [Test]
    public void Seals_DamageIsAnErrorAndUnsealedYearsAWarning()
    {
        var intact = new ArchiveSealDto { Year = 2024, SealedAt = 1, SizeBytes = 1, Sha256 = "x", AppVersion = "1", SessionCount = 1, Intact = true };

        ArchiveHealthRules.Seals(new ArchiveSealOverviewDto { Seals = [intact], AvailableYears = [] }).Status.Should().Be("ok");
        ArchiveHealthRules.Seals(new ArchiveSealOverviewDto { Seals = [intact], AvailableYears = [2025] }).Status.Should().Be("warning");
        ArchiveHealthRules.Seals(new ArchiveSealOverviewDto { Seals = [intact with { Intact = false }], AvailableYears = [] }).Status.Should().Be("error");
    }
}

[TestFixture]
public class ArchiveDataServiceTests
{
    private const string SteamId = "76561198000000001";
    private string _directory = null!;
    private AppPaths _paths = null!;
    private MemorySettings _settings = null!;
    private FixedTime _time = null!;
    private SemanticLayerService _semantic = null!;
    private ArchiveSealService _seals = null!;
    private readonly StubWeekly _weekly = new();
    private TestFactory _factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "steam-stat-reliability-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_directory);
        Directory.CreateDirectory(_paths.DatabaseDirectory);
        _factory = new TestFactory(_paths.DatabaseFile);
        await using (var db = _factory.CreateDbContext()) await db.Database.MigrateAsync();
        _settings = new MemorySettings();
        _time = new FixedTime(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        _semantic = new SemanticLayerService(_paths, _settings, NullLogger<SemanticLayerService>.Instance);
        await _semantic.InstallAsync();
        _seals = new ArchiveSealService(_paths, _semantic, _settings, _weekly, new AppEnvironment(false, "zh-CN", false, _paths, "5.1.0"),
            new StubShell(), _time, NullLogger<ArchiveSealService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _seals.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_directory, true);
    }

    private async Task AddAsync(params UseAppRecord[] records)
    {
        await using var db = _factory.CreateDbContext();
        db.UseAppRecordTable.AddRange(records);
        await db.SaveChangesAsync();
    }

    private static UseAppRecord Record(int appId, DateTimeOffset start, int duration, long idle = 0, string source = "auto", string? endReason = "ended") => new()
    {
        AppId = appId,
        SteamId = SteamId,
        StartTime = (int)start.ToUnixTimeSeconds(),
        EndTime = (int)start.ToUnixTimeSeconds() + duration,
        Duration = duration,
        IdleDuration = idle,
        Source = source,
        EndReason = endReason
    };

    private static DateTimeOffset Noon(int year, int month, int day) => new(year, month, day, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day, 12, 0, 0)));

    private StatsExplainService Explain() => new(_factory, _semantic, _settings, _time);

    #region 封存包

    [Test]
    public async Task Seal_WritesAnOpenPackageThatDescribesItself()
    {
        await AddAsync(Record(10, Noon(2025, 5, 1), 3600, 600), Record(20, Noon(2025, 6, 1), 1800));

        var result = await _seals.CreateAsync(2025);

        result.Status.Should().Be("sealed");
        using var zip = ZipFile.OpenRead(Path.Combine(_directory, "Sealed", "steam-stat-archive-2025.zip"));
        zip.Entries.Select(entry => entry.FullName).Should().Contain(["index.html", "README.md", "manifest.json", "data/sessions.csv", "data/archive.json", "data/daily_playtime.csv"]);

        var manifest = JsonDocument.Parse(Read(zip, "manifest.json")).RootElement;
        manifest.GetProperty("format").GetString().Should().Be("steam-stat-open-archive");
        foreach (var file in manifest.GetProperty("files").EnumerateArray())
        {
            using var stream = zip.GetEntry(file.GetProperty("name").GetString()!)!.Open();
            Convert.ToHexStringLower(SHA256.HashData(stream)).Should().Be(file.GetProperty("sha256").GetString());
        }

        var sessions = Read(zip, "data/sessions.csv");
        sessions.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(3);
        var html = Read(zip, "index.html");
        html.Should().NotContain("<script");
        html.Should().Contain("1.5").And.Contain("2025");
        JsonDocument.Parse(Read(zip, "data/archive.json")).RootElement.GetProperty("summary").GetProperty("SessionCount").GetInt32().Should().Be(2);
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Test]
    public async Task Seal_LeavesOutHiddenGamesAndFriends()
    {
        await AddAsync(Record(10, Noon(2025, 5, 1), 3600), Record(99, Noon(2025, 5, 2), 3600));
        await using (var db = _factory.CreateDbContext())
        {
            db.SteamAppTable.AddRange(new SteamApp { AppId = 99, Name = "Secret Game" }, new SteamApp { AppId = 10, Name = "Fine Game" });
            db.FriendStatusRecordTable.Add(new FriendStatusRecord
            {
                AccountName = "a", FriendSteamId = "1", FriendPersonaName = "Secret Friend", ChangeType = "state",
                Timestamp = (int)Noon(2025, 5, 3).ToUnixTimeSeconds()
            });
            await db.SaveChangesAsync();
        }
        _settings.Current.Analytics = new AnalyticsSettings { HiddenAppIds = [99] };
        await _semantic.InstallAsync();

        await _seals.CreateAsync(2025);

        using var zip = ZipFile.OpenRead(Path.Combine(_directory, "Sealed", "steam-stat-archive-2025.zip"));
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var content = reader.ReadToEnd();
            content.Should().NotContain("Secret Game", entry.FullName);
            content.Should().NotContain("Secret Friend", entry.FullName);
        }
    }

    [Test]
    public async Task Seal_RefusesTheCurrentYearAndEmptyYears()
    {
        await AddAsync(Record(10, Noon(2026, 2, 1), 3600));

        (await _seals.CreateAsync(2026)).Status.Should().Be("notFinished");
        (await _seals.CreateAsync(2020)).Status.Should().Be("empty");
        Directory.GetFiles(Path.Combine(_directory, "Sealed"), "*.zip").Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_directory, "Sealed"), "*.tmp").Should().BeEmpty();
    }

    [Test]
    public async Task Seal_IsNeverOverwritten()
    {
        await AddAsync(Record(10, Noon(2025, 5, 1), 3600));
        var first = await _seals.CreateAsync(2025);

        await AddAsync(Record(10, Noon(2025, 7, 1), 3600));
        var second = await _seals.CreateAsync(2025);

        second.Status.Should().Be("exists");
        second.Seal!.Sha256.Should().Be(first.Seal!.Sha256);
        second.Seal.SessionCount.Should().Be(1);
    }

    [Test]
    public async Task Overview_ListsUnsealedYearsAndDetectsTampering()
    {
        await AddAsync(Record(10, Noon(2024, 5, 1), 3600), Record(10, Noon(2025, 5, 1), 3600), Record(10, Noon(2026, 2, 1), 3600));
        await _seals.CreateAsync(2024);

        var overview = await _seals.GetOverviewAsync();
        overview.AvailableYears.Should().Equal(2025);
        overview.Seals.Single().Intact.Should().BeTrue();

        var file = Path.Combine(_directory, "Sealed", "steam-stat-archive-2024.zip");
        File.SetAttributes(file, FileAttributes.Normal);
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[100] ^= 0xFF;
        await File.WriteAllBytesAsync(file, bytes);

        (await _seals.GetOverviewAsync()).Seals.Single().Intact.Should().BeFalse();
    }

    [Test]
    public void Csv_NeutralizesFormulasAndQuotesDelimiters()
    {
        var table = new SemanticQueryResult(["name", "value"], [["=HYPERLINK(1)", 1.5], ["a,\"b\"", null]], false, SemanticQueryFailure.None, 0);

        var csv = System.Text.Encoding.UTF8.GetString(OpenArchiveExporter.ToCsv(table));

        csv.Should().Contain("'=HYPERLINK(1)").And.Contain("\"a,\"\"b\"\"\"");
    }

    #endregion

    #region 可解释的数字

    [Test]
    public async Task Explain_AddsUpFromRawRecordsToTheResult()
    {
        _settings.Current.SessionTracking = new SessionTrackingSettings { MinDurationSeconds = 300, ExcludedAppIds = [30] };
        _settings.Current.Analytics = new AnalyticsSettings { HiddenAppIds = [40] };
        await _semantic.InstallAsync();
        await AddAsync(
            Record(10, Noon(2026, 1, 10), 3600, 600),
            Record(10, Noon(2026, 1, 11), 60),
            Record(30, Noon(2026, 1, 12), 1000),
            Record(40, Noon(2026, 1, 13), 2000),
            Record(10, Noon(2026, 1, 14), 1200, 0, "imported", null),
            Record(10, Noon(2026, 1, 15), 1500, 0, "auto", "recovered"));

        var result = await Explain().ExplainAsync(new StatsExplainRequest { FromDay = "2026-01-01", ToDay = "2026-01-31" });

        result.RawRecords.Should().Be(6);
        result.TotalSeconds.Should().Be(3600 + 1200 + 1500);
        result.EffectiveSeconds.Should().Be(3000 + 1200 + 1500);
        result.SessionCount.Should().Be(3);
        var deductions = result.Deductions.ToDictionary(item => item.Kind);
        deductions["excluded"].Seconds.Should().Be(1000);
        deductions["hidden"].Seconds.Should().Be(2000);
        deductions["belowMinimum"].Seconds.Should().Be(60);
        deductions["idle"].Seconds.Should().Be(600);
        (result.RawSeconds - deductions["excluded"].Seconds - deductions["hidden"].Seconds - deductions["belowMinimum"].Seconds)
            .Should().Be(result.TotalSeconds);
        result.Sources.Select(source => source.Evidence).Should().BeEquivalentTo(["recorded", "recovered", "imported"]);
    }

    [Test]
    public async Task Explain_ClipsSessionsToTheRangeLikeThePerDayView()
    {
        // 跨过 2 月 1 日分界点的会话只算落在 1 月内的部分
        var start = new DateTimeOffset(2026, 2, 1, 2, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 2, 1, 2, 0, 0)));
        await AddAsync(Record(10, start, 4 * 3600));

        var january = await Explain().ExplainAsync(new StatsExplainRequest { FromDay = "2026-01-01", ToDay = "2026-01-31", ClipToRange = true });
        var february = await Explain().ExplainAsync(new StatsExplainRequest { FromDay = "2026-02-01", ToDay = "2026-02-28", ClipToRange = true });

        january.TotalSeconds.Should().Be(2 * 3600);
        february.TotalSeconds.Should().Be(2 * 3600);
        january.RawSeconds.Should().Be(2 * 3600);
    }

    [Test]
    public async Task Explain_ByDefaultCountsWholeSessionsByStartLikeTheStatsPage()
    {
        // 统计页的汇总卡：开始时间在范围内就整局计入
        var start = new DateTimeOffset(2026, 2, 1, 2, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 2, 1, 2, 0, 0)));
        await AddAsync(Record(10, start, 4 * 3600));

        var january = await Explain().ExplainAsync(new StatsExplainRequest { FromDay = "2026-01-01", ToDay = "2026-01-31" });
        var february = await Explain().ExplainAsync(new StatsExplainRequest { FromDay = "2026-02-01", ToDay = "2026-02-28" });

        january.TotalSeconds.Should().Be(4 * 3600);
        january.RawSeconds.Should().Be(4 * 3600);
        february.TotalSeconds.Should().Be(0);
    }

    [Test]
    public async Task Explain_FiltersByAccountAndGame()
    {
        await AddAsync(Record(10, Noon(2026, 1, 10), 3600), Record(20, Noon(2026, 1, 11), 1800));

        var result = await Explain().ExplainAsync(new StatsExplainRequest { AppIds = [20], SteamIds = [SteamId] });

        result.TotalSeconds.Should().Be(1800);
        result.Sessions.Should().ContainSingle().Which.AppId.Should().Be(20);
    }

    [Test]
    public async Task Explain_DayStringsSurviveTheElectronBridge()
    {
        await AddAsync(Record(10, Noon(2026, 1, 10), 3600));
        var bridge = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new JsonToBoxedPrimitivesConverter() }
        };
        var binder = new IpcRequestBinder(NullLogger<IpcRequestBinder>.Instance);

        // 与 Electron.NET IpcMain.FormatArguments 相同：[event, args] → object[] → args
        var payload = JsonSerializer.Serialize(new object?[] { null, new { fromDay = "2026-01-01", toDay = "2026-01-31" } });
        var boxed = JsonSerializer.Deserialize<object[]>(payload, bridge)![1];
        var request = binder.Bind<StatsExplainRequest>(boxed, ReliabilityIpc.Explain);

        var result = await Explain().ExplainAsync(request);

        result.TotalSeconds.Should().Be(3600);
    }

    [TestCase("auto", "ended", null, "recorded")]
    [TestCase("auto", "recovered", null, "recovered")]
    [TestCase("imported", null, null, "imported")]
    [TestCase("merged", "ended", null, "edited")]
    [TestCase("auto", "ended", 123, "edited")]
    public void Evidence_FollowsHowTheRecordCameToBe(string source, string? endReason, int? updatedAt, string expected)
        => StatsExplainService.EvidenceOf(new UseAppRecord { Source = source, EndReason = endReason, UpdatedAt = updatedAt }).Should().Be(expected);

    #endregion

    private sealed class TestFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(SqliteConnectionStrings.Create(databaseFile, pooling: false)).Options);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; } = new();
        public AppSettings GetSettings() => Current;
        public Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class StubWeekly : IWeeklyYearSource
    {
        public List<SteamStat.Core.Insights.WeeklyPayload> Issues { get; } = [];

        public Task<IReadOnlyList<SteamStat.Core.Insights.WeeklyPayload>> GetYearAsync(int year, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SteamStat.Core.Insights.WeeklyPayload>>(Issues);
    }


    private sealed class StubShell : IShellLauncher
    {
        public Task<bool> ShowItemInFolderAsync(string fullPath) => Task.FromResult(true);
        public Task<bool> OpenExternalAsync(string url) => Task.FromResult(true);
    }
}
