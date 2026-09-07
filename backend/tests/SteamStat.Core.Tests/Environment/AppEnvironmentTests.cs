using FluentAssertions;
using SteamStat.Core.Environment;

namespace SteamStat.Core.Tests.Environment;

[TestFixture]
public sealed class AppEnvironmentTests
{
    [Test]
    public void AppPaths_DerivesStablePathsFromUserDataDirectory()
    {
        var userDataDirectory = Path.Combine(Path.GetTempPath(), "steam-stat-tests", "profile");

        var paths = new AppPaths(userDataDirectory);

        paths.UserDataDirectory.Should().Be(Path.GetFullPath(userDataDirectory));
        paths.DatabaseDirectory.Should().Be(Path.Combine(paths.UserDataDirectory, "Database"));
        paths.DatabaseFile.Should().Be(Path.Combine(paths.DatabaseDirectory, "steam-stat.db"));
        paths.DatabaseBackupFile.Should().Be(Path.Combine(paths.DatabaseDirectory, "steam-stat.bak"));
        paths.SettingsDirectory.Should().Be(Path.Combine(paths.UserDataDirectory, "Settings"));
        paths.SettingsFile.Should().Be(Path.Combine(paths.SettingsDirectory, "app-settings.json"));
        paths.TempDirectory.Should().Be(Path.Combine(paths.UserDataDirectory, "Temp"));
    }

    [Test]
    public void EnvironmentAndPaths_AreImmutable()
    {
        var paths = new AppPaths(Path.GetTempPath());
        var environment = new AppEnvironment(true, "zh-CN", true, paths);

        environment.IsDevelopment.Should().BeTrue();
        environment.Locale.Should().Be("zh-CN");
        environment.IsSilentStart.Should().BeTrue();
        environment.Paths.Should().BeSameAs(paths);
        typeof(AppEnvironment).GetProperties().Should().OnlyContain(property => property.SetMethod == null);
        typeof(AppPaths).GetProperties().Should().OnlyContain(property => property.SetMethod == null);
    }
}
