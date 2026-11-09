using FluentAssertions;
using SteamStat.Core.Insights;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class InsightModelTests
{
    private static readonly InsightPeriod Period = new("2026-01-01", "2026-01-31");

    private static Insight Sample(int samples, double confidence, string id = "x") => new(
        id, InsightKind.Behavior, Period.FromDay, Period.ToDay, new Dictionary<string, double>(), samples, confidence, []);

    private sealed class FixedDetector(params Insight[] insights) : IInsightDetector
    {
        public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period) => insights;
    }

    [TestCase(9, 0.9, false)]
    [TestCase(10, 0.59, false)]
    [TestCase(10, 0.6, true)]
    public void Gate_RequiresBothSamplesAndConfidence(int samples, double confidence, bool expected)
        => new InsightGate().Passes(Sample(samples, confidence)).Should().Be(expected);

    [Test]
    public void Gate_EventsDoNotNeedSamplesButStillNeedConfidence()
    {
        new InsightGate().Passes(Sample(1, 0.7) with { IsEvent = true }).Should().BeTrue();
        new InsightGate().Passes(Sample(1, 0.4) with { IsEvent = true }).Should().BeFalse();
    }

    [Test]
    public void Engine_DropsWeakInsightsAndRanksTheRest()
    {
        var engine = new InsightEngine([new FixedDetector(Sample(50, 0.7, "b"), Sample(3, 0.99, "weak")), new FixedDetector(Sample(50, 0.9, "a"))]);

        var result = engine.Run(InsightFixtures.Archive(), Period);

        result.Select(insight => insight.Id).Should().Equal("a", "b");
    }

    [Test]
    public void IdleRate_ReportsNotableRatesWithFactsAndEvidence()
    {
        var sessions = Enumerable.Range(0, 40).Select(index => InsightFixtures.Session(1, "2026-01-05", 9, 1000, idle: 300, id: index)).ToList();

        var insight = new IdleRateDetector().Detect(InsightFixtures.Archive(sessions), Period).Should().ContainSingle().Subject;

        insight.Facts["rate"].Should().BeApproximately(0.3, 0.0001);
        insight.SampleSize.Should().Be(40);
        insight.Evidence.Single().View.Should().Be("v_session");
    }

    [Test]
    public void IdleRate_StaysQuietWhenThereIsNothingToSay()
    {
        var low = Enumerable.Range(0, 40).Select(index => InsightFixtures.Session(1, "2026-01-05", 9, 1000, idle: 100, id: index)).ToList();

        new IdleRateDetector().Detect(InsightFixtures.Archive(low), Period).Should().BeEmpty();
        new IdleRateDetector().Detect(InsightFixtures.Archive(), Period).Should().BeEmpty();
    }

    [Test]
    public void DefaultDetectors_AllHaveCopyInBothLanguages()
    {
        var ids = new[]
        {
            "behavior.idleRate", "behavior.scheduleDrift", "behavior.droppedGenre", "behavior.playStyle", "causal.comeback",
            "forecast.backlogClearance", "forecast.completionDate", "forecast.abandonRisk",
            "anomaly.playtimeSpike", "anomaly.playtimeDrop", "anomaly.officialDiff", "anomaly.recordGap"
        };
        foreach (var id in ids)
        {
            var text = InsightCopy.Describe(Sample(10, 1, id));
            text.Zh.Should().NotBe(id);
            text.En.Should().NotBe(id);
        }
    }
}
