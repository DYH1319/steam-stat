using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Archives;
using SteamStat.Core.Circle;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Insights;
using static SteamStat.Core.Tests.InsightFixtures;

namespace SteamStat.Core.Tests.Circle;

[TestFixture]
public sealed class SpansTests
{
    [Test]
    public void Union_MergesTouchingAndOverlapping()
        => Spans.Union([new Span(10, 20), new Span(5, 12), new Span(20, 25), new Span(40, 50)])
            .Should().Equal(new Span(5, 25), new Span(40, 50));

    [Test]
    public void Subtract_CutsHolesOutOfEverySpan()
        => Spans.Subtract([new Span(0, 100)], [new Span(10, 20), new Span(90, 120)])
            .Should().Equal(new Span(0, 10), new Span(20, 90));

    [Test]
    public void Intersect_KeepsOnlyCommonTime()
        => Spans.Intersect([new Span(0, 50), new Span(60, 100)], [new Span(40, 70)])
            .Should().Equal(new Span(40, 50), new Span(60, 70));

    [Test]
    public void WeekHours_SplitsOnTheHourAndAlignsToWeekStart()
    {
        // 2026-02-02 是星期一，WeekStart = 1 时是第 0 天
        var matrix = WeekHours.From([new Span(Unix("2026-02-02", 20.5), Unix("2026-02-02", 22))], Options);
        matrix[20].Should().Be(1800);
        matrix[21].Should().Be(3600);
        matrix.Sum().Should().Be(5400);
        WeekHours.Index(Unix("2026-02-08", 23), Options).Should().Be(6 * 24 + 23);
    }

    [Test]
    public void WeekHours_ShiftWrapsAroundTheWeek()
    {
        var matrix = new double[WeekHours.Cells];
        matrix[0] = 1;
        WeekHours.Shift(matrix, -1)[WeekHours.Cells - 1].Should().Be(1);
        WeekHours.Shift(matrix, 8)[8].Should().Be(1);
    }
}

[TestFixture]
public sealed class TogetherDetectorTests
{
    private const string Friend = "76561198000000009";
    private const string Other = "76561198000000010";
    private static readonly long Start = Unix("2026-02-02", 20);

    private static InsightFriendData Plays(params InsightFriendPlay[] plays) => new(plays, [], []);

    private static TogetherSession MySession(long start, long seconds, int appId = 1) => new(100, Me, appId, start, start + seconds);

    private static IReadOnlyList<GroupSpan> Lobby(params (string SteamId, string Key, long From, long To)[] items)
        => GroupTimeline.Build(items.SelectMany(item => new[]
        {
            new GroupObservation(item.SteamId, 1, PresenceGroupKinds.Lobby, item.Key, item.From),
            new GroupObservation(item.SteamId, 1, PresenceGroupKinds.Lobby, string.Empty, item.To)
        }));

    [Test]
    public void WithoutGroupData_SameOnlineGameAtTheSameTimeStillCounts()
    {
        var result = TogetherDetector.Detect([MySession(Start, 3600)], Plays(new InsightFriendPlay(Friend, "Pal", 1, Start + 600, Start + 3000)), [], _ => true);

        result.Segments.Should().ContainSingle().Which.Should().Be(new TogetherSegment(100, Me, Friend, 1, Start + 600, Start + 3000, 2400, 0));
        result.CoopSeconds[100].Should().Be(2400);
        result.ConfirmedSeconds.Should().BeEmpty();
    }

    [Test]
    public void SameLobby_IsConfirmed()
    {
        var groups = Lobby((Me, "a", Start, Start + 1800), (Friend, "a", Start + 300, Start + 3600));
        var result = TogetherDetector.Detect([MySession(Start, 3600)], Plays(new InsightFriendPlay(Friend, "Pal", 1, Start, Start + 3600)), groups, _ => true);

        var segment = result.Segments.Should().ContainSingle().Subject;
        segment.Seconds.Should().Be(3600);
        segment.ConfirmedSeconds.Should().Be(1500);
        segment.Confirmed.Should().BeTrue();
        result.ConfirmedSeconds[100].Should().Be(1500);
    }

    [Test]
    public void DifferentLobbies_AreNotTogether()
    {
        var groups = Lobby((Me, "a", Start, Start + 3600), (Friend, "b", Start, Start + 3600));
        var result = TogetherDetector.Detect([MySession(Start, 3600)], Plays(new InsightFriendPlay(Friend, "Pal", 1, Start, Start + 3600)), groups, _ => true);

        result.Segments.Should().BeEmpty();
        result.CoopSeconds.Should().BeEmpty();
    }

    [Test]
    public void DifferentLobbies_OnlyRemoveTheTimeTheyCover()
    {
        var groups = Lobby((Me, "a", Start, Start + 1200), (Friend, "b", Start, Start + 1200));
        var result = TogetherDetector.Detect([MySession(Start, 3600)], Plays(new InsightFriendPlay(Friend, "Pal", 1, Start, Start + 3600)), groups, _ => true);

        result.Segments.Should().ContainSingle().Which.Seconds.Should().Be(2400);
    }

    [Test]
    public void SameGroupInOneKind_WinsOverADifferentServer()
    {
        var observations = new[]
        {
            new GroupObservation(Me, 1, PresenceGroupKinds.Lobby, "lobby", Start),
            new GroupObservation(Friend, 1, PresenceGroupKinds.Lobby, "lobby", Start),
            new GroupObservation(Me, 1, PresenceGroupKinds.Server, "s1", Start),
            new GroupObservation(Friend, 1, PresenceGroupKinds.Server, "s2", Start)
        };
        var result = TogetherDetector.Detect([MySession(Start, 3600)], Plays(new InsightFriendPlay(Friend, "Pal", 1, Start, Start + 3600)), GroupTimeline.Build(observations), _ => true);

        result.Segments.Should().ContainSingle().Which.ConfirmedSeconds.Should().Be(3600);
    }

    [Test]
    public void SingleplayerGames_OnlyCountConfirmedTime()
    {
        var play = Plays(new InsightFriendPlay(Friend, "Pal", 1, Start, Start + 3600));
        TogetherDetector.Detect([MySession(Start, 3600)], play, [], _ => false).Segments.Should().BeEmpty();

        var groups = Lobby((Me, "a", Start, Start + 1800), (Friend, "a", Start, Start + 1800));
        TogetherDetector.Detect([MySession(Start, 3600)], play, groups, _ => false).Segments.Should().ContainSingle().Which.Seconds.Should().Be(1800);
    }

    [Test]
    public void TwoFriends_GetTheirOwnSegmentsButCoopCountsOverlapOnce()
    {
        var plays = Plays(
            new InsightFriendPlay(Friend, "Pal", 1, Start, Start + 2400),
            new InsightFriendPlay(Other, "Buddy", 1, Start + 1200, Start + 3600));
        var result = TogetherDetector.Detect([MySession(Start, 3600)], plays, [], _ => true);

        result.Segments.Select(segment => (segment.FriendSteamId, segment.Seconds)).Should().BeEquivalentTo([(Friend, 2400L), (Other, 2400L)]);
        result.CoopSeconds[100].Should().Be(3600);
    }

    [Test]
    public void DuplicatePlaysFromTwoObservingAccounts_AreNotCountedTwice()
    {
        var plays = Plays(
            new InsightFriendPlay(Friend, "Pal", 1, Start, Start + 1800),
            new InsightFriendPlay(Friend, "Pal", 1, Start, Start + 1800));
        TogetherDetector.Detect([MySession(Start, 3600)], plays, [], _ => true).Segments.Should().ContainSingle().Which.Seconds.Should().Be(1800);
    }

    [Test]
    public void ShortOverlaps_AreNotSegments()
        => TogetherDetector.Detect([MySession(Start, 3600)], Plays(new InsightFriendPlay(Friend, "Pal", 1, Start + 3400, Start + 4000)), [], _ => true)
            .Segments.Should().BeEmpty();

    [Test]
    public void GroupSpans_StopAtTheCapWhenNothingClosesThem()
    {
        var spans = GroupTimeline.Build([new GroupObservation(Me, 1, PresenceGroupKinds.Lobby, "a", 0)]);
        spans.Should().ContainSingle().Which.End.Should().Be(GroupTimeline.MaxSpanSeconds);
    }

    [Test]
    public void RichPresenceGroups_AreReadFromRawValues()
    {
        var joined = GroupTimeline.FromRichPresence(Me, 1, new Dictionary<string, string> { ["Steam_Player_Group"] = "abc" }, 10);
        var left = GroupTimeline.FromRichPresence(Me, 1, new Dictionary<string, string> { ["status"] = "menu" }, 20);
        joined.Key.Should().Be(PresenceGroupKeys.RichPresence(1, "abc")).And.NotBeEmpty();
        left.Key.Should().BeEmpty();
        GroupTimeline.Build([joined, left]).Should().ContainSingle().Which.End.Should().Be(20);
    }

    [Test]
    public void GroupKeys_AreHashesThatIncludeTheApp()
    {
        PresenceGroupKeys.Lobby(1, 42).Should().HaveLength(24).And.NotContain("42");
        PresenceGroupKeys.Lobby(1, 42).Should().NotBe(PresenceGroupKeys.Lobby(2, 42));
        PresenceGroupKeys.Lobby(1, 0).Should().BeEmpty();
        PresenceGroupKeys.Server(1, 0x0A000001, 0).Should().BeEmpty();
    }
}

[TestFixture]
public sealed class FriendshipTests
{
    private const string Old = "76561198000000011";
    private const string New = "76561198000000012";
    private const string Gone = "76561198000000013";

    [Test]
    public void Since_PrefersSteamThenTheRecordedAdd()
    {
        var report = Friendship.Build(
            [new CircleFriend(Old, "Old", true, true, 0, true), new CircleFriend(New, "New", true, false, 0, false)],
            new Dictionary<string, long> { [Old] = Unix("2020-03-01") },
            [new InsightFriendChange(New, "New", "added", null, null, Unix("2026-01-20"))],
            [],
            Options,
            Now);

        var old = report.Bonds.Single(bond => bond.SteamId == Old);
        old.Since.Should().Be(Unix("2020-03-01"));
        old.SinceEvidence.Should().Be(SinceEvidence.Official);
        old.NextAnniversary.Should().Be(Unix("2026-03-01"));
        old.NextAnniversaryYears.Should().Be(6);
        var fresh = report.Bonds.Single(bond => bond.SteamId == New);
        fresh.SinceEvidence.Should().Be(SinceEvidence.Recorded);
        fresh.Status.Should().Be(BondStatus.New);
        report.Events.Where(item => item.Kind == FriendshipEventKinds.Anniversary).Select(item => item.Value).Should().BeEquivalentTo([1L, 3L, 5L]);
    }

    [Test]
    public void RemovedFriends_ComeFromRecordsEvenWhenNotInTheSnapshot()
    {
        var report = Friendship.Build([], new Dictionary<string, long>(),
            [new InsightFriendChange(Gone, "Wren", "added", null, null, Unix("2025-01-01")), new InsightFriendChange(Gone, "Wren", "removed", null, null, Unix("2026-01-01"))],
            [], Options, Now);

        var bond = report.Bonds.Should().ContainSingle().Subject;
        bond.Status.Should().Be(BondStatus.Removed);
        bond.RemovedAt.Should().Be(Unix("2026-01-01"));
        bond.NextAnniversary.Should().BeNull();
        report.Events.Select(item => item.Kind).Should().Equal(FriendshipEventKinds.Removed, FriendshipEventKinds.Added);
    }

    [Test]
    public void LostFriends_PlayedALotButNotForHalfAYear()
    {
        var together = new[]
        {
            new TogetherSegment(1, Me, Old, 7, Unix("2024-05-01", 20), Unix("2024-05-01", 23), 3 * 3600, 3600),
            new TogetherSegment(2, Me, Old, 7, Unix("2025-04-01", 20), Unix("2025-04-01", 21), 3600, 0)
        };
        var report = Friendship.Build([new CircleFriend(Old, "Old", true, true, 0, false)], new Dictionary<string, long>(), [], together, Options, Now);

        var bond = report.Bonds.Single();
        bond.Status.Should().Be(BondStatus.Lost);
        bond.TogetherSeconds.Should().Be(4 * 3600);
        bond.ConfirmedSeconds.Should().Be(3600);
        bond.FirstTogether.Should().Be(new TogetherMoment(Unix("2024-05-01", 20), 7, true));
        bond.PeakYear.Should().Be(2024);
        bond.Years.Should().Equal(new YearSeconds(2024, 3 * 3600), new YearSeconds(2025, 3600));
        report.Events.Should().Contain(item => item.Kind == FriendshipEventKinds.Lost && item.Time == Unix("2025-04-01", 21) + Friendship.LostAfterSeconds);
        report.Events.Should().Contain(item => item.Kind == FriendshipEventKinds.Peak && item.Value == 3 * 3600);
    }

    [Test]
    public void FriendsWhoPlayedALittleLongAgo_AreOccasionalNotStrangers()
    {
        var together = new[] { new TogetherSegment(1, Me, Old, 7, Unix("2025-01-01", 20), Unix("2025-01-01", 21), 720, 0) };
        var bonds = Friendship.Build([new CircleFriend(Old, "Old", true, true, 0, false), new CircleFriend(New, "New", true, true, 0, false)], new Dictionary<string, long>(), [], together, Options, Now).Bonds;
        bonds.Single(bond => bond.SteamId == Old).Status.Should().Be(BondStatus.Occasional);
        bonds.Single(bond => bond.SteamId == New).Status.Should().Be(BondStatus.Quiet);
    }

    [Test]
    public void DormantFriends_HaveNotBeenOnlineForHalfAYear()
        => Friendship.Build([new CircleFriend(Old, "Old", true, false, Now - 200 * 86400L, false)], new Dictionary<string, long>(), [], [], Options, Now)
            .Bonds.Single().Status.Should().Be(BondStatus.Dormant);

    [Test]
    public void SameDayRecordedAdd_IsNotRepeatedNextToTheOfficialDate()
    {
        var report = Friendship.Build([new CircleFriend(Old, "Old", true, false, 0, false)],
            new Dictionary<string, long> { [Old] = Unix("2026-01-10", 10) },
            [new InsightFriendChange(Old, "Old", "added", null, null, Unix("2026-01-10", 11))], [], Options, Now);
        report.Events.Should().ContainSingle().Which.Kind.Should().Be(FriendshipEventKinds.Met);
    }
}

[TestFixture]
public sealed class CompatibilityTests
{
    private static double[] Evening(double hours)
    {
        var matrix = new double[WeekHours.Cells];
        for (var day = 0; day < 7; day++) matrix[day * 24 + 21] = hours * 3600 / 7;
        return matrix;
    }

    private static Dictionary<int, int> Games(params (int AppId, int Minutes)[] games) => games.ToDictionary(game => game.AppId, game => game.Minutes);

    [Test]
    public void SimilarPeople_ScoreHighAndShareTheirFavourites()
    {
        var library = Games((1, 6000), (2, 3000), (3, 1200), (4, 600), (5, 300));
        var score = Compatibility.Score(new CompatibilityInput(Evening(40), Evening(30), library, library, new Dictionary<int, InsightAppFacts>(), 20 * 3600));

        score.Schedule.Should().BeApproximately(1, 1e-9);
        score.Taste.Should().BeApproximately(1, 1e-9);
        score.Together.Should().BeApproximately(1 - Math.Exp(-1), 1e-9);
        score.Overall.Should().BeGreaterThan(0.85);
        score.TopShared.First().Should().Be(1);
        score.BestCell.Should().Be(21);
    }

    [Test]
    public void ThinSamples_LeaveTheirPartEmptyInsteadOfScoringZero()
    {
        var score = Compatibility.Score(new CompatibilityInput(Evening(40), Evening(2), Games((1, 10)), Games((1, 10)), new Dictionary<int, InsightAppFacts>(), 0));
        score.Schedule.Should().BeNull();
        score.Taste.Should().BeNull();
        score.Together.Should().BeNull();
        score.Overall.Should().BeNull();
    }

    [Test]
    public void WeightedJaccard_UsesLogMinutes()
    {
        Compatibility.WeightedJaccard(Games((1, 100)), Games((2, 100))).Should().Be(0);
        Compatibility.WeightedJaccard(Games((1, 100)), Games((1, 100))).Should().Be(1);
        Compatibility.WeightedJaccard(Games((1, 30000)), Games((1, 3000))).Should().BeGreaterThan(0.7);
    }
}

[TestFixture]
public sealed class PresenceForecastTests
{
    [Test]
    public void Probability_IsOnlineTimeOverObservedTime()
    {
        var monday = Unix("2026-01-26");
        var observed = new[] { new Span(monday + 20 * 3600, monday + 22 * 3600), new Span(monday + 7 * 86400 + 20 * 3600, monday + 7 * 86400 + 22 * 3600) };
        var online = new[] { new Span(monday + 20 * 3600, monday + 21 * 3600) };

        var cells = PresenceForecast.Build(online, observed, Options, monday, monday + 14 * 86400);

        cells[20].Should().Be(0.5);
        cells[21].Should().Be(0);
        cells[22].Should().BeNull("应用没开着的时段不知道对方在不在线");
    }

    [Test]
    public void OnlineTimeOutsideObservation_IsIgnored()
    {
        var monday = Unix("2026-01-26");
        var cells = PresenceForecast.Build([new Span(monday, monday + 86400)], [new Span(monday + 3 * 3600, monday + 4 * 3600)], Options, monday, monday + 86400);
        cells[3].Should().Be(1);
        cells.Count(cell => cell != null).Should().Be(1);
    }
}

[TestFixture]
public sealed class PartyPlannerTests
{
    private static Dictionary<int, PartyOwnership> Library(params int[] appIds) => appIds.ToDictionary(id => id, id => new PartyOwnership(id * 10, id == 3 ? 30 : 0, 0));

    private static readonly Dictionary<int, InsightAppFacts> Facts = new()
    {
        [1] = new InsightAppFacts(1) { Tags = ["Co-op"] },
        [2] = new InsightAppFacts(2) { Tags = ["Singleplayer"] },
        [3] = new InsightAppFacts(3) { Tags = ["Multiplayer"] },
        [4] = new InsightAppFacts(4) { Tags = ["PvP"] }
    };

    [Test]
    public void Games_SplitIntoCommonAndAlmostCommon()
    {
        var plan = PartyPlanner.Plan(
        [
            new PartyMember("me", true, Library(1, 2, 3, 4), null),
            new PartyMember("a", false, Library(1, 2, 3), null),
            new PartyMember("b", false, Library(1, 2, 3, 4), null),
            new PartyMember("c", false, null, null)
        ], Facts);

        plan.Common.Select(game => game.AppId).Should().Equal(3, 1);
        plan.Common[0].RecentPlayers.Should().Be(3);
        plan.NearMiss.Should().ContainSingle().Which.Missing.Should().Equal("a");
        plan.Common.Concat(plan.NearMiss).Should().NotContain(game => game.AppId == 2, "单机游戏不是开黑的选项");
        plan.UnknownLibraries.Should().Equal("c");
        plan.NoForecast.Should().Equal("me", "a", "b", "c");
    }

    [Test]
    public void UntaggedGames_AreListedButNotVerified()
    {
        var plan = PartyPlanner.Plan([new PartyMember("me", true, Library(9), null), new PartyMember("a", false, Library(9), null)], Facts);
        plan.Common.Should().ContainSingle().Which.Verified.Should().BeFalse();
    }

    [Test]
    public void Slots_FollowTheChanceThatEveryoneIsOnline()
    {
        double?[] Forecast(params (int Cell, double Value)[] cells)
        {
            var values = Enumerable.Repeat<double?>(0.05, WeekHours.Cells).ToArray();
            foreach (var (cell, value) in cells) values[cell] = value;
            return values;
        }
        var plan = PartyPlanner.Plan(
        [
            new PartyMember("a", false, null, Forecast((4 * 24 + 21, 0.9), (4 * 24 + 22, 0.8))),
            new PartyMember("b", false, null, Forecast((4 * 24 + 21, 0.8), (4 * 24 + 22, 0.9), (2 * 24 + 10, 0.9)))
        ], Facts);

        var slot = plan.Slots.First();
        (slot.Weekday, slot.StartHour, slot.EndHour).Should().Be((4, 21, 23));
        slot.Probability.Should().BeApproximately(0.72, 1e-6);
        plan.Expected[4 * 24 + 21].Should().BeApproximately(1.7, 1e-6);
    }

    [Test]
    public void MissingForecastCells_LeaveTheJointChanceUnknown()
    {
        var forecast = new double?[WeekHours.Cells];
        forecast[5] = 0.5;
        var plan = PartyPlanner.Plan([new PartyMember("a", false, null, forecast), new PartyMember("b", false, null, Enumerable.Repeat<double?>(0.5, WeekHours.Cells).ToArray())], Facts);
        plan.Together[5].Should().Be(0.25);
        plan.Together[6].Should().BeNull();
    }
}

[TestFixture]
public sealed class AdoptionTests
{
    [Test]
    public void Chain_OrdersAdoptersAndFindsWhoBroughtThemIn()
    {
        var day = 86400L;
        var evidence = new[]
        {
            new AdoptionEvidence("a", 1, 10 * day, AdoptionEvidenceKinds.Before),
            new AdoptionEvidence("b", 1, 40 * day, AdoptionEvidenceKinds.Inferred),
            new AdoptionEvidence("me", 1, 50 * day, AdoptionEvidenceKinds.Recorded),
            new AdoptionEvidence("me", 1, 60 * day, AdoptionEvidenceKinds.Recorded)
        };
        var activity = new[]
        {
            new ActivitySpan("a", 1, 30 * day, 30 * day + 3600),
            new ActivitySpan("b", 1, 45 * day, 45 * day + 7200),
            new ActivitySpan("a", 2, 45 * day, 46 * day)
        };

        var chain = Adoption.Chain(1, evidence, activity, "me");

        chain.Adopters.Select(adopter => adopter.SteamId).Should().Equal("a", "b", "me");
        chain.MyRank.Should().Be(3);
        chain.Adopters[0].InfluencedBy.Should().BeNull("开始记录前就有了，不知道是谁带的");
        chain.Adopters[1].InfluencedBy.Should().Be("a");
        chain.Adopters[2].InfluencedBy.Should().Be("b");
        chain.Adopters[2].InfluenceSeconds.Should().Be(7200);
        chain.Adopters[2].Time.Should().Be(50 * day);
    }

    [Test]
    public void BeforeEvidence_WinsAndKeepsTheEarliestBound()
    {
        var chain = Adoption.Chain(1, [new AdoptionEvidence("a", 1, 500, AdoptionEvidenceKinds.Recorded), new AdoptionEvidence("a", 1, 900, AdoptionEvidenceKinds.Before)], [], "me");
        chain.Adopters.Single().Should().Be(new Adopter("a", 500, AdoptionEvidenceKinds.Before, null, 0));
    }

    [Test]
    public void Overview_NeedsTwoPeopleAndOneObservedStart()
    {
        var evidence = new[]
        {
            new AdoptionEvidence("a", 1, 1, AdoptionEvidenceKinds.Before),
            new AdoptionEvidence("b", 1, 2, AdoptionEvidenceKinds.Before),
            new AdoptionEvidence("a", 2, 1, AdoptionEvidenceKinds.Before),
            new AdoptionEvidence("b", 2, 5, AdoptionEvidenceKinds.Recorded),
            new AdoptionEvidence("a", 3, 5, AdoptionEvidenceKinds.Recorded)
        };
        Adoption.Overview(evidence, [], "me").Select(chain => chain.AppId).Should().Equal(2);
    }
}

[TestFixture]
public sealed class LeaderboardTests
{
    private const long Day = 86400;

    [Test]
    public void Delta_NeedsASnapshotBeforeTheWindow()
    {
        var rows = new[] { new PlaytimeObservation("a", 1, 100, 100 * Day), new PlaytimeObservation("a", 1, 160, 110 * Day), new PlaytimeObservation("a", 2, 30, 112 * Day) };
        Leaderboard.Delta(rows, 105 * Day).Should().Be(90, "第 2 款在窗口开始时没有快照，说明那时是 0");
        Leaderboard.Delta(rows, 99 * Day).Should().BeNull();
        Leaderboard.Delta(rows.Where(row => row.AppId == 2).ToList(), 105 * Day, observedSince: 100 * Day).Should().Be(30);
        Leaderboard.Delta([], 105 * Day, observedSince: 100 * Day).Should().Be(0);
    }

    [Test]
    public void Build_PrefersSnapshotsForWindowsAndNewerCardsForTotals()
    {
        var now = 200 * Day;
        var me = new BoardEntry("me", BoardSources.Me, now, 60, 300, 9000, 40, 500, null);
        var snapshots = new Dictionary<string, IReadOnlyList<PlaytimeObservation>>
        {
            ["a"] = [new PlaytimeObservation("a", 1, 1000, 190 * Day), new PlaytimeObservation("a", 1, 1100, 199 * Day)]
        };
        var cards = new[]
        {
            new CardNumbers("a", 199 * Day + 10, 70, 400, 50_000, 120, 3000, new Dictionary<int, int>()),
            new CardNumbers("c", 150 * Day, 10, 20, 700, 5, 10, new Dictionary<int, int>()),
            new CardNumbers("c", 120 * Day, 99, 99, 99, 9, 9, new Dictionary<int, int>())
        };

        var entries = Leaderboard.Build(me, snapshots, new Dictionary<string, int>(), cards, now);

        var a = entries.Single(entry => entry.SteamId == "a");
        a.Source.Should().Be(BoardSources.Snapshot);
        a.Last7.Should().Be(100);
        a.Last30.Should().Be(400, "快照只有 10 天，30 天的数字用卡");
        a.Lifetime.Should().Be(50_000, "卡比快照新，私密游戏库也在卡里");
        var c = entries.Single(entry => entry.SteamId == "c");
        c.Source.Should().Be(BoardSources.Card);
        c.Lifetime.Should().Be(700, "同一个人只用最新的卡");
    }

    [Test]
    public void ForGame_RanksEveryoneWhoHasTheGame()
    {
        var now = 200 * Day;
        var board = Leaderboard.ForGame(
            1,
            new GameBoardEntry("me", BoardSources.Me, now, 500, 40),
            new Dictionary<string, IReadOnlyList<PlaytimeObservation>> { ["a"] = [new PlaytimeObservation("a", 2, 10, 100 * Day), new PlaytimeObservation("a", 1, 900, 190 * Day)] },
            [new CardNumbers("b", now, 0, 0, 0, 0, 0, new Dictionary<int, int> { [1] = 200 })],
            now);
        board.Select(entry => (entry.SteamId, entry.Minutes)).Should().Equal(("a", 900), ("me", 500), ("b", 200));
        board[0].Last30.Should().Be(900, "快照从第 100 天开始，那时还没有这款游戏");
    }
}

[TestFixture]
public sealed class ExchangeCardTests
{
    private static CardPayload Card(string steamId = "76561198000000042") => new()
    {
        SteamId = steamId,
        Name = "Kestrel",
        GeneratedAt = Now,
        Year = 2026,
        UtcOffsetMinutes = 480,
        Totals = new CardTotals(12000, 3000, 200, 900, 150, 90, 6, 1200, 210),
        Months = [100, 200, 300],
        Heatmap = Enumerable.Range(0, 168).Select(index => index % 24 == 21 ? 255 : 3).ToList(),
        Games = [new CardGame(570, "Dota 2", 9000, 1500, 0, 0, Now - 3600), new CardGame(620, "Portal 2", 900, 60, 51, 51, Now - 86400)],
        Persona = new CardPersona("ESNB", "夜行社交派", "Night-owl socialite")
    };

    [Test]
    public void Code_RoundTripsWithoutAPassphrase()
    {
        var code = ExchangeCardCodec.Encode(Card(), null);
        code.Should().StartWith(ExchangeCardCodec.Prefix);
        code.Length.Should().BeLessThan(4000, "要能放进一条聊天消息");
        var result = ExchangeCardCodec.Decode($"给你我的卡 {code} 记得导入", null);
        result.Status.Should().Be(CardDecodeStatus.Ok);
        result.Protected.Should().BeFalse();
        result.Card!.Games.Select(game => game.Name).Should().Equal("Dota 2", "Portal 2");
        result.Card.Months.Should().HaveCount(12);
        result.Card.Persona!.Code.Should().Be("ESNB");
    }

    [Test]
    public void Passphrase_IsRequiredAndChecked()
    {
        var code = ExchangeCardCodec.Encode(Card(), "暗号 42");
        ExchangeCardCodec.Decode(code, null).Status.Should().Be(CardDecodeStatus.NeedsPassphrase);
        ExchangeCardCodec.Decode(code, "wrong").Status.Should().Be(CardDecodeStatus.WrongPassphrase);
        var result = ExchangeCardCodec.Decode(code, "暗号 42");
        result.Status.Should().Be(CardDecodeStatus.Ok);
        result.Protected.Should().BeTrue();
    }

    [Test]
    public void TamperedCards_DoNotOpen()
    {
        var code = ExchangeCardCodec.Encode(Card(), null);
        var index = code.Length - 10;
        var tampered = code[..index] + (code[index] == 'A' ? 'B' : 'A') + code[(index + 1)..];
        ExchangeCardCodec.Decode(tampered, null).Status.Should().Be(CardDecodeStatus.Invalid);
        ExchangeCardCodec.Decode("SSCARD1.not-a-card", null).Status.Should().Be(CardDecodeStatus.Invalid);
        ExchangeCardCodec.Decode("随便一段话", null).Status.Should().Be(CardDecodeStatus.Invalid);
    }

    [Test]
    public void Codes_AreDifferentEveryTime()
        => ExchangeCardCodec.Encode(Card(), null).Should().NotBe(ExchangeCardCodec.Encode(Card(), null));

    [Test]
    public void Sanitize_ClampsUntrustedInput()
    {
        var hostile = Card() with
        {
            Name = new string('x', 500),
            UtcOffsetMinutes = 99_999,
            Heatmap = [999, -5],
            Games = Enumerable.Range(1, 300).Select(id => new CardGame(id, new string('n', 400), -1, int.MaxValue, 5, 2, -9)).ToList(),
            Persona = new CardPersona("TOOLONGCODE", "a", "b")
        };
        var clean = ExchangeCardCodec.Sanitize(hostile)!;
        clean.Name.Should().HaveLength(64);
        clean.UtcOffsetMinutes.Should().Be(14 * 60);
        clean.Heatmap.Should().HaveCount(168);
        clean.Heatmap[0].Should().Be(255);
        clean.Heatmap[1].Should().Be(0);
        clean.Games.Should().HaveCount(100);
        clean.Games[0].Name.Should().HaveLength(128);
        clean.Games[0].Minutes.Should().Be(0);
        clean.Persona.Should().BeNull();
        ExchangeCardCodec.Sanitize(Card() with { SteamId = "1234abc" }).Should().BeNull();
        ExchangeCardCodec.Sanitize(Card() with { GeneratedAt = 0 }).Should().BeNull();
    }

    [Test]
    public void NewerVersions_AreReportedInsteadOfMisread()
    {
        var code = ExchangeCardCodec.Encode(Card() with { V = ExchangeCardCodec.Version + 1 }, null);
        ExchangeCardCodec.Decode(code, null).Status.Should().Be(CardDecodeStatus.UnsupportedVersion);
    }

    [Test]
    public void Builder_SummarisesTheArchiveAndSkipsWhatIsNotThere()
    {
        var archive = Archive(
        [
            Session(1, "2026-01-05", 21, 7200),
            Session(1, "2026-01-12", 21, 3600),
            Session(2, "2025-12-30", 10, 1800),
            Session(2, "2026-02-01", 20, 600)
        ]).With(
            owned: [new InsightOwnedGame(1, "Alpha", 600, Unix("2026-01-12")), new InsightOwnedGame(3, "Never", 0, 0)],
            progress: [new InsightProgress(1, "Alpha", 10, 10), new InsightProgress(2, "Beta", 20, 4)],
            unlocks: [new InsightUnlock(1, "a", "A", null, Unix("2026-01-05", 22), "2026-01-05"), new InsightUnlock(2, "b", "B", null, Unix("2025-12-30", 11), "2025-12-30")]);

        var card = ExchangeCardBuilder.Build(archive, Me, "Me", 2026, null);

        card.Totals.YearMinutes.Should().Be(190);
        card.Totals.LifetimeMinutes.Should().Be(600 + 40, "官方时长比记录多时用官方的");
        card.Totals.OwnedGames.Should().Be(2);
        card.Totals.PlayedGames.Should().Be(2);
        card.Totals.CompletedGames.Should().Be(1);
        card.Totals.Achievements.Should().Be(2);
        card.Totals.YearAchievements.Should().Be(1);
        card.Totals.Last7Minutes.Should().Be(10);
        card.Months[0].Should().Be(180);
        card.Months[1].Should().Be(10);
        card.Games.Select(game => game.AppId).Should().Equal(1, 2);
        card.Heatmap.Max().Should().Be(255);
        card.Heatmap[WeekHours.Index(Unix("2026-01-05", 21), Options with { WeekStart = 1 })].Should().Be(255);
        card.UtcOffsetMinutes.Should().Be(0);
    }
}

[TestFixture]
public sealed class DuoReviewTests
{
    private static CardPayload Card(string steamId, int offset, int hour, string persona, params CardGame[] games) => new()
    {
        SteamId = steamId,
        Name = steamId,
        GeneratedAt = Now,
        Year = 2026,
        UtcOffsetMinutes = offset,
        Totals = new CardTotals(0, games.Sum(game => game.YearMinutes), 0, 0, 0, 0, 0, 0, 3),
        Months = Enumerable.Range(0, 12).Select(month => month == 1 ? 500 : 10).ToList(),
        Heatmap = Enumerable.Range(0, 168).Select(cell => cell == hour ? 255 : 0).ToList(),
        Games = games,
        Persona = new CardPersona(persona, "", "")
    };

    [Test]
    public void Duo_ComparesGamesSchedulesAndPersonas()
    {
        // 对方在 UTC+0 的 13 点玩，我在 UTC+8：对方的 13 点是我的 21 点
        var mine = Card("me", 480, 21, "ESNB", new CardGame(1, "Alpha", 0, 600, 0, 0, 0), new CardGame(2, "Beta", 0, 100, 0, 0, 0), new CardGame(3, "Solo", 0, 50, 0, 0, 0));
        var theirs = Card("you", 0, 13, "ISNA", new CardGame(1, "Alpha", 0, 300, 0, 0, 0), new CardGame(2, "Beta", 0, 900, 0, 0, 0), new CardGame(4, "Theirs", 0, 70, 0, 0, 0));
        var together = new[] { new TogetherSegment(1, "me", "you", 1, Now - 7200, Now - 3600, 3600, 1200) };

        var report = DuoReview.Build(mine, theirs, together, 12345);

        report.Shared.Select(game => game.AppId).Should().Equal(1, 2);
        report.OnlyMine.Should().ContainSingle().Which.AppId.Should().Be(3);
        report.OnlyTheirs.Should().ContainSingle().Which.AppId.Should().Be(4);
        report.Schedule.Should().Be(1);
        report.BestCell.Should().Be(21);
        report.TheirHeatmap[21].Should().Be(255);
        report.BusiestMonth.Should().Be(2);
        report.SameAxes.Should().Equal(1, 2);
        report.TogetherSeconds.Should().Be(3600);
        report.ConfirmedSeconds.Should().Be(1200);
        report.Since.Should().Be(12345);
    }
}

[TestFixture]
public sealed class PresenceGroupCollectorTests
{
    private const ulong Friend = 76561198000000009;

    private sealed class MemoryGroups : IPresenceGroupRecorder
    {
        public List<PresenceGroupEntry> Baseline { get; } = [];
        public List<PresenceGroupEntry> Records { get; } = [];

        public Task<IReadOnlyList<PresenceGroupEntry>> GetLatestAsync(string accountName, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PresenceGroupEntry>>(Baseline);

        public Task RecordAsync(PresenceGroupEntry entry, CancellationToken cancellationToken = default)
        {
            lock (Records) Records.Add(entry);
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task OnlyChangesOfTrackedFriendsAndSelfAreRecorded()
    {
        var store = new MemoryGroups();
        var tracking = new RichPresenceFakes.TrackingRecorder();
        tracking.Tracked.Add(Friend.ToString());
        await using var collector = new PresenceGroupCollector(store, tracking, TimeProvider.System, NullLogger<PresenceGroupCollector>.Instance);

        collector.Observe("me", Me, new SteamPersonaGroupUpdate(Friend, 1, 42, 0, 0));
        collector.Observe("me", Me, new SteamPersonaGroupUpdate(Friend, 1, 42, 0, 0));
        collector.Observe("me", Me, new SteamPersonaGroupUpdate(76561198000000099, 1, 42, 0, 0));
        collector.Observe("me", Me, new SteamPersonaGroupUpdate(ulong.Parse(Me), 1, 0, 0x0A000001, 27015));
        collector.Observe("me", Me, new SteamPersonaGroupUpdate(Friend, 0, 0, 0, 0));
        await collector.DrainAsync();

        store.Records.Select(entry => (entry.SteamId, entry.Kind, entry.GroupKey.Length > 0, entry.IsSelf)).Should().Equal(
            (Friend.ToString(), PresenceGroupKinds.Lobby, true, false),
            (Me, PresenceGroupKinds.Server, true, true),
            (Friend.ToString(), PresenceGroupKinds.Lobby, false, false));
    }

    [Test]
    public async Task TheLastStoredKeyIsTheBaseline()
    {
        var store = new MemoryGroups();
        store.Baseline.Add(new PresenceGroupEntry("me", Me, true, 1, PresenceGroupKinds.Lobby, PresenceGroupKeys.Lobby(1, 42), 0));
        await using var collector = new PresenceGroupCollector(store, new RichPresenceFakes.TrackingRecorder(), TimeProvider.System, NullLogger<PresenceGroupCollector>.Instance);

        collector.Observe("me", Me, new SteamPersonaGroupUpdate(ulong.Parse(Me), 1, 42, 0, 0));
        await collector.DrainAsync();

        store.Records.Should().BeEmpty();
    }
}

[TestFixture]
public sealed class FriendSinceParserTests
{
    [Test]
    public void Parse_ReadsFriendsAndKeepsZeroForVeryOldOnes()
    {
        var json = """{"friendslist":{"friends":[{"steamid":"76561198000000009","relationship":"friend","friend_since":1600000000},{"steamid":"76561198000000010","relationship":"friend","friend_since":0},{"steamid":"x","relationship":"friend"}]}}""";
        SteamArchiveGateway.ParseFriendSince(json).Should().Equal(new SteamFriendSince(76561198000000009, 1600000000), new SteamFriendSince(76561198000000010, 0));
    }

    [Test]
    public void Parse_TellsAnEmptyReplyFromNoFriends()
    {
        SteamArchiveGateway.ParseFriendSince("{}").Should().BeNull("私密或令牌不被接受时没有 friendslist，不能当成一个好友都没有");
        SteamArchiveGateway.ParseFriendSince("""{"friendslist":{"friends":[]}}""").Should().BeEmpty();
    }
}
