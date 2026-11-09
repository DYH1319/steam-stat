using FluentAssertions;
using SteamStat.Core.Insights;
using static SteamStat.Core.Tests.InsightFixtures;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class InsightDetectorTests
{
    private static readonly InsightPeriod Week = new("2026-01-26", "2026-02-01");

    [Test]
    public void ScheduleDrift_NoticesLaterSessions()
    {
        var sessions = Days("2025-12-29", 28).Select(day => Session(1, day, 20, 3600))
            .Concat(Days("2026-01-26", 7).Select(day => Session(1, day, 23, 3600)));

        var insight = new ScheduleDriftDetector().Detect(Archive(sessions), Week).Should().ContainSingle().Subject;

        insight.Facts["shiftHours"].Should().BeApproximately(3, 0.05);
        InsightCopy.Describe(insight).Zh.Should().Contain("晚了 3 小时");
    }

    [Test]
    public void ScheduleDrift_NeedsAnEstablishedBaseline()
        => new ScheduleDriftDetector().Detect(Archive(Days("2026-01-26", 7).Select(day => Session(1, day, 23, 3600))), Week).Should().BeEmpty();

    [Test]
    public void DroppedGenre_ReportsATagWorseThanTheOverallRate()
    {
        var old = "2025-01-01";
        var dropped = Enumerable.Range(1, 10).Select(appId => Session(appId, old, 10, 1800));
        var kept = Enumerable.Range(11, 20).Select(appId => Session(appId, old, 10, 10 * 3600));
        var apps = Enumerable.Range(1, 30).ToDictionary(appId => appId, appId => new InsightAppFacts(appId) { Tags = appId <= 10 ? ["Roguelike"] : ["Strategy"] });

        var insight = new DroppedGenreDetector().Detect(Archive(dropped.Concat(kept)).With(apps: apps), Week).Should().ContainSingle().Subject;

        insight.Labels["tag"].Should().Be("Roguelike");
        insight.Facts["rate"].Should().Be(1);
    }

    [Test]
    public void DroppedGenre_IgnoresGamesStillInTheirFirstMonths()
    {
        var recent = Enumerable.Range(1, 20).Select(appId => Session(appId, "2026-01-20", 10, 1800));
        var apps = Enumerable.Range(1, 20).ToDictionary(appId => appId, appId => new InsightAppFacts(appId) { Tags = ["Roguelike"] });

        new DroppedGenreDetector().Detect(Archive(recent).With(apps: apps), Week).Should().BeEmpty();
    }

    [Test]
    public void PlayStyle_TellsBingeFromSteady()
    {
        var binge = Enumerable.Range(1, 12).SelectMany(appId => Days($"2025-{appId:00}-01", 5).Select(day => Session(appId, day, 20, 2 * 3600)));
        var steady = Enumerable.Range(1, 12).SelectMany(appId => Days("2025-03-01", 30, 10).Select(day => Session(appId, day, 20, 3600)));

        new PlayStyleDetector().Detect(Archive(binge), Week).Single().Labels["style"].Should().Be("binge");
        new PlayStyleDetector().Detect(Archive(steady), Week).Single().Labels["style"].Should().Be("steady");
    }

    [Test]
    public void Comeback_PrefersAnUpdateOverACalendarCoincidence()
    {
        var archive = Archive(
        [
            Session(1, "2025-10-01", 20, 2 * 3600),
            Session(1, "2026-01-30", 20, 3 * 3600, tags: SessionTags.Comeback)
        ]).With(builds: [new InsightBuildChange(1, 5, Unix("2026-01-28"), false)]);

        var comeback = ComebackAnalysis.Find(archive, Week.FromDay, Week.ToDay).Should().ContainSingle().Subject;

        comeback.GapDays.Should().BeGreaterThan(100);
        comeback.MainCause!.Kind.Should().Be("update");
        comeback.MainCause.DaysBefore.Should().Be(2);
    }

    [Test]
    public void Comeback_FindsDlcBoughtBeforeReturning()
    {
        var archive = Archive(
        [
            Session(1, "2025-10-01", 20, 2 * 3600),
            Session(1, "2026-01-30", 20, 3 * 3600, tags: SessionTags.Comeback)
        ]).With(dlcs: [new InsightDlc(1, 99)], acquisitions: [new InsightAcquisition(99, Unix("2026-01-20"))]);

        ComebackAnalysis.Find(archive, Week.FromDay, Week.ToDay).Single().MainCause!.Kind.Should().Be("dlc");
    }

    [Test]
    public void Comeback_FindsAFriendWhoWasPlaying()
    {
        var archive = Archive(
        [
            Session(1, "2025-10-01", 20, 2 * 3600),
            Session(1, "2026-01-30", 20, 3 * 3600, tags: SessionTags.Comeback)
        ]).With(friends: new InsightFriendData([new InsightFriendPlay("7", "Pal", 1, Unix("2026-01-28"), Unix("2026-01-28", 3))], [], []));

        var cause = ComebackAnalysis.Find(archive, Week.FromDay, Week.ToDay).Single().MainCause!;

        cause.Kind.Should().Be("friend");
        cause.Name!.Zh.Should().Contain("[[f:7|Pal]]");
    }

    [Test]
    public void Comeback_WithoutCluesIsNotAnInsightButIsStillFound()
    {
        var archive = Archive(
        [
            Session(1, "2025-10-01", 20, 2 * 3600),
            Session(1, "2026-01-30", 20, 3 * 3600, tags: SessionTags.Comeback)
        ], options: Options with { Language = "en" });

        ComebackAnalysis.Find(archive, Week.FromDay, Week.ToDay).Should().ContainSingle();
        InsightEngine.CreateDefault().Run(archive, Week).Should().NotContain(insight => insight.Id == "causal.comeback");
    }

    [Test]
    public void Comeback_NeedsSomethingToComeBackTo()
    {
        var archive = Archive(
        [
            Session(1, "2025-10-01", 20, 600),
            Session(1, "2026-01-30", 20, 3 * 3600, tags: SessionTags.Comeback)
        ]);

        ComebackAnalysis.Find(archive, Week.FromDay, Week.ToDay).Should().BeEmpty("只玩过十分钟谈不上回坑");
    }

    [Test]
    public void BacklogClearance_DividesByLastYearsPace()
    {
        var owned = Enumerable.Range(100, 40).Select(appId => new InsightOwnedGame(appId, $"Game {appId}", 0, 0)).ToList();
        var started = Enumerable.Range(1, 20).Select(appId => Session(appId, "2025-06-01", 10, 3 * 3600));

        var insight = new BacklogClearanceDetector().Detect(Archive(started).With(owned: owned), Week).Should().ContainSingle().Subject;

        insight.Facts["backlog"].Should().Be(40);
        insight.Facts["perYear"].Should().Be(20);
        insight.Facts["years"].Should().Be(2);
    }

    [Test]
    public void Backlog_LeavesOutGamesMarkedDone()
    {
        var archive = Archive().With(
            owned: [new InsightOwnedGame(1, "A", 0, 0), new InsightOwnedGame(2, "B", 0, 0), new InsightOwnedGame(3, "C", 0, 0), new InsightOwnedGame(4, "D", 90, 0)],
            statuses: [new InsightGameStatus(1, "completed", 0, null, null, null), new InsightGameStatus(2, "abandoned", 0, null, null, null)]);

        BacklogClearanceDetector.Backlog(archive).Select(game => game.AppId).Should().Equal(3);
    }

    [Test]
    public void CompletionDate_ExtrapolatesTheRecentPace()
    {
        var unlocks = Enumerable.Range(0, 30).Select(index => new InsightUnlock(1, $"a{index}", "A", 10, Unix("2026-01-10"), "2026-01-10")).ToList();
        var archive = Archive().With(unlocks: unlocks, progress: [new InsightProgress(1, "Game", 100, 70)]);

        var insight = new CompletionDateDetector().Detect(archive, Week).Should().ContainSingle().Subject;

        insight.Facts["remaining"].Should().Be(30);
        insight.Facts["days"].Should().BeApproximately(90, 0.1);
    }

    [Test]
    public void CompletionDate_SkipsFinishedAndBarelyStartedGames()
    {
        var unlocks = Enumerable.Range(0, 30).Select(index => new InsightUnlock(1, $"a{index}", "A", 10, Unix("2026-01-10"), "2026-01-10")).ToList();

        new CompletionDateDetector().Detect(Archive().With(unlocks: unlocks, progress: [new InsightProgress(1, "Game", 100, 100)]), Week).Should().BeEmpty();
        new CompletionDateDetector().Detect(Archive().With(unlocks: unlocks, progress: [new InsightProgress(1, "Game", 100, 10)]), Week).Should().BeEmpty();
    }

    [Test]
    public void AbandonRisk_ComesFromYourOwnHistory()
    {
        // 十二款老游戏：其中九款走到这一步之后再也没回去
        var history = Enumerable.Range(10, 12).SelectMany(appId => new[]
        {
            Session(appId, "2024-01-01", 20, 3600),
            Session(appId, "2024-01-02", 20, 3600),
            Session(appId, appId < 13 ? "2024-01-20" : "2024-01-03", 20, 3600)
        });
        var current = Days("2025-12-01", 6, 8).Select(day => Session(1, day, 20, 2 * 3600));

        var insight = new AbandonRiskDetector().Detect(Archive(history.Concat(current)), Week).Should().ContainSingle().Subject;

        insight.AppId.Should().Be(1);
        insight.Facts["probability"].Should().BeGreaterThan(0.5);
    }

    [Test]
    public void AbandonRisk_LeavesManualStatusesAlone()
    {
        var history = Enumerable.Range(10, 12).SelectMany(appId => Days("2024-01-01", 3).Select(day => Session(appId, day, 20, 3600)));
        var current = Days("2025-12-01", 6, 8).Select(day => Session(1, day, 20, 2 * 3600));
        var archive = Archive(history.Concat(current)).With(statuses: [new InsightGameStatus(1, "playing", 0, null, null, null)]);

        new AbandonRiskDetector().Detect(archive, Week).Should().BeEmpty();
    }

    [Test]
    public void PlaytimeSpike_ComparesWithTheSameLengthOfTimeBefore()
    {
        var usual = Enumerable.Range(1, 12).SelectMany(index => Enumerable.Range(0, 5).Select(day => Session(1, InsightDays.Shift("2026-01-26", -7 * index + day), 20, 3600)));
        var spike = Days("2026-01-26", 7).Select(day => Session(1, day, 10, 8 * 3600));

        var insight = new PlaytimeSpikeDetector().Detect(Archive(usual.Concat(spike)), Week).Should().ContainSingle().Subject;

        insight.Id.Should().Be("anomaly.playtimeSpike");
        insight.Facts["hours"].Should().Be(56);
    }

    [Test]
    public void PlaytimeSpike_WaitsUntilThereIsAHistory()
        => new PlaytimeSpikeDetector().Detect(Archive(Days("2026-01-26", 7).Select(day => Session(1, day, 10, 8 * 3600))), Week).Should().BeEmpty();

    [Test]
    public void OfficialDiff_NeedsANotableShare()
    {
        var notable = Archive().With(reconciliation: new InsightReconciliation(20, 3, 1, []));
        var minor = Archive().With(reconciliation: new InsightReconciliation(100, 1, 1, []));

        new OfficialDiffDetector().Detect(notable, Week).Single().Facts["share"].Should().Be(0.2);
        new OfficialDiffDetector().Detect(minor, Week).Should().BeEmpty();
    }

    [Test]
    public void RecordGap_OnlyCountsGapsTouchingThePeriod()
    {
        var archive = Archive().With(reconciliation: new InsightReconciliation(1, 0, 0,
        [
            new InsightGap("2026-01-28", "2026-01-29", 7200),
            new InsightGap("2025-06-01", "2025-06-02", 7200)
        ]));

        var insight = new RecordGapDetector().Detect(archive, Week).Should().ContainSingle().Subject;

        insight.Facts["days"].Should().Be(2);
        insight.IsEvent.Should().BeTrue();
    }
}
