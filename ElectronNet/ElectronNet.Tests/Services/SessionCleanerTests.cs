using ElectronNet.Features.UsageTracking;
using FluentAssertions;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class SessionCleanerTests
{
    private static UseAppRecordDto Record(
        int appId, string steamId, int startTime, int endTime, int duration,
        int idle = 0, int? foreground = 0) => new()
    {
        AppId = appId,
        SteamId = steamId,
        StartTime = startTime,
        EndTime = endTime,
        Duration = duration,
        IdleDuration = idle,
        ForegroundDuration = foreground
    };

    private static readonly SessionCleaningRules NoRules = new(0, 0, new HashSet<int>());

    [Test]
    public void Apply_DropsExcludedApps()
    {
        var records = new[]
        {
            Record(570, "s1", 100, 200, 100),
            Record(730, "s1", 100, 200, 100)
        };
        var rules = new SessionCleaningRules(0, 0, new HashSet<int> { 570 });

        var result = SessionCleaner.Apply(records, rules);

        result.Should().ContainSingle().Which.AppId.Should().Be(730);
    }

    [Test]
    public void Apply_MergesWithinGap_SummingDurationsAndNullingForegroundIfAnyNull()
    {
        var records = new[]
        {
            Record(570, "s1", 0, 100, 100, idle: 10, foreground: 60),
            Record(570, "s1", 130, 200, 70, idle: 5, foreground: null),
            Record(570, "s1", 400, 500, 100, idle: 0, foreground: 80)
        };
        var rules = new SessionCleaningRules(0, 60, new HashSet<int>());

        var result = SessionCleaner.Apply(records, rules);

        result.Should().HaveCount(2);
        result[0].StartTime.Should().Be(0);
        result[0].EndTime.Should().Be(200);
        result[0].Duration.Should().Be(170);
        result[0].IdleDuration.Should().Be(15);
        result[0].ForegroundDuration.Should().BeNull();
        result[1].StartTime.Should().Be(400);
    }

    [Test]
    public void Apply_DoesNotMergeAcrossDifferentAppOrUser()
    {
        var records = new[]
        {
            Record(570, "s1", 0, 100, 100),
            Record(730, "s1", 110, 200, 90),
            Record(570, "s2", 120, 210, 90)
        };
        var rules = new SessionCleaningRules(0, 3600, new HashSet<int>());

        var result = SessionCleaner.Apply(records, rules);

        result.Should().HaveCount(3);
    }

    [Test]
    public void Apply_MinDurationIsAppliedAfterMerge()
    {
        var records = new[]
        {
            Record(570, "s1", 0, 30, 30),
            Record(570, "s1", 40, 70, 30),
            Record(730, "s1", 0, 10, 10)
        };
        var rules = new SessionCleaningRules(50, 60, new HashSet<int>());

        var result = SessionCleaner.Apply(records, rules);

        // 570 两条合并后 60s 保留；730 的 10s 被过滤
        result.Should().ContainSingle();
        result[0].Duration.Should().Be(60);
    }

    [Test]
    public void Apply_WithNoRules_ReturnsSortedByStartTime()
    {
        var records = new[]
        {
            Record(570, "s1", 200, 300, 100),
            Record(730, "s1", 0, 50, 50)
        };

        var result = SessionCleaner.Apply(records, NoRules);

        result.Select(r => r.StartTime).Should().Equal(0, 200);
    }
}
