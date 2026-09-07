using FluentAssertions;
using SteamStat.Core.Helpers;

namespace SteamStat.Core.Tests.Helpers;

[TestFixture]
public class SteamIdHelperTests
{
    // SteamID64 = 76561197960265728 + AccountID
    private const long STEAM_ID_BASE = 76561197960265728L;

    [Test]
    public void AccountIdToSteamId_AddsUniverseOffset()
    {
        SteamIdHelper.AccountIdToSteamId(1).Should().Be("76561197960265729");
        // 76561197960265728 + 39734273 == 76561198000000001
        SteamIdHelper.AccountIdToSteamId(39734273).Should().Be("76561198000000001");
    }

    [Test]
    public void SteamIdToAccountId_SubtractsUniverseOffset()
    {
        SteamIdHelper.SteamIdToAccountId("76561197960265729").Should().Be(1);
        SteamIdHelper.SteamIdToAccountId("76561198000000001").Should().Be(39734273);
    }

    [TestCase(1)]
    [TestCase(1234567)]
    [TestCase(int.MaxValue)]
    public void Conversion_RoundTrips(int accountId)
    {
        var steamId = SteamIdHelper.AccountIdToSteamId(accountId);

        steamId.Should().NotBeNull();
        SteamIdHelper.SteamIdToAccountId(steamId).Should().Be(accountId);
    }

    [Test]
    public void AccountIdToSteamId_WhenNullOrZero_ReturnsNull()
    {
        SteamIdHelper.AccountIdToSteamId(null).Should().BeNull();
        // 0 表示「未登录 / 无账号」，不是合法 AccountID
        SteamIdHelper.AccountIdToSteamId(0).Should().BeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    public void SteamIdToAccountId_WhenNullOrEmpty_ReturnsNull(string? steamId)
    {
        SteamIdHelper.SteamIdToAccountId(steamId).Should().BeNull();
    }
}
