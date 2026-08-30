using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;

namespace ElectronNet.Tests.Hosting;

[TestFixture]
public sealed class CompositionRootTests
{
    [Test]
    public void Registrations_ExposeEnvironmentPathsAndHostServicesAsSingletons()
    {
        var appEnvironment = new AppEnvironment(false, "en-US", true, new AppPaths(Path.GetTempPath()));
        var services = new ServiceCollection()
            .AddSteamStatCore()
            .AddSteamStatWindows()
            .AddSteamStatElectron(appEnvironment);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        provider.GetRequiredService<AppEnvironment>().Should().BeSameAs(appEnvironment);
        provider.GetRequiredService<IAppPaths>().Should().BeSameAs(appEnvironment.Paths);
        provider.GetRequiredService<IEventBus>().Should().BeSameAs(provider.GetRequiredService<IEventBus>());
        provider.GetRequiredService<IMainWindowAccessor>().Should().BeSameAs(provider.GetRequiredService<MainWindowAccessor>());
        provider.GetServices<IEventHandler<LoginUsersChanged>>().Should().ContainSingle();
        provider.GetServices<IEventHandler<SteamSessionDisconnected>>().Should().ContainSingle();
        provider.GetRequiredService<IpcMainService>().Should().BeSameAs(provider.GetRequiredService<IpcMainService>());
        provider.GetRequiredService<ApplicationStartupCoordinator>().Should().NotBeNull();
        provider.GetRequiredService<ApplicationCleanupService>().Should().NotBeNull();
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType.FullName == "Microsoft.Extensions.Hosting.IHostedService");
    }

    [Test]
    public async Task CleanupService_WhenStoppedMoreThanOnce_CleansUpOnce()
    {
        var cleanupCount = 0;
        var service = new ApplicationCleanupService(() =>
        {
            cleanupCount++;
            return Task.CompletedTask;
        });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.CleanupAsync();

        cleanupCount.Should().Be(1);
    }
}
