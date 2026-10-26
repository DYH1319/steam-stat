using FluentAssertions;
using SteamKit2;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class UnifiedServiceRegistrationTests
{
    [Test]
    public void CommunityUnifiedService_HasCommunityServiceName()
    {
        new CommunityUnifiedService().ServiceName.Should().Be("Community");
    }

    [Test]
    public void CommunityUnifiedService_RegistersWithSteamUnifiedMessages()
    {
        var client = new SteamClient();
        var unifiedMessages = client.GetHandler<SteamUnifiedMessages>()!;

        var service = unifiedMessages.CreateService<CommunityUnifiedService>();

        service.Should().NotBeNull();
        unifiedMessages.CreateService<CommunityUnifiedService>().Should().BeSameAs(service);
    }
}
