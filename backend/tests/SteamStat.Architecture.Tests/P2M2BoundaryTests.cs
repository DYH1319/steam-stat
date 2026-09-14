using System.Collections;
using System.Reflection;
using FluentAssertions;
using SteamKit2;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Sessions;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class P2M2BoundaryTests
{
    [Test]
    public void LoginService_IsThinAndSessionInfrastructureOwnsSteamKitLifecycle()
    {
        var core = typeof(SteamLoginService).Assembly;
        var manager = core.GetType("SteamStat.Core.Steam.Session.SteamSessionManager");
        var connection = core.GetType("SteamStat.Core.Steam.Session.Internal.SteamConnection");

        manager.Should().NotBeNull();
        connection.Should().NotBeNull();
        typeof(SteamLoginService).Should().NotImplement<ISteamSessionAccessor>();
        typeof(SteamLoginService).GetNestedTypes(BindingFlags.NonPublic)
            .Select(type => type.Name).Should().NotContain(["SteamSession", "ReconnectState"]);

        var forbidden = new[] { typeof(SteamClient), typeof(CallbackManager), typeof(ITimer) };
        typeof(SteamLoginService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .Should().NotContain(forbidden);
        typeof(SteamLoginService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .Should().NotContain(type => typeof(IDictionary).IsAssignableFrom(type)
                || type.Name.Contains("Reconnect", StringComparison.Ordinal));
    }

    [Test]
    public void FeatureLayer_DoesNotOwnRawSteamClientOrCallbackManager()
    {
        var featureTypes = typeof(SteamLoginService).Assembly.GetTypes()
            .Where(type => type.Namespace?.Contains(".Features.", StringComparison.Ordinal) == true)
            .Where(type => type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() == null);

        featureTypes.SelectMany(type => type.GetFields(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Select(field => field.FieldType)
            .Should().NotContain([typeof(SteamClient), typeof(CallbackManager)]);
    }

    [Test]
    public void SessionEvents_DoNotExposeCredentialMaterial()
    {
        var eventTypes = typeof(SteamLoginService).Assembly.GetTypes()
            .Where(type => type.Namespace == "SteamStat.Core.Events" && type.Name.StartsWith("SteamSession", StringComparison.Ordinal));

        eventTypes.SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .Should().NotContain(name => name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }
}
