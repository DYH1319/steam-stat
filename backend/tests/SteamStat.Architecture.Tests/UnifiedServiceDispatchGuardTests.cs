using FluentAssertions;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class UnifiedServiceDispatchGuardTests
{
    [Test]
    public void CmAchievementSchemaSource_DispatchesThroughRegisteredPlayerService()
    {
        var source = File.ReadAllText(RepoFile(
            "backend", "src", "SteamStat.Core", "Steam", "Gateway", "Internal",
            "CmAchievementSchemaSource.cs"));

        source.Should().NotContain("SendMessage<");
        source.Should().Contain("CreateService<Player>");
    }

    [Test]
    public void CmPresenceLocalizationSource_RegistersCommunityService()
    {
        var source = File.ReadAllText(RepoFile(
            "backend", "src", "SteamStat.Core", "Steam", "Gateway", "Internal",
            "CmPresenceLocalizationSource.cs"));

        source.Should().Contain("CreateService<CommunityUnifiedService>");
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
