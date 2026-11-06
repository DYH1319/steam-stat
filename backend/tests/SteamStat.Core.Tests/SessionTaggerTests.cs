using FluentAssertions;
using SteamStat.Core.Insights;
using static SteamStat.Core.Tests.InsightFixtures;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SessionTaggerTests
{
    private static readonly SessionTagRules Rules = new(30, TimeZoneInfo.Utc);

    private static TagCandidate Record(long id, string day, double hour, long seconds, long idle = 0, int appId = 1)
    {
        var start = Unix(day, hour);
        return new TagCandidate(id, Me, appId, start, start + seconds, seconds, idle);
    }

    [TestCase(23.5, 3600L, true, TestName = "一局跨过午夜半小时算熬夜")]
    [TestCase(23.6, 2400L, false, TestName = "跨过午夜不到半小时不算")]
    [TestCase(1, 3600L, true, TestName = "凌晨一点开局算熬夜")]
    [TestCase(5, 3600L, false, TestName = "五点之后不算")]
    public void LateNight_NeedsHalfAnHourInTheNightWindow(double hour, long seconds, bool expected)
    {
        var tags = SessionTagger.Tag([Record(1, "2026-01-05", hour, seconds)], Rules);

        (tags[1].HasFlag(SessionTags.LateNight)).Should().Be(expected);
    }

    [Test]
    public void NightSeconds_CountsEveryNightOfAVeryLongSession()
        => SessionTagger.NightSeconds(Unix("2026-01-05", 22), Unix("2026-01-07", 2), TimeZoneInfo.Utc).Should().Be((5 + 2) * 3600);

    [Test]
    public void Idle_NeedsHalfTheTimeAndALongEnoughSession()
    {
        var tags = SessionTagger.Tag([Record(1, "2026-01-05", 10, 3600, idle: 1800), Record(2, "2026-01-06", 10, 3600, idle: 1700), Record(3, "2026-01-07", 10, 1000, idle: 1000)], Rules);

        tags[1].Should().HaveFlag(SessionTags.Idle);
        tags[2].Should().NotHaveFlag(SessionTags.Idle);
        tags[3].Should().NotHaveFlag(SessionTags.Idle);
    }

    [Test]
    public void Comeback_IsMeasuredPerGameAndNeverOnTheFirstSession()
    {
        var tags = SessionTagger.Tag(
        [
            Record(1, "2026-01-01", 10, 3600),
            Record(2, "2026-01-20", 10, 3600),
            Record(3, "2026-03-01", 10, 3600),
            Record(4, "2026-03-01", 12, 3600, appId: 2)
        ], Rules);

        tags[1].Should().NotHaveFlag(SessionTags.Comeback);
        tags[2].Should().NotHaveFlag(SessionTags.Comeback);
        tags[3].Should().HaveFlag(SessionTags.Comeback);
        tags[4].Should().NotHaveFlag(SessionTags.Comeback);
    }

    [Test]
    public void Crash_ThreeShortLaunchesInARowAreOneBurst()
    {
        var start = Unix("2026-01-05", 20);
        var records = new[]
        {
            new TagCandidate(1, Me, 1, start, start + 30, 30, 0),
            new TagCandidate(2, Me, 1, start + 90, start + 120, 30, 0),
            new TagCandidate(3, Me, 1, start + 200, start + 240, 40, 0),
            new TagCandidate(4, Me, 1, start + 300, start + 4000, 3700, 0)
        };

        var (burst, ids) = CrashDetector.Detect(records).Should().ContainSingle().Subject;

        burst.Attempts.Should().Be(3);
        burst.Seconds.Should().Be(100);
        ids.Should().Equal(1, 2, 3);
        SessionTagger.Tag(records, Rules)[4].Should().NotHaveFlag(SessionTags.Crash);
    }

    [Test]
    public void Crash_TwoShortSessionsOrALongGapAreNotABurst()
    {
        var start = Unix("2026-01-05", 20);
        CrashDetector.Detect([new TagCandidate(1, Me, 1, start, start + 30, 30, 0), new TagCandidate(2, Me, 1, start + 60, start + 90, 30, 0)]).Should().BeEmpty();
        CrashDetector.Detect(
        [
            new TagCandidate(1, Me, 1, start, start + 30, 30, 0),
            new TagCandidate(2, Me, 1, start + 600, start + 630, 30, 0),
            new TagCandidate(3, Me, 1, start + 1200, start + 1230, 30, 0)
        ]).Should().BeEmpty();
    }

    [Test]
    public void Crash_DoesNotCountAsTheLastPlayForComebacks()
    {
        var start = Unix("2026-03-01", 10);
        var records = new[]
        {
            Record(1, "2026-01-01", 10, 3600),
            new TagCandidate(2, Me, 1, start, start + 30, 30, 0),
            new TagCandidate(3, Me, 1, start + 60, start + 90, 30, 0),
            new TagCandidate(4, Me, 1, start + 120, start + 150, 30, 0),
            new TagCandidate(5, Me, 1, start + 600, start + 4200, 3600, 0)
        };

        SessionTagger.Tag(records, Rules)[5].Should().HaveFlag(SessionTags.Comeback);
    }

    [Test]
    public void UpdateSuspicion_NeedsARealChangeInTheWindow()
    {
        var burst = new CrashBurst(Me, 1, Unix("2026-01-05", 20), Unix("2026-01-05", 20) + 300, 3, 90, 100);

        CrashDetector.WithUpdateSuspicion([burst], [new InsightBuildChange(1, 100, Unix("2026-01-05", 8), false)]).Single().AfterUpdate.Should().BeTrue();
        CrashDetector.WithUpdateSuspicion([burst], [new InsightBuildChange(1, 100, Unix("2026-01-05", 8), true)]).Single().AfterUpdate.Should().BeFalse("第一次看到只是基线");
        CrashDetector.WithUpdateSuspicion([burst], [new InsightBuildChange(1, 100, Unix("2026-01-01", 8), false)]).Single().AfterUpdate.Should().BeFalse("太久以前了");
        CrashDetector.WithUpdateSuspicion([burst], [new InsightBuildChange(2, 100, Unix("2026-01-05", 8), false)]).Single().AfterUpdate.Should().BeFalse("别的游戏");
    }

    [TestCase(new string[0], true, TestName = "没有标签的游戏按可以联机处理")]
    [TestCase(new[] { "Singleplayer", "RPG" }, false, TestName = "纯单机不算")]
    [TestCase(new[] { "RPG", "Online Co-Op" }, true, TestName = "有联机标签算")]
    public void CanPlayTogether_MatchesTheRendererRule(string[] tags, bool expected)
        => SessionTagger.CanPlayTogether(tags).Should().Be(expected);
}

[TestFixture]
public sealed class FriendTimelineTests
{
    private static FriendEventRow Row(string type, string current, long time, string? previous = null) => new("f1", "Friend", type, previous, current, time);

    [Test]
    public void Build_ClosesAGameWhenTheFriendSwitchesOrLeaves()
    {
        var data = FriendTimeline.Build(
        [
            Row("game", """{"gameId":"10"}""", 100),
            Row("game", """{"gameId":"0"}""", 200),
            Row("game", """{"gameId":"20"}""", 300),
            Row("state", """{"personaState":0}""", 400)
        ]);

        data.Plays.Select(play => (play.AppId, play.Start, play.End)).Should().Equal((10, 100L, 200L), (20, 300L, 400L));
    }

    [Test]
    public void Build_DropsIntervalsThatNeverClose()
        => FriendTimeline.Build([Row("game", """{"gameId":"10"}""", 100)]).Plays.Should().BeEmpty();

    [Test]
    public void Build_CapsAnIntervalThatRanThroughAGapInObservation()
        => FriendTimeline.Build([Row("game", """{"gameId":"10"}""", 0), Row("game", """{"gameId":"0"}""", 100 * 3600)]).Plays.Single().End.Should().Be(12 * 3600);

    [Test]
    public void Build_SurvivesBrokenJson()
        => FriendTimeline.Build([Row("game", "not json", 100)]).Plays.Should().BeEmpty();

    [Test]
    public void Build_ReadsRelationshipsAndRenames()
    {
        var data = FriendTimeline.Build(
        [
            Row("relationship", """{"relationship":3}""", 100),
            Row("relationship", """{"relationship":0}""", 200),
            Row("personaName", """{"personaName":"New"}""", 300, """{"personaName":"Old"}""")
        ]);

        data.Changes.Select(change => change.Kind).Should().Equal("added", "removed", "renamed");
        data.Changes[2].From.Should().Be("Old");
        data.Changes[2].To.Should().Be("New");
    }

    [Test]
    public void OverlapSeconds_CountsTwoFriendsOnlyOnce()
    {
        var data = new InsightFriendData(
            [new InsightFriendPlay("a", "A", 1, 100, 300), new InsightFriendPlay("b", "B", 1, 200, 400), new InsightFriendPlay("c", "C", 2, 100, 900)],
            [],
            []);

        data.OverlapSeconds(1, 150, 350).Should().Be(200);
        data.OverlapSeconds(3, 0, 1000).Should().Be(0);
    }
}

[TestFixture]
public sealed class GameLifecycleTests
{
    [TestCase(null, GameLifecycle.Unplayed)]
    [TestCase(10L, GameLifecycle.Active)]
    [TestCase(30L, GameLifecycle.Paused)]
    [TestCase(89L, GameLifecycle.Paused)]
    [TestCase(90L, GameLifecycle.Abandoned)]
    public void Infer_UsesTheTwoThresholds(long? daysAgo, string expected)
    {
        const long now = 1_000_000_000;
        GameLifecycle.Infer(daysAgo == null ? null : now - daysAgo * 86400, now, 30, 90).Should().Be(expected);
    }
}

[TestFixture]
public sealed class SteamCalendarTests
{
    [Test]
    public void SpringFestival_FollowsTheLunarCalendar()
    {
        SteamCalendar.SpringFestival(2026).Should().Be(new DateOnly(2026, 2, 17));
        SteamCalendar.SpringFestival(2025).Should().Be(new DateOnly(2025, 1, 29));
    }

    [Test]
    public void Holidays_DependOnTheLanguage()
    {
        SteamCalendar.HolidayOn(new DateOnly(2026, 10, 3), "zh")!.Id.Should().Be("national");
        SteamCalendar.HolidayOn(new DateOnly(2026, 10, 3), "en").Should().BeNull();
        SteamCalendar.HolidayOn(new DateOnly(2026, 12, 25), "en")!.Id.Should().Be("christmas");
    }

    [Test]
    public void WinterSale_CrossesTheNewYear()
    {
        SteamCalendar.SaleOn(new DateOnly(2026, 12, 25))!.Id.Should().Be("winterSale");
        SteamCalendar.SaleOn(new DateOnly(2026, 1, 3))!.Id.Should().Be("winterSale");
        SteamCalendar.SaleOn(new DateOnly(2026, 2, 3)).Should().BeNull();
    }
}

[TestFixture]
public sealed class InsightMathTests
{
    [Test]
    public void CircularMean_AveragesAcrossMidnight()
    {
        var mean = InsightMath.CircularMeanHour([23, 1], out var concentration);

        Math.Min(mean, 24 - mean).Should().BeApproximately(0, 0.001);
        concentration.Should().BeGreaterThan(0.9);
    }

    [TestCase(23, 1, 2)]
    [TestCase(1, 23, -2)]
    [TestCase(10, 12, 2)]
    public void HourDifference_TakesTheShortWayRound(double from, double to, double expected)
        => InsightMath.HourDifference(from, to).Should().BeApproximately(expected, 0.001);
}
