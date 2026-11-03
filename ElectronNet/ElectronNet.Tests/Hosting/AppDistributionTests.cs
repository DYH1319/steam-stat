using ElectronNet.Hosting;
using FluentAssertions;

namespace ElectronNet.Tests.Hosting;

[TestFixture]
public sealed class AppDistributionTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "steam-stat-distribution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void Detect_DevelopmentBuild_NotifiesOnly()
        => AppDistribution.Detect(_directory, isDevelopment: true, _ => null).Should().Be(AppDistribution.Development);

    [Test]
    public void Detect_Descriptor_WinsOverEnvironment()
    {
        File.WriteAllText(Path.Combine(_directory, AppDistribution.DescriptorFileName), """{ "kind": "Scoop" }""");

        var distribution = AppDistribution.Detect(_directory, false, name => name == "APPIMAGE" ? "/tmp/a.AppImage" : null);

        distribution.Kind.Should().Be("scoop");
        distribution.UpdateMode.Should().Be(AppUpdateMode.Managed);
        distribution.PortableData.Should().BeTrue();
        distribution.ManagedUpdateCommand.Should().Be("scoop update steam-stat");
        distribution.PortableDataDirectory(_directory).Should().Be(Path.Combine(_directory, "data"));
    }

    [Test]
    public void Detect_PortableDescriptor_KeepsDataNextToTheExecutable()
    {
        File.WriteAllText(Path.Combine(_directory, AppDistribution.DescriptorFileName), """{ "kind": "portable" }""");

        var distribution = AppDistribution.Detect(_directory, false, _ => null);

        distribution.UpdateMode.Should().Be(AppUpdateMode.Notify);
        distribution.PortableData.Should().BeTrue();
    }

    [Test]
    public void Detect_CustomDescriptorWithUpdateCommand_IsManaged()
    {
        File.WriteAllText(Path.Combine(_directory, AppDistribution.DescriptorFileName),
            """{ "kind": "nixpkgs", "portableData": false, "updateCommand": "nix profile upgrade steam-stat" }""");

        var distribution = AppDistribution.Detect(_directory, false, _ => null);

        distribution.Should().Be(new AppDistribution("nixpkgs", AppUpdateMode.Managed, false, "nix profile upgrade steam-stat"));
    }

    [TestCase("FLATPAK_ID", "flatpak", AppUpdateMode.Managed)]
    [TestCase("APPIMAGE", "appimage", AppUpdateMode.Auto)]
    public void Detect_SandboxEnvironment(string variable, string kind, AppUpdateMode mode)
    {
        var distribution = AppDistribution.Detect(_directory, false, name => name == variable ? "x" : null);

        distribution.Kind.Should().Be(kind);
        distribution.UpdateMode.Should().Be(mode);
        distribution.PortableData.Should().BeFalse();
    }

    [Test]
    public void Detect_InvalidDescriptor_FallsBackToEnvironment()
    {
        File.WriteAllText(Path.Combine(_directory, AppDistribution.DescriptorFileName), "{ not json");

        AppDistribution.Detect(_directory, false, name => name == "APPIMAGE" ? "x" : null).Kind.Should().Be("appimage");
    }

    [Test]
    public void Detect_WithoutMarkers_NeverMovesDataIntoTheProgramDirectory()
    {
        var distribution = AppDistribution.Detect(_directory, false, _ => null);

        distribution.PortableData.Should().BeFalse();
        distribution.UpdateMode.Should().Be(AppUpdateMode.Notify);
        distribution.Kind.Should().Be(OperatingSystem.IsWindows() ? "unknown" : "archive");
    }

    [Test]
    public void Detect_WindowsInstallDirectory_UsesElectronUpdater()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Installer detection only applies on Windows");
        File.WriteAllText(Path.Combine(_directory, AppDistribution.WindowsUninstallerName), "");

        AppDistribution.Detect(_directory, false, _ => null).UpdateMode.Should().Be(AppUpdateMode.Auto);
    }
}

[TestFixture]
public sealed class LinuxAutoStartTests
{
    [TestCase("/usr/bin/steam-stat", "/usr/bin/steam-stat")]
    [TestCase("/opt/Steam Stat/Steam Stat", "\"/opt/Steam Stat/Steam Stat\"")]
    [TestCase("/home/u/$HOME/a\"b", "\"/home/u/\\\\$HOME/a\\\\\"b\"")]
    [TestCase("100%", "\"100%%\"")]
    public void QuoteExecArgument_FollowsDesktopEntrySpec(string argument, string expected)
        => LinuxAutoStart.QuoteExecArgument(argument).Should().Be(expected.Replace("\\\\", "\\"));

    [Test]
    public void LaunchCommand_PrefersAppImageAndFlatpak()
    {
        var archive = AppDistribution.FromKind("archive", null, null);
        LinuxAutoStart.LaunchCommand(archive, name => name == "APPIMAGE" ? "/home/u/Steam-Stat.AppImage" : null, "/tmp/.mount/Steam Stat")
            .Should().Equal("/home/u/Steam-Stat.AppImage");
        LinuxAutoStart.LaunchCommand(AppDistribution.FromKind("flatpak", null, null), name => name == "FLATPAK_ID" ? "asia.dyh1319.steamstat" : null, "/app/x")
            .Should().Equal("flatpak", "run", "asia.dyh1319.steamstat");
        LinuxAutoStart.LaunchCommand(archive, _ => null, "/opt/steam-stat/Steam Stat").Should().Equal("/opt/steam-stat/Steam Stat");
    }

    [Test]
    public void Apply_WritesAndRemovesTheAutostartEntry()
    {
        var file = LinuxAutoStart.AutostartFile(
            name => name == "XDG_CONFIG_HOME" ? Path.Combine(Path.GetTempPath(), "steam-stat-xdg-" + Guid.NewGuid().ToString("N")) : null,
            "/home/unused");

        LinuxAutoStart.Apply(file, ["/opt/Steam Stat/Steam Stat"], autoStart: true, silentStart: true, iconPath: "/opt/icon.png");
        var content = File.ReadAllText(file);
        content.Should().Contain("Exec=\"/opt/Steam Stat/Steam Stat\" --silent-start").And.Contain("Icon=/opt/icon.png").And.StartWith("[Desktop Entry]");

        LinuxAutoStart.Apply(file, ["/opt/x"], autoStart: false, silentStart: false, iconPath: null);
        File.Exists(file).Should().BeFalse();
        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(file))!, recursive: true);
    }
}
