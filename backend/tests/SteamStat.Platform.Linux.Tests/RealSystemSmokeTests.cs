using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Platform.Linux;

namespace SteamStat.Platform.Linux.Tests;

/// <summary>只在开发者本机手动运行（dotnet test --filter Category=RealSystem），读取真实的 Steam / 密钥环 / X11。</summary>
[Explicit("Reads the real Steam installation, keyring and X display of the developer machine")]
[Category("RealSystem")]
public sealed class RealSystemSmokeTests
{
    [Test]
    public void PrintRealSteamState()
    {
        var context = LinuxSystemContext.Current();
        var locator = new LinuxSteamInstallLocator(context, NullLogger<LinuxSteamInstallLocator>.Instance);
        var registry = locator.ReadSteamRegistry();
        var active = locator.ReadActiveProcess();
        TestContext.Out.WriteLine($"SteamPath={registry.SteamPath} SteamExe={registry.SteamExe} AutoLogin={registry.AutoLoginUser} RunningAppID={registry.RunningAppID}");
        TestContext.Out.WriteLine($"Pid={active.Pid} ActiveUser={active.ActiveUser}");
        TestContext.Out.WriteLine($"Running apps: {string.Join(',', locator.ReadAppRegistry().Where(a => a.Value.Running == 1).Select(a => a.Key))}");
        TestContext.Out.WriteLine($"Launch: {locator.GetSteamLaunchCommand()}");
    }

    [Test]
    public void RoundTripRealKeyring()
    {
        var store = new LibSecretSecretStore(new LibSecretKeyring(), NullLogger<LibSecretSecretStore>.Instance);
        var protectedValue = store.Protect("smoke");
        TestContext.Out.WriteLine($"Protected: {protectedValue}");
        if (protectedValue != null) store.Unprotect(protectedValue).Should().Be("smoke");
    }

    [Test]
    public void SampleRealDesktop()
    {
        using var probe = new LinuxDesktopActivityProbe(LinuxSystemContext.Current(), NullLogger<LinuxDesktopActivityProbe>.Instance);
        var sample = probe.Sample();
        TestContext.Out.WriteLine($"Idle={sample.IdleTime} Foreground={sample.ForegroundProcessPath} Cwd={sample.ForegroundWorkingDirectory}");
    }
}
