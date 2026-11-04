using ElectronNet.Features.Sync;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNet.Models;
using ElectronNet.Persistence;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Platform;
using SteamStat.Core.Sync;

namespace ElectronNet.Tests.Features.Sync;

[TestFixture]
public sealed class SyncServiceTests
{
    private const string Passphrase = "correct horse battery staple";
    private string _root = null!;
    private string _shared = null!;
    private readonly List<SyncService> _services = [];

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "steam-stat-sync-e2e", Guid.NewGuid().ToString("N"));
        _shared = Path.Combine(_root, "shared");
        Directory.CreateDirectory(_shared);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var service in _services) service.Dispose();
        _services.Clear();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public async Task TwoDevices_ConvergeOnSessionsArchivesEditsAndDeletes()
    {
        var a = Track(await TestDevice.CreateAsync(Path.Combine(_root, "a"), "device-a"));
        var b = Track(await TestDevice.CreateAsync(Path.Combine(_root, "b"), "device-b"));

        await using (var db = await a.Factory.CreateDbContextAsync())
        {
            db.SteamAppTable.Add(new SteamApp { AppId = 570, Name = "Dota 2", NameLocalizedJson = "{}", Installed = true, InstallDir = "dota" });
            db.UseAppRecordTable.AddRange(
                new UseAppRecord { AppId = 570, SteamId = "765", StartTime = 1000, EndTime = 4600, Duration = 3600, DeviceId = "device-a", Note = "a1" },
                new UseAppRecord { AppId = 570, SteamId = "765", StartTime = 9000, DeviceId = "device-a" }); // 进行中，不同步
            db.AchievementUnlockArchiveTable.Add(new AchievementUnlockArchive { AccountName = "alice", SteamId = "765", AppId = 570, InternalName = "WIN", Name = "Win", UnlockTime = 4000, RecordedAt = 4000 });
            db.LibraryGameMetaTable.Add(new LibraryGameMeta { AccountName = "alice", AppId = 570, Status = "playing", UpdatedAt = 1 });
            db.SteamLoginTokenTable.Add(new SteamLoginToken { AccountName = "alice", AccessToken = "secret", RefreshToken = "secret" });
            await db.SaveChangesAsync();
        }

        // A 创建保险库
        var created = await a.Sync.SetupAsync(Setup(_shared, Passphrase));
        created.Success.Should().BeTrue(created.Error);
        created.CreatedVault.Should().BeTrue();
        (await a.Sync.RunAsync("test")).Should().BeNull();
        File.ReadAllText(Path.Combine(_shared, "steam-stat-sync", "vault.json")).Should().NotContain("secret");

        // B 用错误口令加入失败，用正确口令加入成功
        (await b.Sync.SetupAsync(Setup(_shared, "wrong passphrase"))).Error.Should().Be("wrongPassphrase");
        await using (var db = await b.Factory.CreateDbContextAsync())
        {
            db.UseAppRecordTable.Add(new UseAppRecord { AppId = 730, SteamId = "765", StartTime = 2000, EndTime = 2600, Duration = 600, DeviceId = "device-b", Note = "b1" });
            await db.SaveChangesAsync();
        }
        var joined = await b.Sync.SetupAsync(Setup(_shared, Passphrase));
        joined.Success.Should().BeTrue(joined.Error);
        joined.CreatedVault.Should().BeFalse();
        (await b.Sync.RunAsync("test")).Should().BeNull();
        (await a.Sync.RunAsync("test")).Should().BeNull();

        foreach (var device in new[] { a, b })
        {
            await using var db = await device.Factory.CreateDbContextAsync();
            (await db.UseAppRecordTable.Where(row => row.EndTime != null).OrderBy(row => row.StartTime).Select(row => row.Note).ToListAsync())
                .Should().Equal("a1", "b1");
            (await db.AchievementUnlockArchiveTable.SingleAsync()).Name.Should().Be("Win");
            (await db.LibraryGameMetaTable.SingleAsync()).Status.Should().Be("playing");
            var devices = await db.DeviceTable.ToListAsync();
            devices.Should().HaveCount(2);
            devices.Single(row => row.IsCurrent).Id.Should().Be(device.Id);
        }
        await using (var db = await b.Factory.CreateDbContextAsync())
        {
            // B 只拿到 A 的元数据，不会以为本机装了这个游戏；凭证与进行中的会话不会同步
            var app = await db.SteamAppTable.SingleAsync(row => row.AppId == 570);
            app.Name.Should().Be("Dota 2");
            app.Installed.Should().BeFalse();
            app.InstallDir.Should().BeNull();
            (await db.SteamLoginTokenTable.CountAsync()).Should().Be(0);
            (await db.UseAppRecordTable.CountAsync(row => row.EndTime == null)).Should().Be(0);
        }

        // 应用远端变更不会回写 outbox（不会来回反弹）
        (await a.Sync.GetStatusAsync()).PendingChanges.Should().Be(0);
        (await b.Sync.GetStatusAsync()).PendingChanges.Should().Be(0);

        // B 编辑 A 的会话备注；A 删除 B 的会话
        await using (var db = await b.Factory.CreateDbContextAsync())
        {
            var session = await db.UseAppRecordTable.SingleAsync(row => row.Note == "a1");
            session.Note = "edited on b";
            await db.SaveChangesAsync();
        }
        await using (var db = await a.Factory.CreateDbContextAsync())
        {
            db.UseAppRecordTable.Remove(await db.UseAppRecordTable.SingleAsync(row => row.Note == "b1"));
            await db.SaveChangesAsync();
        }
        (await b.Sync.GetStatusAsync()).PendingChanges.Should().Be(1);
        (await b.Sync.RunAsync("test")).Should().BeNull();
        (await a.Sync.RunAsync("test")).Should().BeNull();
        (await b.Sync.RunAsync("test")).Should().BeNull();

        foreach (var device in new[] { a, b })
        {
            await using var db = await device.Factory.CreateDbContextAsync();
            (await db.UseAppRecordTable.Where(row => row.EndTime != null).Select(row => row.Note).ToListAsync()).Should().Equal("edited on b");
        }

        // 冲突：两边先后修改同一游戏的状态，较晚的修改胜出
        await using (var db = await a.Factory.CreateDbContextAsync())
        {
            (await db.LibraryGameMetaTable.SingleAsync()).Status = "dropped";
            await db.SaveChangesAsync();
        }
        await Task.Delay(20);
        await using (var db = await b.Factory.CreateDbContextAsync())
        {
            (await db.LibraryGameMetaTable.SingleAsync()).Status = "completed";
            await db.SaveChangesAsync();
        }
        (await a.Sync.RunAsync("test")).Should().BeNull();
        (await b.Sync.RunAsync("test")).Should().BeNull();
        (await a.Sync.RunAsync("test")).Should().BeNull();
        foreach (var device in new[] { a, b })
        {
            await using var db = await device.Factory.CreateDbContextAsync();
            (await db.LibraryGameMetaTable.SingleAsync()).Status.Should().Be("completed");
        }

        var status = await a.Sync.GetStatusAsync();
        status.Enabled.Should().BeTrue();
        status.Devices.Should().HaveCount(2);
        status.Devices.Single(device => !device.IsCurrent).Name.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Compaction_ReplacesOldSegmentsWithASnapshotThatNewDevicesCanReadAlone()
    {
        var a = Track(await TestDevice.CreateAsync(Path.Combine(_root, "a"), "device-a"));
        (await a.Sync.SetupAsync(Setup(_shared, Passphrase))).Success.Should().BeTrue();
        (await a.Sync.RunAsync("test")).Should().BeNull();
        for (var i = 0; i < SyncService.CompactThreshold + 1; i++)
        {
            await using (var db = await a.Factory.CreateDbContextAsync())
            {
                db.UseAppRecordTable.Add(new UseAppRecord { AppId = 10 + i, SteamId = "765", StartTime = 100 * i, EndTime = 100 * i + 50, Duration = 50, DeviceId = "device-a" });
                await db.SaveChangesAsync();
            }
            (await a.Sync.RunAsync("test")).Should().BeNull();
        }

        var segments = Directory.GetFiles(Path.Combine(_shared, "steam-stat-sync", "devices", "device-a"), "seg-*.ssec");
        segments.Length.Should().BeLessThan(SyncService.CompactThreshold);

        var c = Track(await TestDevice.CreateAsync(Path.Combine(_root, "c"), "device-c"));
        (await c.Sync.SetupAsync(Setup(_shared, Passphrase))).Success.Should().BeTrue();
        (await c.Sync.RunAsync("test")).Should().BeNull();
        await using var check = await c.Factory.CreateDbContextAsync();
        (await check.UseAppRecordTable.CountAsync()).Should().Be(SyncService.CompactThreshold + 1);
    }

    [Test]
    public async Task Disconnect_StopsCapturingAndForgetsCredentials()
    {
        var a = Track(await TestDevice.CreateAsync(Path.Combine(_root, "a"), "device-a"));
        (await a.Sync.SetupAsync(Setup(_shared, Passphrase))).Success.Should().BeTrue();
        (await a.Sync.RunAsync("test")).Should().BeNull();

        (await a.Sync.DisableAsync(new SyncDisableRequest { ForgetCredentials = true })).Should().BeTrue();
        await using (var db = await a.Factory.CreateDbContextAsync())
        {
            db.UseAppRecordTable.Add(new UseAppRecord { AppId = 1, SteamId = "765", StartTime = 1, EndTime = 2, Duration = 1, DeviceId = "device-a" });
            await db.SaveChangesAsync();
            (await db.SyncOutboxTable.CountAsync()).Should().Be(0);
        }
        var status = await a.Sync.GetStatusAsync();
        status.Configured.Should().BeFalse();
        status.Config.Should().BeNull();
        (await a.Sync.RunAsync("test")).Should().Be("notConfigured");

        // 远端数据保留：重新连接（加入已有保险库）需要口令
        (await a.Sync.SetupAsync(Setup(_shared, null))).Error.Should().Be("passphraseRequired");
    }

    [Test]
    public async Task Setup_RequiresAReasonablePassphraseForNewVaults()
    {
        var a = Track(await TestDevice.CreateAsync(Path.Combine(_root, "a"), "device-a"));
        (await a.Sync.SetupAsync(Setup(_shared, "short"))).Error.Should().Be("weakPassphrase");
        (await a.Sync.SetupAsync(Setup(Path.Combine(_root, "missing"), Passphrase))).Error.Should().Be("configuration");
        (await a.Sync.TestAsync(Setup(_shared, null).Connection)).Should().Match<SyncTestResultDto>(result => result.Success && !result.VaultExists);
    }

    [Test]
    public void Triggers_AreIdempotentAndCoverEverySyncedTable()
    {
        var statements = SyncSchema.TriggerStatements().ToList();
        statements.Should().OnlyContain(statement => statement.Sql.Contains("IF NOT EXISTS"));
        SyncSchema.Tables.Select(spec => spec.Table).Should().NotContain(["steam_login_token", "friend_status_record", "screenshot_archive", "steam_user"]);
        statements.Select(statement => statement.Table).Distinct().Should().BeEquivalentTo(SyncSchema.Tables.Select(spec => spec.Table));
    }

    private TestDevice Track(TestDevice device)
    {
        _services.Add(device.Sync);
        return device;
    }

    private static SyncSetupRequest Setup(string folder, string? passphrase)
        => new()
        {
            Connection = new SyncConnectRequest { Config = new SyncConfigDto { Backend = "folder", FolderPath = folder, IntervalMinutes = 15 } },
            Passphrase = passphrase == null ? null : IpcSecret.Encode(passphrase)
        };

    private sealed class TestDevice
    {
        public required string Id { get; init; }
        public required IDbContextFactory<AppDbContext> Factory { get; init; }
        public required SyncService Sync { get; init; }

        public static async Task<TestDevice> CreateAsync(string root, string deviceId)
        {
            var paths = new AppPaths(root);
            Directory.CreateDirectory(paths.DatabaseDirectory);
            var factory = new TestDbContextFactory(paths.DatabaseFile);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                db.DeviceTable.Add(new Device { Id = deviceId, Name = deviceId.ToUpperInvariant(), IsCurrent = true, CreatedAt = 1 });
                await db.SaveChangesAsync();
            }
            var deviceService = new DeviceService(factory, TimeProvider.System, NullLogger<DeviceService>.Instance);
            var sync = new SyncService(
                paths,
                new SyncConfigStore(paths, new FakeSecretStore(), NullLogger<SyncConfigStore>.Instance),
                new FakeHttpClientFactory(),
                deviceService,
                new AppEnvironment(false, "en-US", false, paths, "4.2.0"),
                new NoWindow(),
                new NullEventBus(),
                TimeProvider.System,
                NullLogger<SyncService>.Instance) { RunAfterSetup = false };
            return new TestDevice { Id = deviceId, Factory = factory, Sync = sync };
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public string? Protect(string? plainText) => plainText == null ? null : "test:" + plainText;
        public string? Unprotect(string? protectedText) => protectedText?.StartsWith("test:") == true ? protectedText[5..] : null;
        public bool IsProtected(string? value) => value?.StartsWith("test:") == true;
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NoWindow : IMainWindowAccessor
    {
        public Task<MainWindowSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MainWindowSnapshot(null, MainWindowAvailability.Missing));
    }

    private sealed class NullEventBus : IEventBus
    {
        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : notnull => Task.CompletedTask;
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile))
            .Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
