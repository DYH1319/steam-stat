using ElectronNet.Infrastructure;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Tests.Features.Planner;

[TestFixture]
public sealed class PlannerServiceTests
{
    private SqliteConnection _connection = null!;
    private TestDbContextFactory _factory = null!;
    private PlannerService _planner = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _planner = new PlannerService(_factory, TimeProvider.System);
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    [Test]
    public async Task Journey_DecodesTextKeepsGoalFieldsConsistentAndDeletesWhenEmpty()
    {
        var saved = await _planner.SaveJourneyAsync(new GameJourneyInput
        {
            AppId = 10,
            StartedAt = 1_700_000_000,
            GoalKind = "achievements",
            GoalHours = 40,
            GoalNote = IpcSecret.Encode("2026-01-01 之前全成就"),
            Milestones =
            [
                new GameMilestoneDto { Id = "a", Title = IpcSecret.Encode("第一章") },
                new GameMilestoneDto { Id = "a", Title = IpcSecret.Encode("重复的") },
                new GameMilestoneDto { Id = "b", Title = "  " }
            ],
            ReviewScores = new GameReviewScoresDto()
        });

        saved.Should().NotBeNull();
        saved!.GoalNote.Should().Be("2026-01-01 之前全成就", "日期样子的文字经 s1: 编码，原样保存");
        saved.GoalAchievementPercent.Should().Be(100, "成就目标没填百分比时按全成就");
        saved.GoalHours.Should().BeNull("时长只属于时长目标");
        saved.Milestones.Should().ContainSingle().Which.Title.Should().Be("第一章");
        saved.ReviewScores.Should().BeNull("全空的分项评分不存");

        (await _planner.GetSnapshotAsync()).Journeys.Should().ContainSingle().Which.Milestones.Should().ContainSingle();

        (await _planner.SaveJourneyAsync(new GameJourneyInput { AppId = 10 })).Should().BeNull();
        (await _planner.GetSnapshotAsync()).Journeys.Should().BeEmpty();
    }

    [Test]
    public async Task QueueOrder_FollowsTheListAndSurvivesJourneyEdits()
    {
        await _planner.SaveJourneyAsync(new GameJourneyInput { AppId = 1, GoalKind = "story" });
        await _planner.SetQueueOrderAsync(new GameQueueOrderSetRequest { AppIds = [3, 1, 2] });

        var queue = (await _planner.GetSnapshotAsync()).Journeys.OrderBy(item => item.QueueOrder).Select(item => item.AppId);
        queue.Should().Equal(3, 1, 2);

        // 编辑坑不能把队列顺序冲掉
        await _planner.SaveJourneyAsync(new GameJourneyInput { AppId = 1, GoalKind = "storyPlus" });
        (await _planner.GetSnapshotAsync()).Journeys.Single(item => item.AppId == 1).QueueOrder.Should().Be(1);

        // 退出队列：只有队列信息的行删掉，有别的内容的保留
        await _planner.SetQueueOrderAsync(new GameQueueOrderSetRequest { AppIds = [2] });
        var journeys = (await _planner.GetSnapshotAsync()).Journeys;
        journeys.Select(item => item.AppId).Should().BeEquivalentTo([1, 2]);
        journeys.Single(item => item.AppId == 1).QueueOrder.Should().BeNull();
        journeys.Single(item => item.AppId == 2).QueueOrder.Should().Be(0);
    }

    [Test]
    public async Task Journal_UsesSyncIdsAndCountsPerGame()
    {
        var first = await _planner.SaveJournalAsync(new GameJournalSaveRequest { AppId = 7, Kind = "note", Body = IpcSecret.Encode("**好玩**"), Spoiler = false, CreatedAt = 1_600_000_000 });
        await _planner.SaveJournalAsync(new GameJournalSaveRequest { AppId = 7, Kind = "review", Body = IpcSecret.Encode("结局"), Spoiler = true, Mood = "moved" });
        await _planner.SaveJournalAsync(new GameJournalSaveRequest { AppId = 8, Kind = "note", Body = IpcSecret.Encode("另一款"), Spoiler = false });

        first.Id.Should().MatchRegex("^[0-9a-f]{32}$");
        first.CreatedAt.Should().Be(1_600_000_000, "可以补记以前的心得");

        var edited = await _planner.SaveJournalAsync(new GameJournalSaveRequest { Id = first.Id, AppId = 7, Kind = "note", Body = IpcSecret.Encode("改过"), Spoiler = false });
        edited.Id.Should().Be(first.Id);
        edited.CreatedAt.Should().Be(1_600_000_000, "编辑不改写下的时间");

        (await _planner.GetJournalAsync(new GameJournalQueryRequest { AppId = 7 })).Select(entry => entry.Body).Should().Equal("结局", "改过");
        (await _planner.GetJournalAsync(null)).Should().HaveCount(3);
        (await _planner.GetSnapshotAsync()).JournalStats.Should().BeEquivalentTo([
            new { AppId = 7, Count = 2 },
            new { AppId = 8, Count = 1 }
        ]);

        await FluentActions.Invoking(() => _planner.SaveJournalAsync(new GameJournalSaveRequest { AppId = 7, Kind = "note", Body = IpcSecret.Encode("   "), Spoiler = false }))
            .Should().ThrowAsync<ArgumentException>();
        (await _planner.DeleteItemAsync(new PlannerItemDeleteRequest { Kind = "journal", Id = first.Id })).Should().BeTrue();
        (await _planner.GetJournalAsync(new GameJournalQueryRequest { AppId = 7 })).Should().ContainSingle();
    }

    [Test]
    public async Task Tags_LinkBySyncIdAndDeletingATagRemovesItsLinks()
    {
        var weekend = await _planner.SaveTagAsync(new GameTagSaveRequest { Name = IpcSecret.Encode("周末"), Color = "violet" });
        var coop = await _planner.SaveTagAsync(new GameTagSaveRequest { Name = IpcSecret.Encode("开黑"), Color = "sky" });
        coop.SortOrder.Should().Be(weekend.SortOrder + 1);

        var links = await _planner.SetTagLinksAsync(new GameTagLinksSetRequest { AppId = 5, TagIds = [weekend.Id, "ffffffffffffffffffffffffffffffff", coop.Id] });
        links.Select(link => link.TagId).Should().Equal(weekend.Id, coop.Id);

        (await _planner.AssignTagAsync(new GameTagAssignRequest { TagId = coop.Id, AppIds = [5, 6, 7], Assigned = true })).Should().Be(2);
        (await _planner.SetTagLinksAsync(new GameTagLinksSetRequest { AppId = 5, TagIds = [coop.Id] })).Should().ContainSingle();
        (await _planner.GetSnapshotAsync()).TagLinks.Should().HaveCount(3);

        await _planner.DeleteItemAsync(new PlannerItemDeleteRequest { Kind = "tag", Id = coop.Id });
        var snapshot = await _planner.GetSnapshotAsync();
        snapshot.Tags.Should().ContainSingle().Which.Name.Should().Be("周末");
        snapshot.TagLinks.Should().BeEmpty();
    }

    [Test]
    public async Task Plans_NeedAGameOrTitleAndNormaliseTheirRange()
    {
        await FluentActions.Invoking(() => _planner.SavePlanAsync(new PlayPlanSaveRequest { Kind = "day", StartDay = 20000, Status = "pending" }))
            .Should().ThrowAsync<ArgumentException>();

        var day = await _planner.SavePlanAsync(new PlayPlanSaveRequest { AppId = 1, Kind = "day", StartDay = 20000, EndDay = 20010, Status = "pending" });
        day.EndDay.Should().BeNull("单日计划没有结束日");

        var bucket = await _planner.SavePlanAsync(new PlayPlanSaveRequest { Title = IpcSecret.Encode("清积压"), Kind = "bucket", Bucket = "month", StartDay = 20000, EndDay = 20029, Status = "pending" });
        bucket.Title.Should().Be("清积压");
        (bucket.Bucket, bucket.EndDay).Should().Be(("month", 20029));

        var moved = await _planner.SavePlanAsync(new PlayPlanSaveRequest { Id = day.Id, AppId = 1, Kind = "range", StartDay = 20005, EndDay = 20003, Status = "done" });
        moved.EndDay.Should().BeNull("结束早于开始时不存");
        moved.Status.Should().Be("done");

        var challenge = await _planner.SaveChallengeAsync(new PlayChallengeSaveRequest { Year = 2026, Kind = "completeGames", Target = 12 });
        var playthrough = await _planner.SavePlaythroughAsync(new GamePlaythroughSaveRequest { AppId = 1, Outcome = "abandoned", CompletionLevel = "beaten", StartedAt = 10, EndedAt = 5 });
        playthrough.CompletionLevel.Should().BeNull("没完成的周目没有完成程度");
        playthrough.EndedAt.Should().BeNull();

        var snapshot = await _planner.GetSnapshotAsync();
        snapshot.Plans.Should().HaveCount(2);
        snapshot.Challenges.Should().ContainSingle().Which.Id.Should().Be(challenge.Id);
        snapshot.Playthroughs.Should().ContainSingle();
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
