using ElectronNet.Features.History;
using FluentAssertions;

namespace ElectronNet.Tests.Features.History;

[TestFixture]
public sealed class PlaytimeReconcilerTests
{
    private static SnapshotPoint Snap(int minutes, int captured, int? lastSeen = null, bool baseline = false)
        => new(minutes, captured, lastSeen ?? captured, baseline);

    [Test]
    public void Reconcile_Matched_WhenLocalCoversOfficialDelta()
    {
        var result = PlaytimeReconciler.Reconcile(
            [Snap(100, 1000, baseline: true), Snap(160, 5000)],
            [new LocalSessionSpan(1000, 4600, 3600)]);

        result.Status.Should().Be("matched");
        result.UnaccountedSeconds.Should().Be(0);
        result.PreTrackingSeconds.Should().Be(100 * 60);
        result.BaselineMinutes.Should().Be(100);
        result.OfficialMinutes.Should().Be(160);
    }

    [Test]
    public void Reconcile_Elsewhere_WhenOfficialExceedsLocal()
    {
        var result = PlaytimeReconciler.Reconcile(
            [Snap(100, 1000, baseline: true), Snap(220, 5000)],
            [new LocalSessionSpan(1000, 2800, 1800)]);

        result.Status.Should().Be("elsewhere");
        result.UnaccountedSeconds.Should().Be(120 * 60 - 1800);
    }

    [Test]
    public void Reconcile_LocalExceeds_WhenLocalExceedsOfficial()
    {
        var result = PlaytimeReconciler.Reconcile(
            [Snap(100, 1000, baseline: true), Snap(160, 5000)],
            [new LocalSessionSpan(1000, 5000, 4800)]);

        result.Status.Should().Be("localExceeds");
        result.UnaccountedSeconds.Should().Be(3600 - 4800);
    }

    [Test]
    public void Reconcile_BaselineOnly_WhenSingleRowAndNoLocal()
    {
        var result = PlaytimeReconciler.Reconcile(
            [Snap(300, 1000, baseline: true)],
            []);

        result.Status.Should().Be("baselineOnly");
        result.OfficialDeltaSeconds.Should().Be(0);
        result.Intervals.Should().BeEmpty();
    }

    [Test]
    public void Reconcile_PreTracking_SubtractsLocalBeforeBaseline()
    {
        var result = PlaytimeReconciler.Reconcile(
            [Snap(200, 5000, baseline: true)],
            [new LocalSessionSpan(0, 3600, 3600)]);

        result.PreTrackingSeconds.Should().Be(200 * 60 - 3600);
    }

    [Test]
    public void Reconcile_ClipsMergedSessionProportionally()
    {
        // 合并会话：跨度 4000s 但 Duration 只有 2000s，窗口命中一半跨度 → 500s
        var result = PlaytimeReconciler.Reconcile(
            [Snap(0, 2000, baseline: true), Snap(100, 3000)],
            [new LocalSessionSpan(0, 4000, 2000)]);

        result.LocalSinceBaselineSeconds.Should().Be(500);
    }

    [Test]
    public void Reconcile_IntervalWindowStartsAtPreviousLastSeen()
    {
        var result = PlaytimeReconciler.Reconcile(
            [
                Snap(100, 1000, lastSeen: 2000, baseline: true),
                Snap(220, 5000)
            ],
            [new LocalSessionSpan(2000, 5000, 3000)]);

        result.Intervals.Should().ContainSingle();
        var interval = result.Intervals[0];
        interval.From.Should().Be(2000);
        interval.To.Should().Be(5000);
        interval.OfficialDeltaSeconds.Should().Be(7200);
        interval.LocalSeconds.Should().Be(3000);
        interval.UnaccountedSeconds.Should().Be(4200);
    }

    [Test]
    public void Reconcile_IntervalWithinToleranceIsSkipped()
    {
        var result = PlaytimeReconciler.Reconcile(
            [
                Snap(100, 1000, baseline: true),
                Snap(130, 3000),
                Snap(130, 5000)
            ],
            [new LocalSessionSpan(1000, 3000, 1800)]);

        // 第一段 delta 1800s、unaccounted 0 → 忽略；第二段 delta 0 → 忽略
        result.Intervals.Should().BeEmpty();
    }
}
