using ElectronNet.Features.Archive;
using ElectronNet.Models;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Settings;
using ValveKeyValue;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class ArchiveExtensionTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "steam-stat-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    #region 截图

    [Test]
    public void ScreenshotScanner_ReadsFilesAndPrefersVdfMetadata()
    {
        var steam = Path.Combine(_root, "Steam");
        var shots = Path.Combine(steam, "userdata", "123", "760", "remote", "570", "screenshots");
        Directory.CreateDirectory(Path.Combine(shots, "thumbnails"));
        File.WriteAllBytes(Path.Combine(shots, "20240102030405_1.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(shots, "thumbnails", "20240102030405_1.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(shots, "20240105000000_1.jpg"), [1, 2]);
        File.WriteAllText(Path.Combine(shots, "notes.txt"), "ignored");
        File.WriteAllText(Path.Combine(steam, "userdata", "123", "760", "screenshots.vdf"), """
            "Screenshots"
            {
                "570"
                {
                    "0"
                    {
                        "filename" "570/screenshots/20240102030405_1.jpg"
                        "creation" "1700000000"
                        "width" "1920"
                        "height" "1080"
                        "caption" "Victory"
                    }
                }
            }
            """);

        var result = ScreenshotScanner.ScanAll(steam).OrderBy(item => item.FileName).ToArray();

        result.Should().HaveCount(2);
        result[0].SteamId.Should().Be("76561197960265851");
        result[0].AppId.Should().Be(570);
        result[0].TakenAt.Should().Be(1_700_000_000);
        result[0].Width.Should().Be(1920);
        result[0].Caption.Should().Be("Victory");
        result[0].ThumbnailPath.Should().NotBeNull();
        result[1].ThumbnailPath.Should().BeNull();
        result[1].TakenAt.Should().Be(ScreenshotScanner.ParseTakenAt("20240105000000_1.jpg"));
    }

    [Test]
    public void ScreenshotScanner_ParsesSteamFileNamesOnly()
    {
        ScreenshotScanner.ParseTakenAt("20240102030405_1.jpg").Should()
            .Be(new DateTimeOffset(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Local)).ToUnixTimeSeconds());
        ScreenshotScanner.ParseTakenAt("screenshot.png").Should().BeNull();
        ScreenshotScanner.ParseTakenAt("20241399000000_1.jpg").Should().BeNull();
    }

    #endregion

    #region 存档目录定位

    [Test]
    public void SaveLocationDiscovery_CommonDirectory_UsesSharedParentOnly()
    {
        SaveLocationDiscovery.CommonDirectory(["Team Cherry/Hollow Knight/user1.dat", "Team Cherry/Hollow Knight/user2.dat"])
            .Should().Be("Team Cherry/Hollow Knight");
        SaveLocationDiscovery.CommonDirectory(["Game/a/1.sav", "Game/b/2.sav"]).Should().Be("Game");
        SaveLocationDiscovery.CommonDirectory(["root.sav", "Game/2.sav"]).Should().BeEmpty();
    }

    [Test]
    public void SaveLocationDiscovery_InfersAutoCloudFolders_AndSkipsUnsafeOnes()
    {
        var documents = Path.Combine(_root, "Documents");
        Directory.CreateDirectory(Path.Combine(documents, "My Games", "Example"));
        var entries = new[]
        {
            new RemoteCacheEntry("My Games/Example/save1.sav", 2),
            new RemoteCacheEntry("My Games/Example/save2.sav", 2),
            new RemoteCacheEntry("loose.sav", 4),
            new RemoteCacheEntry("../escape/x.sav", 9),
            new RemoteCacheEntry("remote.sav", 0)
        };

        var result = SaveLocationDiscovery.InferAutoCloud(entries, root => root switch
        {
            2 => documents,
            4 => Path.Combine(_root, "Roaming"),
            9 => Path.Combine(_root, "Saved Games"),
            _ => null
        }).ToArray();

        result.Should().ContainSingle();
        result[0].Path.Should().Be(Path.GetFullPath(Path.Combine(documents, "My Games", "Example")));
        result[0].FileCount.Should().Be(2);
    }

    [Test]
    public void SaveLocationDiscovery_ParsesRemoteCacheAndFindsSteamCloudFolders()
    {
        var steam = Path.Combine(_root, "Steam");
        var app = Path.Combine(steam, "userdata", "123", "570");
        Directory.CreateDirectory(Path.Combine(app, "remote", "cfg"));
        File.WriteAllText(Path.Combine(app, "remote", "cfg", "config.cfg"), "x");
        Directory.CreateDirectory(Path.Combine(steam, "userdata", "123", "760", "remote"));
        File.WriteAllText(Path.Combine(steam, "userdata", "123", "760", "remote", "x.jpg"), "x");
        File.WriteAllText(Path.Combine(app, "remotecache.vdf"), """
            "570"
            {
                "ChangeNumber" "5"
                "cfg/config.cfg"
                {
                    "root" "0"
                    "size" "1"
                }
            }
            """);

        var candidates = SaveLocationDiscovery.Discover(steam, _ => null);

        candidates.Should().ContainSingle();
        candidates[0].Kind.Should().Be("steamCloud");
        candidates[0].AppId.Should().Be(570);
        candidates[0].FileCount.Should().Be(1);
        using var stream = File.OpenRead(Path.Combine(app, "remotecache.vdf"));
        var document = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream);
        SaveLocationDiscovery.ParseRemoteCache(document).Should().Equal(new RemoteCacheEntry("cfg/config.cfg", 0));
    }

    #endregion

    #region 存档快照

    [Test]
    public async Task SaveBackup_CreatesVersionsOnlyWhenContentChanges_AndNeverTouchesSource()
    {
        var saves = Path.Combine(_root, "Saves");
        Directory.CreateDirectory(Path.Combine(saves, "slots"));
        var slot = Path.Combine(saves, "slots", "slot1.json");
        await File.WriteAllTextAsync(slot, """{ "gold": 1 }""");
        await File.WriteAllTextAsync(Path.Combine(saves, "settings.ini"), "[a]\nb=1");
        File.SetLastWriteTimeUtc(slot, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        using var service = new SaveBackupService(
            factory,
            new AppPaths(Path.Combine(_root, "UserData")),
            null!,
            new FakeSettingsStore(),
            null!,
            new NullEventBus(),
            TimeProvider.System,
            NullLogger<SaveBackupService>.Instance);

        var add = await service.AddTargetAsync(new SaveBackupTargetAddRequest { AppId = 570, Path = saves });
        add.Success.Should().BeTrue();
        (await service.AddTargetAsync(new SaveBackupTargetAddRequest { AppId = 570, Path = saves + Path.DirectorySeparatorChar }))
            .Error.Should().Be("duplicate");
        var targetId = add.Target!.Id;

        (await service.SnapshotTargetAsync(targetId, "manual", null, CancellationToken.None)).Created.Should().BeTrue();
        (await service.SnapshotTargetAsync(targetId, "schedule", null, CancellationToken.None)).Created.Should().BeFalse();

        await File.WriteAllTextAsync(slot, """{ "gold": 2 }""");
        (await service.SnapshotTargetAsync(targetId, "sessionEnd", null, CancellationToken.None)).Created.Should().BeTrue();

        var snapshots = service.GetSnapshots(targetId);
        snapshots.Should().HaveCount(2);
        snapshots[0].Trigger.Should().Be("sessionEnd");
        snapshots[0].ChangedFiles.Should().Be(1);

        var files = service.GetSnapshotFiles(snapshots[0].Id);
        files.PreviousSnapshotId.Should().Be(snapshots[1].Id);
        files.Items.Single(item => item.RelativePath == "slots/slot1.json").Change.Should().Be("modified");
        files.Items.Single(item => item.RelativePath == "settings.ini").Change.Should().Be("unchanged");

        // 原存档内容保持不变（备份只读源文件）
        (await File.ReadAllTextAsync(slot)).Should().Be("""{ "gold": 2 }""");

        service.GetTargets().Single().SnapshotCount.Should().Be(2);
        (await service.DeleteSnapshotAsync(snapshots[1].Id)).Should().BeTrue();
        var objects = Directory.GetFiles(Path.Combine(_root, "UserData", "SaveBackups", "objects"), "*", SearchOption.AllDirectories);
        objects.Should().HaveCount(2, "the old slot content is no longer referenced and is garbage-collected");

        (await service.RemoveTargetAsync(new SaveBackupTargetRemoveRequest { Id = targetId })).Should().BeTrue();
        service.GetTargets().Single().Enabled.Should().BeFalse("removing a target keeps its history unless asked otherwise");
        (await service.RemoveTargetAsync(new SaveBackupTargetRemoveRequest { Id = targetId, DeleteSnapshots = true })).Should().BeTrue();
        service.GetTargets().Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_root, "UserData", "SaveBackups", "objects"), "*", SearchOption.AllDirectories)
            .Should().BeEmpty();
        File.Exists(slot).Should().BeTrue();
    }

    [Test]
    public async Task SaveBackup_RejectsBroadOrMissingDirectories()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        using var service = new SaveBackupService(
            factory, new AppPaths(Path.Combine(_root, "UserData")), null!, new FakeSettingsStore(), null!,
            new NullEventBus(), TimeProvider.System, NullLogger<SaveBackupService>.Instance);

        (await service.AddTargetAsync(new SaveBackupTargetAddRequest { AppId = 1, Path = Path.Combine(_root, "missing") }))
            .Error.Should().Be("notFound");
        (await service.AddTargetAsync(new SaveBackupTargetAddRequest { AppId = 1, Path = "relative/path" }))
            .Error.Should().Be("notFound");
        (await service.AddTargetAsync(new SaveBackupTargetAddRequest { AppId = 1, Path = Path.GetPathRoot(_root)! }))
            .Error.Should().Be("forbidden");
        Directory.CreateDirectory(Path.Combine(_root, "UserData", "SaveBackups"));
        (await service.AddTargetAsync(new SaveBackupTargetAddRequest { AppId = 1, Path = Path.Combine(_root, "UserData") }))
            .Error.Should().Be("forbidden");
    }

    [Test]
    public void SaveBackupObjectStore_ScanEnforcesLimits_AndExportStaysInsideFolder()
    {
        var saves = Path.Combine(_root, "Saves");
        Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(saves, "a.sav"), new byte[10]);
        File.WriteAllBytes(Path.Combine(saves, "b.sav"), new byte[10]);

        SaveBackupObjectStore.Scan(saves, 10, 100).Files.Should().HaveCount(2);
        SaveBackupObjectStore.Scan(saves, 1, 100).Error.Should().Be("tooManyFiles");
        SaveBackupObjectStore.Scan(saves, 10, 15).Error.Should().Be("tooLarge");
        SaveBackupObjectStore.Scan(Path.Combine(_root, "none"), 10, 100).Error.Should().Be("notFound");

        var store = new SaveBackupObjectStore(Path.Combine(_root, "Store"));
        var exported = store.Export([new SaveManifestEntry("../../escape.sav", 1, 0, new string('a', 64))], "x");
        Directory.GetFiles(exported, "*", SearchOption.AllDirectories).Should().BeEmpty();
        File.Exists(Path.Combine(_root, "escape.sav")).Should().BeFalse();

        SaveBackupObjectStore.ManifestHash([new SaveManifestEntry("b", 1, 1, "x"), new SaveManifestEntry("a", 1, 1, "y")])
            .Should().Be(SaveBackupObjectStore.ManifestHash([new SaveManifestEntry("a", 9, 9, "y"), new SaveManifestEntry("b", 9, 9, "x")]));
    }

    #endregion

    #region 资料变化

    [Test]
    public async Task ProfileHistory_RecordsBaselineThenOnlyChanges()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        using var service = new ProfileHistoryService(
            factory, null!, new NullAvatarUris(), null!, null!, null!,
            new AppPaths(Path.Combine(_root, "UserData")), TimeProvider.System, NullLogger<ProfileHistoryService>.Instance);
        const string steamId = "76561197960265851";

        (await service.ObserveAsync(steamId, "cm", "Ann", null, 10, "none")).Should().Be(3);
        (await service.ObserveAsync(steamId, "friends", "Ann", null, 10, null)).Should().Be(0);
        (await service.ObserveAsync(steamId, "friends", "Annie", null, 11, null)).Should().Be(2);

        var history = service.Get(new ProfileHistoryQueryRequest { SteamId = steamId });
        history.Should().HaveCount(5);
        var rename = history.Single(entry => entry.Field == "personaName" && entry.Value == "Annie");
        rename.PreviousValue.Should().Be("Ann");
        rename.Source.Should().Be("friends");

        ProfileHistoryService.NormalizeAvatar(new string('0', 40)).Should().BeNull();
        ProfileHistoryService.NormalizeAvatar("FEF49E7FA7E1997310D705B2A6158FF8DC1CDFEB").Should().Be("fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb");
        ProfileHistoryService.NormalizeName("[unknown]").Should().BeNull();
    }

    [Test]
    public async Task ProfileHistory_EnrichesBadgesFromTheBadgeNameCache()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        var paths = new AppPaths(Path.Combine(_root, "UserData"));
        const string steamId = "76561197960265851";
        const string badge = """{"badgeId":1,"level":40,"appId":4228010,"communityItemId":"0","borderColor":0,"appName":null}""";
        using (var service = new ProfileHistoryService(
                   factory, null!, new NullAvatarUris(), null!, null!, null!,
                   paths, TimeProvider.System, NullLogger<ProfileHistoryService>.Instance))
        {
            (await service.ObserveAsync(steamId, "cm", null, null, null, badge)).Should().Be(1);
            service.StoreBadgeName(4228010, "english", "Autumn Sale 2025", "Steam Autumn Sale");
        }

        // 新实例从磁盘读取缓存；徽章名不参与比较，因此同一徽章不会产生新记录
        using var reloaded = new ProfileHistoryService(
            factory, null!, new NullAvatarUris(), null!, null!, null!,
            paths, TimeProvider.System, NullLogger<ProfileHistoryService>.Instance);
        var value = reloaded.Get(new ProfileHistoryQueryRequest { SteamId = steamId }).Single().Value;
        value.Should().Contain("\"badgeName\":\"Autumn Sale 2025\"").And.Contain("\"appName\":\"Steam Autumn Sale\"");
        (await reloaded.ObserveAsync(steamId, "cm", null, null, null, value)).Should().Be(0);
        ProfileHistoryService.BadgeAppId(badge).Should().Be(4228010);
        ProfileHistoryService.BadgeAppId("none").Should().Be(0);
    }

    #endregion

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public AppSettings GetSettings() => new() { Archive = new ArchiveSettings { SaveBackupMaxTargetMb = 16, SaveBackupKeepVersions = 0 } };
        public Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class NullEventBus : IEventBus
    {
        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : notnull
            => Task.CompletedTask;
    }

    private sealed class NullAvatarUris : SteamStat.Core.Features.Profile.Contracts.ISteamAvatarUriProvider
    {
        public Uri GetDefaultAvatarUri(SteamStat.Core.Features.Profile.Contracts.SteamAvatarSize size) => new("https://example.invalid/");
        public Uri? GetAvatarUri(string? avatarHash, SteamStat.Core.Features.Profile.Contracts.SteamAvatarSize size) => null;
    }
}
