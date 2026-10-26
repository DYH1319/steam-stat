using System.Text.Json;
using ElectronNet.Features.History;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features;

namespace ElectronNet.Services;

/// <summary>
/// 从 CSV 导入会话：预览（校验 / 重复 / 重叠检测）、提交（按批次写入，source = imported）与按批次撤销。
/// </summary>
public sealed class SessionImportService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppMetadataWriter appMetadataWriter,
    TimeProvider timeProvider,
    ILogger<SessionImportService> logger)
{
    private const string ImportedSource = "imported";
    private const int DuplicateStartToleranceSeconds = 60;

    private sealed record AnalyzedRow(ParsedSessionRow Row, string SteamId, string Status);

    private sealed record Analysis(
        SessionCsvParseResult Parsed,
        IReadOnlyList<AnalyzedRow> Rows,
        IReadOnlyList<CsvRowError> Errors);

    public SessionImportPreviewDto Preview(SessionImportRequest request)
    {
        try
        {
            var analysis = Analyze(request);
            var parsed = analysis.Parsed;
            var newRows = analysis.Rows.Where(r => r.Status == "new").ToList();
            return new SessionImportPreviewDto
            {
                FatalError = parsed.FatalError,
                Columns = parsed.Columns,
                Mapping = parsed.Mapping.Select(m => new SessionImportColumnMappingDto(m.Field, m.Column)).ToList(),
                TotalRows = parsed.TotalRows,
                ValidRows = analysis.Rows.Count,
                NewRows = newRows.Count,
                DuplicateRows = analysis.Rows.Count(r => r.Status == "duplicate"),
                OverlapRows = analysis.Rows.Count(r => r.Status == "overlap"),
                ErrorRows = analysis.Errors.Count,
                AppCount = analysis.Rows.Select(r => r.Row.AppId).Distinct().Count(),
                NewSeconds = newRows.Sum(r => r.Row.Duration),
                MinStartTime = analysis.Rows.Count == 0 ? null : analysis.Rows.Min(r => r.Row.StartTime),
                MaxEndTime = analysis.Rows.Count == 0 ? null : analysis.Rows.Max(r => r.Row.EndTime),
                Errors = analysis.Errors.Take(100)
                    .Select(e => new SessionImportRowErrorDto { Line = e.Line, Code = e.Code, Value = Truncate(e.Value, 120) })
                    .ToList(),
                Sample = analysis.Rows.Take(20).Select(r => new SessionImportRowDto
                {
                    Line = r.Row.Line,
                    AppId = r.Row.AppId,
                    AppName = r.Row.AppName,
                    SteamId = r.SteamId,
                    StartTime = r.Row.StartTime,
                    EndTime = r.Row.EndTime,
                    Duration = r.Row.Duration,
                    Note = Truncate(r.Row.Note, 120),
                    Status = r.Status
                }).ToList()
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to preview session import");
            return new SessionImportPreviewDto
            {
                FatalError = "empty",
                Columns = [],
                Mapping = [],
                TotalRows = 0,
                ValidRows = 0,
                NewRows = 0,
                DuplicateRows = 0,
                OverlapRows = 0,
                ErrorRows = 0,
                AppCount = 0,
                NewSeconds = 0,
                Errors = [],
                Sample = []
            };
        }
    }

    public async Task<SessionImportCommitResultDto> Commit(SessionImportRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var analysis = Analyze(request);
            if (analysis.Parsed.FatalError != null)
                return new SessionImportCommitResultDto { Success = false, Inserted = 0, Skipped = 0, Error = "invalidFile" };

            var skipDuplicates = request.SkipDuplicates ?? true;
            var skipOverlaps = request.SkipOverlaps ?? true;
            var rows = analysis.Rows
                .Where(r => r.Status == "new"
                    || (r.Status == "overlap" && !skipOverlaps)
                    || (r.Status == "duplicate" && !skipDuplicates))
                .ToList();
            var skipped = analysis.Rows.Count - rows.Count + analysis.Errors.Count;
            if (rows.Count == 0)
                return new SessionImportCommitResultDto { Success = false, Inserted = 0, Skipped = skipped, Error = "nothingToImport" };

            var now = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var batch = new SessionImportBatch
            {
                FileName = Path.GetFileName(request.FileName),
                CreatedAt = now,
                RowCount = rows.Count
            };
            db.SessionImportBatchTable.Add(batch);
            await db.SaveChangesAsync(cancellationToken);

            db.UseAppRecordTable.AddRange(rows.Select(r => new UseAppRecord
            {
                AppId = r.Row.AppId,
                SteamId = r.SteamId,
                StartTime = r.Row.StartTime,
                EndTime = r.Row.EndTime,
                Duration = r.Row.Duration,
                LastHeartbeat = r.Row.EndTime,
                Source = ImportedSource,
                Note = r.Row.Note,
                Tags = r.Row.Tags.Count == 0 ? null : JsonSerializer.Serialize(r.Row.Tags),
                UpdatedAt = now,
                ImportBatchId = batch.Id
            }));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await appMetadataWriter.EnsureCachedAsync(
                rows.Where(r => !string.IsNullOrWhiteSpace(r.Row.AppName))
                    .Select(r => new AppMetadata((uint)r.Row.AppId, r.Row.AppName!.Trim())),
                cancellationToken);

            logger.LogInformation("Imported {Count} sessions from CSV as batch {BatchId}", rows.Count, batch.Id);
            return new SessionImportCommitResultDto { Success = true, BatchId = batch.Id, Inserted = rows.Count, Skipped = skipped };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to commit session import");
            return new SessionImportCommitResultDto { Success = false, Inserted = 0, Skipped = 0, Error = "failed" };
        }
    }

    public IReadOnlyList<SessionImportBatchDto> GetBatches()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            var remaining = db.UseAppRecordTable.AsNoTracking()
                .Where(r => r.ImportBatchId != null && r.Source == ImportedSource)
                .GroupBy(r => r.ImportBatchId!.Value)
                .Select(g => new { BatchId = g.Key, Count = g.Count() })
                .ToDictionary(x => x.BatchId, x => x.Count);
            return db.SessionImportBatchTable.AsNoTracking()
                .OrderByDescending(b => b.CreatedAt)
                .ToList()
                .Select(b => new SessionImportBatchDto
                {
                    Id = b.Id,
                    FileName = b.FileName,
                    CreatedAt = b.CreatedAt,
                    RowCount = b.RowCount,
                    RemainingCount = remaining.GetValueOrDefault(b.Id)
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
    /// 撤销导入批次：删除该批次仍为 imported 的会话（已被合并的不动），返回删除条数
    /// </summary>
    public async Task<int> DeleteBatch(SessionImportBatchDeleteRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var deleted = await db.UseAppRecordTable
                .Where(r => r.ImportBatchId == request.Id && r.Source == ImportedSource)
                .ExecuteDeleteAsync(cancellationToken);
            await db.SessionImportBatchTable.Where(b => b.Id == request.Id).ExecuteDeleteAsync(cancellationToken);
            logger.LogInformation("Reverted import batch {BatchId}: {Count} sessions deleted", request.Id, deleted);
            return deleted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete import batch {BatchId}", request.Id);
            return 0;
        }
    }

    private Analysis Analyze(SessionImportRequest request)
    {
        var parsed = SessionCsvParser.Parse(request.Content, TimeZoneInfo.Local);
        if (parsed.FatalError != null) return new Analysis(parsed, [], []);

        var defaultSteamId = string.IsNullOrWhiteSpace(request.DefaultSteamId)
            ? null
            : SessionCsvParser.NormalizeSteamId(request.DefaultSteamId);
        var now = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var errors = parsed.Errors.ToList();
        var candidates = new List<(ParsedSessionRow Row, string SteamId)>();
        foreach (var row in parsed.Rows)
        {
            var steamId = row.SteamId ?? defaultSteamId;
            if (steamId == null) errors.Add(new CsvRowError(row.Line, "missingSteamId", null));
            else if (row.EndTime > now + 60) errors.Add(new CsvRowError(row.Line, "futureTime", null));
            else candidates.Add((row, steamId));
        }

        using var db = dbContextFactory.CreateDbContext();
        var steamIds = candidates.Select(c => c.SteamId).Distinct().ToList();
        var appIds = candidates.Select(c => c.Row.AppId).Distinct().ToList();
        var existing = db.UseAppRecordTable.AsNoTracking()
            .Where(r => r.EndTime != null && steamIds.Contains(r.SteamId) && appIds.Contains(r.AppId))
            .Select(r => new { r.SteamId, r.AppId, r.StartTime, EndTime = r.EndTime!.Value })
            .ToList()
            .GroupBy(r => (r.SteamId, r.AppId))
            .ToDictionary(g => g.Key, g => g.Select(r => (r.StartTime, r.EndTime)).ToList());

        var rows = new List<AnalyzedRow>();
        foreach (var (row, steamId) in candidates.OrderBy(c => c.Row.StartTime))
        {
            var key = (steamId, row.AppId);
            if (!existing.TryGetValue(key, out var spans))
            {
                spans = [];
                existing[key] = spans;
            }

            var status = spans.Any(s => Math.Abs(s.StartTime - row.StartTime) <= DuplicateStartToleranceSeconds)
                ? "duplicate"
                : spans.Any(s => s.StartTime < row.EndTime && s.EndTime > row.StartTime) ? "overlap" : "new";
            rows.Add(new AnalyzedRow(row, steamId, status));
            spans.Add((row.StartTime, row.EndTime));
        }

        return new Analysis(parsed, rows.OrderBy(r => r.Row.Line).ToList(), errors.OrderBy(e => e.Line).ToList());
    }

    private static string? Truncate(string? value, int length)
        => value == null || value.Length <= length ? value : value[..length];
}
