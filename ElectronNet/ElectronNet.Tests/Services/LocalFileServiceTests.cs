using ElectronNet.Services;
using ElectronNet.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElectronNet.Tests.Services;

/// <summary>
/// LocalFileService 是纯粹的本地文件解析层：不依赖网络、不依赖 Electron、不依赖数据库，
/// 因此可以完整地做单元测试。这里用临时目录里的 fixture 替代真实 Steam 安装。
/// </summary>
[TestFixture]
public class LocalFileServiceTests
{
    private readonly LocalFileService _service = new(NullLogger<LocalFileService>.Instance);

    #region loginusers.vdf

    [Test]
    public void ReadLoginUsersVdf_ParsesAllUsers()
    {
        using var layout = new TempSteamLayout().WithConfigFile("loginusers.vdf");

        var users = _service.ReadLoginUsersVdf(layout.SteamPath);

        users.Should().HaveCount(2);

        var first = users.Single(u => u.SteamID == "76561198000000001");
        first.AccountName.Should().Be("testuser1");
        first.PersonaName.Should().Be("Test User One");
        first.RememberPassword.Should().BeTrue();
        first.WantsOfflineMode.Should().BeFalse();
        first.SkipOfflineModeWarning.Should().BeFalse();
        first.AllowAutoLogin.Should().BeTrue();
        first.MostRecent.Should().BeTrue();
        first.Timestamp.Should().Be(1700000000);

        var second = users.Single(u => u.SteamID == "76561198000000002");
        second.AccountName.Should().Be("testuser2");
        second.RememberPassword.Should().BeFalse();
        second.WantsOfflineMode.Should().BeTrue();
        second.AllowAutoLogin.Should().BeFalse();
        second.MostRecent.Should().BeFalse();
        second.Timestamp.Should().Be(1699999000);
    }

    [Test]
    public void ReadLoginUsersVdf_DecodesEscapeSequences()
    {
        using var layout = new TempSteamLayout().WithConfigFile("loginusers.vdf");

        var users = _service.ReadLoginUsersVdf(layout.SteamPath);

        // fixture 里写的是转义后的引号与反斜杠，HasEscapeSequences = true 时应被解码回原字符
        users.Single(u => u.AccountName == "testuser2")
            .PersonaName.Should().Be("引号 \"昵称\" 与反斜杠 \\ 测试");
    }

    [Test]
    public void ReadLoginUsersVdf_WhenFileMissing_ReturnsEmpty()
    {
        using var layout = new TempSteamLayout();

        _service.ReadLoginUsersVdf(layout.SteamPath).Should().BeEmpty();
    }

    [Test]
    public void ReadLoginUsersVdf_WhenOptionalFieldsAreMissing_UsesSafeDefaultsAndSkipsInvalidEntries()
    {
        using var layout = new TempSteamLayout();
        var config = Path.Combine(layout.SteamPath, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "loginusers.vdf"),
            """
            "users"
            {
                "76561198000000001"
                {
                    "AccountName" "partial-user"
                }
                "metadata"
                {
                    "AccountName" "not-a-user"
                }
            }
            """);

        var users = _service.ReadLoginUsersVdf(layout.SteamPath);

        users.Should().ContainSingle();
        users[0].AccountName.Should().Be("partial-user");
        users[0].PersonaName.Should().BeEmpty();
        users[0].RememberPassword.Should().BeFalse();
        users[0].Timestamp.Should().Be(0);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void ReadLoginUsersVdf_WhenSteamPathBlank_ReturnsEmpty(string steamPath)
    {
        _service.ReadLoginUsersVdf(steamPath).Should().BeEmpty();
    }

    #endregion

    #region libraryfolders.vdf

    [Test]
    public void ReadLibraryFoldersVdf_ParsesAllLibrariesAndApps()
    {
        using var layout = new TempSteamLayout().WithConfigFile("libraryfolders.vdf");

        var libraries = _service.ReadLibraryFoldersVdf(layout.SteamPath);

        libraries.Should().HaveCount(2);

        var main = libraries.Single(l => l.Index == 0);
        main.Path.Should().Be(@"C:\Program Files (x86)\Steam");
        main.Label.Should().BeEmpty();
        main.ContentId.Should().Be(1234567890123456789L);
        main.TotalSize.Should().Be(0);
        main.UpdateCleanBytesTally.Should().Be(123456789L);
        main.TimeLastUpdateVerified.Should().Be(1700000000);
        main.Apps.Should().HaveCount(2);
        main.Apps[570].Should().Be(38000000000L);
        main.Apps[730].Should().Be(35000000000L);

        var second = libraries.Single(l => l.Index == 1);
        second.Path.Should().Be(@"D:\SteamLibrary");
        second.Label.Should().Be("游戏盘");
        second.Apps.Should().ContainSingle().Which.Key.Should().Be(440);
    }

    [Test]
    public void ReadLibraryFoldersVdf_WhenFileMissing_ReturnsEmpty()
    {
        using var layout = new TempSteamLayout();

        _service.ReadLibraryFoldersVdf(layout.SteamPath).Should().BeEmpty();
    }

    #endregion

    #region appmanifest_*.acf

    [Test]
    public void ReadAllAppManifestAcfs_ParsesFullManifest()
    {
        using var layout = new TempSteamLayout();
        var library = layout.WithLibrary("Steam", "appmanifest_570.acf");

        var manifests = _service.ReadAllAppManifestAcfs([library]);

        manifests.Should().ContainKey(570);
        var dota = manifests[570];

        dota.Name.Should().Be("Dota 2");
        dota.InstallDir.Should().Be("dota 2 beta");
        dota.LibraryPath.Should().Be(library);
        dota.Universe.Should().Be(1);
        dota.LauncherPath.Should().Be(@"C:\Program Files (x86)\Steam\steam.exe");
        dota.StateFlags.Should().Be(4);
        dota.LastUpdated.Should().Be(1700000000);
        dota.LastPlayed.Should().Be(1700001000);
        dota.SizeOnDisk.Should().Be(38000000000L);
        dota.BuildId.Should().Be(12345678);
        dota.LastOwner.Should().Be(76561198000000001L);
        dota.AllowOtherDownloadsWhileRunning.Should().BeTrue();

        dota.UserConfig.Language.Should().Be("schinese");
        dota.UserConfig.BetaKey.Should().Be("public-beta");
        dota.MountedConfig.Language.Should().Be("schinese");

        dota.SharedDepots.Should().ContainSingle();
        dota.SharedDepots![228990].Should().Be(228980);
    }

    [Test]
    public void ReadAllAppManifestAcfs_ParsesDepotManifestIdsExceedingInt64()
    {
        using var layout = new TempSteamLayout();
        var library = layout.WithLibrary("Steam", "appmanifest_570.acf");

        var depots = _service.ReadAllAppManifestAcfs([library])[570].InstalledDepots;

        depots.Should().HaveCount(2);
        depots![373301].Manifest.Should().Be(1234567890123456789UL);
        depots[373301].Size.Should().Be(1000000L);
        depots[373301].DlcAppId.Should().BeNull();

        // manifest 是 uint64，这个值超出 long.MaxValue，必须走 ToUInt64 才不会溢出
        depots[381451].Manifest.Should().Be(18000000000000000001UL);
        depots[381451].DlcAppId.Should().Be(381450);
    }

    [Test]
    public void ReadAllAppManifestAcfs_WhenOptionalFieldsMissing_LeavesThemNull()
    {
        using var layout = new TempSteamLayout();
        var library = layout.WithLibrary("Steam", "appmanifest_440.acf");

        var tf2 = _service.ReadAllAppManifestAcfs([library])[440];

        tf2.Name.Should().Be("Team Fortress 2");
        tf2.InstallDir.Should().Be("Team Fortress 2");
        // 精简的 manifest 里没有这些键，解析结果应为 null 而不是 0 或抛异常
        tf2.Universe.Should().BeNull();
        tf2.StateFlags.Should().BeNull();
        tf2.SizeOnDisk.Should().BeNull();
        tf2.LastPlayed.Should().BeNull();
        tf2.AllowOtherDownloadsWhileRunning.Should().BeNull();
        tf2.SharedDepots.Should().BeNull();
        tf2.UserConfig.Language.Should().BeNull();
    }

    [Test]
    public void ReadAllAppManifestAcfs_AggregatesAcrossMultipleLibraries()
    {
        using var layout = new TempSteamLayout();
        var mainLibrary = layout.WithLibrary("Steam", "appmanifest_570.acf");
        var secondLibrary = layout.WithLibrary("SteamLibrary", "appmanifest_440.acf");

        var manifests = _service.ReadAllAppManifestAcfs([mainLibrary, secondLibrary]);

        manifests.Should().HaveCount(2);
        manifests[570].LibraryPath.Should().Be(mainLibrary);
        manifests[440].LibraryPath.Should().Be(secondLibrary);
    }

    [Test]
    public void ReadAllAppManifestAcfs_SkipsMissingOrBlankLibraryPaths()
    {
        using var layout = new TempSteamLayout();
        var library = layout.WithLibrary("Steam", "appmanifest_570.acf");
        var missing = Path.Combine(layout.BaseDir, "DoesNotExist");

        var manifests = _service.ReadAllAppManifestAcfs([library, missing, "", "   "]);

        manifests.Should().ContainSingle().Which.Key.Should().Be(570);
    }

    [Test]
    public void ReadAllAppManifestAcfs_WhenOneManifestIsCorrupt_StillReturnsTheOthers()
    {
        using var layout = new TempSteamLayout();
        var library = layout.WithLibrary("Steam", "appmanifest_570.acf", "appmanifest_9999.acf");

        var manifests = _service.ReadAllAppManifestAcfs([library]);

        // 单个损坏的 acf 不应让整次扫描失败——用户库里出现半写入的 acf 是很常见的
        manifests.Should().ContainKey(570);
        manifests.Should().NotContainKey(9999);
    }

    #endregion
}
