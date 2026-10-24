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
public sealed class SessionImportServiceTests
{
    private string _tempDir = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private SessionImportService _service = null!;

    private const string SteamId = "76561198012345678";

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Persistence.SqliteConnectionStrings.Create(Path.Combine(_tempDir, "test.db")))
            .Options;
        using var context = new AppDbContext(_options);
        context.Database.Migrate();
        _service = new SessionImportService(
            new TestDbContextFactory(_options), TimeProvider.System, NullLogger<SessionImportService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteCsv(string content)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        return path;
    }

    private List<UseAppRecord> GetRecords()
    {
        using var context = new AppDbContext(_options);
        return context.UseAppRecordTable.AsNoTracking().OrderBy(r => r.StartTime).ToList();
    }

    [Test]
    public async Task Import_DryRunInsertsNothing()
    {
        var file = _service.RegisterFile(WriteCsv(
            $"appId,steamId,start,end\n570,{SteamId},1700000000,1700003600\n"));

        var result = await _service.ImportAsync(new SessionImportRequest
        {
            Token = file.Token,
            DryRun = true,
            SkipDuplicates = true
        });

        result.Error.Should().BeNull();
        result.Valid.Should().Be(1);
        result.Imported.Should().Be(0);
        result.BatchId.Should().BeNull();
        result.Preview.Should().ContainSingle().Which.Duplicate.Should().BeFalse();
        GetRecords().Should().BeEmpty();
        _service.GetBatches().Should().BeEmpty();
    }

    [Test]
    public async Task Import_CommitsRowsWithImportedSourceAndDevice()
    {
        var file = _service.RegisterFile(WriteCsv(
            $"appId,steamId,start,end,note,tags\n" +
            $"570,{SteamId},1700000000,1700003600,hello,a|b\n"));

        var result = await _service.ImportAsync(new SessionImportRequest
        {
            Token = file.Token,
            DryRun = false,
            SkipDuplicates = true
        });

        result.Error.Should().BeNull();
        result.Imported.Should().Be(1);
        result.BatchId.Should().NotBeNull();

        var record = GetRecords().Should().ContainSingle().Subject;
        record.Source.Should().Be("imported");
        record.DeviceId.Should().Be("imported");
        record.ImportBatchId.Should().Be(result.BatchId);
        record.EndReason.Should().BeNull();
        record.LastHeartbeat.Should().Be(record.EndTime);
        record.IdleDuration.Should().Be(0);
        record.ForegroundDuration.Should().BeNull();
        record.Note.Should().Be("hello");
        record.Tags.Should().Be("""["a","b"]""");

        var batches = _service.GetBatches();
        batches.Should().ContainSingle();
        batches[0].RowCount.Should().Be(1);
        batches[0].RemainingCount.Should().Be(1);
    }

    [Test]
    public async Task Import_SkipsDuplicateRows()
    {
        using (var context = new AppDbContext(_options))
        {
            context.UseAppRecordTable.Add(new UseAppRecord
            {
                AppId = 570,
                SteamId = SteamId,
                StartTime = 1700000000,
                EndTime = 1700003600,
                Duration = 3600,
                Source = "auto"
            });
            context.SaveChanges();
        }

        var file = _service.RegisterFile(WriteCsv(
            $"appId,steamId,start,end\n" +
            $"570,{SteamId},1700001000,1700002000\n" +   // overlaps existing
            $"570,{SteamId},1700010000,1700013600\n" +   // new
            $"570,{SteamId},1700010000,1700013600\n"));  // identical earlier row

        var result = await _service.ImportAsync(new SessionImportRequest
        {
            Token = file.Token,
            DryRun = false,
            SkipDuplicates = true
        });

        result.Valid.Should().Be(3);
        result.Duplicates.Should().Be(2);
        result.Imported.Should().Be(1);
        GetRecords().Should().HaveCount(2);
    }

    [Test]
    public async Task Import_BadToken_ReturnsFileNotFound()
    {
        var result = await _service.ImportAsync(new SessionImportRequest
        {
            Token = "nonexistent",
            DryRun = true,
            SkipDuplicates = true
        });
        result.Error.Should().Be("fileNotFound");
    }

    [Test]
    public async Task DeleteBatch_RemovesRowsAndBatch()
    {
        var file = _service.RegisterFile(WriteCsv(
            $"appId,steamId,start,end\n570,{SteamId},1700000000,1700003600\n"));
        var result = await _service.ImportAsync(new SessionImportRequest
        {
            Token = file.Token,
            DryRun = false,
            SkipDuplicates = true
        });

        var deleted = await _service.DeleteBatchAsync(
            new SessionImportBatchDeleteRequest { BatchId = result.BatchId! });

        deleted.Should().Be(1);
        GetRecords().Should().BeEmpty();
        _service.GetBatches().Should().BeEmpty();
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(options));
    }
}
