using ElectronNet;
using ElectronNet.Models;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class SessionManagementServiceTests
{
    private string _tempDir = null!;
    private string _databaseFile = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private SessionManagementService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _databaseFile = Path.Combine(_tempDir, "test.db");
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Persistence.SqliteConnectionStrings.Create(_databaseFile))
            .Options;
        using var context = new AppDbContext(_options);
        context.Database.Migrate();
        _service = new SessionManagementService(
            new TestDbContextFactory(_options), TimeProvider.System, NullLogger<SessionManagementService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private int AddRecord(int appId, string steamId, int start, int? end, int? duration,
        long idle = 0, long? foreground = 0, string? note = null, string? tags = null,
        string source = "auto", string? endReason = "exited")
    {
        using var context = new AppDbContext(_options);
        var record = new UseAppRecord
        {
            AppId = appId,
            SteamId = steamId,
            StartTime = start,
            EndTime = end,
            Duration = duration,
            IdleDuration = idle,
            ForegroundDuration = foreground,
            Note = note,
            Tags = tags,
            Source = source,
            EndReason = endReason
        };
        context.UseAppRecordTable.Add(record);
        context.SaveChanges();
        return record.Id;
    }

    private UseAppRecord GetRecord(int id)
    {
        using var context = new AppDbContext(_options);
        return context.UseAppRecordTable.AsNoTracking().First(r => r.Id == id);
    }

    [Test]
    public async Task Merge_CombinesSameAppRowsIntoEarliest()
    {
        var first = AddRecord(570, "s1", 100, 200, 100, idle: 10, foreground: 60, note: "a", tags: """["x","y"]""");
        var second = AddRecord(570, "s1", 300, 420, 120, idle: 5, foreground: 70, note: "b", tags: """["y","z"]""", endReason: "suspended");
        var third = AddRecord(730, "s1", 0, 50, 50);

        var result = await _service.Merge(new SessionIdsRequest { Ids = [first, second] });

        result.Success.Should().BeTrue();
        var merged = GetRecord(first);
        merged.StartTime.Should().Be(100);
        merged.EndTime.Should().Be(420);
        merged.Duration.Should().Be(220);
        merged.IdleDuration.Should().Be(15);
        merged.ForegroundDuration.Should().Be(130);
        merged.Note.Should().Be("a\nb");
        merged.Source.Should().Be("merged");
        merged.EndReason.Should().Be("suspended");
        merged.UpdatedAt.Should().NotBeNull();
        using (var context = new AppDbContext(_options))
        {
            context.UseAppRecordTable.Count(r => r.Id == second).Should().Be(0);
            context.UseAppRecordTable.Count(r => r.Id == third).Should().Be(1);
        }
    }

    [Test]
    public async Task Merge_NullsForegroundWhenAnyRowUnknown()
    {
        var first = AddRecord(570, "s1", 100, 200, 100, foreground: 60);
        var second = AddRecord(570, "s1", 300, 400, 100, foreground: null);

        var result = await _service.Merge(new SessionIdsRequest { Ids = [first, second] });

        result.Success.Should().BeTrue();
        GetRecord(first).ForegroundDuration.Should().BeNull();
    }

    [Test]
    public async Task Merge_RejectsMismatchAndOngoing()
    {
        var otherApp = AddRecord(730, "s1", 100, 200, 100);
        var own = AddRecord(570, "s1", 100, 200, 100);
        var ongoing = AddRecord(570, "s1", 300, null, null);

        (await _service.Merge(new SessionIdsRequest { Ids = [own, otherApp] }))
            .Error.Should().Be("mismatch");
        (await _service.Merge(new SessionIdsRequest { Ids = [own, ongoing] }))
            .Error.Should().Be("ongoing");
        (await _service.Merge(new SessionIdsRequest { Ids = [own] }))
            .Error.Should().Be("tooFew");
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(options));
    }
}
