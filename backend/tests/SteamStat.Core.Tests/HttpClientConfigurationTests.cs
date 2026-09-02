using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SteamStat.Core.Http;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class HttpClientConfigurationTests
{
    [Test]
    public void NamedClients_PreserveExistingTimeouts()
    {
        using var provider = new ServiceCollection().AddSteamStatCore().BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        factory.CreateClient(SteamStatHttpClients.Download).Timeout.Should().Be(TimeSpan.FromSeconds(30));
        factory.CreateClient(SteamStatHttpClients.SteamApi).Timeout.Should().Be(TimeSpan.FromSeconds(15));
    }
}
