using ElectronNet.Features.UsageTracking;
using FluentAssertions;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class SessionActivityTrackerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);

    private static SessionActivityTracker NewTracker() => new(1, "76561198000000000", 570, T0);

    [Test]
    public void Observe_WhenIdleExceedsThreshold_OpensRetroactiveSpan()
    {
        var tracker = NewTracker();
        var now = T0.AddMinutes(15);

        tracker.Observe(now, TimeSpan.FromMinutes(12), isForeground: null, afkEnabled: true, afkThreshold: Threshold);

        // 阈值内的 10 分钟也计入 AFK
        tracker.IdleSecondsAt(now).Should().Be(12 * 60);
    }

    [Test]
    public void Observe_WhenInputResumes_ClosesSpanAtNowMinusIdle()
    {
        var tracker = NewTracker();
        tracker.Observe(T0.AddMinutes(15), TimeSpan.FromMinutes(12), null, true, Threshold);

        var resumeAt = T0.AddMinutes(20);
        tracker.Observe(resumeAt, TimeSpan.FromSeconds(30), null, true, Threshold);

        // span: T0+3min -> T0+19:30min = 990s
        tracker.IdleSecondsAt(resumeAt).Should().Be(990);
    }

    [Test]
    public void Observe_WhenAfkDisabled_ClosesOpenSpan()
    {
        var tracker = NewTracker();
        tracker.Observe(T0.AddMinutes(15), TimeSpan.FromMinutes(12), null, true, Threshold);
        var now = T0.AddMinutes(18);

        tracker.Observe(now, TimeSpan.FromMinutes(14), null, afkEnabled: false, afkThreshold: Threshold);

        // 关闭于 now：T0+3min -> T0+18min = 900s
        tracker.IdleSecondsAt(now).Should().Be(900);
    }

    [Test]
    public void IdleSecondsAt_IsClampedToElapsed()
    {
        var tracker = NewTracker();
        var now = T0.AddMinutes(5);
        // idle 超过已流逝时长
        tracker.Observe(now, TimeSpan.FromHours(1), null, true, Threshold);

        tracker.IdleSecondsAt(now).Should().Be(300);
    }

    [Test]
    public void ForegroundSeconds_AccumulatesOnlyWhenTrue()
    {
        var tracker = NewTracker();
        tracker.Observe(T0.AddSeconds(10), null, isForeground: true, afkEnabled: true, afkThreshold: Threshold);
        tracker.Observe(T0.AddSeconds(20), null, isForeground: false, afkEnabled: true, afkThreshold: Threshold);
        tracker.Observe(T0.AddSeconds(30), null, isForeground: true, afkEnabled: true, afkThreshold: Threshold);

        tracker.ForegroundSeconds.Should().Be(20);
    }

    [Test]
    public void ForegroundSeconds_IsNullWhenNeverKnown()
    {
        var tracker = NewTracker();
        tracker.Observe(T0.AddSeconds(10), TimeSpan.FromSeconds(5), isForeground: null, afkEnabled: true, afkThreshold: Threshold);

        tracker.ForegroundSeconds.Should().BeNull();
    }
}
