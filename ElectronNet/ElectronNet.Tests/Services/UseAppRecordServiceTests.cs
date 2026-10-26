using ElectronNet;
using ElectronNet.Models;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class UseAppRecordServiceTests
{
    private string _tempDir = null!;
    private string _databaseFile = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private TestDbContextFactory _factory = null!;
    private UseAppRecordService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-record-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _databaseFile = Path.Combine(_tempDir, "test.db");
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Persistence.SqliteConnectionStrings.Create(_databaseFile))
            .Options;
        using var context = new AppDbContext(_options);
        context.Database.Migrate();
        _factory = new TestDbContextFactory(_options);
        var deviceService = new DeviceService(_factory, TimeProvider.System, NullLogger<DeviceService>.Instance);
        _service = new UseAppRecordService(
            _factory, TimeProvider.System, deviceService, new FakeSettingsStore(),
            NullLogger<UseAppRecordService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public async Task InitDb_RecoversOpenRecordFromLastHeartbeatAndBackfillsDeviceId()
    {
        int recordId;
        using (var context = new AppDbContext(_options))
        {
            var record = new UseAppRecord
            {
                AppId = 570,
                SteamId = "s1",
                StartTime = 1000,
                EndTime = null,
                Duration = null,
                LastHeartbeat = 1600
            };
            context.UseAppRecordTable.Add(record);
            context.SaveChanges();
            recordId = record.Id;
        }

        await _service.InitDb();

        using (var db = new AppDbContext(_options))
        {
            var record = db.UseAppRecordTable.First(r => r.Id == recordId);
            record.EndTime.Should().Be(1600);
            record.Duration.Should().Be(600);
            record.EndReason.Should().Be("recovered");
            record.DeviceId.Should().NotBeNullOrEmpty();
            db.DeviceTable.Count(d => d.IsCurrent).Should().Be(1);
            record.DeviceId.Should().Be(db.DeviceTable.Single(d => d.IsCurrent).Id);
        }
    }

    [Test]
    public async Task InitDb_RecoversOpenRecordWithoutHeartbeatFromStartTime()
    {
        int recordId;
        using (var context = new AppDbContext(_options))
        {
            var record = new UseAppRecord
            {
                AppId = 570,
                SteamId = "s1",
                StartTime = 1000,
                EndTime = null,
                Duration = null,
                LastHeartbeat = null
            };
            context.UseAppRecordTable.Add(record);
            context.SaveChanges();
            recordId = record.Id;
        }

        await _service.InitDb();

        using var db = new AppDbContext(_options);
        var recovered = db.UseAppRecordTable.First(r => r.Id == recordId);
        recovered.EndTime.Should().Be(1000);
        recovered.Duration.Should().Be(0);
        recovered.EndReason.Should().Be("recovered");
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(options));
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public AppSettings GetSettings() => new()
        {
            SessionTracking = new SessionTrackingSettings
            {
                AfkEnabled = true,
                AfkThresholdMinutes = 10,
                MinDurationSeconds = 0,
                MergeGapSeconds = 0,
                ExcludedAppIds = []
            }
        };

        public Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
        public Task<bool> ResetAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
