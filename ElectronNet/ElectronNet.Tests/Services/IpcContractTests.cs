using System.Text.RegularExpressions;
using FluentAssertions;

namespace ElectronNet.Tests.Services;

[TestFixture]
public partial class IpcContractTests
{
    private static readonly string[] InvokeChannels =
    [
        "job:updateAppRunningStatus:get",
        "setting:get",
        "setting:update",
        "steam:appsInfo:get",
        "steam:appsInfo:refresh",
        "steam:libraryFolders:get",
        "steam:loginUser:change",
        "steam:loginUsers:get",
        "steam:loginUsers:refresh",
        "steam:runningApps:get",
        "steam:status:get",
        "steam:status:refresh",
        "steam:useAppRecording:discard",
        "steam:useAppRecording:end",
        "steam:usersInRecords:get",
        "steam:validUseAppRecord:get",
        "steamFriends:getAll",
        "steamFriends:getCached",
        "steamFriends:getForUser",
        "steamFriends:records:clear",
        "steamFriends:records:get",
        "steamFriends:track:get",
        "steamFriends:track:getAll",
        "steamFriends:track:start",
        "steamFriends:track:stop",
        "steamLibrary:getForAllUsers",
        "steamLibrary:getForUser",
        "steamLibrary:syncForAllUsers",
        "steamLibrary:syncForUser",
        "steamLogin:credentials:start",
        "steamLogin:guardCode:submit",
        "steamLogin:loggedInUsers:get",
        "steamLogin:qr:start",
        "steamLogin:savedToken:delete",
        "steamLogin:savedTokens:get",
        "steamLogin:token:start",
        "steamLogin:user:logout",
        "steamLogin:user:setPersonaState",
        "updater:status:get",
        "window:isMaximized",
        "window:maximize"
    ];

    private static readonly string[] SendChannels =
    [
        "app:quit",
        "shell:openExternal",
        "shell:openPath",
        "steamFriends:requestFriendInfo",
        "steamLogin:cancel",
        "steamLogin:confirmDevice",
        "steamLogin:switchToUseCode",
        "updater:check",
        "updater:download",
        "updater:quitAndInstall",
        "window:close",
        "window:minimize",
        "window:minimizeToTray"
    ];

    private static readonly string[] EventChannels =
    [
        "steam:loginUsers:updated",
        "steamFriends:update",
        "steamLogin:event",
        "updater:event"
    ];

    [Test]
    public void MainAndPreload_KeepCurrentChannelsAndDirections()
    {
        var mainSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "IpcMainService.cs"));
        var preloadSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Resources", "preload.mjs"));

        Channels(MainHandleRegex(), mainSource).Should().Equal(InvokeChannels);
        Channels(PreloadInvokeRegex(), preloadSource).Should().Equal(InvokeChannels);
        Channels(MainOnRegex(), mainSource).Should().Equal(SendChannels);
        Channels(PreloadSendRegex(), preloadSource).Should().Equal(SendChannels);
        Channels(PreloadOnRegex(), preloadSource).Should().Equal(EventChannels);
        Channels(PreloadRemoveRegex(), preloadSource).Should().Equal(EventChannels);
    }

    [Test]
    public void PreloadAndTypes_ExposeTheSameJavascriptMethods()
    {
        var preloadSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Resources", "preload.mjs"));
        var typesSource = File.ReadAllText(RepoFile("src", "types", "ipc.d.ts"));
        var interfaceBody = ElectronApiRegex().Match(typesSource).Groups[1].Value;

        Methods(PreloadMethodRegex(), preloadSource).Should().Equal(Methods(TypeMethodRegex(), interfaceBody));
    }

    [Test]
    public void HostToRendererEvents_ArePublishedAsTypedEventsAndForwardedCentrally()
    {
        var userSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "SteamUserService.cs"));
        var loginSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "SteamLoginService.cs"));
        var friendsSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "SteamFriendsService.cs"));
        var updaterSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "UpdateService.cs"));
        var forwarderSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Hosting", "ElectronIpcEventForwarder.cs"));

        userSource.Should().Contain("new LoginUsersChanged()");
        loginSource.Should().Contain("new SteamLoginProgressChanged(type, data)");
        friendsSource.Should().Contain("new FriendsChanged(accountName, ToSnapshot(data))");
        updaterSource.Should().Contain("new UpdaterStateChanged(updaterEvent, data)");
        EventChannels.Should().OnlyContain(channel => forwarderSource.Contains($"\"{channel}\""));
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
        var loginSource = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "SteamLoginService.cs"));

        loginSource.Should().NotContain("SteamFriendsService.");
        loginSource.Should().Contain("new SteamSessionDisconnected(accountName)")
            .And.Contain("new SteamSessionReconnected(accountName)");
    }

    private static string[] Channels(Regex regex, string source) => regex.Matches(source)
        .Select(match => match.Groups[1].Value)
        .Distinct()
        .Order()
        .ToArray();

    private static string[] Methods(Regex regex, string source) => regex.Matches(source)
        .Select(match => match.Groups[1].Value)
        .Order()
        .ToArray();

    private static string RepoFile(params string[] segments) => Path.Combine([RepoRoot(), .. segments]);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [GeneratedRegex("ipcMain\\.Handle\\(\"([^\"]+)\"")]
    private static partial Regex MainHandleRegex();

    [GeneratedRegex("ipcMain\\.On\\(\"([^\"]+)\"")]
    private static partial Regex MainOnRegex();

    [GeneratedRegex("ipcRenderer\\.invoke\\(\"([^\"]+)\"")]
    private static partial Regex PreloadInvokeRegex();

    [GeneratedRegex("ipcRenderer\\.send\\(\"([^\"]+)\"")]
    private static partial Regex PreloadSendRegex();

    [GeneratedRegex("ipcRenderer\\.on\\(\"([^\"]+)\"")]
    private static partial Regex PreloadOnRegex();

    [GeneratedRegex("ipcRenderer\\.removeAllListeners\\(\"([^\"]+)\"")]
    private static partial Regex PreloadRemoveRegex();

    [GeneratedRegex("(?m)^  ([A-Za-z0-9]+):")]
    private static partial Regex PreloadMethodRegex();

    [GeneratedRegex("interface ElectronAPI \\{([\\s\\S]*?)^}", RegexOptions.Multiline)]
    private static partial Regex ElectronApiRegex();

    [GeneratedRegex("(?m)^  ([A-Za-z0-9]+):")]
    private static partial Regex TypeMethodRegex();
}
