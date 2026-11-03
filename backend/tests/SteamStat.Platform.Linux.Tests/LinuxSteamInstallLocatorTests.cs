using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Platform;
using SteamStat.Platform.Linux;

namespace SteamStat.Platform.Linux.Tests;

/// <summary>定位器依赖 Linux 的路径分隔符与 /proc 语义，只在 Linux 上运行（CI backend-linux 作业）。</summary>
[Platform(Include = "Linux")]
public sealed class LinuxSteamInstallLocatorTests
{
    private const string SteamBody = """
        					"AutoLoginUser"		"alice"
        					"language"		"schinese"
        					"RememberPassword"		"1"
        					"SourceModInstallPath"		"/home/u/.local/share/Steam/steamapps\\sourcemods"
        					"ActiveProcess"
        					{
        						"pid"		"4242"
        						"ActiveUser"		"12345678"
        						"SteamClientDll"		"/s/ubuntu12_32/steamclient.so"
        						"Universe"		"Public"
        					}
        					"Apps"
        					{
        						"730"
        						{
        							"installed"		"1"
        							"Running"		"1"
        							"name"		"Counter-Strike 2"
        						}
        						"570"
        						{
        							"installed"		"1"
        							"Running"		"0"
        						}
        					}
        """;

    [Test]
    public void ReadSteamRegistry_ReadsCaseInsensitiveValuesAndResolvesSteamRoot()
    {
        using var fixture = new LinuxFixture().WithNativeSteam(LinuxFixture.Registry(SteamBody)).WithExecutable("steam");
        var locator = CreateLocator(fixture);

        var snapshot = locator.ReadSteamRegistry();

        snapshot.SteamPath.Should().Be(fixture.SteamRoot);
        snapshot.AutoLoginUser.Should().Be("alice");
        snapshot.Language.Should().Be("schinese");
        snapshot.RememberPassword.Should().Be(1);
        snapshot.SourceModInstallPath.Should().Be("/home/u/.local/share/Steam/steamapps/sourcemods");
        snapshot.SteamExe.Should().Be(Path.Combine(fixture.Bin, "steam"));
    }

    [Test]
    public void ReadSteamRegistry_WithoutSteam_ReturnsEmptySnapshot()
    {
        using var fixture = new LinuxFixture();
        CreateLocator(fixture).ReadSteamRegistry().SteamPath.Should().BeEmpty();
    }

    [Test]
    public void ReadAppRegistry_UsesReaperProcessesAsTheSourceOfRunningState()
    {
        using var fixture = new LinuxFixture()
            .WithNativeSteam(LinuxFixture.Registry(SteamBody))
            .WithProcess(4242, "steam", "/s/steam")
            .WithProcess(5000, "reaper", "/s/ubuntu12_32/reaper", "SteamLaunch", "AppId=570", "--", "/s/game")
            .WithProcess(5001, "reaper", "/s/ubuntu12_32/reaper", "SteamLaunch", "AppId=1245620", "--", "/s/proton")
            .WithProcess(5002, "bash", "bash", "SteamLaunch", "AppId=999");
        var apps = CreateLocator(fixture).ReadAppRegistry();

        // 730 在 registry.vdf 中残留 Running=1，但没有对应的 reaper 进程
        apps[730].Running.Should().Be(0);
        apps[730].Name.Should().Be("Counter-Strike 2");
        apps[570].Running.Should().Be(1);
        apps[1245620].Running.Should().Be(1);
        apps.Should().NotContainKey(999);
    }

    [Test]
    public void ReadAppRegistry_InsideFlatpakSandbox_TrustsRegistryFlags()
    {
        using var fixture = new LinuxFixture()
            .WithNativeSteam(LinuxFixture.Registry(SteamBody))
            .WithEnvironment("FLATPAK_ID", "asia.dyh1319.steamstat")
            .WithProcess(5000, "reaper", "reaper", "SteamLaunch", "AppId=570", "--");
        var apps = CreateLocator(fixture).ReadAppRegistry();

        apps[730].Running.Should().Be(1);
        apps[570].Running.Should().Be(0);
    }

    [Test]
    public void ReadActiveProcess_DropsStalePidAndUser()
    {
        using var fixture = new LinuxFixture().WithNativeSteam(LinuxFixture.Registry(SteamBody));
        var active = CreateLocator(fixture).ReadActiveProcess();

        active.Pid.Should().Be(0);
        active.ActiveUser.Should().Be(0);
    }

    [Test]
    public void ReadActiveProcess_FallsBackToWebHelperSteamId()
    {
        using var fixture = new LinuxFixture()
            .WithNativeSteam(LinuxFixture.Registry("""
                					"AutoLoginUser"		"alice"
                """, machineBody: """
                					"SteamPID"		"777"
                """))
            .WithProcess(777, "steam", "/s/steam")
            .WithProcess(778, "steamwebhelper", "./steamwebhelper", "-nocrashdialog", "-steamid=76561198000000001");
        var active = CreateLocator(fixture).ReadActiveProcess();

        active.Pid.Should().Be(777);
        active.ActiveUser.Should().Be(39734273);
        active.Universe.Should().Be("Public");
    }

    [Test]
    public void ReadActiveProcess_WhenRegistryPidIsNamespaced_FindsClientUnderSteamRoot()
    {
        using var fixture = new LinuxFixture().WithNativeSteam(LinuxFixture.Registry("", machineBody: """
            					"SteamPID"		"999999"
            """));
        fixture.WithProcess(300, "steam", "/usr/bin/steam").WithProcessLinks(300, "/usr/lib/steam/steam", "/");
        fixture.WithProcess(301, "steam", "steam").WithProcessLinks(301, Path.Combine(fixture.SteamRoot, "steamrt64", "steam"), fixture.SteamRoot);
        fixture.WithProcess(302, "steamwebhelper", "./steamwebhelper", "-steamid=76561198000000001");

        var active = CreateLocator(fixture).ReadActiveProcess();

        active.Pid.Should().Be(301);
        active.ActiveUser.Should().Be(39734273);
    }

    [Test]
    public void ReadActiveProcess_WebHelperWithoutLogin_HasNoActiveUser()
    {
        using var fixture = new LinuxFixture()
            .WithNativeSteam(LinuxFixture.Registry("", machineBody: """
                					"SteamPID"		"777"
                """))
            .WithProcess(777, "steam", "/s/steam")
            .WithProcess(778, "steamwebhelper", "./steamwebhelper", "-steamid=0");

        CreateLocator(fixture).ReadActiveProcess().ActiveUser.Should().Be(0);
    }

    [Test]
    public void SetAutoLoginUser_RewritesRegistryAndKeepsOtherValues()
    {
        using var fixture = new LinuxFixture().WithNativeSteam(LinuxFixture.Registry(SteamBody));
        var locator = CreateLocator(fixture);

        locator.SetAutoLoginUser("bob", rememberPassword: false);

        var snapshot = locator.ReadSteamRegistry();
        snapshot.AutoLoginUser.Should().Be("bob");
        snapshot.RememberPassword.Should().Be(0);
        snapshot.Language.Should().Be("schinese");
        var text = File.ReadAllText(fixture.RegistryFile);
        text.Should().Contain("\"Counter-Strike 2\"");
        System.Text.RegularExpressions.Regex.Matches(text, "AutoLoginUser\"").Count.Should().Be(1);
        Directory.GetFiles(Path.GetDirectoryName(fixture.RegistryFile)!, "*.tmp").Should().BeEmpty();
    }

    [Test]
    public void HasRegistryKey_ReportsAppsAvailableWhenProcIsReadable()
    {
        using var fixture = new LinuxFixture().WithNativeSteam(LinuxFixture.Registry("""
            					"AutoLoginUser"		"alice"
            """));
        var locator = CreateLocator(fixture);

        locator.HasRegistryKey(SteamRegistryKey.Steam).Should().BeTrue();
        locator.HasRegistryKey(SteamRegistryKey.Apps).Should().BeTrue();
        locator.HasRegistryKey(SteamRegistryKey.ActiveProcess).Should().BeFalse();
        locator.DescribeRegistryKey(SteamRegistryKey.Apps).Should().Be("~/.steam/registry.vdf → HKCU/Software/Valve/Steam/Apps");
    }

    [Test]
    public void GetSteamLaunchCommand_PrefersSystemLauncherThenSteamScript()
    {
        using var fixture = new LinuxFixture().WithNativeSteam(LinuxFixture.Registry(""));
        CreateLocator(fixture).GetSteamLaunchCommand().Should().Be(new SteamLaunchCommand(Path.Combine(fixture.SteamRoot, "steam.sh"), null));
    }

    [Test]
    public void GetSteamLaunchCommand_FlatpakSteamUsesFlatpakRun()
    {
        using var fixture = new LinuxFixture().WithExecutable("flatpak");
        var flatpakRoot = Path.Combine(fixture.Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        Directory.CreateDirectory(Path.Combine(flatpakRoot, "steamapps"));
        Directory.CreateDirectory(Path.Combine(fixture.Home, ".var", "app", "com.valvesoftware.Steam", ".steam"));
        File.WriteAllText(Path.Combine(fixture.Home, ".var", "app", "com.valvesoftware.Steam", ".steam", "registry.vdf"), LinuxFixture.Registry(""));
        var locator = CreateLocator(fixture);

        locator.ReadSteamRegistry().SteamPath.Should().Be(flatpakRoot);
        locator.GetSteamLaunchCommand().Should().Be(new SteamLaunchCommand(Path.Combine(fixture.Bin, "flatpak"), "run com.valvesoftware.Steam"));
    }

    [Test]
    public void GetSteamLaunchCommand_FromInsideFlatpakSandbox_UsesFlatpakSpawn()
    {
        using var fixture = new LinuxFixture()
            .WithNativeSteam(LinuxFixture.Registry(""))
            .WithExecutable("steam")
            .WithEnvironment("FLATPAK_ID", "asia.dyh1319.steamstat");

        CreateLocator(fixture).GetSteamLaunchCommand()
            .Should().Be(new SteamLaunchCommand("flatpak-spawn", $"--host {Path.Combine(fixture.Bin, "steam")}"));
    }

    [Test]
    public void Resolve_PrefersMostRecentlyUsedInstallation()
    {
        using var fixture = new LinuxFixture().WithNativeSteam(LinuxFixture.Registry(""));
        File.SetLastWriteTimeUtc(fixture.RegistryFile, DateTime.UtcNow.AddDays(-3));
        var flatpakHome = Path.Combine(fixture.Home, ".var", "app", "com.valvesoftware.Steam");
        Directory.CreateDirectory(Path.Combine(flatpakHome, ".local", "share", "Steam", "steamapps"));
        Directory.CreateDirectory(Path.Combine(flatpakHome, ".steam"));
        File.WriteAllText(Path.Combine(flatpakHome, ".steam", "registry.vdf"), LinuxFixture.Registry(""));

        LinuxSteamInstallationResolver.Resolve(fixture.Context)!.Flavor.Should().Be(LinuxSteamFlavor.Flatpak);
    }

    [TestCase(new[] { "reaper", "SteamLaunch", "AppId=440", "--", "AppId=1" }, 440)]
    [TestCase(new[] { "reaper", "SteamLaunch", "AppId=3172394093", "--" }, null)]
    [TestCase(new[] { "reaper", "AppId=440" }, null)]
    public void ParseReaperAppId(string[] commandLine, int? expected)
        => ProcScanner.ParseReaperAppId(commandLine).Should().Be(expected);

    private static LinuxSteamInstallLocator CreateLocator(LinuxFixture fixture)
        => new(fixture.Context, NullLogger<LinuxSteamInstallLocator>.Instance);
}
