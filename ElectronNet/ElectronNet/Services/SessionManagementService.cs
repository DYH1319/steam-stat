using System.Text.Json;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Services;

/// <summary>
/// 会话管理：对原始 use_app_record 行提供查询/编辑/合并/删除（不应用清洗规则）。
/// </summary>
public sealed class SessionManagementService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<SessionManagementService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string DeriveStatus(UseAppRecord record)
        => record.EndTime == null ? "ongoing" : record.Duration < 0 ? "discarded" : "completed";

    private static IReadOnlyList<string> ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(tags, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 分页查询会话列表
    /// </summary>
    public SessionListDto GetList(SessionListQueryRequest request)
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            var query = db.UseAppRecordTable
                .Where(r => request.SteamIds == null || request.SteamIds.Count == 0 || request.SteamIds.Contains(r.SteamId))
                .Where(r => request.AppIds == null || request.AppIds.Count == 0 || request.AppIds.Contains(r.AppId))
                .Where(r => request.StartDate == null || r.StartTime >= request.StartDate)
                .Where(r => request.EndDate == null || r.StartTime <= request.EndDate);

            var rows = query
                // ReSharper disable EntityFramework.UnsupportedServerSideFunctionCall
                .LeftJoin(
                    db.SteamAppTable,
                    record => record.AppId,
                    app => app.AppId,
                    (record, app) => new { record, app })
                .LeftJoin(
                    db.SteamUserTable,
                    x => x.record.SteamId,
                    user => user.SteamId,
                    (x, user) => new { x.record, x.app, user })
                .LeftJoin(
                    db.DeviceTable,
                    x => x.record.DeviceId,
                    device => device.Id,
                    (x, device) => new { x.record, x.app, x.user, device })
                // ReSharper restore EntityFramework.UnsupportedServerSideFunctionCall
                .AsNoTracking()
                .ToList();

            var items = rows
                .Select(x => ToDto(x.record, x.app, x.user, x.device))
                .Where(dto => request.Status == null || request.Status == "all" || dto.Status == request.Status)
                .Where(dto => request.Tag == null ||
                    dto.Tags.Any(tag => string.Equals(tag, request.Tag, StringComparison.OrdinalIgnoreCase)))
                .Where(dto => MatchesKeyword(dto, request.Keyword))
                .ToList();

            var sortField = request.SortField ?? "startTime";
            var ascending = request.SortOrder == "asc";
            items = sortField switch
            {
                "duration" => Sort(items, dto => dto.Duration, ascending),
                "idleDuration" => Sort(items, dto => dto.IdleDuration, ascending),
                _ => Sort(items, dto => (long?)dto.StartTime, ascending)
            };

            var total = items.Count;
            var page = items
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToList();
            return new SessionListDto(page, total);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query session list");
            return new SessionListDto([], 0);
        }
    }

    private static List<SessionRecordDto> Sort<TKey>(List<SessionRecordDto> items, Func<SessionRecordDto, TKey?> key, bool ascending)
    {
        // nulls last
        return ascending
            ? items.OrderBy(dto => key(dto) == null ? 1 : 0).ThenBy(key).ToList()
            : items.OrderBy(dto => key(dto) == null ? 1 : 0).ThenByDescending(key).ToList();
    }

    private static bool MatchesKeyword(SessionRecordDto dto, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        var comparison = StringComparison.OrdinalIgnoreCase;
        return (dto.AppName?.Contains(keyword, comparison) ?? false)
            || (dto.AppNameLocalized?.Contains(keyword, comparison) ?? false)
            || dto.AppId.ToString().Contains(keyword, comparison)
            || (dto.Note?.Contains(keyword, comparison) ?? false)
            || dto.Tags.Any(tag => tag.Contains(keyword, comparison));
    }

    private static SessionRecordDto ToDto(UseAppRecord record, SteamApp? app, SteamUser? user, Device? device) => new()
    {
        Id = record.Id,
        AppId = record.AppId,
        SteamId = record.SteamId,
        StartTime = record.StartTime,
        EndTime = record.EndTime,
        Duration = record.Duration,
        IdleDuration = record.IdleDuration,
        ForegroundDuration = record.ForegroundDuration,
        DeviceId = record.DeviceId,
        DeviceName = device?.Name,
        Source = record.Source,
        Status = DeriveStatus(record),
        EndReason = record.EndReason,
        Note = record.Note,
        Tags = ParseTags(record.Tags),
        ImportBatchId = record.ImportBatchId,
        AppName = app?.Name,
        AppNameLocalized = app?.NameLocalizedJson,
        UserPersonaName = user?.PersonaName,
        UpdatedAt = record.UpdatedAt
    };

    /// <summary>
    /// 编辑会话时间范围、备注与标签
    /// </summary>
    public async Task<SessionMutationResultDto> Update(SessionUpdateRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var record = await db.UseAppRecordTable.FirstOrDefaultAsync(r => r.Id == request.Id, cancellationToken);
            if (record == null) return new SessionMutationResultDto { Success = false, Error = "notFound" };
            if (record.EndTime == null) return new SessionMutationResultDto { Success = false, Error = "ongoing" };

            var now = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (request.StartTime >= request.EndTime || request.EndTime > now + 60)
                return new SessionMutationResultDto { Success = false, Error = "invalidRange" };

            record.StartTime = request.StartTime;
            record.EndTime = request.EndTime;
            record.Duration = request.EndTime - request.StartTime;
            record.IdleDuration = Math.Clamp(record.IdleDuration, 0, Math.Max(0, record.Duration.Value));
            record.ForegroundDuration = record.ForegroundDuration == null
                ? null
                : Math.Clamp(record.ForegroundDuration.Value, 0, Math.Max(0, record.Duration.Value));

            var note = request.Note?.Trim();
            record.Note = string.IsNullOrEmpty(note) ? null : note;
            record.Tags = request.Tags == null ? record.Tags : JsonSerializer.Serialize(NormalizeTags(request.Tags));
            record.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken);
            return new SessionMutationResultDto { Success = true };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update session {RecordId}", request.Id);
            return new SessionMutationResultDto { Success = false, Error = "notFound" };
        }
    }

    internal static List<string> NormalizeTags(IEnumerable<string> tags)
        => tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Select(tag => tag.Length > 32 ? tag[..32] : tag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

    /// <summary>
    /// 合并多条会话到最早的一条
    /// </summary>
    public async Task<SessionMutationResultDto> Merge(SessionIdsRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var ids = request.Ids.Distinct().ToList();
            if (ids.Count < 2) return new SessionMutationResultDto { Success = false, Error = "tooFew" };

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var records = await db.UseAppRecordTable.Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
            if (records.Count != ids.Count) return new SessionMutationResultDto { Success = false, Error = "notFound" };
            if (records.Any(r => r.EndTime == null)) return new SessionMutationResultDto { Success = false, Error = "ongoing" };
            if (records.Any(r => r.Duration < 0)) return new SessionMutationResultDto { Success = false, Error = "discarded" };
            if (records.Select(r => (r.SteamId, r.AppId)).Distinct().Count() > 1)
                return new SessionMutationResultDto { Success = false, Error = "mismatch" };

            var keep = records.OrderBy(r => r.StartTime).ThenBy(r => r.Id).First();
            var latest = records.OrderByDescending(r => r.EndTime).First();
            var others = records.Where(r => r.Id != keep.Id).ToList();

            keep.StartTime = records.Min(r => r.StartTime);
            keep.EndTime = records.Max(r => r.EndTime);
            keep.Duration = records.Sum(r => r.Duration ?? 0);
            keep.IdleDuration = records.Sum(r => r.IdleDuration);
            keep.ForegroundDuration = records.Any(r => r.ForegroundDuration == null)
                ? null
                : records.Sum(r => r.ForegroundDuration!.Value);
            keep.Note = string.Join("\n", records
                .OrderBy(r => r.StartTime)
                .Select(r => r.Note?.Trim())
                .Where(note => !string.IsNullOrEmpty(note)));
            if (keep.Note.Length == 0) keep.Note = null;
            keep.Tags = JsonSerializer.Serialize(NormalizeTags(records.SelectMany(r => ParseTags(r.Tags))));
            keep.Source = "merged";
            keep.ImportBatchId = null;
            keep.EndReason = latest.EndReason;
            keep.UpdatedAt = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();

            db.UseAppRecordTable.RemoveRange(others);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SessionMutationResultDto { Success = true };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to merge sessions");
            return new SessionMutationResultDto { Success = false, Error = "notFound" };
        }
    }

    /// <summary>
    /// 删除指定会话（跳过仍在进行中的），返回删除条数
    /// </summary>
    public async Task<int> Delete(SessionIdsRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var records = await db.UseAppRecordTable
                .Where(r => request.Ids.Contains(r.Id) && r.EndTime != null)
                .ToListAsync(cancellationToken);
            db.UseAppRecordTable.RemoveRange(records);
            await db.SaveChangesAsync(cancellationToken);
            return records.Count;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete sessions");
            return 0;
        }
    }

    /// <summary>
    /// 所有出现过的标签（去重排序）
    /// </summary>
    public IReadOnlyList<string> GetTags()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            return db.UseAppRecordTable.AsNoTracking()
                .Where(r => r.Tags != null)
                .Select(r => r.Tags)
                .ToList()
                .SelectMany(ParseTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query session tags");
            return [];
        }
    }

    /// <summary>
    /// 有会话记录的应用列表
    /// </summary>
    public IReadOnlyList<SessionAppDto> GetApps()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            return db.UseAppRecordTable.AsNoTracking()
                .GroupBy(r => r.AppId)
                .Select(g => new { AppId = g.Key, SessionCount = g.Count() })
                .ToList()
                // ReSharper disable once EntityFramework.UnsupportedServerSideFunctionCall
                .GroupJoin(
                    db.SteamAppTable.AsNoTracking().ToList(),
                    g => g.AppId,
                    app => app.AppId,
                    (g, apps) => new { g.AppId, g.SessionCount, App = apps.FirstOrDefault() })
                .Select(x => new SessionAppDto
                {
                    AppId = x.AppId,
                    AppName = x.App?.Name,
                    AppNameLocalized = x.App?.NameLocalizedJson,
                    SessionCount = x.SessionCount
                })
                .OrderBy(dto => dto.AppName ?? dto.AppNameLocalized ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query session apps");
            return [];
        }
    }
}
