using ElectronNet.Hosting;
using FluentAssertions;
using SteamStat.Core.Http;

namespace ElectronNet.Tests.Hosting;

[TestFixture]
public sealed class RendererProxySyncTests
{
    [Test]
    public void CustomProxy_UsesFixedServersAndBypassesLoopback()
    {
        var config = RendererProxySync.ToProxyConfig(
            new SteamNetworkProfile(SteamProxyMode.Custom, new Uri("socks5://127.0.0.1:1080"), false));

        config.Mode.Should().Be("fixed_servers");
        config.ProxyRules.Should().Be("socks5://127.0.0.1:1080");
        config.ProxyBypassRules.Should().Contain("localhost").And.Contain("<local>");
    }

    [TestCase(SteamProxyMode.None, "direct")]
    [TestCase(SteamProxyMode.System, "system")]
    public void NonCustomModes_MapToChromiumModes(SteamProxyMode mode, string expected)
    {
        var config = RendererProxySync.ToProxyConfig(new SteamNetworkProfile(mode, null, false));

        config.Mode.Should().Be(expected);
        config.ProxyRules.Should().BeNull();
    }
}
