using ElectronNet.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class IpcRequestBinderTests
{
    private readonly IpcRequestBinder _binder = new(NullLogger<IpcRequestBinder>.Instance);

    [Test]
    public void Bind_ConvertsBoxedCamelCaseValuesToTypedRequest()
    {
        var request = _binder.Bind<SteamLoginCredentialsRequest>(new Dictionary<string, object>
        {
            ["username"] = "alice",
            ["password"] = "secret",
            ["rememberMe"] = true
        }, SteamLoginIpc.StartCredentials);

        request.Should().BeEquivalentTo(new SteamLoginCredentialsRequest
        {
            Username = "alice",
            Password = "secret",
            RememberMe = true
        });
    }

    [Test]
    public void Bind_RejectsMissingUnknownAndOutOfRangeValues()
    {
        var missing = () => _binder.Bind<SteamLoginCredentialsRequest>(new Dictionary<string, object>
        {
            ["username"] = "alice",
            ["rememberMe"] = true
        }, SteamLoginIpc.StartCredentials);
        var unknown = () => _binder.Bind<AccountNameRequest>(new Dictionary<string, object>
        {
            ["accountName"] = "alice",
            ["unexpected"] = true
        }, SteamLoginIpc.LogoutUser);
        var outOfRange = () => _binder.Bind<SteamPersonaStateRequest>(new Dictionary<string, object>
        {
            ["accountName"] = "alice",
            ["personaState"] = 99
        }, SteamLoginIpc.SetPersonaState);
        var missingValue = () => _binder.Bind<SteamLoginQrRequest>(
            new Dictionary<string, object>(), SteamLoginIpc.StartQr);

        missing.Should().Throw<IpcRequestBindingException>();
        unknown.Should().Throw<IpcRequestBindingException>();
        outOfRange.Should().Throw<IpcRequestBindingException>();
        missingValue.Should().Throw<IpcRequestBindingException>();
    }

    [Test]
    public void Bind_CreatesEmptyOptionalRequestButRejectsMissingRequiredRequest()
    {
        _binder.Bind<SteamAppsQueryRequest>(null, SteamIpc.GetAppsInfo).Should().Be(new SteamAppsQueryRequest());
        var required = () => _binder.Bind<AccountNameRequest>(null, SteamLoginIpc.LogoutUser);
        required.Should().Throw<IpcRequestBindingException>();
    }

    [Test]
    public void Bind_AcceptsAchievementRequestsAtBoundaries()
    {
        var game = _binder.Bind<SteamAchievementGameRequest>(new Dictionary<string, object>
        {
            ["accountName"] = "alice",
            ["appId"] = uint.MaxValue
        }, AchievementIpc.GetGame);
        var refresh = _binder.Bind<SteamAchievementGameRequest>(new Dictionary<string, object>
        {
            ["accountName"] = "alice",
            ["appId"] = 1
        }, AchievementIpc.RefreshGame);
        var overview = _binder.Bind<SteamAchievementOverviewRequest>(new Dictionary<string, object>
        {
            ["accountName"] = "alice"
        }, AchievementIpc.GetOverview);

        game.AccountName.Should().Be("alice");
        game.AppId.Should().Be(uint.MaxValue);
        refresh.AppId.Should().Be(1u);
        overview.AccountName.Should().Be("alice");
    }

    [Test]
    public void Bind_RejectsInvalidAchievementRequests()
    {
        var missingAccount = () => _binder.Bind<SteamAchievementGameRequest>(new Dictionary<string, object>
        {
            ["appId"] = 10
        }, AchievementIpc.GetGame);
        var nullAccount = () => _binder.Bind<SteamAchievementGameRequest>(new Dictionary<string, object>
        {
            ["accountName"] = null!,
            ["appId"] = 10
        }, AchievementIpc.GetGame);
        var oversizedAccount = () => _binder.Bind<SteamAchievementOverviewRequest>(new Dictionary<string, object>
        {
            ["accountName"] = new string('a', 65)
        }, AchievementIpc.GetOverview);
        var zeroApp = () => _binder.Bind<SteamAchievementGameRequest>(new Dictionary<string, object>
        {
            ["accountName"] = "alice",
            ["appId"] = 0
        }, AchievementIpc.GetGame);
        var negativeApp = () => _binder.Bind<SteamAchievementGameRequest>(new Dictionary<string, object>
        {
            ["accountName"] = "alice",
            ["appId"] = -1
        }, AchievementIpc.GetGame);
        var overflowingApp = () => _binder.Bind<SteamAchievementGameRequest>(new Dictionary<string, object>
        {
            ["accountName"] = "alice",
            ["appId"] = 4294967296L
        }, AchievementIpc.RefreshGame);
        var unknown = () => _binder.Bind<SteamAchievementGameRequest>(new Dictionary<string, object>
        {
            ["accountName"] = "alice",
            ["appId"] = 10,
            ["unexpected"] = true
        }, AchievementIpc.GetGame);

        missingAccount.Should().Throw<IpcRequestBindingException>();
        nullAccount.Should().Throw<IpcRequestBindingException>();
        oversizedAccount.Should().Throw<IpcRequestBindingException>();
        zeroApp.Should().Throw<IpcRequestBindingException>();
        negativeApp.Should().Throw<IpcRequestBindingException>();
        overflowingApp.Should().Throw<IpcRequestBindingException>();
        unknown.Should().Throw<IpcRequestBindingException>();
    }
}
