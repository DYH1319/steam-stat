using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Environment;
using SteamStat.Core.Settings;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class WeeklySettingsTests
{
    private static JsonSettingsStore Store()
    {
        var paths = new AppPaths(Path.GetTempPath());
        return new JsonSettingsStore(paths, new AppSettingsFactory(new AppEnvironment(false, "zh-CN", false, paths)), NullLogger<JsonSettingsStore>.Instance);
    }

    [Test]
    public void Merge_KeepsAbandonAfterPause()
    {
        var merged = Store().Merge(new AppSettings { Analytics = new AnalyticsSettings { PauseDays = 200, AbandonDays = 100 } });

        merged.Analytics!.PauseDays.Should().Be(200);
        merged.Analytics.AbandonDays.Should().Be(201);
    }

    [Test]
    public void Merge_FallsBackToTheDefaultDeliveryForAnUnknownValue()
        => Store().Merge(new AppSettings { Weekly = new WeeklySettings { Delivery = "shout" } }).Weekly!.Delivery.Should().Be(WeeklyDelivery.Open);

    [Test]
    public void Merge_KeepsTheBaselineWhenOtherFieldsChange()
    {
        var old = new AppSettings { Weekly = new WeeklySettings { BaselineAt = 123 } };

        Store().Merge(new AppSettings { Weekly = new WeeklySettings { Enabled = false } }, old).Weekly!.BaselineAt.Should().Be(123);
    }
}
