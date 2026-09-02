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
}
