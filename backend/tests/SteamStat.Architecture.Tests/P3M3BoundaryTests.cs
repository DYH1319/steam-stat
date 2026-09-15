using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class P3M3BoundaryTests
{
    [Test]
    public void CmLibrarySource_UsesSharedProgressSourceInsteadOfProtobuf()
    {
        var source = File.ReadAllText(
            RepoFile(
                "backend", "src", "SteamStat.Core", "Steam", "Gateway", "Internal",
                "CmLibrarySource.cs"));

        source.Should().Contain("IAchievementProgressSource");
        source.Should().NotContain("CPlayer_GetAchievementsProgress_Request");
        source.Should().NotContain(".GetAchievementsProgress(");
    }

    [Test]
    public void GetAchievementsProgressCall_ExistsOnlyInSharedProgressSource()
    {
        var coreRoot = RepoFile("backend", "src", "SteamStat.Core");
        var offenders = Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !string.Equals(
                    Path.GetFileName(path),
                    "CmAchievementProgressSource.cs",
                    StringComparison.Ordinal))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains(".GetAchievementsProgress(")
                    || text.Contains("CPlayer_GetAchievementsProgress_Request");
            })
            .Select(path => Path.GetRelativePath(coreRoot, path))
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Test]
    public void UnlockTransport_UsesClientGetUserStatsHandlerOnly()
    {
        var source = File.ReadAllText(
            RepoFile(
                "backend", "src", "SteamStat.Core", "Steam", "Gateway", "Internal",
                "CmAchievementProgressSource.cs"));
        source.Should().Contain("AchievementUserStatsProtocolHandler");
        source.Should().Contain(".GetUserStats(");

        var connection = File.ReadAllText(
            RepoFile(
                "backend", "src", "SteamStat.Core", "Steam", "Session", "Internal",
                "SteamConnection.cs"));
        connection.Should().Contain("new AchievementUserStatsProtocolHandler()");

        var coreRoot = RepoFile("backend", "src", "SteamStat.Core");
        var offenders = Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("GetUserAchievements")
                    || text.Contains("AchievementUserProgress")
                    || text.Contains("UserAchievementsServiceMethod");
            })
            .Select(path => Path.GetRelativePath(coreRoot, path))
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Test]
    public void ProgressSourceAndGateway_AreRegisteredAsSingletons()
    {
        var services = new ServiceCollection().AddSteamStatCore();

        var source = services.Single(
            service => service.ServiceType.Name == "IAchievementProgressSource");
        source.Lifetime.Should().Be(ServiceLifetime.Singleton);
        source.ImplementationType?.Name.Should().Be("CmAchievementProgressSource");

        var progressGateway = services.Single(
            service => service.ServiceType == typeof(ISteamAchievementProgressGateway));
        progressGateway.Lifetime.Should().Be(ServiceLifetime.Singleton);
        progressGateway.ImplementationType?.Name.Should().Be("SteamAchievementProgressGateway");

        var overview = services.Single(
            service => service.ServiceType == typeof(SteamAchievementOverviewQuery));
        overview.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Test]
    public void OverviewQuery_DependsOnlyOnCatalogAndProgressGateway()
    {
        typeof(SteamAchievementOverviewQuery)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single()
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().Equal(typeof(IOwnedGameCatalog), typeof(ISteamAchievementProgressGateway));
    }

    [Test]
    public void PersonalProgressKeys_NeverUsePublicScope()
    {
        SteamAchievementCacheKeys.ProgressSummary(76561198000000001UL).ScopeId
            .Should().NotBe(SteamAchievementCacheKeys.PublicScope);
        SteamAchievementCacheKeys.Unlocks(76561198000000001UL, 730).ScopeId
            .Should().NotBe(SteamAchievementCacheKeys.PublicScope);
        SteamAchievementCacheKeys.ProgressSummary(76561198000000001UL).ScopeId
            .Should().Be("76561198000000001");
    }

    [Test]
    public void ProgressProtocolTypes_StayInsideGatewayInternal()
    {
        var offenders = typeof(SteamAchievementGameResult).Assembly.GetTypes()
            .Where(type => type.Name.Contains("AchievementUserProgress")
                && (type.Namespace != "SteamStat.Core.Steam.Gateway.Internal" || type.IsPublic))
            .Select(type => type.FullName)
            .ToArray();

        offenders.Should().BeEmpty();
    }

    private static string RepoFile(params string[] segments) => Path.Combine([RepoRoot(), .. segments]);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
