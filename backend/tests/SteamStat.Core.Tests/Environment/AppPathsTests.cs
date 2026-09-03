using FluentAssertions;
using SteamStat.Core.Environment;

namespace SteamStat.Core.Tests.Environment;

[TestFixture]
public sealed class AppPathsTests
{
    [Test]
    public void LogPaths_AreBoundedToUserData()
    {
        var userData = Path.Combine(Path.GetTempPath(), "steam-stat-paths");
        var paths = new AppPaths(userData);

        paths.LogsDirectory.Should().Be(Path.Combine(Path.GetFullPath(userData), "Logs"));
        paths.LogFilePattern.Should().Be(Path.Combine(paths.LogsDirectory, "steam-stat-.log"));
        Path.GetRelativePath(paths.UserDataDirectory, paths.LogFilePattern).Should().NotStartWith("..");
    }
}
