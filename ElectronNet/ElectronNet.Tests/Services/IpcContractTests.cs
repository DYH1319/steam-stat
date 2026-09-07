using ElectronNet.Hosting;
using FluentAssertions;
using GenerateIpcContracts;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class IpcContractTests
{
    [Test]
    public void Catalog_ContainsEveryExistingEndpointWithUniqueNamesAndDirections()
    {
        IpcCatalog.All.Should().HaveCount(58);
        IpcCatalog.All.Count(endpoint => endpoint.Direction == IpcDirection.Invoke).Should().Be(41);
        IpcCatalog.All.Count(endpoint => endpoint.Direction == IpcDirection.Send).Should().Be(13);
        IpcCatalog.All.Count(endpoint => endpoint.Direction == IpcDirection.HostToRendererEvent).Should().Be(4);
        IpcCatalog.All.Select(endpoint => endpoint.ApiMethod).Should().OnlyHaveUniqueItems();
        IpcCatalog.All.Select(endpoint => (endpoint.Channel, endpoint.Direction)).Should().OnlyHaveUniqueItems();
        IpcCatalog.All.Where(endpoint => endpoint.Direction == IpcDirection.HostToRendererEvent)
            .Should().OnlyContain(endpoint => endpoint.RemoveApiMethod != null);
    }

    [Test]
    public void GeneratedFiles_MatchTheContractCatalogByteForByte()
    {
        foreach (var output in IpcContractGenerator.Generate(RepoRoot()))
        {
            File.Exists(output.Key).Should().BeTrue();
            File.ReadAllText(output.Key).Should().Be(output.Value, output.Key);
            output.Value.Should().NotContain("\r\n");
        }
    }

    [Test]
    public void ContractSnapshot_CapturesEveryApiNameDirectionAndWireShape()
    {
        var snapshot = File.ReadAllText(RepoFile(
            "ElectronNet", "ElectronNet.Tests", "Snapshots", "ipc-contracts.snapshot.json"));

        foreach (var endpoint in IpcCatalog.All)
        {
            snapshot.Should().Contain($"\"apiMethod\": \"{endpoint.ApiMethod}\"");
            snapshot.Should().Contain($"\"channel\": \"{endpoint.Channel}\"");
        }
        snapshot.Should().Contain("\"types\":");
        snapshot.Should().NotContain(": \"any\"");
    }

    [Test]
    public void HostRegistrars_UseDescriptorsInsteadOfChannelStringLiterals()
    {
        var registrar = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "IpcMainService.cs"));
        var forwarder = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Hosting", "ElectronIpcEventForwarder.cs"));

        registrar.Should().NotContain("ipcMain.Handle(\"")
            .And.NotContain("ipcMain.On(\"")
            .And.NotContain("Program.ElectronMainWindow");
        forwarder.Should().NotContain("SendAsync(\"").And.NotContain("Electron.IpcMain.Send(snapshot.Window, \"");
        registrar.Should().Contain("endpoint.Channel");
        forwarder.Should().Contain("endpoint.Channel");
    }

    [Test]
    public void Preload_ExposesOnlyTheGeneratedContextBridgeApi()
    {
        var preload = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Resources", "preload.mjs"));

        preload.Should().Contain("contextBridge.exposeInMainWorld(\"electron\"");
        preload.Should().NotContain("ipcRenderer,");
        preload.Should().NotContain("exposeInMainWorld(\"ipcRenderer\"");
        preload.Should().NotContain("require(\"fs\")").And.NotContain("require(\"child_process\")");
    }

    [Test]
    public void MainWindow_EnforcesRendererIsolationAndSandbox()
    {
        var source = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Program.cs"));

        source.Should().Contain("NodeIntegration = false")
            .And.Contain("NodeIntegrationInWorker = false")
            .And.Contain("NodeIntegrationInSubFrames = false")
            .And.Contain("ContextIsolation = true")
            .And.Contain("WebSecurity = true")
            .And.Contain("AllowRunningInsecureContent = false")
            .And.Contain("Sandbox = true");
    }

    [Test]
    public void HostToRendererEvents_ArePublishedAsTypedEventsAndForwardedCentrally()
    {
        var userSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "SteamUserService.cs"));
        var loginSource = File.ReadAllText(RepoFile("backend", "src", "SteamStat.Core", "Features", "Login", "SteamLoginService.cs"));
        var friendsSource = File.ReadAllText(RepoFile("backend", "src", "SteamStat.Core", "Features", "Friends", "SteamFriendsService.cs"));
        var updaterSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "UpdateService.cs"));
        var forwarderSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Hosting", "ElectronIpcEventForwarder.cs"));

        userSource.Should().Contain("new LoginUsersChanged()");
        loginSource.Should().Contain("new SteamLoginProgressChanged(type, data)");
        friendsSource.Should().Contain("new FriendsChanged(accountName, snapshot)");
        updaterSource.Should().Contain("new UpdaterStateChanged(new UpdaterEventDto");
        forwarderSource.Should().Contain("SteamIpc.LoginUsersUpdated")
            .And.Contain("SteamLoginIpc.Event")
            .And.Contain("SteamFriendsIpc.Updated")
            .And.Contain("UpdaterIpc.Event");
    }

    [Test]
    public void ElectronIpcSend_ExistsOnlyInHostForwarder()
    {
        var productRoot = RepoFile("ElectronNet", "ElectronNet");
        var sendFiles = Directory.GetFiles(productRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("Electron.IpcMain.Send"))
            .Select(Path.GetFullPath);

        sendFiles.Should().Equal(Path.GetFullPath(RepoFile(
            "ElectronNet", "ElectronNet", "Hosting", "ElectronIpcEventForwarder.cs")));
    }

    [Test]
    public void LoginLifecycle_DoesNotCallFriendsImplementationDirectly()
    {
        var loginSource = File.ReadAllText(RepoFile("backend", "src", "SteamStat.Core", "Features", "Login", "SteamLoginService.cs"));

        loginSource.Should().NotContain("SteamFriendsService.");
        loginSource.Should().Contain("new SteamSessionEnded(accountName)")
            .And.Contain("new SteamSessionReady(accountName)");
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
