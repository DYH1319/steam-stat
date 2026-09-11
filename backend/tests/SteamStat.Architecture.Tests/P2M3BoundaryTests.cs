using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Http;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class P2M3BoundaryTests
{
    [Test]
    public void SteamDependencies_HaveSeparateNamedResiliencePipelines()
    {
        using var provider = new ServiceCollection().AddSteamStatCore().BuildServiceProvider();
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        var clientNames = new[]
        {
            SteamStatHttpClients.SteamStore,
            SteamStatHttpClients.SteamWebApi,
            SteamStatHttpClients.SteamCdn,
            SteamStatHttpClients.Download
        };

        var resilienceHandlers = clientNames.Select(name => FindResilienceHandlers(
            handlerFactory.CreateHandler(name)).Should().ContainSingle().Subject).ToArray();

        resilienceHandlers.Should().OnlyHaveUniqueItems();
        typeof(SteamStatHttpClients).GetField("SteamApi").Should().BeNull();
    }

    [Test]
    public async Task CmSchedulerAndConnectivityMonitor_AreCoreOwnedServices()
    {
        await using var provider = new ServiceCollection().AddSteamStatCore().BuildServiceProvider();

        provider.GetRequiredService<ISteamCmOperationScheduler>()
            .Should().BeSameAs(provider.GetRequiredService<SteamCmOperationScheduler>());
        provider.GetRequiredService<ISteamConnectivityMonitor>()
            .Should().BeSameAs(provider.GetRequiredService<SteamConnectivityMonitor>());
        typeof(ISteamSession).GetProperty(nameof(ISteamSession.Generation)).Should().NotBeNull();
    }

    [Test]
    public void LibraryHttpAndBatchEntrypoints_ExposeCallerCancellation()
    {
        var methods = new[]
        {
            nameof(SteamLibraryService.GetLibraryForUserAsync),
            nameof(SteamLibraryService.GetLibraryForAllUsersAsync),
            nameof(SteamLibraryService.SyncLibraryForUserAsync),
            nameof(SteamLibraryService.SyncLibraryForAllUsersAsync)
        };

        methods.Select(name => typeof(SteamLibraryService).GetMethod(name)!.GetParameters().Last().ParameterType)
            .Should().OnlyContain(type => type == typeof(CancellationToken));
    }

    private static IReadOnlyList<DelegatingHandler> FindResilienceHandlers(HttpMessageHandler handler)
    {
        var handlers = new List<DelegatingHandler>();
        while (handler is DelegatingHandler delegating)
        {
            if (delegating.GetType().Name.Contains("ResilienceHandler", StringComparison.Ordinal))
                handlers.Add(delegating);
            handler = delegating.InnerHandler!;
        }
        return handlers;
    }
}
