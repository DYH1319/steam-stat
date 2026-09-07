using FluentAssertions;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class HostEventBoundaryTests
{
    [Test]
    public void ElectronIpcSend_ExistsOnlyInHostForwarder()
    {
        var hostRoot = RepoFile("ElectronNet", "ElectronNet");
        var sendFiles = Directory.GetFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("Electron.IpcMain.Send"))
            .Select(Path.GetFullPath);

        sendFiles.Should().Equal(Path.GetFullPath(RepoFile(
            "ElectronNet", "ElectronNet", "Hosting", "ElectronIpcEventForwarder.cs")));
    }

    [Test]
    public void LoginLifecycle_DoesNotDependOnFriendsImplementation()
    {
        var loginSource = File.ReadAllText(RepoFile(
            "backend", "src", "SteamStat.Core", "Features", "Login", "SteamLoginService.cs"));

        loginSource.Should().NotContain("SteamFriendsService.");
        loginSource.Should().Contain("new SteamSessionEnded(accountName)")
            .And.Contain("new SteamSessionReady(accountName)");
    }

    private static string RepoFile(params string[] segments) => Path.Combine([RepoRoot(), .. segments]);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
