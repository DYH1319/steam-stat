using FluentAssertions;
using ProtoBuf;
using SteamKit2;
using SteamStat.Core.Insights;
using SteamStat.Core.Memoir;
using SteamStat.Core.Steam.Gateway.Internal;
using static SteamStat.Core.Tests.InsightFixtures;

namespace SteamStat.Core.Tests.Memoir;

internal static class MemoirFixtures
{
    internal static MemoirArchive Memoir(
        InsightArchive insight,
        IReadOnlyList<MemoirOfficialPlaytime>? official = null,
        IReadOnlyList<MemoirFirstPlay>? firstPlays = null,
        IReadOnlyList<MemoirTrace>? traces = null,
        IReadOnlyList<YearReview>? reviews = null,
        IReadOnlyList<MemoirFriend>? friends = null,
        IReadOnlyList<MemoirFriendChange>? friendChanges = null,
        IReadOnlyDictionary<string, long>? knownSince = null,
        IReadOnlyList<MemoirLifeEvent>? events = null,
        IReadOnlyList<MemoirAcquisition>? acquisitions = null)
        => new()
        {
            Insight = insight,
            Official = official ?? [],
            FirstPlays = firstPlays ?? [],
            Traces = (traces ?? []).OrderBy(trace => trace.Time).ToList(),
            YearReviews = reviews ?? [],
            Friends = friends ?? [],
            FriendChanges = friendChanges ?? [],
            FriendsKnownSince = knownSince ?? new Dictionary<string, long>(),
            LifeEvents = events ?? [],
            Acquisitions = acquisitions ?? [],
            Names = new Dictionary<int, string> { [10] = "Alpha", [20] = "Beta", [30] = "Gamma" }
        };

    internal static int Day(string day) => MemoirDays.ToEpoch(day);

    /// <summary>每月一局，时长给定，日期在月中。</summary>
    internal static IEnumerable<InsightSession> Monthly(int appId, string firstMonth, int months, double hours)
    {
        var start = MemoirDays.MonthStart(firstMonth);
        for (var index = 0; index < months; index++)
        {
            var day = InsightDays.Format(start.AddMonths(index).AddDays(14));
            yield return Session(appId, day, 20, (long)(hours * 3600));
        }
    }
}

[TestFixture]
public sealed class PlaytimeTimelineTests
{
    private static readonly long Install = Unix("2025-01-01");

    private static AppPlaytimeTimeline Timeline(
        IEnumerable<(long, long)>? sessions = null,
        IEnumerable<MemoirOfficialPlaytime>? plateaus = null,
        long? firstPlayed = null,
        IEnumerable<long>? traces = null,
        long? officialNow = null)
        => new(Me, 10, sessions ?? [], plateaus ?? [], firstPlayed, traces ?? [], officialNow, Now);

    [Test]
    public void BeforeTheOfficialFirstPlay_IsAnOfficialZero()
    {
        var timeline = Timeline(firstPlayed: Unix("2020-05-01"), plateaus: [new MemoirOfficialPlaytime(Me, 10, 600, Install, Install + 86400)]);

        timeline.At(Unix("2020-04-30")).Should().Be(new MemoirValue(0, MemoirEvidence.Official));
    }

    [Test]
    public void InsideAPlateau_IsOfficial_AndRecordedSessionsAddUp()
    {
        var timeline = Timeline(
            sessions: [(Install + 10 * 86400, 3600)],
            plateaus: [new MemoirOfficialPlaytime(Me, 10, 600, Install, Install + 86400)]);

        timeline.At(Install + 3600).Should().Be(new MemoirValue(36000, MemoirEvidence.Official));
        timeline.At(Install + 20 * 86400).Value.Should().Be(36000 + 3600);
        timeline.Between(Install + 5 * 86400, Install + 20 * 86400).Should().Be(new MemoirValue(3600, MemoirEvidence.Official));
    }

    [Test]
    public void BetweenFirstPlayAndInstall_IsInferredFromTraces_AndNeverDecreases()
    {
        var first = Unix("2024-01-01");
        var traces = new[] { Unix("2024-03-10"), Unix("2024-03-11"), Unix("2024-09-01") };
        var timeline = Timeline(firstPlayed: first, traces: traces, plateaus: [new MemoirOfficialPlaytime(Me, 10, 6000, Install, Install)]);

        var march = timeline.At(Unix("2024-03-12"));
        var october = timeline.At(Unix("2024-10-01"));

        march.Evidence.Should().Be(MemoirEvidence.Inferred);
        march.Upper.Should().Be(360000);
        october.Value.Should().BeGreaterThan(march.Value);
        october.Value.Should().BeLessThan(360000);
        // 两个痕迹日比几十个安静的日子分到更多
        timeline.Between(Unix("2024-03-09"), Unix("2024-03-12")).Value.Should().BeGreaterThan(timeline.Between(Unix("2024-05-01"), Unix("2024-06-01")).Value);
        timeline.Untracked.Should().Be(360000);
    }

    [Test]
    public void WithoutAnyStart_ThePrehistoryIsUnknown_ButBounded()
    {
        var timeline = Timeline(plateaus: [new MemoirOfficialPlaytime(Me, 10, 600, Install, Install)]);

        var value = timeline.At(Unix("2024-06-01"));

        value.Evidence.Should().Be(MemoirEvidence.Unknown);
        value.Upper.Should().Be(36000);
    }

    [Test]
    public void WithoutSnapshots_TheGapToTheOfficialTotalIsInferredBeforeTheFirstSession()
    {
        var first = Install + 86400;
        var timeline = Timeline(sessions: [(first, 7200)], officialNow: 7200 + 36000, firstPlayed: Unix("2023-01-01"));

        timeline.Untracked.Should().Be(36000);
        timeline.At(Install + 2 * 86400).Should().Be(new MemoirValue(36000 + 7200, MemoirEvidence.Inferred));
        // 分界之后的一段时间只看记录
        timeline.Between(first - 1, Install + 2 * 86400).Should().Be(new MemoirValue(7200, MemoirEvidence.Recorded));
    }

    [Test]
    public void NoGap_MeansAnOfficialZeroBeforeTheRecord()
    {
        var timeline = Timeline(sessions: [(Install, 3600)], officialNow: 3600);

        timeline.At(Unix("2020-01-01")).Should().Be(new MemoirValue(0, MemoirEvidence.Official));
    }
}

[TestFixture]
public sealed class LifeChapterTests
{
    private static IReadOnlyList<MemoirMonth> Months(params (int AppId, double Hours, int Count)[] runs)
    {
        var months = new List<MemoirMonth>();
        var cursor = new DateOnly(2020, 1, 1);
        foreach (var (appId, hours, count) in runs)
            for (var index = 0; index < count; index++)
            {
                var seconds = (long)(hours * 3600);
                months.Add(new MemoirMonth(MemoirDays.Month(cursor), seconds, MemoirEvidence.Recorded, seconds > 0 ? new Dictionary<int, long> { [appId] = seconds } : new Dictionary<int, long>()));
                cursor = cursor.AddMonths(1);
            }
        return months;
    }

    [Test]
    public void ChangesInWhatAndHowMuch_StartNewChapters()
    {
        var months = Months((10, 40, 12), (20, 40, 12), (20, 1, 6));

        LifeChapters.Segment(months, new HashSet<int>()).Should().Equal((0, 11), (12, 23), (24, 29));
    }

    [Test]
    public void SmallWobbles_DoNotSplitAChapter()
    {
        var months = Months((10, 30, 4), (10, 34, 4), (10, 28, 4));

        LifeChapters.Segment(months, new HashSet<int>()).Should().HaveCount(1);
    }

    [Test]
    public void ABoundaryEvent_AlwaysStartsAChapter()
    {
        var months = Months((10, 30, 12));

        LifeChapters.Segment(months, new HashSet<int> { 5 }).Should().Equal((0, 4), (5, 11));
    }

    [Test]
    public void Chapters_AreNamedByWhatDominated()
    {
        var sessions = MemoirFixtures.Monthly(10, "2024-01", 6, 40).Concat(MemoirFixtures.Monthly(20, "2024-07", 6, 40));
        var archive = MemoirFixtures.Memoir(Archive(sessions, now: Unix("2024-12-31", 12)),
            events: [new MemoirLifeEvent(7, "New job", null, "work", MemoirFixtures.Day("2024-07-01"), null, true, 0)]);
        var timelines = PlaytimeTimelines.Build(archive);

        var chapters = LifeChapters.Build(archive, MemoirMonths.Build(archive, timelines));

        chapters.Should().HaveCount(2);
        chapters[0].Kind.Should().Be("era");
        chapters[0].AppIds.Should().Equal(10);
        chapters[1].StartEventId.Should().Be(7);
        chapters[1].FromMonth.Should().Be("2024-07");
    }

    [Test]
    public void Classify_CoversEveryKind()
    {
        LifeChapters.Classify(3600, 6, [new LifeChapterGame(1, 3600, 1)], 0).Kind.Should().Be("quiet");
        LifeChapters.Classify(360000, 6, [new LifeChapterGame(1, 1, 0.4), new LifeChapterGame(2, 1, 0.3)], 0).Kind.Should().Be("duo");
        LifeChapters.Classify(360000, 6, [new LifeChapterGame(1, 1, 0.2), new LifeChapterGame(2, 1, 0.1)], 9).Kind.Should().Be("sampler");
        LifeChapters.Classify(360000, 6, [new LifeChapterGame(1, 1, 0.2), new LifeChapterGame(2, 1, 0.1)], 0).Kind.Should().Be("variety");
        LifeChapters.Classify(360000, 6, [new LifeChapterGame(1, 1, 0.4), new LifeChapterGame(2, 1, 0.1)], 0).Kind.Should().Be("mostly");
    }
}

[TestFixture]
public sealed class TimeMachineTests
{
    private static readonly long Install = Unix("2025-06-01");

    private static MemoirArchive Sample()
    {
        var sessions = new[] { Session(10, "2025-06-10", 20, 7200), Session(20, "2025-06-10", 23, 1800) };
        var insight = Archive(sessions, now: Unix("2026-01-01", 12)).With(
            unlocks: [
                new InsightUnlock(10, "A", "First", 50, Unix("2023-02-01", 10), "2023-02-01"),
                new InsightUnlock(10, "B", "Second", 2.5, Unix("2025-06-10", 21), "2025-06-10")
            ],
            progress: [new InsightProgress(10, "Alpha", 2, 2)],
            owned: [new InsightOwnedGame(10, "Alpha", 1000, 0), new InsightOwnedGame(20, "Beta", 30, 0), new InsightOwnedGame(30, "Gamma", 0, 0)]);
        return MemoirFixtures.Memoir(
            insight,
            official: [new MemoirOfficialPlaytime(Me, 10, 860, Install, Install), new MemoirOfficialPlaytime(Me, 20, 0, Install, Install)],
            firstPlays: [new MemoirFirstPlay(Me, 10, Unix("2023-01-15"))],
            traces: [new MemoirTrace(Me, 10, MemoirTraceKind.Achievement, Unix("2023-02-01", 10))],
            acquisitions: [
                new MemoirAcquisition(Me, 10, 1, Unix("2023-01-10")),
                new MemoirAcquisition(Me, 20, 2, Unix("2025-06-09")),
                new MemoirAcquisition(Me, 30, 0, Install)
            ],
            friends: [new MemoirFriend(Me, "f1", "Ann"), new MemoirFriend(Me, "f2", "Bob")],
            friendChanges: [new MemoirFriendChange(Me, "f2", "Bob", "added", null, null, Unix("2025-08-01"))],
            knownSince: new Dictionary<string, long> { [Me] = Install });
    }

    private static TimeMachineSnapshot At(MemoirArchive archive, string day)
    {
        var timelines = PlaytimeTimelines.Build(archive);
        return TimeMachine.At(archive, timelines, LifeChapters.Build(archive, MemoirMonths.Build(archive, timelines)), [], MemoirFixtures.Day(day));
    }

    [Test]
    public void ADayAfterInstall_IsRecorded()
    {
        var snapshot = At(Sample(), "2025-06-10");

        snapshot.Summary.DaySeconds.Should().Be(new MemoirValue(9000, MemoirEvidence.Recorded));
        snapshot.Summary.Sessions.Should().HaveCount(2);
        snapshot.Achievements.DayUnlocks.Select(unlock => unlock.InternalName).Should().Equal("B");
        snapshot.Achievements.Completed.Value.Should().Be(1);
    }

    [Test]
    public void ADayBeforeInstall_IsInferred_AndShowsItsTraces()
    {
        var snapshot = At(Sample(), "2023-02-01");

        snapshot.Summary.DaySeconds.Evidence.Should().Be(MemoirEvidence.Inferred);
        snapshot.Summary.DaySeconds.Value.Should().BeGreaterThan(0);
        snapshot.Summary.Traces.Should().ContainSingle(trace => trace.Kind == MemoirTraceKind.Achievement);
        snapshot.Achievements.Unlocked.Value.Should().Be(1);
        snapshot.Achievements.Completed.Value.Should().Be(0);
    }

    [Test]
    public void TheLibrary_SeparatesProvenLaterAndUnknown()
    {
        var snapshot = At(Sample(), "2024-01-01");

        snapshot.Library.Games.Select(game => game.AppId).Should().Contain(10).And.Contain(30);
        snapshot.Library.Games.Single(game => game.AppId == 10).AcquiredEvidence.Should().Be(MemoirEvidence.Official);
        snapshot.Library.Games.Single(game => game.AppId == 30).AcquiredEvidence.Should().Be(MemoirEvidence.Unknown);
        snapshot.Library.Later.Should().Be(1);
        snapshot.Library.Owned.Should().Be(new MemoirValue(1, MemoirEvidence.Inferred, 2));
    }

    [Test]
    public void Friends_BeforeTheFirstObservationAreUnknown_AndAdditionsAreRespected()
    {
        var before = At(Sample(), "2024-01-01");
        var between = At(Sample(), "2025-07-01");
        var after = At(Sample(), "2025-09-01");

        before.Friends.Friends.Should().ContainSingle(friend => friend.SteamId == "f1" && friend.Evidence == MemoirEvidence.Unknown);
        before.Friends.Friends.Should().NotContain(friend => friend.SteamId == "f2");
        between.Friends.Friends.Should().ContainSingle(friend => friend.SteamId == "f1" && friend.Evidence == MemoirEvidence.Recorded);
        between.Friends.Friends.Should().NotContain(friend => friend.SteamId == "f2");
        after.Friends.Count.Should().Be(new MemoirValue(2, MemoirEvidence.Recorded));
    }
}

[TestFixture]
public sealed class YearReviewTests
{
    private static byte[] Encode(CSaleFeature_GetUserYearInReview_Response response)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, response);
        return stream.ToArray();
    }

    [Test]
    public void Parse_ReadsMonthsGamesAndPercentiles()
    {
        var stats = new YirUserPlaytimeStats { total_stats = new YirPlaytimeStats { total_playtime_seconds = 100_000, windows_playtime_percentagex100 = 7500, deck_playtime_percentagex100 = 2500 } };
        stats.games.Add(new YirGamePlaytimeStats { appid = 10, stats = new YirPlaytimeStats { total_playtime_seconds = 60_000 } });
        stats.game_summary.Add(new YirGameSummary { appid = 10, new_this_year = true, rtime_first_played_lifetime = 1_600_000_000, total_sessions = 12 });
        stats.game_summary.Add(new YirGameSummary { appid = 20, total_playtime_percentagex100 = 4000 });
        var month = new YirMonthlyPlaytimeStats { rtime_month = (uint)Unix("2023-03-01", 8), stats = new YirPlaytimeStats { total_playtime_seconds = 20_000 } };
        month.game_summary.Add(new YirSimpleGameSummary { appid = 20, relative_playtime_percentagex100 = 5000 });
        stats.months.Add(month);
        stats.playtime_streak = new YirPlaytimeStreak { longest_consecutive_days = 9, rtime_start = 1_680_000_000 };
        var response = new CSaleFeature_GetUserYearInReview_Response
        {
            stats = new YirStats { year = 2023, playtime_stats = stats },
            percentiles = new YirGlobalPercentiles { median_games = 7 }
        };

        var review = YearReviewParser.Parse(Me, 2023, 1, Encode(response))!;

        review.Seconds.Should().Be(100_000);
        review.Games.Should().HaveCount(2);
        review.Games[0].Should().Be(new YearReviewGame(10, 60_000, null, 12, true, 1_600_000_000));
        review.Games[1].Seconds.Should().Be(40_000);
        review.Months.Should().ContainSingle(item => item.Month == 3 && item.Seconds == 20_000);
        review.SecondsOf(3, 20).Should().Be(10_000);
        review.Streak!.Days.Should().Be(9);
        review.Percentiles!.MedianGames.Should().Be(7);
        review.Platforms.Should().ContainKey("windows").WhoseValue.Should().Be(0.75);
        review.IsEmpty.Should().BeFalse();
    }

    [Test]
    public void Parse_UnknownFieldsSurviveTheRoundTrip()
    {
        var response = new CSaleFeature_GetUserYearInReview_Response { stats = new YirStats { year = 2024 } };
        Extensible.AppendValue(response, 99, "future");

        var bytes = Encode(response);
        using var stream = new MemoryStream(bytes);
        var again = Serializer.Deserialize<CSaleFeature_GetUserYearInReview_Response>(stream);

        Extensible.GetValue<string>(again, 99).Should().Be("future");
        YearReviewParser.Parse(Me, 2024, 1, bytes)!.IsEmpty.Should().BeTrue();
    }

    [Test]
    public void Parse_RejectsGarbage()
    {
        YearReviewParser.Parse(Me, 2024, 1, [0xFF, 0xFF, 0xFF, 0x01]).Should().BeNull();
    }

    [Test]
    public void TheSaleFeatureService_RegistersWithSteamKit()
    {
        var unified = new SteamClient().GetHandler<SteamUnifiedMessages>()!;

        unified.CreateService<SaleFeatureUnifiedService>().ServiceName.Should().Be("SaleFeature");
    }
}

[TestFixture]
public sealed class StaticSiteTests
{
    private static StaticSiteOptions Options(bool mask = false, IReadOnlySet<int>? excluded = null, bool covers = false, bool rounded = true, string identity = "none")
        => new(null, null, identity, null, StaticSiteBuilder.AllSections.ToHashSet(), mask, excluded ?? new HashSet<int>(), rounded, false, covers, false, true, "en", "auto", "#ff0000");

    private static StaticSiteResult Build(StaticSiteOptions options)
    {
        var sessions = MemoirFixtures.Monthly(10, "2025-06", 6, 30).Concat(MemoirFixtures.Monthly(20, "2025-06", 6, 5));
        var insight = Archive(sessions, now: Unix("2025-12-31", 12)).With(
            unlocks: [new InsightUnlock(10, "RARE", "Secret Ending", 0.8, Unix("2025-07-01"), "2025-07-01")],
            progress: [new InsightProgress(10, "Alpha", 1, 1)]);
        var archive = MemoirFixtures.Memoir(insight);
        var timelines = PlaytimeTimelines.Build(archive);
        var months = MemoirMonths.Build(archive, timelines);
        return StaticSiteBuilder.Build(archive, timelines, months, LifeChapters.Build(archive, months), options, "RealName");
    }

    [Test]
    public void ThePage_HasNoScripts_AndLocksDownResources()
    {
        var site = Build(Options());

        site.Html.Should().NotContain("<script").And.Contain("default-src 'none'").And.NotContain("img-src");
        site.Html.Should().Contain("Alpha").And.Contain("#ff0000");
        site.Games.Should().Be(2);
    }

    [Test]
    public void MaskedNames_LeakNeitherGameNamesNorAchievementNamesNorCovers()
    {
        var site = Build(Options(mask: true, covers: true));

        site.Html.Should().NotContain("Alpha").And.NotContain("Beta").And.NotContain("Secret Ending").And.NotContain("steamstatic");
        site.Json.Should().NotContain("Alpha").And.NotContain("\"appId\"");
        site.Html.Should().Contain("Game #1");
    }

    [Test]
    public void ExcludedGames_DisappearFromTheTotals()
    {
        var all = Build(Options(rounded: false));
        var without = Build(Options(excluded: new HashSet<int> { 20 }, rounded: false));

        without.Html.Should().NotContain("Beta");
        without.Games.Should().Be(1);
        without.Json.Should().Contain("\"hours\": 180");
        all.Json.Should().Contain("\"hours\": 210");
    }

    [Test]
    public void Warnings_FollowTheRedactionRules()
    {
        Build(Options(identity: "real", covers: true, rounded: false)).Warnings.Should().Contain(["realName", "covers", "exactHours", "exactDates"]);
        Build(Options(mask: true, covers: true)).Warnings.Should().NotContain("covers");
    }

    [Test]
    public void Hours_AreRoundedToTens()
    {
        StaticSiteBuilder.FormatHours(4, true, false).Should().Be("<10 h");
        StaticSiteBuilder.FormatHours(234, true, false).Should().Be("230 h");
        StaticSiteBuilder.FormatHours(234.25, false, true).Should().Be("234 小时");
        StaticSiteBuilder.NormalizeAccent("red;}body{").Should().Be("#3b82f6");
    }
}

[TestFixture]
public sealed class JournalRuleTests
{
    [Test]
    public void Capsules_OpenOnTheirDate_OrOnTheFirstLaunchAfterIt()
    {
        TimeCapsuleRules.IsReady(100, null, 99, null).Should().BeFalse();
        TimeCapsuleRules.IsReady(100, null, 100, null).Should().BeTrue();
        TimeCapsuleRules.IsReady(100, 10, 500, null).Should().BeFalse();
        TimeCapsuleRules.IsReady(100, 10, 500, 90).Should().BeFalse();
        TimeCapsuleRules.IsReady(100, 10, 500, 120).Should().BeTrue();
    }

    [Test]
    public void Capsules_MustBeSealedForAtLeastHalfADay()
    {
        TimeCapsuleRules.Validate(" ", 1_000_000, 0).Should().Be("empty");
        TimeCapsuleRules.Validate("hi", 3600, 0).Should().Be("tooSoon");
        TimeCapsuleRules.Validate("hi", 86400, 0).Should().BeNull();
        TimeCapsuleRules.Validate("hi", 60L * 366 * 86400, 0).Should().Be("tooLate");
    }

    [Test]
    public void PreviouslyOn_NeedsAGapOfAtLeastTheThreshold()
    {
        PreviouslyOnRules.GapDays(0, 100 * 86400, 90).Should().BeNull();
        PreviouslyOnRules.GapDays(86400, 80 * 86400, 90).Should().BeNull();
        PreviouslyOnRules.GapDays(86400, 100 * 86400, 90).Should().Be(99);
    }
}
