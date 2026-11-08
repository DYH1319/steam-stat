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

    [Test]
    public void Accounts_DefaultToUnconfirmed()
    {
        var merged = Store().Merge(new AppSettings());

        merged.Accounts!.Mine.Should().BeEmpty();
        merged.Accounts.Confirmed.Should().BeFalse();
        AccountSettings.MineOf(merged).Should().BeEmpty();
    }

    [Test]
    public void Accounts_KeepOnlySteamIdsAndSortThem()
    {
        var merged = Store().Merge(new AppSettings
        {
            Accounts = new AccountSettings { Mine = ["76561198000000002", " 76561198000000001 ", "abc", "", "76561198000000002"], Confirmed = true }
        });

        merged.Accounts!.Mine.Should().Equal("76561198000000001", "76561198000000002");
        AccountSettings.MineOf(merged).Should().Equal("76561198000000001", "76561198000000002");
    }

    [Test]
    public void Accounts_AreIgnoredUntilConfirmed()
        => AccountSettings.MineOf(Store().Merge(new AppSettings { Accounts = new AccountSettings { Mine = ["76561198000000001"] } }))
            .Should().BeEmpty("没确认的「我」只是建议，Host 不能拿它缩小范围");

    [Test]
    public void Accounts_KeepTheOtherFieldWhenOnlyOneChanges()
    {
        var old = new AppSettings { Accounts = new AccountSettings { Mine = ["76561198000000001"], Confirmed = true } };

        var merged = Store().Merge(new AppSettings { Accounts = new AccountSettings { Mine = ["76561198000000002"] } }, old);

        merged.Accounts!.Confirmed.Should().BeTrue();
        merged.Accounts.Mine.Should().Equal("76561198000000002");
    }
}
