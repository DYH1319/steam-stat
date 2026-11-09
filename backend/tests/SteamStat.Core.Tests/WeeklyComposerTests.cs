using System.Text.Json;
using FluentAssertions;
using SteamStat.Core.Insights;
using static SteamStat.Core.Tests.InsightFixtures;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class WeeklyComposerTests
{
    private static readonly InsightPeriod Week = new("2026-01-26", "2026-02-01");

    private static WeeklyPayload Compose(InsightArchive archive, IReadOnlyList<Insight>? insights = null) => WeeklyComposer.Compose(archive, Week, insights ?? []);

    [Test]
    public void IssueNumber_CountsWeeksFromTheFirstSession()
    {
        var archive = Archive([Session(1, "2026-01-07", 10, 3600)]);

        WeeklyComposer.IssueNumber(archive, "2026-01-05").Should().Be(1);
        WeeklyComposer.IssueNumber(archive, "2026-01-26").Should().Be(4);
    }

    [Test]
    public void AnEmptyWeekIsPublishedAsQuiet()
    {
        var issue = Compose(Archive([Session(1, "2026-01-07", 10, 3600)]));

        issue.Quiet.Should().BeTrue();
        issue.Headline.Id.Should().Be("quiet");
        issue.Schedule.Should().BeNull();
    }

    [Test]
    public void Headline_CompletionBeatsEverythingElse()
    {
        var archive = Archive(Days("2026-01-26", 7).Select(day => Session(1, day, 20, 5 * 3600, name: "Big Game")))
            .With(
                unlocks: [new InsightUnlock(2, "last", "Last", 1, Unix("2026-01-28"), "2026-01-28")],
                progress: [new InsightProgress(2, "Done Game", 40, 40)]);

        var issue = Compose(archive);

        issue.Headline.Id.Should().Be("completed:2");
        issue.Headline.Title.Zh.Should().Contain("[[g:2|Done Game]]").And.Contain("40");
        issue.Headline.Title.En.Should().Contain("[[g:2|Done Game]]");
    }

    [Test]
    public void Headline_ACompletedGameFromAnotherWeekIsNotNews()
    {
        var archive = Archive([Session(1, "2026-01-27", 20, 3600)])
            .With(
                unlocks: [new InsightUnlock(2, "last", "Last", 1, Unix("2025-01-28"), "2025-01-28")],
                progress: [new InsightProgress(2, "Done Game", 40, 40)]);

        Compose(archive).Headline.Id.Should().NotStartWith("completed");
    }

    [Test]
    public void Headline_ANewRecordIsReportedWithThePreviousOne()
    {
        var archive = Archive(
        [
            Session(1, "2026-01-01", 10, 3 * 3600),
            Session(2, "2026-01-27", 10, 7 * 3600, name: "Marathon")
        ]);

        var issue = Compose(archive);

        issue.Headline.Id.Should().Be("record");
        issue.Headline.Title.Zh.Should().Contain("7 小时");
        issue.Headline.AppId.Should().Be(2);
    }

    [Test]
    public void Headline_AComebackWithACluePrefersThatGame()
    {
        var archive = Archive(
        [
            Session(1, "2025-06-01", 20, 3 * 3600),
            Session(2, "2025-11-01", 20, 3 * 3600),
            Session(1, "2026-01-30", 20, 2 * 3600, tags: SessionTags.Comeback),
            Session(2, "2026-01-30", 22, 2 * 3600, tags: SessionTags.Comeback)
        ]).With(builds: [new InsightBuildChange(2, 5, Unix("2026-01-28"), false)]);

        Compose(archive).Headline.Id.Should().Be("comeback:2");
    }

    [Test]
    public void Text_NeverLeaksATemplatePlaceholder()
    {
        var archive = Archive(Days("2026-01-05", 28).Select(day => Session(1, day, 23, 2 * 3600, tags: SessionTags.LateNight)))
            .With(
                owned: [new InsightOwnedGame(5, "Old One", 0, 0), new InsightOwnedGame(6, "Tried One", 20, 0)],
                acquisitions: [new InsightAcquisition(5, Unix("2024-01-01")), new InsightAcquisition(6, Unix("2024-02-01"))],
                apps: new Dictionary<int, InsightAppFacts> { [5] = new(5) { PriceCents = 1999 } });

        var json = JsonSerializer.Serialize(Compose(archive));

        json.Should().NotContain("{game}").And.NotContain("{hours}").And.NotContain("{days}");
        json.Should().NotMatchRegex(@"\{[a-z]+\}");
    }

    [Test]
    public void Schedule_FillsTheGridFromTheFirstDayOfTheIssue()
    {
        var archive = Archive([Session(1, "2026-01-27", 22, 3 * 3600)], options: Options);

        var schedule = Compose(archive).Schedule!;

        schedule.Heat[1][22].Should().Be(60);
        schedule.Heat[1][23].Should().Be(60);
        schedule.Heat[2][0].Should().Be(60, "跨过午夜后按日历归到下一行");
        schedule.Heat.Sum(row => row.Sum()).Should().Be(180);
    }

    [Test]
    public void LatestFinish_ComparesPastMidnight()
    {
        var archive = Archive(
        [
            Session(1, "2026-01-26", 23, 3600, tags: SessionTags.LateNight),
            Session(1, "2026-01-28", 1, 3 * 3600, tags: SessionTags.LateNight)
        ]);

        Compose(archive).Schedule!.LatestHour.Should().Be(28);
    }

    [Test]
    public void Friends_ReportsTimeTogetherAndListChanges()
    {
        var session = Session(1, "2026-01-27", 20, 2 * 3600, tags: SessionTags.Coop, name: "Team Game");
        var archive = Archive([session]).With(friends: new InsightFriendData(
            [new InsightFriendPlay("7", "Pal", 1, session.Start, session.End)],
            [],
            [new InsightFriendChange("8", "New", "added", null, null, Unix("2026-01-28")), new InsightFriendChange("9", "Old", "renamed", "Old", "Older", Unix("2026-01-28"))]));

        var items = Compose(archive).Friends!.Items;

        items.Should().HaveCount(3);
        items[0].Zh.Should().Contain("[[f:7|Pal]]").And.Contain("2 小时");
        items[2].En.Should().Contain("Older");
    }

    [Test]
    public void Friends_IsLeftOutWhenThereIsNothingToSay()
        => Compose(Archive([Session(1, "2026-01-27", 20, 3600)])).Friends.Should().BeNull();

    [Test]
    public void Obituary_FallsOnTheWeekTheAbandonThresholdIsCrossed()
    {
        var archive = Archive([Session(1, "2025-10-30", 20, 5 * 3600, name: "Gone"), Session(2, "2026-01-27", 20, 3600)]);

        var obituary = Compose(archive).Obituaries.Should().ContainSingle().Subject;

        obituary.AppId.Should().Be(1);
        obituary.Text.Zh.Should().Contain("享年 5 小时");
    }

    [Test]
    public void Obituary_ManualStatusAlwaysWins()
    {
        var sessions = new[] { Session(1, "2025-10-30", 20, 5 * 3600), Session(2, "2026-01-27", 20, 5 * 3600) };

        Compose(Archive(sessions).With(statuses: [new InsightGameStatus(1, "playing", 0, null, null, null)])).Obituaries.Should().BeEmpty("手动标记在玩");
        Compose(Archive(sessions).With(statuses: [new InsightGameStatus(2, "abandoned", Unix("2026-01-28"), null, null, null)]))
            .Obituaries.Select(item => item.AppId).Should().Contain(2, "手动标记弃坑就是这周走的");
    }

    [Test]
    public void Market_CandlesAreRealRollingSums()
    {
        var sessions = Days("2026-01-19", 7).Select(day => Session(1, day, 20, 3600))
            .Concat(Days("2026-01-26", 7).Select(day => Session(1, day, 20, 2 * 3600)));

        var market = Compose(Archive(sessions)).Market!;
        var last = market.Candles[^1];

        last.Open.Should().Be(7);
        last.Close.Should().Be(14);
        last.High.Should().Be(14);
        last.Low.Should().Be(7);
        last.Volume.Should().Be(14);
        market.IndexChange.Should().Be(1);
        market.Gainers.Single().Change.Should().Be(1);
    }

    [Test]
    public void Market_NeedsMoreThanOneWeek()
        => Compose(Archive([Session(1, "2026-01-27", 20, 3600)])).Market.Should().BeNull();

    [Test]
    public void Forecast_NeedsFourWeeksOfHistory()
    {
        Compose(Archive(Days("2026-01-19", 14).Select(day => Session(1, day, 20, 3600)))).Forecast.Should().BeNull();
        Compose(Archive(Days("2025-12-08", 56).Select(day => Session(1, day, 20, 3600)))).Forecast!.Days.Should().HaveCount(7);
    }

    [Test]
    public void Forecast_UsesTheSameWeekdayInEarlierWeeks()
    {
        // 只在周五玩：预报里周五晴，其余日子没有活动
        var fridays = Days("2025-12-12", 8, 7).Select(day => Session(1, day, 20, 2 * 3600));

        var forecast = Compose(Archive(fridays)).Forecast!;

        var friday = forecast.Days.Single(day => InsightDays.Parse(day.Day).DayOfWeek == DayOfWeek.Friday);
        friday.Probability.Should().Be(1);
        friday.Sky.Should().Be("clear");
        friday.Part.Should().Be("evening");
        friday.ExpectedHours.Should().Be(2);
        forecast.Days.Where(day => day != friday).Should().OnlyContain(day => day.Sky == "calm");
        forecast.Summary.Zh.Should().Contain("周五").And.Contain("100%");
    }

    [Test]
    public void Forecast_MentionsOnlyAFriendSeenOftenEnough()
    {
        var sessions = Days("2025-12-08", 56).Select(day => Session(1, day, 20, 3600)).ToList();
        var online = Days("2025-12-12", 8, 7).Select(day => new InsightFriendOnline("7", "Pal", Unix(day, 20), Unix(day, 22))).ToList();

        Compose(Archive(sessions).With(friends: new InsightFriendData([], online, []))).Forecast!.Friend!.Zh.Should().Contain("[[f:7|Pal]]").And.Contain("周五");
        Compose(Archive(sessions).With(friends: new InsightFriendData([], online.Take(2).ToList(), []))).Forecast!.Friend.Should().BeNull();
    }

    [Test]
    public void Classifieds_RotateWithTheIssueButStayDeterministic()
    {
        var owned = Enumerable.Range(1, 12).Select(appId => new InsightOwnedGame(appId, $"Game {appId}", 0, 0)).ToList();
        var acquisitions = owned.Select(game => new InsightAcquisition(game.AppId, Unix("2024-01-01") + game.AppId)).ToList();
        var archive = Archive([Session(100, "2026-01-27", 20, 3600)]).With(owned: owned, acquisitions: acquisitions);

        var first = Compose(archive).Classifieds;
        var second = Compose(archive).Classifieds;

        first.Should().HaveCount(3);
        first.Select(ad => ad.AppId).Should().Equal(second.Select(ad => ad.AppId));
        first[0].Text.Zh.Should().Contain("天");
    }

    [Test]
    public void Observations_LeaveComebacksToTheStories()
    {
        var comeback = new Insight("causal.comeback", InsightKind.Causal, "a", "b", new Dictionary<string, double>(), 1, 0.9, []) { IsEvent = true };
        var idle = new Insight("behavior.idleRate", InsightKind.Behavior, "a", "b", new Dictionary<string, double> { ["rate"] = 0.3 }, 20, 0.8, []);

        Compose(Archive([Session(1, "2026-01-27", 20, 3600)]), [comeback, idle]).Observations.Should().ContainSingle();
    }

    [Test]
    public void Extra_HasOneStoryAndTheDayItHappened()
    {
        var archive = Archive([Session(1, "2026-01-27", 20, 3600)]);

        var issue = WeeklyComposer.ComposeExtra(archive, new WeeklyExtraEvent("total:1000", "total", Unix("2026-01-27", 22), null, null, 1000));

        issue.Kind.Should().Be("extra");
        issue.FromDay.Should().Be("2026-01-27");
        issue.Headline.Title.Zh.Should().Contain("1000");
        issue.Headline.Body.Zh.Should().Contain("42 天");
    }

    [Test]
    public void Say_ResolveSwapsBothKindsOfToken()
    {
        var text = $"{Say.Game(570, "Dota|2")} 和 {Say.Friend("7656", "A]B")}";

        Say.Resolve(text, (appId, name) => $"<{appId}:{name}>", (id, name) => $"<{id}:{name}>").Should().Be("<570:Dota/2> 和 <7656:A)B>");
    }
}

[TestFixture]
public sealed class WeeklyExtrasTests
{
    private static readonly long Since = Unix("2026-01-27");

    [Test]
    public void Detect_AGameCompletedAfterTheBaseline()
    {
        var archive = Archive().With(
            unlocks: [new InsightUnlock(1, "a", "A", 1, Since + 100, "2026-01-27")],
            progress: [new InsightProgress(1, "Done", 10, 10), new InsightProgress(2, "Old", 10, 10)]);

        WeeklyExtras.Detect(archive, Since).Select(item => item.Key).Should().Equal("completed:1");
    }

    [Test]
    public void Detect_ARecordNeedsAPreviousRecordAndEnoughHours()
    {
        var first = Archive([Session(1, "2026-01-28", 20, 9 * 3600)]);
        var small = Archive([Session(1, "2026-01-01", 20, 3600), Session(1, "2026-01-28", 20, 2 * 3600)]);
        var real = Archive([Session(1, "2026-01-01", 20, 5 * 3600), Session(1, "2026-01-28", 20, 6 * 3600)]);

        WeeklyExtras.Detect(first, Since).Should().NotContain(item => item.Kind == "record", "第一局不是破纪录");
        WeeklyExtras.Detect(small, Since).Should().NotContain(item => item.Kind == "record");
        WeeklyExtras.Detect(real, Since).Single(item => item.Kind == "record").Previous.Should().Be(5);
    }

    [Test]
    public void Detect_MilestonesFireOnlyOnTheSessionThatCrossesThem()
    {
        var sessions = new[] { Session(1, "2026-01-01", 20, 95 * 3600), Session(2, "2026-01-28", 20, 10 * 3600) };

        WeeklyExtras.Detect(Archive(sessions), Since).Select(item => item.Key).Should().Contain("total:100");
        WeeklyExtras.Detect(Archive(sessions), Unix("2026-01-30")).Should().BeEmpty("已经过了基线");
    }

    [Test]
    public void Detect_AppMilestonesAreSeparateFromTheTotal()
    {
        var sessions = new[] { Session(1, "2026-01-01", 20, 95 * 3600), Session(1, "2026-01-28", 20, 10 * 3600) };

        WeeklyExtras.Detect(Archive(sessions), Since).Select(item => item.Key).Should().Contain("app:1:100");
    }
}

[TestFixture]
public sealed class PersonaTests
{
    private static InsightArchive Year(Func<int, InsightSession> session, int count = 60)
        => Archive(Enumerable.Range(0, count).Select(session));

    [Test]
    public void ByYear_NeedsEnoughData()
        => PersonaAnalyzer.ByYear(Archive(Days("2025-01-01", 20).Select(day => Session(1, day, 20, 3600)))).Should().BeEmpty();

    [Test]
    public void ByYear_AOneGameNightOwlIsFocusedAndNocturnal()
    {
        var archive = Year(index => Session(1, InsightDays.Shift("2025-01-01", index * 5), 23, 3 * 3600));

        var persona = PersonaAnalyzer.ByYear(archive).Should().ContainSingle().Subject;

        persona.Code[0].Should().Be('F');
        persona.Code[2].Should().Be('N');
        persona.Name.Zh.Should().NotBeEmpty();
        persona.Name.En.Should().NotBeEmpty();
    }

    [Test]
    public void ByYear_ManyGamesInDaylightAreExplorerAndDay()
    {
        var archive = Year(index => Session(index + 1, InsightDays.Shift("2025-01-01", index * 5), 10, 2 * 3600));

        var persona = PersonaAnalyzer.ByYear(archive).Single();

        persona.Code[0].Should().Be('E');
        persona.Code[2].Should().Be('D');
    }

    [Test]
    public void ByYear_CoopTimeMakesItSocial()
    {
        var archive = Year(index => Session(1, InsightDays.Shift("2025-01-01", index * 5), 20, 2 * 3600, tags: SessionTags.Coop));

        PersonaAnalyzer.ByYear(archive).Single().Code[1].Should().Be('S');
    }

    [Test]
    public void ByYear_ATwoMonthBurstIsBurst()
    {
        var archive = Year(index => Session(1, InsightDays.Shift("2025-01-01", index), 20, 2 * 3600))
            .With();
        var late = Archive(archive.Sessions.Concat([Session(1, "2025-12-20", 20, 3600)]));

        PersonaAnalyzer.ByYear(late).Single().Code[3].Should().Be('B');
    }

    [Test]
    public void ByYear_EveryYearIsJudgedOnItsOwn()
    {
        var archive = Archive(
            Enumerable.Range(0, 60).Select(index => Session(1, InsightDays.Shift("2024-01-01", index * 5), 23, 3 * 3600))
                .Concat(Enumerable.Range(0, 60).Select(index => Session(index + 2, InsightDays.Shift("2025-01-01", index * 5), 10, 2 * 3600))));

        PersonaAnalyzer.ByYear(archive).Select(item => item.Code[0]).Should().Equal('F', 'E');
    }

    [Test]
    public void AllSixteenCombinationsHaveANameInBothLanguages()
    {
        // 用四条轴的极端值凑出全部组合，缺名字时 Types[code] 会抛异常
        foreach (var explore in new[] { true, false })
        foreach (var coop in new[] { true, false })
        foreach (var night in new[] { true, false })
        foreach (var burst in new[] { true, false })
        {
            var sessions = Enumerable.Range(0, 60).Select(index => Session(
                explore ? index + 1 : 1,
                burst ? InsightDays.Shift("2025-01-01", index) : InsightDays.Shift("2025-01-01", index * 6),
                night ? 23 : 10,
                2 * 3600,
                tags: coop ? SessionTags.Coop : SessionTags.None)).ToList();
            if (burst) sessions.Add(Session(1, "2025-12-20", 10, 3600));
            var persona = PersonaAnalyzer.ByYear(Archive(sessions)).Single();
            persona.Name.Zh.Should().NotBeEmpty(persona.Code);
            persona.Title.Text.Zh.Should().StartWith("2025 ");
        }
    }

    [Test]
    public void Title_ComesFromTheStrongestHabitWithAReason()
    {
        var old = new Dictionary<int, InsightAppFacts> { [1] = new(1) { ReleaseDate = Unix("2010-01-01") } };
        var archive = Year(index => Session(1, InsightDays.Shift("2025-01-01", index * 5), 23, 3 * 3600)).With(apps: old);

        var title = PersonaAnalyzer.ByYear(archive).Single().Title;

        title.Text.Zh.Should().Be("2025 深夜考古学家");
        title.Text.En.Should().Be("2025 Midnight Archaeologist");
        title.Reasons.Should().HaveCount(2);
    }
}

[TestFixture]
public sealed class CareerTests
{
    [Test]
    public void Build_IsEmptyWithoutSessions() => CareerAnalyzer.Build(Archive()).Should().BeEmpty();

    [Test]
    public void Build_OrdersHighlightsByTime()
    {
        var archive = Archive(
        [
            Session(1, "2024-03-01", 20, 3600, name: "First"),
            Session(2, "2024-06-01", 20, 8 * 3600, name: "Long"),
            Session(3, "2025-02-01", 20, 2 * 3600, name: "Next Year")
        ]).With(
            unlocks: [new InsightUnlock(3, "rare", "Rare", 0.4, Unix("2025-03-01"), "2025-03-01"), new InsightUnlock(3, "common", "Common", 40, Unix("2025-03-02"), "2025-03-02")],
            progress: [new InsightProgress(3, "Next Year", 2, 2)]);

        var highlights = CareerAnalyzer.Build(archive);

        highlights.Select(item => item.Time).Should().BeInAscendingOrder();
        highlights[0].Kind.Should().Be("firstSession");
        highlights.Single(item => item.Kind == "longestSession").AppId.Should().Be(2);
        highlights.Single(item => item.Kind == "rarestAchievement").Text.Zh.Should().Contain("Rare").And.Contain("0.4%");
        highlights.Single(item => item.Kind == "firstCompletion").AppId.Should().Be(3);
        highlights.Should().Contain(item => item.Kind == "yearLongest" && item.AppId == 3);
    }

    [Test]
    public void Build_FindsTheLongestNightWithAFriend()
    {
        var session = Session(1, "2025-02-01", 20, 3 * 3600, tags: SessionTags.Coop, name: "Team");
        var archive = Archive([session]).With(friends: new InsightFriendData(
            [new InsightFriendPlay("7", "Pal", 1, session.Start, session.Start + 2 * 3600), new InsightFriendPlay("8", "Brief", 1, session.Start, session.Start + 600)],
            [],
            []));

        CareerAnalyzer.Build(archive).Single(item => item.Kind == "friendNight").Text.Zh.Should().Contain("[[f:7|Pal]]").And.Contain("2 小时");
    }

    [Test]
    public void Build_MarksTheHundredHourGameAndTotalMilestones()
    {
        var archive = Archive(Enumerable.Range(0, 11).Select(index => Session(1, InsightDays.Shift("2025-01-01", index), 10, 10 * 3600)));

        var highlights = CareerAnalyzer.Build(archive);

        highlights.Should().Contain(item => item.Kind == "firstHundred");
        highlights.Should().ContainSingle(item => item.Kind == "totalMilestone");
    }
}

[TestFixture]
public sealed class SpectacleTests
{
    [Test]
    public void Priciest_ListsOnlyUnplayedGamesAndPrefersTheEnteredPrice()
    {
        var archive = Archive([Session(3, "2026-01-01", 10, 3600)]).With(
            owned: [new InsightOwnedGame(1, "Cheap", 0, 0), new InsightOwnedGame(2, "Dear", 0, 0), new InsightOwnedGame(3, "Played", 0, 0), new InsightOwnedGame(4, "Unknown", 0, 0)],
            statuses: [new InsightGameStatus(2, "wantToPlay", 0, null, 298, "CNY")],
            apps: new Dictionary<int, InsightAppFacts>
            {
                [1] = new(1) { PriceCents = 999 },
                [2] = new(2) { PriceCents = 5999 },
                [3] = new(3) { PriceCents = 9999 }
            });

        var board = SpectacleAnalyzer.Build(archive);

        board.PriciestUnplayed.Select(item => item.Name).Should().Equal("Dear", "Cheap");
        board.PriciestUnplayed[0].Value.Should().Be(298);
        board.PriciestUnplayed[0].Currency.Should().Be("CNY");
        board.PriciestUnplayed[1].Value.Should().Be(9.99);
        board.PriciestUnplayed[1].Currency.Should().Be("USD");
    }

    [Test]
    public void Untouched_UsesTheLaterOfBothSources()
    {
        var archive = Archive([Session(1, "2026-01-30", 10, 3600)]).With(owned:
        [
            new InsightOwnedGame(1, "Recent By Session", 60, Unix("2020-01-01")),
            new InsightOwnedGame(2, "Ancient", 60, Unix("2021-01-01")),
            new InsightOwnedGame(3, "Never", 0, 0)
        ]);

        var untouched = SpectacleAnalyzer.Build(archive).LongestUntouched;

        untouched.Select(item => item.Name).Should().Equal("Ancient", "Recent By Session");
    }

    [Test]
    public void Delisted_ComesFromTheStoreStatus()
    {
        var archive = Archive().With(
            owned: [new InsightOwnedGame(1, "Gone", 600, 0), new InsightOwnedGame(2, "Here", 600, 0)],
            apps: new Dictionary<int, InsightAppFacts> { [1] = new(1) { Delisted = true, DelistedAt = 5 }, [2] = new(2) });

        var delisted = SpectacleAnalyzer.Build(archive).Delisted.Should().ContainSingle().Subject;

        delisted.AppId.Should().Be(1);
        delisted.Time.Should().Be(5);
    }
}

[TestFixture]
public sealed class StoreBrowseParserTests
{
    /// <summary>字段取自 2026-11-06 线上实测的响应。</summary>
    private const string Response = """
        {"response":{"store_items":[
          {"item_type":0,"id":570,"success":1,"visible":true,"name":"Dota 2","is_free":true,"appid":570,"type":0,
           "reviews":{"summary_filtered":{"review_count":2811217,"percent_positive":80}},
           "basic_info":{"franchises":[{"name":"Dota"}]},"release":{"steam_release_date":1373389200}},
          {"item_type":0,"id":50300,"success":1,"visible":true,"name":"Spec Ops: The Line","appid":50300,"type":0,
           "reviews":{"summary_filtered":{"review_count":56468,"percent_positive":93}}},
          {"item_type":0,"id":224060,"success":15,"visible":false,"name":"","appid":0},
          {"item_type":0,"id":1091500,"success":1,"visible":true,"name":"Cyberpunk 2077","appid":1091500,"type":0,
           "best_purchase_option":{"packageid":367653,"final_price_in_cents":"2999","original_price_in_cents":"5999"}},
          {"item_type":0,"id":999,"success":1,"visible":true,"name":"Soon","type":0,"release":{"is_coming_soon":true}}
        ]}}
        """;

    [Test]
    public void Parse_ReadsTheFieldsAsTheyAreOnTheWire()
    {
        var items = StoreBrowseParser.Parse(Response).ToDictionary(item => item.AppId);

        items[570].IsFree.Should().BeTrue();
        items[570].ReviewPercent.Should().Be(80);
        items[570].Franchise.Should().Be("Dota");
        items[570].ReleaseDate.Should().Be(1373389200);
        items[1091500].PriceCents.Should().Be(5999, "原价，不是打折价");
        items[1091500].Purchasable.Should().BeTrue();
    }

    [TestCase(570, false, TestName = "免费游戏没有购买选项也不算下架")]
    [TestCase(50300, true, TestName = "商店页还在但买不了算下架")]
    [TestCase(224060, true, TestName = "商店页消失算下架")]
    [TestCase(1091500, false, TestName = "能买的不算下架")]
    [TestCase(999, false, TestName = "还没发售的不算下架")]
    public void Delisted_MatchesWhatWasMeasuredOnline(int appId, bool expected)
        => StoreBrowseParser.Parse(Response).Single(item => item.AppId == appId).Delisted.Should().Be(expected);

    [TestCase("""{"response":{}}""")]
    [TestCase("{}")]
    public void Parse_ToleratesAnEmptyResponse(string json) => StoreBrowseParser.Parse(json).Should().BeEmpty();

    [Test]
    public void BuildRequest_AsksForTheThreeSections()
    {
        var request = StoreBrowseParser.BuildRequest([570, 10]);

        request.Should().Contain("\"appid\":570").And.Contain("include_reviews").And.Contain("include_basic_info").And.Contain("country_code");
    }
}
