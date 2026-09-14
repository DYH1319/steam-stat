using System.Reflection;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SteamKit2;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class P2M4BoundaryTests
{
    [Test]
    public void FeatureSources_DoNotContainSteamHttpUrlsOrGeneratedProtocolOperations()
    {
        var files = Directory.GetFiles(
                RepoFile("backend", "src", "SteamStat.Core", "Features"),
                "*.cs",
                SearchOption.AllDirectories)
            .Append(RepoFile("ElectronNet", "ElectronNet", "Services", "SteamUserService.cs"));

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            source.Should().NotContain("store.steampowered.com", $"{file} must use a source adapter")
                .And.NotContain("api.steampowered.com", $"{file} must use a source adapter")
                .And.NotContain("steam-chat.com", $"{file} must not depend on miniprofile")
                .And.NotContain("SteamKit2.Internal", $"{file} must not use generated protobuf types")
                .And.NotContain("GetHandler<", $"{file} must not operate SteamKit handlers")
                .And.NotContain("ClientMsgProtobuf<", $"{file} must not build generated protocol messages");
        }
    }

    [Test]
    public void LibraryFriendsAndProfile_DependOnNarrowGatewayCapabilities()
    {
        ConstructorParameterTypes<SteamLibraryService>().Should().Contain([
            typeof(ISteamLibraryGateway),
            typeof(ISteamWishlistGateway)
        ]).And.NotContain([typeof(IHttpClientFactory), typeof(ISteamCmOperationScheduler)]);
        ConstructorParameterTypes<SteamFriendsService>().Should().Contain(typeof(ISteamPresenceFeed));
        ConstructorParameterTypes<SteamUserService>().Should().Contain([
            typeof(ISteamProfileGateway),
            typeof(ISteamAvatarUriProvider)
        ]).And.NotContain(typeof(IHttpClientFactory));
        typeof(IRichPresenceResolver).GetMethod(nameof(IRichPresenceResolver.ResolveAsync))!
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().NotContain(typeof(SteamClient));
    }

    [Test]
    public void CoreComposition_RegistersM4GatewaysAndRetiresCommunityHttpClient()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSteamStatCore();

        services.Select(descriptor => descriptor.ServiceType).Should().Contain([
            typeof(ISteamLibraryGateway),
            typeof(ISteamWishlistGateway),
            typeof(ISteamProfileGateway),
            typeof(ISteamAvatarUriProvider),
            typeof(ISteamPresenceLocalizationGateway),
            typeof(ISteamPresenceFeed)
        ]);
        typeof(SteamStat.Core.Http.SteamStatHttpClients).GetField("SteamCommunity").Should().BeNull();
    }

    private static Type[] ConstructorParameterTypes<T>()
        => typeof(T).GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single()
            .GetParameters().Select(parameter => parameter.ParameterType).ToArray();

    private static string RepoFile(params string[] segments) => Path.Combine([RepoRoot(), .. segments]);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
