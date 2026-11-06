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
        public Task<IReadOnlyList<Insight>> DetectAsync(IInsightDataSource data, InsightPeriod period, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Insight>>(insights);
    }

    private sealed class FixedData(params object?[][] rows) : IInsightDataSource
    {
        public Task<IReadOnlyList<object?[]>> QueryAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<object?[]>>(rows);
    }

    [TestCase(9, 0.9, false)]
    [TestCase(10, 0.59, false)]
    [TestCase(10, 0.6, true)]
    public void Gate_RequiresBothSamplesAndConfidence(int samples, double confidence, bool expected)
        => new InsightGate().Passes(Sample(samples, confidence)).Should().Be(expected);

    [Test]
    public async Task Engine_DropsWeakInsightsAndRanksTheRest()
    {
        var engine = new InsightEngine([new FixedDetector(Sample(50, 0.7, "b"), Sample(3, 0.99, "weak")), new FixedDetector(Sample(50, 0.9, "a"))]);

        var result = await engine.RunAsync(new FixedData(), Period);

        result.Select(insight => insight.Id).Should().Equal("a", "b");
    }

    [Test]
    public async Task IdleRate_ReportsNotableRatesWithFactsAndEvidence()
    {
        var result = await new IdleRateDetector().DetectAsync(new FixedData([40L, 3000.0, 10000.0]), Period);

        var insight = result.Should().ContainSingle().Subject;
        insight.Facts["rate"].Should().BeApproximately(0.3, 0.0001);
        insight.SampleSize.Should().Be(40);
        insight.Evidence.Single().View.Should().Be("v_session_day");
    }

    [TestCase(1000.0, 10000.0)]
    [TestCase(0.0, 0.0)]
    public async Task IdleRate_StaysQuietWhenThereIsNothingToSay(double idle, double total)
        => (await new IdleRateDetector().DetectAsync(new FixedData([40L, idle, total]), Period)).Should().BeEmpty();

    [Test]
    public async Task IdleRate_HandlesAnEmptyPeriod()
        => (await new IdleRateDetector().DetectAsync(new FixedData([0L, null, null]), Period)).Should().BeEmpty();
}
