using ElectronNet.Services;
using FluentAssertions;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class TrayServiceTests
{
    [TestCase("/dashboard", true)]
    [TestCase("/friend/76561198000000001?account=a", true)]
    [TestCase("//evil.example.com", false)]
    [TestCase("https://example.com", false)]
    [TestCase("javascript:alert(1)", false)]
    [TestCase(@"\\server\share", false)]
    [TestCase(@"/a\b", false)]
    [TestCase("dashboard", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void AppPaths_AllowOnlyInAppRoutes(string? path, bool expected)
        => TrayService.IsAppPath(path).Should().Be(expected);

    [Test]
    public void Normalize_CapsListsAndDropsUnsafePages()
    {
        var state = CreateState() with
        {
            ToolTip = " ",
            StatusLines = ["a", "", "b", "c", "d", "e"],
            Accounts = Enumerable.Range(0, 30)
                .Select(i => new TrayAccountItem { SteamId = i.ToString(), Label = $"u{i}", Current = i == 0 })
                .ToArray(),
            Pages = [new TrayPageItem { Path = "/dashboard", Label = "D" }, new TrayPageItem { Path = "https://x", Label = "X" }]
        };

        var normalized = TrayService.Normalize(state);

        normalized.ToolTip.Should().Be("Steam Stat");
        normalized.StatusLines.Should().Equal("a", "b", "c", "d");
        normalized.Accounts.Should().HaveCount(TrayService.MaxAccounts);
        normalized.Pages.Select(page => page.Path).Should().Equal("/dashboard");
    }

    [Test]
    public void MenuSignature_ChangesOnlyWhenMenuContentChanges()
    {
        var state = CreateState();
        var same = state with { ToolTip = "different tooltip" };
        var changed = state with { StatusLines = ["Today 2h"] };

        TrayService.MenuSignature(same).Should().Be(TrayService.MenuSignature(state));
        TrayService.MenuSignature(changed).Should().NotBe(TrayService.MenuSignature(state));
    }

    private static TrayStateRequest CreateState() => new()
    {
        ToolTip = "Steam Stat",
        StatusLines = ["Today 1h"],
        Accounts = [new TrayAccountItem { SteamId = "1", Label = "me", Current = true }],
        Pages = [new TrayPageItem { Path = "/dashboard", Label = "Dashboard" }],
        Labels = new TrayLabels
        {
            ShowWindow = "Show",
            SwitchAccount = "Switch",
            OpenPage = "Open",
            Exit = "Exit"
        }
    };
}
