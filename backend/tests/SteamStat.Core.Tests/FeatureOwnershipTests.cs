using System.Reflection;
using FluentAssertions;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Login;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class FeatureOwnershipTests
{
    [Test]
    public void ExperimentalFeatureTypes_LiveInCoreAssembly()
    {
        var core = typeof(ISteamLoginTokenStore).Assembly;
        new[]
        {
            typeof(SteamLoginService), typeof(SteamLibraryService), typeof(SteamOwnedGame),
            typeof(SteamFriendsService), typeof(SteamFriendData), typeof(SteamFriendInfo),
            typeof(SteamRichPresenceResolver)
        }.Should().OnlyContain(type => type.Assembly == core);
    }

    [Test]
    public void LoginTokenBoundary_UsesImmutableRecords()
    {
        typeof(SteamLoginTokenData).GetProperties().Should().OnlyContain(property =>
            property.SetMethod == null || IsInitOnly(property.SetMethod));
        typeof(SteamLoginTokenWrite).GetProperties().Should().OnlyContain(property =>
            property.SetMethod == null || IsInitOnly(property.SetMethod));
    }

    private static bool IsInitOnly(MethodInfo setter) => setter.ReturnParameter
        .GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));
}
