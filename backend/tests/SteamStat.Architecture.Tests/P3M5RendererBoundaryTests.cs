using System.Text.RegularExpressions;
using FluentAssertions;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class P3M5RendererBoundaryTests
{
    [Test]
    public void AchievementsRoute_IsRegisteredAsExperimental()
    {
        var source = File.ReadAllText(RepoFile("src", "router", "modules", "steam.ts"));

        var pathIndex = source.IndexOf("path: '/achievements'", StringComparison.Ordinal);
        pathIndex.Should().BeGreaterThanOrEqualTo(0);
        var blockEnd = source.IndexOf("\n    }", pathIndex, StringComparison.Ordinal);
        blockEnd.Should().BeGreaterThan(pathIndex);
        var routeBlock = source[pathIndex..blockEnd];

        routeBlock.Should().Contain("name: 'steamAchievements'")
            .And.Contain("component: () => import('@/views/steam/achievements.vue')")
            .And.Contain("t('menu.steamAchievements')")
            .And.Contain("icon: 'i-mdi:trophy-variant'")
            .And.Contain("experimental: true");
    }

    [Test]
    public void AchievementsPage_GoesThroughComposablesNotRawWindowOrChannels()
    {
        var source = File.ReadAllText(RepoFile("src", "views", "steam", "achievements.vue"));

        source.Should().NotContain("window.electron")
            .And.NotContain("steamAchievements:")
            .And.NotContain("ipcRenderer")
            .And.NotMatchRegex("(interface|type)\\s+SteamAchievement\\b")
            .And.Contain("useIpc()")
            .And.Contain("useAsyncResource")
            .And.Contain("useSteamStore()");
    }

    [Test]
    public void AchievementsPage_VirtualizesTheOverviewAndOnlyLoadsDetailsOnOpen()
    {
        var source = File.ReadAllText(RepoFile("src", "views", "steam", "achievements.vue"));

        source.Should().Contain("useVirtualList");

        var script = ScriptSection(source);
        Regex.Matches(script, "steamAchievementsGameGet").Should().HaveCount(1);
        Regex.Matches(script, "steamAchievementsGameRefresh").Should().HaveCount(1);
        Regex.Matches(script, "steamAchievementsOverviewGet").Should().HaveCount(1);

        var detailLoaderStart = script.IndexOf("loadGameDetail", StringComparison.Ordinal);
        detailLoaderStart.Should().BeGreaterThanOrEqualTo(0);
        var detailLoaderEnd = script.IndexOf("const selectedAppId", StringComparison.Ordinal);
        detailLoaderEnd.Should().BeGreaterThan(detailLoaderStart);
        var detailLoader = script[detailLoaderStart..detailLoaderEnd];
        detailLoader.Should().Contain("steamAchievementsGameGet")
            .And.Contain("steamAchievementsGameRefresh");

        var watchStart = script.IndexOf("watch(", StringComparison.Ordinal);
        watchStart.Should().BeGreaterThanOrEqualTo(0);
        var watchEnd = script.IndexOf("function onAccountChange", watchStart, StringComparison.Ordinal);
        watchEnd.Should().BeGreaterThan(watchStart);
        var watchBlock = script[watchStart..watchEnd];
        watchBlock.Should().Contain("overview.execute")
            .And.Contain("immediate: true")
            .And.NotContain("steamAchievementsGameGet")
            .And.NotContain("steamAchievementsGameRefresh")
            .And.NotContain("detail.execute");

        var openGameStart = script.IndexOf("function openGame", StringComparison.Ordinal);
        openGameStart.Should().BeGreaterThanOrEqualTo(0);
        var openGameEnd = script.IndexOf("function refreshDetail", openGameStart, StringComparison.Ordinal);
        openGameEnd.Should().BeGreaterThan(openGameStart);
        script[openGameStart..openGameEnd].Should().Contain("detail.reset()")
            .And.Contain("selectedAppId.value !== appId");

        var refreshStart = script.IndexOf("function refreshDetail", StringComparison.Ordinal);
        refreshStart.Should().BeGreaterThanOrEqualTo(0);
        var refreshEnd = script.IndexOf("function retryDetail", refreshStart, StringComparison.Ordinal);
        refreshEnd.Should().BeGreaterThan(refreshStart);
        script[refreshStart..refreshEnd].Should().Contain("detailRefreshing");
    }

    [Test]
    public void UseIpc_StaysATypedFacadeWithoutChannelsOrAny()
    {
        var source = File.ReadAllText(RepoFile("src", "composables", "useIpc.ts"));

        source.Should().NotContain("as any")
            .And.NotContain("new Proxy")
            .And.NotContain("ipcRenderer")
            .And.NotContain("invoke(")
            .And.NotContain("steamAchievements:")
            .And.NotContain("channel");
    }

    [Test]
    public void SteamStore_KeepsNoCredentialMaterial()
    {
        var source = File.ReadAllText(RepoFile("src", "store", "modules", "steam.ts"));

        source.Should().Contain("useIpc()")
            .And.NotContain("password")
            .And.NotContain("guardCode")
            .And.NotContain("qrCode")
            .And.NotContain("token")
            .And.NotContain("Token");
    }

    private static string ScriptSection(string vueSource)
    {
        var start = vueSource.IndexOf("<script", StringComparison.Ordinal);
        var end = vueSource.IndexOf("</script>", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        return vueSource[start..end];
    }

    private static string RepoFile(params string[] segments) => Path.Combine([RepoRoot(), .. segments]);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
