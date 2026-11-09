using ElectronNet;
using ElectronNet.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Environment;

namespace ElectronNet.Tests.Services;

[TestFixture]
public class DatabaseSnapshotterTests
{
    private string _tempDir = null!;
    private AppPaths _appPaths = null!;
    private TestDbContextFactory _factory = null!;
    private int _keep = 7;

    [SetUp]
    public async Task SetUp()
    {
        _keep = 7;
        _tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-snapshot-tests", Guid.NewGuid().ToString("N"));
        _appPaths = new AppPaths(_tempDir);
        Directory.CreateDirectory(_appPaths.DatabaseDirectory);
        _factory = new TestDbContextFactory(_appPaths.DatabaseFile);
        await using var context = await _factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    [Test]
    public async Task Snapshot_CreatesUsableBackupUnderBackupsDirectory()
    {
        await CreateSnapshotter().SnapshotAsync();

        var backups = SnapshotFiles();
        backups.Should().HaveCount(1);
        File.Exists($"{backups[0]}.tmp").Should().BeFalse();

        await using var backup = CreateContext(backups[0]);
        (await backup.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty();
    }

    [Test]
    public async Task Snapshot_SkipsWhenRecentSnapshotExists()
    {
        var snapshotter = CreateSnapshotter();

        await snapshotter.SnapshotAsync();
        await snapshotter.SnapshotAsync();

        // 第二份与第一份间隔不足 1 小时，应被跳过（崩溃循环不能把好快照挤掉）
        SnapshotFiles().Should().HaveCount(1);
    }

    [Test]
    public async Task Snapshot_PrunesToKeepOnlyNewest()
    {
        var backupDir = Path.Combine(_appPaths.DatabaseDirectory, "backups");
        Directory.CreateDirectory(backupDir);
        for (var i = 0; i < 10; i++)
        {
            var file = Path.Combine(backupDir, $"steam-stat-20200101000{i:d2}.bak");
            await File.WriteAllTextAsync(file, "stale");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-i - 2));
        }

        await CreateSnapshotter().SnapshotAsync();

        SnapshotFiles().Should().HaveCount(7);
    }

    [Test]
    public async Task Snapshot_KeepsTheConfiguredNumber()
    {
        _keep = 3;
        var backupDir = Path.Combine(_appPaths.DatabaseDirectory, "backups");
        Directory.CreateDirectory(backupDir);
        for (var i = 0; i < 5; i++)
        {
            var file = Path.Combine(backupDir, $"steam-stat-20200101000{i:d2}.bak");
            await File.WriteAllTextAsync(file, "stale");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-i - 2));
        }

        await CreateSnapshotter().SnapshotAsync();

        SnapshotFiles().Should().HaveCount(3);
    }

    [Test]
    public async Task Snapshot_RunsOnEveryLaunchOnceTheLastIsAnHourOld()
    {
        var snapshotter = CreateSnapshotter();
        await snapshotter.SnapshotAsync();
        File.SetLastWriteTimeUtc(SnapshotFiles()[0], DateTime.UtcNow.AddMinutes(-61));
        await Task.Delay(1100);

        await snapshotter.SnapshotAsync();

        SnapshotFiles().Should().HaveCount(2);
    }

    [Test]
    public async Task IsHealthy_RejectsADamagedFile()
    {
        var file = Path.Combine(_tempDir, "damaged.bak");
        await File.WriteAllBytesAsync(file, new byte[8192]);

        (await DatabaseSnapshotter.IsHealthyAsync(file)).Should().BeFalse();
        (await DatabaseSnapshotter.IsHealthyAsync(_appPaths.DatabaseFile)).Should().BeTrue();
    }

    [Test]
    public async Task List_NamesTheKindAndResolve_OnlyAcceptsListedFiles()
    {
        var snapshotter = CreateSnapshotter();
        await snapshotter.SnapshotAsync();
        var backupDir = Path.Combine(_appPaths.DatabaseDirectory, "backups");
        await File.WriteAllTextAsync(Path.Combine(backupDir, "pre-restore-20260101-000000.bak"), "x");
        await File.WriteAllTextAsync(Path.Combine(backupDir, "unrelated.bak"), "x");

        var listed = snapshotter.List();

        listed.Select(snapshot => snapshot.Kind).Should().BeEquivalentTo(["startup", "preRestore"]);
        snapshotter.Resolve(listed[0].Name).Should().NotBeNull();
        snapshotter.Resolve("unrelated.bak").Should().BeNull();
        snapshotter.Resolve("../steam-stat.db").Should().BeNull();
        snapshotter.Resolve("../../Settings/settings.json").Should().BeNull();
    }

    private sealed class StubSettingsStore(int keep) : SteamStat.Core.Settings.ISettingsStore
    {
        public SteamStat.Core.Settings.AppSettings GetSettings() => new()
        {
            Archive = new SteamStat.Core.Settings.ArchiveSettings { StartupSnapshotKeep = keep }
        };

        public Task<bool> UpdateAsync(SteamStat.Core.Settings.AppSettings partialSettings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ResetAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private DatabaseSnapshotter CreateSnapshotter()
        => new(_factory, _appPaths, new StubSettingsStore(_keep), NullLogger<DatabaseSnapshotter>.Instance);

    private string[] SnapshotFiles()
    {
        var backupDir = Path.Combine(_appPaths.DatabaseDirectory, "backups");
        return Directory.Exists(backupDir) ? Directory.GetFiles(backupDir, "steam-stat-*.bak") : [];
    }

    private static AppDbContext CreateContext(string databaseFile)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile, pooling: false))
            .Options;
        return new AppDbContext(options);
    }

    private sealed class TestDbContextFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile))
            .Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
