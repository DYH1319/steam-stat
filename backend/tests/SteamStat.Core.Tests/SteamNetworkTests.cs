using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SteamStat.Core.Environment;
using SteamStat.Core.Http;
using SteamStat.Core.Settings;

namespace SteamStat.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class SteamNetworkTests
{
    private IWebProxy _originalDefaultProxy = null!;

    [SetUp]
    public void SetUp() => _originalDefaultProxy = HttpClient.DefaultProxy;

    [TearDown]
    public void TearDown() => HttpClient.DefaultProxy = _originalDefaultProxy;

    [TestCase("http://127.0.0.1:7890", "http://127.0.0.1:7890")]
    [TestCase("127.0.0.1:7890", "http://127.0.0.1:7890")]
    [TestCase("SOCKS5://localhost:1080", "socks5://localhost:1080")]
    [TestCase("socks4://10.0.0.2:1080/", "socks4://10.0.0.2:1080")]
    [TestCase("http://[::1]:8080", "http://[::1]:8080")]
    public void TryParseProxyUri_AcceptsSchemeHostPort(string input, string expected)
    {
        SteamNetworkProfile.TryParseProxyUri(input, out var uri, out var error).Should().BeTrue();
        error.Should().BeNull();
        uri!.GetLeftPart(UriPartial.Authority).Should().Be(expected);
    }

    [TestCase("", "empty")]
    [TestCase("ftp://127.0.0.1:21", "scheme")]
    [TestCase("https://127.0.0.1:8443", "scheme")]
    [TestCase("http://user:pass@127.0.0.1:7890", "credentials")]
    [TestCase("http://127.0.0.1:7890/path", "path")]
    [TestCase("http://127.0.0.1", "port")]
    [TestCase("socks5://127.0.0.1", "port")]
    public void TryParseProxyUri_RejectsUnsupportedValues(string input, string expectedError)
    {
        SteamNetworkProfile.TryParseProxyUri(input, out var uri, out var error).Should().BeFalse();
        uri.Should().BeNull();
        error.Should().Be(expectedError);
    }

    [Test]
    public void FromSettings_FallsBackToSystemWhenCustomProxyIsInvalid()
    {
        var profile = SteamNetworkProfile.FromSettings(
            new NetworkSettings { ProxyMode = "custom", ProxyUrl = "nope://", CnOptimized = true },
            out var error);

        profile.ProxyMode.Should().Be(SteamProxyMode.System);
        profile.CnOptimized.Should().BeTrue();
        error.Should().NotBeNull();
    }

    [Test]
    public void Proxy_RoutesByModeAndAlwaysBypassesLoopback()
    {
        var proxy = new SteamNetworkProxy();
        var steam = new Uri("https://api.steampowered.com/");
        var loopback = new Uri("http://localhost:9000/");

        proxy.Update(new SteamNetworkProfile(SteamProxyMode.Custom, new Uri("socks5://127.0.0.1:1080"), false));
        proxy.IsBypassed(steam).Should().BeFalse();
        proxy.GetProxy(steam).Should().Be(new Uri("socks5://127.0.0.1:1080"));
        proxy.IsBypassed(loopback).Should().BeTrue();
        proxy.IsBypassed(new Uri("http://127.0.0.1:8000/")).Should().BeTrue();
        proxy.GetProxy(loopback).Should().BeNull();

        proxy.Update(new SteamNetworkProfile(SteamProxyMode.None, null, false));
        proxy.IsBypassed(steam).Should().BeTrue();
        proxy.GetProxy(steam).Should().BeNull();
    }

    [Test]
    public void TransportSelector_UsesWebSocketForCustomProxyOrChinaMode()
    {
        var proxy = new SteamNetworkProxy();

        proxy.Update(new SteamNetworkProfile(SteamProxyMode.None, null, false));
        SteamCmTransportSelector.PreferWebSocket(proxy).Should().BeFalse();

        proxy.Update(new SteamNetworkProfile(SteamProxyMode.None, null, true));
        SteamCmTransportSelector.PreferWebSocket(proxy).Should().BeTrue();

        proxy.Update(new SteamNetworkProfile(SteamProxyMode.Custom, new Uri("http://127.0.0.1:7890"), false));
        SteamCmTransportSelector.PreferWebSocket(proxy).Should().BeTrue();
    }

    [Test]
    public void Controller_InstallsDefaultProxyAndRaisesChangedOnlyOnDifference()
    {
        var proxy = new SteamNetworkProxy();
        var controller = new SteamNetworkController(proxy, new SteamAccessOptions(), NullLogger<SteamNetworkController>.Instance);
        var changes = new List<SteamNetworkProfile>();
        controller.Changed += changes.Add;

        controller.Apply(new NetworkSettings { ProxyMode = "custom", ProxyUrl = "127.0.0.1:7890", CnOptimized = false });
        controller.Apply(new NetworkSettings { ProxyMode = "custom", ProxyUrl = "http://127.0.0.1:7890", CnOptimized = false });

        HttpClient.DefaultProxy.Should().BeSameAs(proxy);
        changes.Should().ContainSingle();
        controller.Current.ProxyUri.Should().Be(new Uri("http://127.0.0.1:7890"));
    }

    [Test]
    public void Tuning_OnlyRelaxesTimeoutsInChinaMode()
    {
        var defaults = new SteamAccessOptions();
        SteamNetworkTuning.Apply(defaults, cnOptimized: false);
        defaults.CnOptimizedTimeouts.Should().BeFalse();
        defaults.JsonTotalTimeout.Should().Be(TimeSpan.FromSeconds(15));

        var relaxed = new SteamAccessOptions();
        SteamNetworkTuning.Apply(relaxed, cnOptimized: true);
        relaxed.CnOptimizedTimeouts.Should().BeTrue();
        relaxed.JsonTotalTimeout.Should().BeGreaterThan(defaults.JsonTotalTimeout);
        relaxed.JsonAttemptTimeout.Should().BeGreaterThan(defaults.JsonAttemptTimeout);
        relaxed.CmOperationTimeout.Should().BeGreaterThan(defaults.CmOperationTimeout);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Tuning_KeepsResiliencePipelinesValid(bool cnOptimized)
    {
        var tuned = new SteamAccessOptions();
        SteamNetworkTuning.Apply(tuned, cnOptimized);
        tuned.CircuitSamplingDuration.Should().BeGreaterThanOrEqualTo(tuned.DownloadAttemptTimeout * 2);
        tuned.CircuitSamplingDuration.Should().BeGreaterThanOrEqualTo(tuned.JsonAttemptTimeout * 2);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSteamStatCore(access => SteamNetworkTuning.Apply(access, cnOptimized));
        using var provider = services.BuildServiceProvider();

        // 与 Host.StartAsync 相同的 ValidateOnStart 校验（中国网络优化模式曾因熔断采样窗口过短导致启动失败）
        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();
        validate.Should().NotThrow();
    }

    [Test]
    public void ParseCmList_PicksFirstWebSocketAndTcpEndpoints()
    {
        const string body = """
            {"response":{"serverlist":[
              {"endpoint":"cm1.steamserver.net:27017","type":"netfilter"},
              {"endpoint":"ext1-sea1.steamserver.net:443","type":"websockets"},
              {"endpoint":"ext2-sea1.steamserver.net:443","type":"websockets"}
            ],"success":true}}
            """;

        SteamNetworkDiagnostics.ParseCmList(body)
            .Should().Be(("ext1-sea1.steamserver.net:443", "cm1.steamserver.net:27017"));
        SteamNetworkDiagnostics.ParseCmList("not json").Should().Be(((string?)null, (string?)null));
    }

    [Test]
    public void Classify_MapsHttpRequestErrors()
    {
        SteamNetworkDiagnostics.Classify(new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"))
            .Code.Should().Be("dns");
        SteamNetworkDiagnostics.Classify(new HttpRequestException(HttpRequestError.ProxyTunnelError, "407"))
            .Code.Should().Be("proxy");
        SteamNetworkDiagnostics.Classify(new TaskCanceledException()).Code.Should().Be("timeout");
    }

    [Test]
    public void ConnectivityMonitor_KeepsRecentFailuresNewestFirst()
    {
        var monitor = new SteamConnectivityMonitor(new SteamAccessOptions(), TimeProvider.System);
        monitor.ReportFailure(SteamDependency.Store, new SteamStat.Core.Steam.Gateway.SteamFailure(
            SteamStat.Core.Steam.Gateway.SteamFailureKind.Timeout, "timeout"));
        monitor.ReportFailure(SteamDependency.Cdn, new SteamStat.Core.Steam.Gateway.SteamFailure(
            SteamStat.Core.Steam.Gateway.SteamFailureKind.Offline, "offline"));
        for (var index = 0; index < 60; index++)
            monitor.ReportFailure(SteamDependency.SteamWebApi, new SteamStat.Core.Steam.Gateway.SteamFailure(
                SteamStat.Core.Steam.Gateway.SteamFailureKind.Transient, "http_503"));

        monitor.RecentFailures.Should().HaveCount(50);
        monitor.RecentFailures[0].Dependency.Should().Be(SteamDependency.SteamWebApi);
    }
}

[TestFixture]
public sealed class OnboardingSettingsTests
{
    private string _directory = null!;
    private AppPaths _paths = null!;
    private JsonSettingsStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "steam-stat-core-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_directory);
        _store = new JsonSettingsStore(
            _paths,
            new AppSettingsFactory(new AppEnvironment(false, "zh-CN", false, _paths)),
            NullLogger<JsonSettingsStore>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _store.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void FreshInstall_HasNotCompletedOnboardingAndUsesSystemProxy()
    {
        var settings = _store.GetSettings();

        settings.OnboardingCompleted.Should().BeFalse();
        settings.Network!.ProxyMode.Should().Be("system");
        settings.Network.CnOptimized.Should().BeFalse();
    }

    [Test]
    public void ExistingSettingsFileWithoutFlag_IsTreatedAsCompleted()
    {
        Directory.CreateDirectory(_paths.SettingsDirectory);
        File.WriteAllText(_paths.SettingsFile, """{"autoStart":true}""");

        _store.GetSettings().OnboardingCompleted.Should().BeTrue();
    }

    [Test]
    public async Task Update_PersistsNetworkAndOnboarding()
    {
        await _store.UpdateAsync(new AppSettings
        {
            Network = new NetworkSettings { ProxyMode = "custom", ProxyUrl = "socks5://127.0.0.1:1080" },
            OnboardingCompleted = true
        });
        await _store.UpdateAsync(new AppSettings { Network = new NetworkSettings { CnOptimized = true } });

        var settings = _store.GetSettings();
        settings.OnboardingCompleted.Should().BeTrue();
        settings.Network!.ProxyMode.Should().Be("custom");
        settings.Network.ProxyUrl.Should().Be("socks5://127.0.0.1:1080");
        settings.Network.CnOptimized.Should().BeTrue();
    }
}
