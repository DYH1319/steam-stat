using System.Net;
using System.Text;
using System.Text.Json;
using ElectronNet.Features.Insights;
using ElectronNet.Features.Semantic;
using ElectronNet.Features.UsageTracking;
using ElectronNet.Models;
using ElectronNet.Persistence;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Http;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Features.Insights;

/// <summary>真实 SQLite + 真实语义视图，假的只有时钟、设置和游戏库快照。</summary>
public abstract class InsightHostFixture
{
    protected const string Me = "76561198000000001";
    protected const string Other = "76561198000000002";

    private string _directory = null!;
    protected AppPaths Paths = null!;
    protected MemorySettings Settings = null!;
    protected MovableTime Clock = null!;
    protected FakeCatalog Catalog = null!;
    protected RecordingBus Bus = null!;
    private Factory _factory = null!;
    private SemanticLayerService _semantic = null!;
    private protected SessionTagService Tags = null!;
    private protected InsightService Insights = null!;
    private protected WeeklyService Weekly = null!;

    protected IDbContextFactory<AppDbContext> DbFactory => _factory;

    protected static long Unix(int year, int month, int day, int hour = 12)
        => new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [SetUp]
    public async Task SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "steam-stat-insight-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Paths = new AppPaths(_directory);
        Directory.CreateDirectory(Paths.DatabaseDirectory);
        _factory = new Factory(Paths.DatabaseFile);
        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.SteamUserTable.Add(new SteamUser { SteamId = Me, AccountId = 1, AccountName = "me", PersonaName = "Me" });
            db.SteamUserTable.Add(new SteamUser { SteamId = Other, AccountId = 2, AccountName = "family", PersonaName = "Family" });
            await db.SaveChangesAsync();
        }
        Settings = new MemorySettings();
        Clock = new MovableTime(new DateTimeOffset(2026, 2, 3, 12, 0, 0, TimeSpan.Zero));
        Catalog = new FakeCatalog();
        Bus = new RecordingBus();
        _semantic = new SemanticLayerService(Paths, Settings, NullLogger<SemanticLayerService>.Instance);
        await _semantic.InstallAsync();
        Tags = new SessionTagService(_factory, Paths, Settings, Catalog, Clock, NullLogger<SessionTagService>.Instance);
        Insights = new InsightService(_semantic, Catalog, Settings, Clock, NullLogger<InsightService>.Instance);
        Weekly = new WeeklyService(_factory, Insights, Tags, Settings, Bus, Clock, NullLogger<WeeklyService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        Weekly.Dispose();
        Tags.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_directory, true);
    }

    protected async Task PlayAsync(int appId, long start, int seconds, string steamId = Me, long idle = 0)
    {
        await using var db = _factory.CreateDbContext();
        if (!await db.SteamAppTable.AnyAsync(app => app.AppId == appId))
            db.SteamAppTable.Add(new SteamApp { AppId = appId, Name = $"Game {appId}", NameLocalizedJson = "{}" });
        db.UseAppRecordTable.Add(new UseAppRecord
        {
            AppId = appId,
            SteamId = steamId,
            StartTime = (int)start,
            EndTime = (int)start + seconds,
            Duration = seconds,
            LastHeartbeat = (int)start + seconds,
            IdleDuration = idle,
            Source = "auto"
        });
        await db.SaveChangesAsync();
    }

    protected async Task<IReadOnlyList<WeeklyIssue>> IssuesAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.WeeklyIssueTable.AsNoTracking().OrderBy(issue => issue.Id).ToListAsync();
    }

    protected async Task<string> ScalarAsync(string sql)
    {
        var result = await _semantic.QueryAsync(sql);
        result.Failure.Should().Be(SemanticQueryFailure.None, sql);
        return Convert.ToString(result.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    protected async Task ApplySettingsAsync()
    {
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(Paths.DatabaseFile, pooling: false));
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await SemanticSchema.WriteConfigAsync(connection, Settings.Current);
        await transaction.CommitAsync();
    }

    protected sealed class Factory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(SqliteConnectionStrings.Create(databaseFile, pooling: false)).Options);
    }

    protected sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; } = new()
        {
            Language = "zh-CN",
            Analytics = new AnalyticsSettings { DayBoundaryHour = 0, WeekStart = 1, HiddenAppIds = [], PauseDays = 30, AbandonDays = 90 },
            Weekly = new WeeklySettings { Enabled = true, Delivery = WeeklyDelivery.Open, ExtraEnabled = true, SteamIds = [] }
        };

        public AppSettings GetSettings() => Current;

        public Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default)
        {
            if (partialSettings.Weekly?.BaselineAt != null) Current.Weekly!.BaselineAt = partialSettings.Weekly.BaselineAt;
            return Task.FromResult(true);
        }

        public Task<bool> ResetAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    protected sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    protected sealed class RecordingBus : IEventBus
    {
        public List<object> Events { get; } = [];

        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : notnull
        {
            Events.Add(message);
            return Task.CompletedTask;
        }
    }

    protected sealed class FakeCatalog : IInsightCatalog
    {
        public List<InsightOwnedGame> Owned { get; } = [];
        public Dictionary<int, InsightAppFacts> Facts { get; } = [];

        public Task<IReadOnlyList<InsightOwnedGame>> GetOwnedGamesAsync(InsightScope scope, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InsightOwnedGame>>(Owned);

        public IReadOnlyDictionary<int, InsightAppFacts> GetAppFacts(IEnumerable<int> appIds) => Facts;

        public Task<InsightReconciliation?> GetReconciliationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<InsightReconciliation?>(null);
    }
}

[TestFixture]
public class SessionTagServiceTests : InsightHostFixture
{
    [Test]
    public async Task Retag_WritesTagsWithoutTouchingUserFields()
    {
        await PlayAsync(1, Unix(2026, 1, 5, 1), 3600);

        (await Tags.RetagAsync()).Should().Be(1);

        await using var db = DbFactory.CreateDbContext();
        var record = await db.UseAppRecordTable.AsNoTracking().SingleAsync();
        record.AutoTags.Should().Be((int)SessionTags.LateNight);
        record.UpdatedAt.Should().BeNull("打标不算用户修改");
    }

    [Test]
    public async Task Retag_IsIdempotent()
    {
        await PlayAsync(1, Unix(2026, 1, 5, 1), 3600);
        await Tags.RetagAsync();

        (await Tags.RetagAsync()).Should().Be(0);
    }

    [Test]
    public async Task Retag_TaggedRowsStillGetNewRules()
    {
        await PlayAsync(1, Unix(2025, 12, 1), 7200);
        await PlayAsync(1, Unix(2026, 1, 20), 7200);
        await Tags.RetagAsync();
        (await ScalarAsync("SELECT SUM(auto_tags & 8 != 0) FROM v_session")).Should().Be("1");

        Settings.Current.Analytics!.PauseDays = 60;
        await Tags.RetagAsync();

        (await ScalarAsync("SELECT SUM(auto_tags & 8 != 0) FROM v_session")).Should().Be("0");
    }

    [Test]
    public async Task Retag_CoopNeedsAFriendInTheSameGame()
    {
        var start = Unix(2026, 2, 2, 20);
        await PlayAsync(1, start, 3600);
        await PlayAsync(2, start + 7200, 3600);
        await using (var db = DbFactory.CreateDbContext())
        {
            db.FriendStatusRecordTable.Add(new FriendStatusRecord { AccountName = "me", FriendSteamId = "9", FriendPersonaName = "Pal", ChangeType = "game", CurrentValue = """{"gameId":"1"}""", Timestamp = (int)start });
            db.FriendStatusRecordTable.Add(new FriendStatusRecord { AccountName = "me", FriendSteamId = "9", FriendPersonaName = "Pal", ChangeType = "game", CurrentValue = """{"gameId":"0"}""", Timestamp = (int)start + 1800 });
            await db.SaveChangesAsync();
        }

        await Tags.RetagAsync();

        (await ScalarAsync("SELECT auto_tags FROM v_session WHERE app_id = 1")).Should().Be("2");
        (await ScalarAsync("SELECT auto_tags FROM v_session WHERE app_id = 2")).Should().Be("0");
    }

    [Test]
    public async Task Retag_SingleplayerGamesAreNeverCoop()
    {
        var start = Unix(2026, 2, 2, 20);
        await PlayAsync(1, start, 3600);
        Catalog.Facts[1] = new InsightAppFacts(1) { Tags = ["Singleplayer"] };
        await using (var db = DbFactory.CreateDbContext())
        {
            db.FriendStatusRecordTable.Add(new FriendStatusRecord { AccountName = "me", FriendSteamId = "9", FriendPersonaName = "Pal", ChangeType = "game", CurrentValue = """{"gameId":"1"}""", Timestamp = (int)start });
            db.FriendStatusRecordTable.Add(new FriendStatusRecord { AccountName = "me", FriendSteamId = "9", FriendPersonaName = "Pal", ChangeType = "game", CurrentValue = """{"gameId":"0"}""", Timestamp = (int)start + 1800 });
            await db.SaveChangesAsync();
        }

        await Tags.RetagAsync();

        (await ScalarAsync("SELECT auto_tags FROM v_session")).Should().Be("0");
    }

    [Test]
    public async Task Retag_KeepsCoopOnOldSessionsWithoutRereadingFriends()
    {
        await PlayAsync(1, Unix(2025, 1, 1), 3600);
        await using (var db = DbFactory.CreateDbContext())
            await db.UseAppRecordTable.ExecuteUpdateAsync(setters => setters.SetProperty(record => record.AutoTags, (int)SessionTags.Coop));

        await Tags.RetagAsync();

        (await ScalarAsync("SELECT auto_tags FROM v_session")).Should().Be("2");
    }

    [Test]
    public async Task Crash_SurvivesTheMinimumDurationAndMergeRules()
    {
        Settings.Current.SessionTracking = new SessionTrackingSettings { MinDurationSeconds = 300, MergeGapSeconds = 600 };
        await ApplySettingsAsync();
        var start = Unix(2026, 2, 2, 20);
        for (var index = 0; index < 3; index++) await PlayAsync(1, start + index * 100, 30);

        await Tags.RetagAsync();

        (await ScalarAsync("SELECT COUNT(*) FROM v_crash_record")).Should().Be("3", "合并后的一局只有 90 秒，被最短时长过滤了，但闪退记录还在");
        (await ScalarAsync("SELECT COUNT(*) FROM v_session")).Should().Be("0");
    }

    [Test]
    public async Task MergedSession_IsACrashOnlyIfEveryPartIs()
    {
        Settings.Current.SessionTracking = new SessionTrackingSettings { MinDurationSeconds = 0, MergeGapSeconds = 600 };
        await ApplySettingsAsync();
        var start = Unix(2026, 2, 2, 20);
        for (var index = 0; index < 3; index++) await PlayAsync(1, start + index * 100, 30);
        await PlayAsync(1, start + 400, 3600);

        await Tags.RetagAsync();

        (await ScalarAsync("SELECT auto_tags & 16 FROM v_session")).Should().Be("0");
    }

    [Test]
    public void SessionCleaner_MergesTagsTheSameWayAsTheView()
    {
        UseAppRecordDto Record(int start, int tags) => new() { AppId = 1, SteamId = Me, StartTime = start, EndTime = start + 10, Duration = 10, IdleDuration = 0, AutoTags = tags };

        var merged = SessionCleaner.Apply(
            [Record(0, (int)(SessionTags.LateNight | SessionTags.Crash)), Record(20, (int)SessionTags.Comeback)],
            new SessionCleaningRules(0, 60, new HashSet<int>()));

        merged.Should().ContainSingle().Which.AutoTags.Should().Be((int)(SessionTags.LateNight | SessionTags.Comeback));
    }
}

[TestFixture]
public class GameStateViewTests : InsightHostFixture
{
    [Test]
    public async Task State_ManualWinsOverInference()
    {
        // 视图按 SQLite 的当前时间算，不经 Host 的时钟
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PlayAsync(1, now - 5 * 86400, 3600);
        await PlayAsync(2, now - 40 * 86400, 3600);
        await PlayAsync(3, now - 120 * 86400, 3600);
        await PlayAsync(4, now - 120 * 86400, 3600);
        await using (var db = DbFactory.CreateDbContext())
        {
            db.LibraryGameMetaTable.Add(new LibraryGameMeta { AccountName = "me", AppId = 4, Status = "playing", UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        (await ScalarAsync("SELECT state FROM v_game_state WHERE app_id = 2")).Should().Be("paused");
        (await ScalarAsync("SELECT state FROM v_game_state WHERE app_id = 3")).Should().Be("abandoned");
        (await ScalarAsync("SELECT state FROM v_game_state WHERE app_id = 4")).Should().Be("playing");
        (await ScalarAsync("SELECT state_source FROM v_game_state WHERE app_id = 4")).Should().Be("manual");
        (await ScalarAsync("SELECT state_source FROM v_game_state WHERE app_id = 3")).Should().Be("derived");
    }

    [Test]
    public async Task State_FollowsTheSettings()
    {
        await PlayAsync(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 40 * 86400, 3600);
        Settings.Current.Analytics!.PauseDays = 7;
        Settings.Current.Analytics.AbandonDays = 14;
        await ApplySettingsAsync();

        (await ScalarAsync("SELECT state FROM v_game_state")).Should().Be("abandoned");
    }

    [Test]
    public async Task HiddenGames_StayOutOfEveryNewView()
    {
        await using (var db = DbFactory.CreateDbContext())
        {
            db.AppBuildTable.Add(new AppBuildRecord { AppId = 1, BuildId = 5, ObservedAt = 1 });
            db.AppStoreStatusTable.Add(new AppStoreStatus { AppId = 1, Visible = true, CheckedAt = 1 });
            await db.SaveChangesAsync();
        }
        await PlayAsync(1, Clock.Now.ToUnixTimeSeconds() - 86400, 3600);
        Settings.Current.Analytics!.HiddenAppIds = [1];
        await ApplySettingsAsync();

        foreach (var view in new[] { "v_app_build", "v_app_store", "v_game_state", "v_crash_record" })
            (await ScalarAsync($"SELECT COUNT(*) FROM {view}")).Should().Be("0", view);
    }
}

[TestFixture]
public class WeeklyServiceTests : InsightHostFixture
{
    private async Task SeedWeekAsync() => await PlayAsync(1, Unix(2026, 1, 28, 20), 2 * 3600);

    private static JsonElement Payload(WeeklyIssue issue) => JsonDocument.Parse(issue.Payload).RootElement;

    [Test]
    public async Task Run_PublishesLastFullWeekExactlyOnce()
    {
        await SeedWeekAsync();

        await Weekly.RunOnceAsync();
        await Weekly.RunOnceAsync();

        var issue = (await IssuesAsync()).Should().ContainSingle().Subject;
        issue.Kind.Should().Be("weekly");
        issue.FromDay.Should().Be("2026-01-26");
        issue.ToDay.Should().Be("2026-02-01");
        Bus.Events.OfType<WeeklyIssuePublished>().Should().ContainSingle();
    }

    [Test]
    public async Task Run_DoesNothingBeforeTheFirstSessionOrWhenTurnedOff()
    {
        await Weekly.RunOnceAsync();
        (await IssuesAsync()).Should().BeEmpty();

        await SeedWeekAsync();
        Settings.Current.Weekly!.Enabled = false;
        await Weekly.RunOnceAsync();

        (await IssuesAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task Issue_IsFrozenAgainstLaterChanges()
    {
        await SeedWeekAsync();
        await Weekly.RunOnceAsync();
        var original = (await IssuesAsync()).Single().Payload;

        await PlayAsync(1, Unix(2026, 1, 29, 20), 9 * 3600);
        await Weekly.RunOnceAsync();

        (await IssuesAsync()).Single().Payload.Should().Be(original);
        Payload((await IssuesAsync()).Single()).GetProperty("numbers").GetProperty("seconds").GetInt64().Should().Be(2 * 3600);
    }

    [Test]
    public async Task Issue_HasBothLanguagesFrozenIn()
    {
        await SeedWeekAsync();
        await Weekly.RunOnceAsync();

        var title = Payload((await IssuesAsync()).Single()).GetProperty("headline").GetProperty("title");
        title.GetProperty("zh").GetString().Should().Contain("[[g:1|Game 1]]");
        title.GetProperty("en").GetString().Should().Contain("[[g:1|Game 1]]");
    }

    [Test]
    public async Task HiddenGames_NeverReachAnIssue()
    {
        await PlayAsync(1, Unix(2026, 1, 28, 20), 2 * 3600);
        await PlayAsync(2, Unix(2026, 1, 28, 23), 3600);
        Settings.Current.Analytics!.HiddenAppIds = [1];
        await ApplySettingsAsync();

        await Weekly.RunOnceAsync();

        (await IssuesAsync()).Single().Payload.Should().NotContain("Game 1");
    }

    [Test]
    public async Task Scope_CoversOnlyTheChosenAccounts()
    {
        await PlayAsync(1, Unix(2026, 1, 28, 20), 2 * 3600);
        await PlayAsync(2, Unix(2026, 1, 28, 23), 3600, Other);
        Settings.Current.Weekly!.SteamIds = [Me];

        await Weekly.RunOnceAsync();

        (await IssuesAsync()).Single().Payload.Should().NotContain("Game 2");
    }

    [Test]
    public async Task QuietWeeks_PublishOnlyOnceInARow()
    {
        await PlayAsync(1, Unix(2026, 1, 12, 20), 3600);
        await Weekly.RunOnceAsync();
        Clock.Now = Clock.Now.AddDays(7);
        await Weekly.RunOnceAsync();
        Clock.Now = Clock.Now.AddDays(7);
        await Weekly.RunOnceAsync();

        (await IssuesAsync()).Should().ContainSingle().Which.ToDay.Should().Be("2026-02-01", "连续没玩时只出一期「本周无事发生」");
    }

    [Test]
    public async Task Extras_FirstRunOnlySetsTheBaseline()
    {
        await PlayAsync(1, Unix(2026, 1, 28, 20), 200 * 3600);

        await Weekly.RunOnceAsync();

        Settings.Current.Weekly!.BaselineAt.Should().Be(Clock.Now.ToUnixTimeSeconds());
        (await IssuesAsync()).Should().NotContain(issue => issue.Kind == "extra");
    }

    [Test]
    public async Task Extras_PublishOncePerEventAfterTheBaseline()
    {
        await PlayAsync(1, Unix(2026, 1, 1, 20), 95 * 3600);
        await Weekly.RunOnceAsync();

        var now = Clock.Now.ToUnixTimeSeconds();
        await PlayAsync(1, now + 3600, 10 * 3600);
        Clock.Now = Clock.Now.AddHours(12);
        await Weekly.RunOnceAsync();
        await PlayAsync(2, now + 12 * 3600, 60);
        await Weekly.RunOnceAsync();

        var extras = (await IssuesAsync()).Where(issue => issue.Kind == "extra").ToList();
        extras.Select(issue => issue.IssueKey).Should().Contain("total:100").And.Contain("app:1:100");
        extras.Should().OnlyHaveUniqueItems(issue => issue.IssueKey);
    }

    [Test]
    public async Task Extras_AreCappedPerDay()
    {
        await PlayAsync(1, Unix(2026, 1, 1, 20), 90 * 3600);
        await PlayAsync(2, Unix(2026, 1, 1, 20), 90 * 3600);
        await PlayAsync(3, Unix(2026, 1, 1, 20), 90 * 3600);
        await PlayAsync(4, Unix(2026, 1, 1, 20), 90 * 3600);
        await Weekly.RunOnceAsync();

        var now = Clock.Now.ToUnixTimeSeconds();
        foreach (var appId in new[] { 1, 2, 3, 4 }) await PlayAsync(appId, now + 3600, 11 * 3600);
        Clock.Now = Clock.Now.AddHours(13);
        await Weekly.RunOnceAsync();

        (await IssuesAsync()).Count(issue => issue.Kind == "extra").Should().Be(3);
    }

    [Test]
    public async Task Extras_CanBeTurnedOff()
    {
        Settings.Current.Weekly!.ExtraEnabled = false;
        Settings.Current.Weekly.BaselineAt = Unix(2026, 1, 1);
        await PlayAsync(1, Unix(2026, 2, 2, 8), 120 * 3600);

        await Weekly.RunOnceAsync();

        (await IssuesAsync()).Should().NotContain(issue => issue.Kind == "extra");
    }

    [Test]
    public async Task Backfill_PrintsAPastWeekAndMarksIt()
    {
        await PlayAsync(1, Unix(2026, 1, 7, 20), 3600);

        var result = await Weekly.BackfillAsync((int)Unix(2026, 1, 8));

        result.Status.Should().Be("created");
        result.Issue!.FromDay.Should().Be("2026-01-05");
        result.Issue.Backfilled.Should().BeTrue();
        Bus.Events.OfType<WeeklyIssuePublished>().Should().BeEmpty("补印是用户自己点的，不用再通知");
        (await Weekly.BackfillAsync((int)Unix(2026, 1, 8))).Status.Should().Be("exists");
    }

    [Test]
    public async Task Backfill_RefusesUnfinishedAndEmptyWeeks()
    {
        await PlayAsync(1, Unix(2026, 1, 7, 20), 3600);

        (await Weekly.BackfillAsync((int)Clock.Now.ToUnixTimeSeconds())).Status.Should().Be("notFinished");
        (await Weekly.BackfillAsync((int)Unix(2025, 6, 1))).Status.Should().Be("empty");
        Settings.Current.Weekly!.Enabled = false;
        (await Weekly.BackfillAsync((int)Unix(2026, 1, 8))).Status.Should().Be("disabled");
    }

    [Test]
    public async Task ListAndRead_TrackWhatWasOpened()
    {
        await SeedWeekAsync();
        await Weekly.RunOnceAsync();
        var summary = (await Weekly.ListAsync(new WeeklyIssuesQueryRequest { Limit = 10 })).Single();
        summary.ReadAt.Should().BeNull();
        summary.Headline.Zh.Should().NotBeEmpty();
        summary.AppId.Should().Be(1);

        (await Weekly.MarkReadAsync(summary.Id)).Should().BeTrue();
        (await Weekly.MarkReadAsync(summary.Id)).Should().BeFalse();

        (await Weekly.GetAsync(summary.Id))!.Summary.ReadAt.Should().Be((int)Clock.Now.ToUnixTimeSeconds());
        (await Weekly.GetAsync(999)).Should().BeNull();
    }

    [Test]
    public async Task List_PagesBackwards()
    {
        await PlayAsync(1, Unix(2026, 1, 7, 20), 3600);
        await PlayAsync(1, Unix(2026, 1, 14, 20), 3600);
        var first = await Weekly.BackfillAsync((int)Unix(2026, 1, 8));
        var second = await Weekly.BackfillAsync((int)Unix(2026, 1, 15));

        var page = await Weekly.ListAsync(new WeeklyIssuesQueryRequest { Limit = 1 });
        page.Single().Id.Should().Be(second.Issue!.Id);
        (await Weekly.ListAsync(new WeeklyIssuesQueryRequest { Limit = 1, BeforeId = second.Issue.Id })).Single().Id.Should().Be(first.Issue!.Id);
    }

    [Test]
    public async Task Year_ReadsBackEverythingPublishedThatYear()
    {
        await PlayAsync(1, Unix(2026, 1, 7, 20), 3600);
        await Weekly.BackfillAsync((int)Unix(2026, 1, 8));

        var issues = await Weekly.GetYearAsync(2026);

        issues.Should().ContainSingle();
        (await Weekly.GetYearAsync(2025)).Should().BeEmpty();
    }

    [Test]
    public async Task Year_SkipsAnIssueThatCanNoLongerBeRead()
    {
        await using (var db = DbFactory.CreateDbContext())
        {
            db.WeeklyIssueTable.Add(new WeeklyIssue { Kind = "weekly", IssueKey = "x", FromDay = "2026-01-05", ToDay = "2026-01-11", Payload = "{broken" });
            await db.SaveChangesAsync();
        }

        (await Weekly.GetYearAsync(2026)).Should().BeEmpty();
    }

    [Test]
    public async Task Report_ContainsEverySection()
    {
        for (var day = 0; day < 60; day++) await PlayAsync(1, Unix(2025, 11, 1) + day * 86400, 2 * 3600);

        var report = await Insights.GetReportAsync(new InsightReportRequest());

        report.Personas.Should().ContainSingle();
        report.Career.Should().NotBeEmpty();
        report.FromDay.Should().Be("2026-01-05");
        report.ToDay.Should().Be("2026-02-03");
    }

    [Test]
    public async Task Report_IsCachedPerScope()
    {
        await PlayAsync(1, Unix(2026, 1, 7, 20), 3600);
        var first = await Insights.GetReportAsync(new InsightReportRequest());

        await PlayAsync(1, Unix(2026, 1, 8, 20), 3600);

        (await Insights.GetReportAsync(new InsightReportRequest())).Should().BeSameAs(first);
        (await Insights.GetReportAsync(new InsightReportRequest { SteamIds = [Me] })).Should().NotBeSameAs(first);
    }
}

[TestFixture]
public class WeeklyBoundVolumeTests
{
    private static WeeklyPayload Issue() => new()
    {
        Kind = "weekly",
        Number = 3,
        FromDay = "2026-01-05",
        ToDay = "2026-01-11",
        GeneratedAt = 1,
        Headline = new WeeklyStory("x", new Bilingual($"《{Say.Game(1, "Secret")}》和 {Say.Friend("7", "Pal")}", $"{Say.Game(1, "Secret")} and {Say.Friend("7", "Pal")}"), new Bilingual("<b>", "<b>"), 1),
        Numbers = new WeeklyNumbers(0, 0, 0, 0, 0, 0, 0, 0, 0),
        Friends = new WeeklyFriends([new Bilingual("好友内容", "friend content")]),
        Obituaries = [new WeeklyObituary(1, "Secret", 1, 1, new Bilingual($"《{Say.Game(1, "Secret")}》", "x")), new WeeklyObituary(2, "Open", 1, 1, new Bilingual($"《{Say.Game(2, "Open")}》", "y"))],
        Forecast = new WeeklyForecast(new Bilingual("预报", "forecast"), [], new Bilingual("好友上线", "friend online"), 8)
    };

    private static string Text(IReadOnlyList<OpenArchiveFile> files, string name)
        => Encoding.UTF8.GetString(files.Single(file => file.Name == name).Content);

    [Test]
    public void Build_LeavesOutHiddenGamesAndFriends()
    {
        var files = WeeklyBoundVolume.Build([Issue()], 2026, "zh", new HashSet<int> { 1 });

        foreach (var name in new[] { "weekly/index.html", "weekly/issues.json" })
        {
            var text = Text(files, name);
            text.Should().NotContain("Secret", name);
            text.Should().NotContain("Pal", name);
            text.Should().NotContain("好友内容", name);
            text.Should().NotContain("好友上线", name);
            text.Should().NotContain("[[", name);
        }
        Text(files, "weekly/index.html").Should().Contain("某款游戏").And.Contain("一位好友").And.Contain("Open");
    }

    [Test]
    public void Build_EscapesHtml()
        => Text(WeeklyBoundVolume.Build([Issue()], 2026, "zh", new HashSet<int>()), "weekly/index.html").Should().Contain("&lt;b&gt;");

    [Test]
    public void Build_FollowsTheLanguage()
        => Text(WeeklyBoundVolume.Build([Issue()], 2026, "en", new HashSet<int>()), "weekly/index.html").Should().Contain("Bound Volume").And.Contain("a friend");

    [Test]
    public void Build_NothingToBindWithoutIssues()
        => WeeklyBoundVolume.Build([], 2026, "zh", new HashSet<int>()).Should().BeEmpty();
}

[TestFixture]
public class StoreStatusServiceTests : InsightHostFixture
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Switch(bool enabled) : IPublicDataSwitch
    {
        public bool Enabled { get; } = enabled;
    }

    private StoreStatusService Service(Handler handler)
        => new(DbFactory, new Clients(handler), Catalog, new Switch(true), Settings, TimeProvider.System, NullLogger<StoreStatusService>.Instance);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Test]
    public async Task Save_RemembersWhenAGameWasFirstSeenDelisted()
    {
        using var service = Service(new Handler(_ => Json("{}")));
        var gone = new StoreItemStatus(1, false, false, false, false, null, null, null, null, null);
        var back = new StoreItemStatus(1, true, true, false, false, 1999, 10, 90, null, null);

        await service.SaveAsync([back]);
        await service.SaveAsync([gone]);
        long? first;
        await using (var db = DbFactory.CreateDbContext())
        {
            var status = await db.AppStoreStatusTable.AsNoTracking().SingleAsync();
            first = status.DelistedAt;
            first.Should().NotBeNull();
            status.PriceCents.Should().Be(1999, "下架后商店不再给价格，保留下架前的");
            status.ReviewPercent.Should().Be(90);
        }

        await service.SaveAsync([gone]);
        await using (var db = DbFactory.CreateDbContext())
            (await db.AppStoreStatusTable.AsNoTracking().SingleAsync()).DelistedAt.Should().Be(first);

        await service.SaveAsync([back]);
        await using (var db = DbFactory.CreateDbContext())
            (await db.AppStoreStatusTable.AsNoTracking().SingleAsync()).DelistedAt.Should().BeNull("重新上架");
    }

    [Test]
    public async Task Refresh_AsksOnlyForDueGamesAndSkipsHiddenOnes()
    {
        Catalog.Owned.AddRange([new InsightOwnedGame(1, "A", 0, 0), new InsightOwnedGame(2, "B", 0, 0), new InsightOwnedGame(3, "C", 0, 0)]);
        Settings.Current.Analytics!.HiddenAppIds = [3];
        await using (var db = DbFactory.CreateDbContext())
        {
            db.AppStoreStatusTable.Add(new AppStoreStatus { AppId = 2, Visible = true, CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            await db.SaveChangesAsync();
        }
        var handler = new Handler(_ => Json("""{"response":{"store_items":[{"id":1,"visible":true,"best_purchase_option":{"final_price_in_cents":"999"}}]}}"""));
        using var service = Service(handler);

        (await service.RefreshAsync()).Should().Be(1);

        var query = Uri.UnescapeDataString(handler.Urls.Single());
        query.Should().StartWith(StoreBrowseParser.Endpoint);
        query.Should().Contain("\"appid\":1").And.NotContain("\"appid\":2").And.NotContain("\"appid\":3");
    }

    [Test]
    public async Task Refresh_StopsQuietlyWhenTheStoreIsUnreachable()
    {
        Catalog.Owned.Add(new InsightOwnedGame(1, "A", 0, 0));
        using var service = Service(new Handler(_ => throw new HttpRequestException("offline")));

        (await service.RefreshAsync()).Should().Be(0);
    }
}
