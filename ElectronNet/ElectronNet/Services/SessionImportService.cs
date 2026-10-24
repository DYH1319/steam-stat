using System.Text;
using System.Text.Json;
using ElectronNet.Features.History;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Services;

/// <summary>
/// CSV 会话导入：文件令牌登记、预览与提交、批次管理
/// </summary>
public sealed class SessionImportService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<SessionImportService> logger)
{
    private const long MaxFileBytes = 20 * 1024 * 1024;
    private const int MaxRegisteredFiles = 10;
    private const int MaxReturnedErrors = 100;
    private const int PreviewRowCount = 20;

    private readonly object _fileGate = new();
    private readonly List<(string Token, string Path)> _registeredFiles = [];

    /// <summary>
    /// 将用户选择的文件路径登记为短期令牌
    /// </summary>
    public SessionImportFileDto RegisterFile(string path)
    {
        var token = Guid.NewGuid().ToString("N");
        lock (_fileGate)
        {
            _registeredFiles.Add((token, path));
            while (_registeredFiles.Count > MaxRegisteredFiles)
            {
                _registeredFiles.RemoveAt(0);
            }
        }
        return new SessionImportFileDto
        {
            Token = token,
            FileName = Path.GetFileName(path),
            SizeBytes = new FileInfo(path).Length
        };
    }

    /// <summary>
    /// 解析并（可选）提交一次 CSV 导入
    /// </summary>
    public async Task<SessionImportResultDto> ImportAsync(
        SessionImportRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string? path;
            lock (_fileGate)
            {
                path = _registeredFiles.FirstOrDefault(f => f.Token == request.Token).Path;
            }
            if (path == null || !File.Exists(path))
            {
                return EmptyResult("fileNotFound");
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaxFileBytes)
            {
                return EmptyResult("fileTooLarge");
            }

            string text;
            await using (var stream = fileInfo.OpenRead())
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                text = await reader.ReadToEndAsync(cancellationToken);
            }

            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var parsed = SessionCsvParser.Parse(text, request.DefaultSteamId, TimeZoneInfo.Local, now);
            if (parsed.Error != null)
            {
                return EmptyResult(parsed.Error);
            }

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.UseAppRecordTable.AsNoTracking()
                .Where(r => r.EndTime != null)
                .Select(r => new { r.SteamId, r.AppId, r.StartTime, End = r.EndTime!.Value })
                .ToListAsync(cancellationToken);
            var existingByPair = existing
                .GroupBy(r => (r.SteamId, r.AppId))
                .ToDictionary(g => g.Key, g => g.ToList());

            var appNames = (await db.SteamAppTable.AsNoTracking()
                    .Select(a => new { a.AppId, a.Name })
                    .ToListAsync(cancellationToken))
                .GroupBy(a => a.AppId)
                .ToDictionary(g => g.Key, g => g.First().Name);

            var seenInFile = new HashSet<(string SteamId, int AppId, int Start, int End)>();
            var flagged = new List<(ParsedSessionRow Row, bool Duplicate)>(parsed.Rows.Count);
            foreach (var row in parsed.Rows)
            {
                var duplicate = !seenInFile.Add((row.SteamId, row.AppId, row.Start, row.End));
                if (!duplicate && existingByPair.TryGetValue((row.SteamId, row.AppId), out var sessions))
                {
                    duplicate = sessions.Any(s => row.Start < s.End && row.End > s.StartTime);
                }
                flagged.Add((row, duplicate));
            }

            var valid = flagged.Count;
            var duplicates = flagged.Count(f => f.Duplicate);
            var importable = flagged
                .Where(f => !request.SkipDuplicates || !f.Duplicate)
                .Select(f => f.Row)
                .ToList();

            var batchId = Guid.NewGuid().ToString("N");
            var imported = 0;
            if (!request.DryRun && importable.Count > 0)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                db.SessionImportBatchTable.Add(new SessionImportBatch
                {
                    Id = batchId,
                    FileName = Path.GetFileName(path),
                    ImportedAt = (int)now,
                    RowCount = importable.Count
                });
                foreach (var row in importable)
                {
                    db.UseAppRecordTable.Add(new UseAppRecord
                    {
                        AppId = row.AppId,
                        SteamId = row.SteamId,
                        StartTime = row.Start,
                        EndTime = row.End,
                        Duration = row.Duration,
                        LastHeartbeat = row.End,
                        IdleDuration = 0,
                        ForegroundDuration = null,
                        DeviceId = "imported",
                        Source = "imported",
                        EndReason = null,
                        Note = row.Note,
                        Tags = row.Tags.Count > 0 ? JsonSerializer.Serialize(row.Tags) : null,
                        ImportBatchId = batchId
                    });
                }
                imported = importable.Count;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            return new SessionImportResultDto
            {
                Total = parsed.Rows.Count + parsed.Errors.Count,
                Valid = valid,
                Duplicates = duplicates,
                Imported = request.DryRun ? 0 : imported,
                ErrorCount = parsed.Errors.Count,
                Errors = parsed.Errors.Take(MaxReturnedErrors)
                    .Select(e => new SessionImportRowErrorDto { Line = e.Line, Code = e.Code })
                    .ToList(),
                Preview = flagged.Take(PreviewRowCount)
                    .Select(f => new SessionImportPreviewRowDto
                    {
                        SteamId = f.Row.SteamId,
                        AppId = f.Row.AppId,
                        AppName = appNames.TryGetValue(f.Row.AppId, out var name) ? name : null,
                        StartTime = f.Row.Start,
                        EndTime = f.Row.End,
                        Duration = f.Row.Duration,
                        Duplicate = f.Duplicate
                    })
                    .ToList(),
                BatchId = request.DryRun ? null : (imported > 0 ? batchId : null)
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to import session CSV");
            return EmptyResult("importFailed");
        }
    }

    private static SessionImportResultDto EmptyResult(string error) => new()
    {
        Total = 0,
        Valid = 0,
        Duplicates = 0,
        Imported = 0,
        ErrorCount = 0,
        Errors = [],
        Preview = [],
        Error = error
    };

    /// <summary>
    /// 导入批次列表（新到旧），附当前仍存在的行数
    /// </summary>
    public IReadOnlyList<SessionImportBatchDto> GetBatches()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            var batches = db.SessionImportBatchTable.AsNoTracking().ToList();
            var counts = db.UseAppRecordTable.AsNoTracking()
                .Where(r => r.ImportBatchId != null)
                .GroupBy(r => r.ImportBatchId!)
                .Select(g => new { BatchId = g.Key, Count = g.Count() })
                .ToList()
                .ToDictionary(g => g.BatchId, g => g.Count);
            return batches
                .OrderByDescending(b => b.ImportedAt)
                .Select(b => new SessionImportBatchDto
                {
                    Id = b.Id,
                    FileName = b.FileName,
                    ImportedAt = b.ImportedAt,
                    RowCount = b.RowCount,
                    RemainingCount = counts.TryGetValue(b.Id, out var count) ? count : 0
                })
                .ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query session import batches");
            return [];
        }
    }

    /// <summary>
    /// 删除批次及其导入的会话行，返回删除的行数
    /// </summary>
    public async Task<int> DeleteBatchAsync(
        SessionImportBatchDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var rows = await db.UseAppRecordTable
                .Where(r => r.ImportBatchId == request.BatchId)
                .ToListAsync(cancellationToken);
            var batch = await db.SessionImportBatchTable
                .FirstOrDefaultAsync(b => b.Id == request.BatchId, cancellationToken);
            db.UseAppRecordTable.RemoveRange(rows);
            if (batch != null) db.SessionImportBatchTable.Remove(batch);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rows.Count;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete session import batch {BatchId}", request.BatchId);
            return 0;
        }
    }
}
