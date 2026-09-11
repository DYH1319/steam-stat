using FluentAssertions;
using SteamKit2;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class LoginCharacterizationTests
{
    private static readonly EResult[] TerminalResults =
    [
        EResult.InvalidPassword,
        EResult.AccessDenied,
        EResult.Expired,
        EResult.Revoked,
        EResult.InvalidSignature,
        EResult.AccountDisabled,
        EResult.AccountLockedDown,
        EResult.AccountLogonDenied,
        EResult.AccountLoginDeniedNeedTwoFactor,
        EResult.Banned,
        EResult.AccountNotFound
    ];

    [Test]
    public void ReconnectLogonResult_TerminalSetIsFixed()
    {
        var policy = CreatePolicy();
        TerminalResults.Should().OnlyContain(result => policy.IsTerminal(result));
    }

    [TestCase(EResult.OK)]
    [TestCase(EResult.Busy)]
    [TestCase(EResult.ServiceUnavailable)]
    [TestCase(EResult.TryAnotherCM)]
    [TestCase(EResult.RateLimitExceeded)]
    [TestCase(EResult.Timeout)]
    [TestCase(EResult.Fail)]
    public void ReconnectLogonResult_NonTerminalSetIsFixed(EResult result)
    {
        CreatePolicy().IsTerminal(result).Should().BeFalse();
    }

    private static SteamReconnectPolicy CreatePolicy() => new(new SteamResultClassifier());
}
