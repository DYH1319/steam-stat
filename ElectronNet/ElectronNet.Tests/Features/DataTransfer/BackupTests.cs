using System.IO.Compression;
using System.Text.Json;
using ElectronNet.Features.DataTransfer;
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

namespace ElectronNet.Tests.Features.DataTransfer;

[TestFixture]
public sealed class BackupTests
{
    private string _root = null!;
    private AppPaths _paths = null!;
    private TestDbContextFactory _factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "steam-stat-backup-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "UserData"));
        Directory.CreateDirectory(_paths.DatabaseDirectory);
        _factory = new TestDbContextFactory(_paths.DatabaseFile);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void IpcSecret_SurvivesValuesTheBridgeWouldRewrite()
    {
        foreach (var value in new[] { "12:30", "2026-01-02", "3f2504e04f8911d39a0c0305e82c3301", "密码 pass" })
            IpcSecret.Decode(IpcSecret.Encode(value)).Should().Be(value);
        IpcSecret.Decode("plain").Should().BeNull();
        IpcSecret.Decode("s1:%%%").Should().BeNull();
    }

    [Test]
    public async Task Create_WritesManifestAndStripsCredentialsAndCaches()
    {
        await SeedAsync("device-a", "session-a", 570);
        await File.WriteAllTextAsync(_paths.SettingsFile.EnsureDirectory(), """{"language":"zh-CN"}""");
        var target = Path.Combine(_root, "out", "backup.ssbackup");

        var result = await CreateService().CreateToAsync(new BackupCreateRequest
        {
            IncludeSaveBackups = false,
            RendererState = """{"privacy":"{\"streamerMode\":true}"}"""
        }, target);

        result.Status.Should().Be("completed");
        result.Encrypted.Should().BeFalse();
        result.ExportId.Should().StartWith("loc_");
        result.Manifest!.HasSettings.Should().BeTrue();
        result.Manifest.HasRendererState.Should().BeTrue();
        result.Manifest.SameDevice.Should().BeTrue();
        result.Manifest.Counts.Single(count => count.Key == "sessions").Count.Should().Be(1);

        using var zip = ZipFile.OpenRead(target);
        var manifest = BackupFormat.ReadManifest(zip)!;
        manifest.Migration.Should().NotBeNullOrEmpty();
        manifest.Files.Select(file => file.Name).Should().BeEquivalentTo(
            BackupFormat.DatabaseEntry, BackupFormat.SettingsEntry, BackupFormat.RendererStateEntry);

        var extracted = Path.Combine(_root, "check.db");
        zip.GetEntry(BackupFormat.DatabaseEntry)!.ExtractToFile(extracted);
        await using (var connection = new SqliteConnection(SqliteConnectionStrings.Create(extracted, pooling: false)))
        {
            await connection.OpenAsync();
            (await SqliteSchema.ScalarLongAsync(connection, "SELECT COUNT(*) FROM steam_login_token")).Should().Be(0);
            (await SqliteSchema.ScalarLongAsync(connection, "SELECT COUNT(*) FROM use_app_record")).Should().Be(1);
        }
        var raw = await File.ReadAllBytesAsync(extracted);
        System.Text.Encoding.UTF8.GetString(raw).Should().NotContain("refresh-secret", "VACUUM must not leave deleted credentials in free pages");
    }

    [Test]
    public async Task EncryptedBackup_NeedsTheRightPassphrase()
    {
        await SeedAsync("device-a", "session-a", 570);
        var target = Path.Combine(_root, "secret.ssbackup");
        var service = CreateService();

        var created = await service.CreateToAsync(new BackupCreateRequest { IncludeSaveBackups = false, Password = IpcSecret.Encode("12:30 hunter2") }, target);
        created.Encrypted.Should().BeTrue();
        (await File.ReadAllBytesAsync(target)).Take(4).Should().Equal("SSEC"u8.ToArray());

        var inspected = await service.InspectAsync(target);
        inspected.Status.Should().Be("locked");
        inspected.Manifest.Should().BeNull();

        var wrong = await service.UnlockAsync(new BackupUnlockRequest { Token = inspected.Token!, Password = IpcSecret.Encode("12:30:00 hunter2") });
        wrong.Error.Should().Be("wrongPassword");

        var unlocked = await service.UnlockAsync(new BackupUnlockRequest { Token = inspected.Token!, Password = IpcSecret.Encode("12:30 hunter2") });
        unlocked.Status.Should().Be("ready");
        unlocked.Manifest!.Counts.Should().Contain(count => count.Key == "sessions" && count.Count == 1);

        (await service.StageRestoreAsync(new BackupRestoreRequest
        {
            Token = unlocked.Token!, RestoreSettings = true, RestoreRendererState = true, RestoreSaveBackups = true
        })).Success.Should().BeTrue();
        service.GetRestoreStatus().Pending.Should().BeTrue();
        service.CancelPendingRestore().Should().BeTrue();
        service.GetRestoreStatus().Pending.Should().BeFalse();
    }

    [Test]
    public async Task Stage_RejectsTamperedFilesAndNewerSchemas()
    {
        await SeedAsync("device-a", "session-a", 570);
        var target = Path.Combine(_root, "backup.ssbackup");
        var service = CreateService();
        await service.CreateToAsync(new BackupCreateRequest { IncludeSaveBackups = false }, target);

        // 改写数据库条目（manifest 里的哈希不再匹配）
        var tampered = Path.Combine(_root, "tampered.ssbackup");
        File.Copy(target, tampered);
        using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            zip.GetEntry(BackupFormat.DatabaseEntry)!.Delete();
            await using var stream = zip.CreateEntry(BackupFormat.DatabaseEntry).Open();
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
        }
        var inspected = await service.InspectAsync(tampered);
        (await service.StageRestoreAsync(Restore(inspected.Token!))).Error.Should().Be("corrupt");
        Directory.Exists(new RestorePaths(_paths).PendingDirectory).Should().BeFalse();

        // 来自更新版本（未知迁移）的备份
        var future = Path.Combine(_root, "future.ssbackup");
        File.Copy(target, future);
        using (var zip = ZipFile.Open(future, ZipArchiveMode.Update))
        {
            BackupManifest manifest;
            await using (var read = zip.GetEntry(BackupFormat.ManifestEntry)!.Open())
                manifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(read))!;
            zip.GetEntry(BackupFormat.ManifestEntry)!.Delete();
            await using var write = zip.CreateEntry(BackupFormat.ManifestEntry).Open();
            await JsonSerializer.SerializeAsync(write, new BackupManifest
            {
                AppVersion = "99.0.0", CreatedAt = manifest.CreatedAt, Migration = "29990101000000_FromTheFuture",
                Counts = manifest.Counts, Files = manifest.Files
            });
        }
        inspected = await service.InspectAsync(future);
        (await service.StageRestoreAsync(Restore(inspected.Token!))).Error.Should().Be("newerVersion");

        (await service.InspectAsync(Path.Combine(_root, "missing.ssbackup"))).Error.Should().Be("io");
        await File.WriteAllTextAsync(Path.Combine(_root, "junk.ssbackup"), "not a zip");
        (await service.InspectAsync(Path.Combine(_root, "junk.ssbackup"))).Error.Should().Be("invalid");
    }

    [Test]
    public async Task PendingRestore_ReplacesTheDatabaseButKeepsThisDevicesIdentityAndCredentials()
    {
        // 备份来自另一台设备 Y，包含会话 B
        await SeedAsync("device-y", "session-b", 730);
        var target = Path.Combine(_root, "from-y.ssbackup");
        await CreateService().CreateToAsync(new BackupCreateRequest { IncludeSaveBackups = true, RendererState = """{"k":"v"}""" }, target);

        // 本机：清空后换成设备 X，会话 A，并保存了登录凭证
        await ResetDatabaseAsync();
        await SeedAsync("device-x", "session-a", 570);
        var service = CreateService();
        var inspected = await service.InspectAsync(target);
        inspected.Manifest!.SameDevice.Should().BeFalse();
        (await service.StageRestoreAsync(Restore(inspected.Token!))).Success.Should().BeTrue();
        SqliteConnection.ClearAllPools();

        var applied = await new PendingRestoreApplier(_paths, TimeProvider.System, NullLogger<PendingRestoreApplier>.Instance).ApplyAsync();

        applied.Should().BeTrue();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.UseAppRecordTable.Select(row => row.Note).ToListAsync()).Should().Equal("session-b");
            var devices = await db.DeviceTable.ToListAsync();
            devices.Single(device => device.IsCurrent).Id.Should().Be("device-x");
            devices.Should().Contain(device => device.Id == "device-y" && !device.IsCurrent);
            (await db.SteamLoginTokenTable.SingleAsync()).AccountName.Should().Be("device-x-account");
        }
        Directory.GetFiles(Path.Combine(_paths.DatabaseDirectory, "backups"), "pre-restore-*.bak").Should().ContainSingle();
        var restorePaths = new RestorePaths(_paths);
        File.Exists(restorePaths.PendingFile).Should().BeFalse();
        Directory.Exists(restorePaths.PendingDirectory).Should().BeFalse();
        service.TakeRendererState().State.Should().Be("""{"k":"v"}""");
        service.TakeRendererState().State.Should().BeNull();
        var status = service.GetRestoreStatus();
        status.LastSuccess.Should().BeTrue();
        status.LastSource.Should().Be("from-y.ssbackup");

        // 没有暂存时什么都不做
        (await new PendingRestoreApplier(_paths, TimeProvider.System, NullLogger<PendingRestoreApplier>.Instance).ApplyAsync()).Should().BeFalse();
    }

    private static BackupRestoreRequest Restore(string token)
        => new() { Token = token, RestoreSettings = true, RestoreRendererState = true, RestoreSaveBackups = true };

    private async Task SeedAsync(string deviceId, string note, int appId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.DeviceTable.Add(new Device { Id = deviceId, Name = deviceId.ToUpperInvariant(), IsCurrent = true, CreatedAt = 1 });
        db.UseAppRecordTable.Add(new UseAppRecord { AppId = appId, SteamId = "76561198000000001", StartTime = 1767312000, EndTime = 1767315600, Duration = 3600, DeviceId = deviceId, Note = note });
        db.SteamLoginTokenTable.Add(new SteamLoginToken { AccountName = $"{deviceId}-account", AccessToken = "access-secret", RefreshToken = "refresh-secret" });
        await db.SaveChangesAsync();
    }

    private async Task ResetDatabaseAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.UseAppRecordTable.ExecuteDeleteAsync();
        await db.DeviceTable.ExecuteDeleteAsync();
        await db.SteamLoginTokenTable.ExecuteDeleteAsync();
    }

    private BackupService CreateService()
        => new(
            _factory,
            _paths,
            new NoWindow(),
            new RevealRegistry(),
            new AppEnvironment(false, "en-US", false, _paths, "4.2.0"),
            new DeviceService(_factory, TimeProvider.System, NullLogger<DeviceService>.Instance),
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

    private sealed class NoWindow : IMainWindowAccessor
    {
        public Task<MainWindowSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MainWindowSnapshot(null, MainWindowAvailability.Missing));
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile))
            .Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}

internal static class PathTestExtensions
{
    internal static string EnsureDirectory(this string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return file;
    }
}
