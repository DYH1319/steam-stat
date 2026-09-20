using System.Text.RegularExpressions;
using FluentAssertions;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class P3M6RendererBoundaryTests
{
    private static readonly string[] SteamCollectionPages = ["login.vue", "library.vue", "friends.vue"];

    private static readonly string[] RetiredCollectionApis =
    [
        "steamFriendsGetAll",
        "steamFriendsGetForUser",
        "steamFriendsGetCached",
        "steamFriendsRequestFriendInfo",
        "steamFriendsTrackGetAll",
        "steamLibraryGetForUser",
        "steamLibraryGetForAllUsers",
        "steamLibrarySyncForUser",
        "steamLibrarySyncForAllUsers"
    ];

    [Test]
    public void SteamPages_GoThroughComposablesStoreAndNoRawTransport()
    {
        foreach (var page in SteamCollectionPages)
        {
            var source = File.ReadAllText(RepoFile("src", "views", "steam", page));

            source.Should().Contain("useIpc()", $"{page} must use the typed ipc facade")
                .And.Contain("useAsyncResource", $"{page} must use the shared async resource")
                .And.Contain("useSteamStore()", $"{page} must share the steam store")
                .And.Contain("ensureBootstrapped", $"{page} must share the account bootstrap")
                .And.NotContain("window.electron", $"{page} must not touch the raw bridge")
                .And.NotContain("ipcRenderer", $"{page} must not touch the raw bridge")
                .And.NotContain("steamLoginLoggedInUsersGet", $"{page} must use the shared account list")
                .And.NotContain("steamOperationalStatusGet", $"{page} must use the shared operational status");

            foreach (var api in RetiredCollectionApis)
                source.Should().NotContain(api, $"{page} must not call the retired collection endpoint {api}");
        }
    }

    [Test]
    public void LibraryPage_UsesExactlyOneSnapshotAndOneRefreshCall()
    {
        var script = ScriptSection(File.ReadAllText(RepoFile("src", "views", "steam", "library.vue")));

        Regex.Matches(script, "steamLibrarySnapshotGet").Should().HaveCount(1);
        Regex.Matches(script, "steamLibraryRefresh").Should().HaveCount(1);

        var handleSyncStart = script.IndexOf("async function handleSync", StringComparison.Ordinal);
        var handleSyncEnd = script.IndexOf("function retryBootstrap", StringComparison.Ordinal);
        handleSyncStart.Should().BeGreaterThanOrEqualTo(0);
        handleSyncEnd.Should().BeGreaterThan(handleSyncStart);
        var handleSync = script[handleSyncStart..handleSyncEnd];
        handleSync.Should().Contain("libraryResource.execute(true)")
            .And.NotContain("steamLibrarySnapshotGet", "a manual refresh must not chain a second snapshot call")
            .And.NotContain("execute(false)", "a manual refresh must not chain a second snapshot call");
    }

    [Test]
    public void FriendsPage_MergesEventsByTimestampAndUsesTypedListenerLifecycle()
    {
        var script = ScriptSection(File.ReadAllText(RepoFile("src", "views", "steam", "friends.vue")));

        script.Should().Contain("useIpcListener")
            .And.Contain("steamFriendsUpdateOnListener")
            .And.Contain("steamFriendsUpdateRemoveListener")
            .And.Contain("lastUpdateTime");
        Regex.Matches(script, "steamFriendsSnapshotGet").Should().HaveCount(1);
        Regex.Matches(script, "steamFriendsRefresh").Should().HaveCount(1);
    }

    [Test]
    public void LoginPage_UsesTypedListenerAndStoreAccountMutations()
    {
        var script = ScriptSection(File.ReadAllText(RepoFile("src", "views", "steam", "login.vue")));

        script.Should().Contain("useIpcListener")
            .And.Contain("steamLoginEventOnListener")
            .And.Contain("steamLoginEventRemoveListener")
            .And.Contain("steamStore.addAccount")
            .And.Contain("steamStore.removeAccount");
    }

    [Test]
    public void RetiredCollectionDescriptors_AreGoneAndTypedResultsAreRegistered()
    {
        var contracts = File.ReadAllText(RepoFile("backend", "src", "SteamStat.Contracts", "IpcContracts.cs"));
        var dtos = File.ReadAllText(RepoFile("backend", "src", "SteamStat.Contracts", "IpcDtos.cs"));
        var registrar = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "IpcMainService.cs"));

        foreach (var channel in new[]
                 {
                     "steamFriends:getAll",
                     "steamFriends:getForUser",
                     "steamFriends:getCached",
                     "steamFriends:requestFriendInfo",
                     "steamFriends:track:getAll",
                     "steamLibrary:getForUser",
                     "steamLibrary:getForAllUsers",
                     "steamLibrary:syncForUser",
                     "steamLibrary:syncForAllUsers"
                 })
            contracts.Should().NotContain(channel);

        contracts.Should().Contain("steamLibrary:snapshot:get")
            .And.Contain("steamLibrary:refresh")
            .And.Contain("steamFriends:snapshot:get")
            .And.Contain("steamFriends:refresh")
            .And.Contain("steamLibrarySnapshotGet")
            .And.Contain("steamLibraryRefresh")
            .And.Contain("steamFriendsSnapshotGet")
            .And.Contain("steamFriendsRefresh");

        dtos.Should().Contain("SteamLibraryResultDto")
            .And.Contain("SteamFriendsResultDto")
            .And.Contain("DiagnosticCode")
            .And.NotContain("SteamFriendInfoRequest");

        registrar.Should().Contain("SteamLibraryIpc.GetSnapshot")
            .And.Contain("SteamLibraryIpc.Refresh")
            .And.Contain("SteamFriendsIpc.GetSnapshot")
            .And.Contain("SteamFriendsIpc.Refresh")
            .And.Contain("libraryService.GetLibrarySnapshotAsync()")
            .And.Contain("libraryService.RefreshLibraryAsync()")
            .And.Contain("friendsService.GetFriendsSnapshotAsync()")
            .And.Contain("friendsService.RefreshFriendsAsync()");
    }

    [Test]
    public void GeneratedApi_ExposesTypedCollectionResultsAndNoRetiredMethods()
    {
        var api = File.ReadAllText(RepoFile("src", "types", "ipc.d.ts"));

        api.Should().Contain("steamLibrarySnapshotGet: () => Promise<SteamLibraryResult>")
            .And.Contain("steamLibraryRefresh: () => Promise<SteamLibraryResult>")
            .And.Contain("steamFriendsSnapshotGet: () => Promise<SteamFriendsResult>")
            .And.Contain("steamFriendsRefresh: () => Promise<SteamFriendsResult>")
            .And.Contain("interface SteamLibraryResult")
            .And.Contain("interface SteamFriendsResult");

        foreach (var apiMethod in RetiredCollectionApis)
            api.Should().NotContain(apiMethod);
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
