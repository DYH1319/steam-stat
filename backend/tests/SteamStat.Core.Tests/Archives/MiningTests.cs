using FluentAssertions;
using NUnit.Framework;
using SteamStat.Core.Archives.Mining;

namespace SteamStat.Core.Tests.Archives;

[TestFixture]
public sealed class PioneerIndexTests
{
    private const long Day = 86400;
    private const long Now = 1000 * Day;

    private static PioneerUnlock Unlock(int id, double first, double current, long unlockDay = 800, long firstDay = 801)
        => new(1, $"A{id}", unlockDay * Day, first, firstDay * Day, current);

    [Test]
    public void UnlocksBeforeTheAppExistedAreLate()
    {
        var report = PioneerIndex.Compute([Unlock(1, 1, 5, unlockDay: 100, firstDay: 801)], Now);

        report.Late.Should().Be(1);
        report.Fresh.Should().Be(0);
        report.Score.Should().BeNull();
    }

    [Test]
    public void RecentUnlocksAreNotMatureYet()
    {
        var report = PioneerIndex.Compute([Unlock(1, 1, 5, 990, 991)], Now);

        report.Fresh.Should().Be(1);
        report.Matured.Should().Be(0);
    }

    [Test]
    public void TooFewSamplesHaveNoScore()
        => PioneerIndex.Compute(Enumerable.Range(0, 4).Select(id => Unlock(id, 1, 10)), Now).Score.Should().BeNull();

    [Test]
    public void TheScoreIsHowMuchMoreCommonItBecame()
    {
        // 当时 1%，现在 10%：领先 90%
        var report = PioneerIndex.Compute(Enumerable.Range(0, 5).Select(id => Unlock(id, 1, 10)), Now);

        report.Score.Should().Be(90);
        report.Top.Should().HaveCount(5);
        report.Top[0].Lead.Should().BeApproximately(0.9, 0.0001);
    }

    [Test]
    public void AnAchievementThatGotRarerGivesNoLead()
        => PioneerIndex.Compute(Enumerable.Range(0, 5).Select(id => Unlock(id, 10, 1)), Now).Score.Should().Be(0);

    [Test]
    public void ZeroPercentDoesNotDivideByZero()
    {
        var report = PioneerIndex.Compute(Enumerable.Range(0, 5).Select(id => Unlock(id, 0, 0)), Now);

        report.Score.Should().Be(0);
    }

    [Test]
    public void MissingFirstObservationsAreSkipped()
    {
        var report = PioneerIndex.Compute([new PioneerUnlock(1, "A", 0, null, null, 5)], Now);

        report.Fresh.Should().Be(0);
        report.Late.Should().Be(0);
    }

    [Test]
    public void TheBiggestLeadsComeFirst()
    {
        var report = PioneerIndex.Compute([Unlock(1, 5, 10), Unlock(2, 1, 50), Unlock(3, 2, 4), Unlock(4, 1, 2), Unlock(5, 1, 3)], Now);

        report.Top[0].InternalName.Should().Be("A2");
    }
}

[TestFixture]
public sealed class RichPresenceMinerTests
{
    private const long T = 1_000_000;

    private static RichPresenceSample Sample(long offset, string json, string? display = null) => new(T + offset, json, display);

    [Test]
    public void TimeRunsUntilTheNextRecord()
    {
        var report = RichPresenceMiner.Mine(
        [
            Sample(0, """{"game:map":"de_dust2","game:mode":"competitive"}"""),
            Sample(600, """{"game:map":"de_inferno","game:mode":"competitive"}"""),
            Sample(900, "{}")
        ], T + 2000);

        report.RecordedSeconds.Should().Be(900);
        var maps = report.Dimensions.Single(dimension => dimension.Kind == RichPresenceDimensions.Map);
        maps.Values.Select(value => (value.Value, value.Seconds)).Should().Equal(("de_dust2", 600L), ("de_inferno", 300L));
        report.Dimensions.Single(dimension => dimension.Kind == RichPresenceDimensions.Mode).Values.Single().Seconds.Should().Be(900);
    }

    [Test]
    public void AClearedStateCountsNoTime()
    {
        var report = RichPresenceMiner.Mine([Sample(0, "{}"), Sample(5000, """{"map":"x"}"""), Sample(5100, "{}")], T + 9000);

        report.RecordedSeconds.Should().Be(100);
    }

    [Test]
    public void ARecordWithNoEndIsCapped()
        => RichPresenceMiner.Mine([Sample(0, """{"map":"x"}""")], T + 100 * 3600).RecordedSeconds.Should().Be(6 * 3600);

    [TestCase("map", RichPresenceDimensions.Map)]
    [TestCase("game:map", RichPresenceDimensions.Map)]
    [TestCase("currentmap", RichPresenceDimensions.Map)]
    [TestCase("mapname", RichPresenceDimensions.Map)]
    [TestCase("gamemode", RichPresenceDimensions.Mode)]
    [TestCase("game:mode", RichPresenceDimensions.Mode)]
    [TestCase("matchgrouploc", RichPresenceDimensions.Mode)]
    [TestCase("rank", RichPresenceDimensions.Rank)]
    [TestCase("skill_group", RichPresenceDimensions.Rank)]
    [TestCase("hero", RichPresenceDimensions.Character)]
    [TestCase("class", RichPresenceDimensions.Character)]
    [TestCase("difficulty", RichPresenceDimensions.Difficulty)]
    public void KnowsCommonKeys(string key, string kind)
        => RichPresenceMiner.Mine([Sample(0, $$"""{"{{key}}":"Value"}""")], T + 10).Dimensions.Should().ContainSingle().Which.Kind.Should().Be(kind);

    [Test]
    public void ProtocolFieldsAreNotContent()
    {
        var report = RichPresenceMiner.Mine([Sample(0, """{"steam_display":"#x","connect":"+connect 1.2.3.4","status":"Menu","steam_player_group":"99","param0":"3"}""")], T + 10);

        report.Dimensions.Should().BeEmpty();
        report.UnknownKeys.Should().BeEmpty();
    }

    [Test]
    public void UnknownKeysAreListedNotDropped()
        => RichPresenceMiner.Mine([Sample(0, """{"mood":"happy"}""")], T + 10).UnknownKeys.Should().Equal("mood");

    [Test]
    public void LocalizationTokensKeepTheirLastPart()
        => RichPresenceMiner.Mine([Sample(0, """{"mode":"#Mode_Competitive"}""")], T + 10)
            .Dimensions.Single().Values.Single().Value.Should().Be("Competitive");

    [TestCase("12345")]
    [TestCase("9f8e7d6c5b4a3210")]
    [TestCase("none")]
    public void IdsAreNotMapNames(string value)
        => RichPresenceMiner.Mine([Sample(0, $$"""{"map":"{{value}}"}""")], T + 10).Dimensions.Should().BeEmpty();

    [Test]
    public void NumericRanksAreKept()
        => RichPresenceMiner.Mine([Sample(0, """{"rank":"18"}""")], T + 10).Dimensions.Single().Values.Single().Value.Should().Be("18");

    [Test]
    public void RankChangesAreAHistory()
    {
        var report = RichPresenceMiner.Mine(
        [
            Sample(0, """{"rank":"Silver"}"""),
            Sample(10, """{"rank":"Silver","map":"a"}"""),
            Sample(20, """{"rank":"Gold"}""")
        ], T + 30);

        report.RankHistory.Should().Equal(new RichPresenceChange(T, "Silver"), new RichPresenceChange(T + 20, "Gold"));
    }

    [Test]
    public void PartySizeBecomesItsOwnDimension()
    {
        var report = RichPresenceMiner.Mine([Sample(0, """{"steam_player_group":"1","steam_player_group_size":"3"}""")], T + 100);

        report.Dimensions.Single(dimension => dimension.Kind == RichPresenceDimensions.Party).Values.Single().Value.Should().Be("3");
    }

    [Test]
    public void DisplayTextCollapsesScores()
    {
        var report = RichPresenceMiner.Mine(
        [
            Sample(0, """{"status":"a"}""", "Competitive (12-8)"),
            Sample(100, """{"status":"b"}""", "Competitive (13-8)")
        ], T + 200);

        var activity = report.Dimensions.Single(dimension => dimension.Kind == RichPresenceDimensions.Activity);
        activity.Values.Single().Should().Match<RichPresenceValue>(value => value.Value == "Competitive (#-#)" && value.Seconds == 200 && value.Times == 2);
    }

    [Test]
    public void BadRecordsAreSkipped()
    {
        var report = RichPresenceMiner.Mine([Sample(0, "not json"), Sample(10, """{"map":"x"}""")], T + 20);

        report.RecordedSeconds.Should().Be(10);
        report.Records.Should().Be(2);
    }

    [Test]
    public void NoRecordsIsAnEmptyReport()
    {
        var report = RichPresenceMiner.Mine([], T);

        report.FirstAt.Should().BeNull();
        report.Dimensions.Should().BeEmpty();
    }
}

[TestFixture]
public sealed class PurchaseSimulatorTests
{
    private static readonly int[] Tags = [1, 2, 3, 4, 5];

    private static SimulatorTarget Target(long? price = 3000)
        => new(9, "Target", Tags, [], ["Dev"], "Saga", price);

    private static SimulatorGame Game(int id, double hours, bool dropped = false, int[]? tags = null, string dev = "Other", string? franchise = null)
        => new(id, $"G{id}", tags ?? Tags, [dev], franchise, hours, dropped);

    [Test]
    public void TooFewSimilarGamesGiveNoPrediction()
    {
        var result = PurchaseSimulator.Simulate(Target(), [Game(1, 10), Game(2, 20), Game(3, 30), Game(4, 40)]);

        result.Enough.Should().BeFalse();
        result.MedianHours.Should().BeNull();
        result.CostPerHourCents.Should().BeNull();
        result.Neighbors.Should().HaveCount(4, "依据还是要列出来");
    }

    [Test]
    public void PredictsFromSimilarGames()
    {
        var result = PurchaseSimulator.Simulate(Target(), [Game(1, 2), Game(2, 10), Game(3, 20), Game(4, 40), Game(5, 80)]);

        result.Enough.Should().BeTrue();
        result.MedianHours.Should().Be(20);
        result.LowHours.Should().Be(10);
        result.HighHours.Should().Be(40);
        result.CostPerHourCents.Should().Be(150);
    }

    [Test]
    public void DropProbabilityIsTheShareOfDroppedNeighbors()
    {
        var result = PurchaseSimulator.Simulate(Target(), [Game(1, 2, true), Game(2, 3, true), Game(3, 20), Game(4, 40), Game(5, 80)]);

        result.DropProbability.Should().Be(0.4);
    }

    [Test]
    public void NeverPlayedGamesAreCounted()
    {
        var result = PurchaseSimulator.Simulate(Target(), [Game(1, 0), Game(2, 0.2), Game(3, 20), Game(4, 40), Game(5, 80)]);

        result.NeverPlayedProbability.Should().Be(0.4);
    }

    [Test]
    public void UnrelatedGamesAreIgnored()
    {
        var unrelated = new[] { 100, 200, 300 };
        var result = PurchaseSimulator.Simulate(Target(), Enumerable.Range(1, 10).Select(id => Game(id, 500, tags: unrelated)).ToArray());

        result.Enough.Should().BeFalse();
        result.Candidates.Should().Be(0);
    }

    [Test]
    public void TheTargetItselfIsNotItsOwnNeighbor()
        => PurchaseSimulator.Simulate(Target(), [Game(9, 500)]).Neighbors.Should().BeEmpty();

    [Test]
    public void GamesWithoutTagsCannotBeCompared()
        => PurchaseSimulator.Simulate(Target(), [Game(1, 5, tags: [])]).Neighbors.Should().BeEmpty();

    [Test]
    public void SameFranchiseAndDeveloperRankHigher()
    {
        var partial = new[] { 1, 2, 3 };
        var result = PurchaseSimulator.Simulate(Target(), [Game(1, 5, tags: partial), Game(2, 5, tags: partial, dev: "Dev", franchise: "Saga")]);

        result.Neighbors[0].AppId.Should().Be(2);
    }

    [Test]
    public void AtMostTwelveNeighborsAreUsed()
    {
        var result = PurchaseSimulator.Simulate(Target(), Enumerable.Range(10, 30).Select(id => Game(id, id)).ToArray());

        result.Candidates.Should().Be(30);
        result.Neighbors.Should().HaveCount(12);
    }

    [Test]
    public void NoPriceMeansNoCostPerHour()
        => PurchaseSimulator.Simulate(Target(null), Enumerable.Range(1, 5).Select(id => Game(id, 10)).ToArray()).CostPerHourCents.Should().BeNull();
}

[TestFixture]
public sealed class PurchaseMatcherTests
{
    private static (IReadOnlyDictionary<string, int> Exact, IReadOnlyDictionary<string, int> Editions) Index(params (int AppId, string Name)[] apps)
        => PurchaseMatcher.BuildIndex(apps.Select(app => new MatchCandidate(app.AppId, app.Name)));

    private static IReadOnlyList<PurchaseItemMatch> Match(string[] items, long total, (IReadOnlyDictionary<string, int> Exact, IReadOnlyDictionary<string, int> Editions) index, params LicenseOnDay[] licenses)
        => PurchaseMatcher.Match(items, 1000 * 86400L, total, index.Exact, index.Editions, licenses);

    [TestCase("Portal 2™", "Portal 2")]
    [TestCase("PORTAL: 2", "portal 2")]
    [TestCase("Tom Clancy's Rainbow Six® Siege", "Tom Clancys Rainbow Six Siege")]
    public void NamesAreNormalized(string left, string right)
        => PurchaseMatcher.Normalize(left).Should().Be(PurchaseMatcher.Normalize(right));

    [Test]
    public void MatchesByName()
    {
        var result = Match(["Portal 2™"], 3500, Index((620, "Portal 2")));

        result.Should().ContainSingle().Which.Should().Be(new PurchaseItemMatch("Portal 2™", 620, MatchSources.Exact, 3500, false));
    }

    [TestCase("Elden Ring Deluxe Edition")]
    [TestCase("Elden Ring - Game of the Year")]
    [TestCase("Elden Ring Digital Deluxe")]
    public void MatchesEditionsToTheBaseGame(string item)
    {
        var result = Match([item], 3500, Index((1245620, "Elden Ring")));

        result[0].AppId.Should().Be(1245620);
        result[0].Source.Should().Be(MatchSources.Edition);
    }

    [Test]
    public void FallsBackToTheOnlyLicenseThatDay()
    {
        var result = Match(["Totally Different Store Name"], 100, Index((1, "Other")), new LicenseOnDay(1000 * 86400L, [42]));

        result[0].AppId.Should().Be(42);
        result[0].Source.Should().Be(MatchSources.License);
    }

    [Test]
    public void DoesNotGuessBetweenTwoLicenses()
        => Match(["Unknown"], 100, Index((1, "Other")), new LicenseOnDay(1000 * 86400L, [42, 43]))[0].AppId.Should().BeNull();

    [Test]
    public void IgnoresLicensesOnOtherDays()
        => Match(["Unknown"], 100, Index((1, "Other")), new LicenseOnDay(900 * 86400L, [42]))[0].AppId.Should().BeNull();

    [Test]
    public void ALicenseAlreadyClaimedIsNotReused()
    {
        var result = Match(["Portal 2", "Unknown"], 100, Index((620, "Portal 2")), new LicenseOnDay(1000 * 86400L, [620]));

        result[1].AppId.Should().BeNull();
    }

    [Test]
    public void SharedPurchasesSplitTheTotalExactly()
    {
        var result = Match(["A", "B", "C"], 1000, Index((1, "A"), (2, "B"), (3, "C")));

        result.Sum(item => item.AllocatedCents).Should().Be(1000);
        result.Should().OnlyContain(item => item.Shared);
    }

    [Test]
    public void DuplicateNamesKeepTheLowestAppId()
        => Index((20, "Game"), (10, "Game")).Exact["game"].Should().Be(10);

    [Test]
    public void NoItemsIsNoMatches() => Match([], 100, Index()).Should().BeEmpty();
}
