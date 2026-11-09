using System.Globalization;
using ElectronNet.Features.Semantic;
using ElectronNet.Features.UsageTracking;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 可解释的数字：一个时长由哪些原始记录算出、中间扣掉了什么、每条记录的可信程度。
/// 结果用语义视图算，和统计页同一口径；扣除明细来自原始表。
/// 证据标记以后和时光机共用：recorded 实测，recovered 按心跳推算结束时间，imported 用户导入，edited 手动改过。
/// </summary>
internal sealed class StatsExplainService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    SemanticLayerService semanticLayer,
    ISettingsStore settingsStore,
    TimeProvider timeProvider)
{
    private const int DefaultLimit = 100;

    /// <summary>
    /// Electron.NET 桥会把形如日期的字符串转成 DateTimeOffset，到这里已经变成
    /// 2026-01-01T00:00:00+08:00 这样的文本，前 10 位才是渲染层传来的逻辑日。
    /// </summary>
    internal static string? NormalizeDay(string? day) => day is { Length: > 10 } ? day[..10] : day;

    public async Task<StatsExplainDto> ExplainAsync(StatsExplainRequest request, CancellationToken cancellationToken = default)
    {
        request = request with { FromDay = NormalizeDay(request.FromDay), ToDay = NormalizeDay(request.ToDay) };
        var settings = settingsStore.GetSettings();
        var boundary = settings.Analytics?.DayBoundaryHour ?? 4;
        var rules = new SessionCleaningRules(
            settings.SessionTracking?.MinDurationSeconds ?? 0,
            settings.SessionTracking?.MergeGapSeconds ?? 0,
            (settings.SessionTracking?.ExcludedAppIds ?? []).ToHashSet());
        var hidden = (settings.Analytics?.HiddenAppIds ?? []).ToHashSet();
        var range = ToRange(request.FromDay, request.ToDay, boundary);
        var clipped = request.ClipToRange == true;
        long Clip(UseAppRecord record) => clipped ? ClipToRange(record, range) : InRangeByStart(record, range);

        var (total, effective, sessions) = await SumViewAsync(request, cancellationToken).ConfigureAwait(false);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var steamIds = request.SteamIds is { Count: > 0 } ids ? ids : null;
        var appIds = request.AppIds is { Count: > 0 } apps ? apps : null;
        var query = db.UseAppRecordTable.AsNoTracking();
        if (steamIds != null) query = query.Where(record => steamIds.Contains(record.SteamId));
        if (appIds != null) query = query.Where(record => appIds.Contains(record.AppId));
        var records = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var open = records.Where(record => record.EndTime == null && Overlaps(record.StartTime, Math.Max(record.LastHeartbeat ?? record.StartTime, record.StartTime), range)).ToList();
        var closed = records.Where(record => record is { EndTime: not null, Duration: > 0 }).ToList();
        var inRange = closed.Where(record => Clip(record) > 0).ToList();

        var excluded = inRange.Where(record => rules.ExcludedAppIds.Contains(record.AppId)).ToList();
        var hiddenRecords = inRange.Where(record => !rules.ExcludedAppIds.Contains(record.AppId) && hidden.Contains(record.AppId)).ToList();
        var counted = inRange.Except(excluded).Except(hiddenRecords).ToList();

        // 最短会话按合并之后的时长判断，所以要用全部历史跑一遍合并，再找出被过滤掉的记录
        var merged = SessionCleaner.Apply(
            closed.Where(record => !rules.ExcludedAppIds.Contains(record.AppId) && !hidden.Contains(record.AppId)).Select(ToDto),
            rules with { MinDurationSeconds = 0 });
        var dropped = merged
            .Where(session => session.Duration < rules.MinDurationSeconds)
            .ToLookup(session => (session.SteamId, session.AppId));
        var below = counted.Where(record => dropped[(record.SteamId, record.AppId)].Any(session =>
            session.StartTime <= record.StartTime && record.StartTime <= session.EndTime)).ToList();
        var kept = counted.Except(below).ToList();

        var deductions = new List<ExplainDeductionDto>();
        void Deduct(string kind, int count, long seconds)
        {
            if (count > 0) deductions.Add(new ExplainDeductionDto { Kind = kind, Records = count, Seconds = seconds });
        }
        Deduct("excluded", excluded.Count, excluded.Sum(record => Clip(record)));
        Deduct("hidden", hiddenRecords.Count, hiddenRecords.Sum(record => Clip(record)));
        Deduct("belowMinimum", below.Count, below.Sum(record => Clip(record)));
        Deduct("idle", kept.Count(record => record.IdleDuration > 0), total - effective);
        Deduct("open", open.Count, 0);

        var limit = request.Limit ?? DefaultLimit;
        var names = await LookupNamesAsync(db, kept, cancellationToken).ConfigureAwait(false);
        var devices = await db.DeviceTable.AsNoTracking()
            .ToDictionaryAsync(device => device.Id, device => device.Name, cancellationToken).ConfigureAwait(false);

        return new StatsExplainDto
        {
            TotalSeconds = total,
            EffectiveSeconds = effective,
            SessionCount = sessions,
            RawSeconds = inRange.Sum(record => Clip(record)),
            RawRecords = inRange.Count,
            MergedRecords = Math.Max(0, kept.Count - sessions),
            Deductions = deductions,
            Sources = kept
                .GroupBy(EvidenceOf)
                .Select(group => new ExplainSourceDto { Evidence = group.Key, Records = group.Count(), Seconds = group.Sum(record => Clip(record)) })
                .OrderByDescending(source => source.Seconds)
                .ToList(),
            Sessions = kept
                .OrderByDescending(record => Clip(record))
                .Take(limit)
                .Select(record => new ExplainSessionDto
                {
                    RecordId = record.Id,
                    AppId = record.AppId,
                    AppName = names.GetValueOrDefault(record.AppId),
                    SteamId = record.SteamId,
                    StartTime = record.StartTime,
                    EndTime = record.EndTime ?? record.StartTime,
                    Seconds = Clip(record),
                    IdleSeconds = Math.Clamp(record.IdleDuration, 0, record.Duration ?? 0),
                    Evidence = EvidenceOf(record),
                    DeviceName = record.DeviceId == null ? null : devices.GetValueOrDefault(record.DeviceId),
                    EndReason = record.EndReason
                })
                .ToList(),
            SessionsTruncated = kept.Count > limit,
            DayBoundaryHour = boundary,
            MinDurationSeconds = rules.MinDurationSeconds,
            MergeGapSeconds = rules.MergeGapSeconds
        };
    }

    internal static string EvidenceOf(UseAppRecord record)
    {
        if (record.Source == "imported") return "imported";
        if (record.Source == "merged" || record.UpdatedAt != null) return "edited";
        return record.EndReason == "recovered" ? "recovered" : "recorded";
    }

    private async Task<(long Total, long Effective, int Sessions)> SumViewAsync(StatsExplainRequest request, CancellationToken cancellationToken)
    {
        var conditions = new List<string>();
        var parameters = new Dictionary<string, object?>();
        if (request.FromDay != null)
        {
            conditions.Add("logical_day >= $from");
            parameters["$from"] = request.FromDay;
        }
        if (request.ToDay != null)
        {
            conditions.Add("logical_day <= $to");
            parameters["$to"] = request.ToDay;
        }
        AddList(conditions, parameters, "steam_id", "s", request.SteamIds?.Cast<object>().ToList());
        AddList(conditions, parameters, "app_id", "a", request.AppIds?.Cast<object>().ToList());
        var where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        var sql = request.ClipToRange == true
            ? $"SELECT SUM(seconds), SUM(effective_seconds), COUNT(DISTINCT session_id) FROM v_session_day{where}"
            : $"SELECT SUM(duration_seconds), SUM(effective_seconds), COUNT(*) FROM v_session{where}";
        var result = await semanticLayer.QueryAsync(
            sql,
            parameters: parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Failure != SemanticQueryFailure.None || result.Rows.Count == 0) return (0, 0, 0);
        var row = result.Rows[0];
        return (
            row[0] == null ? 0 : Convert.ToInt64(row[0], CultureInfo.InvariantCulture),
            row[1] == null ? 0 : (long)Math.Round(Convert.ToDouble(row[1], CultureInfo.InvariantCulture)),
            Convert.ToInt32(row[2], CultureInfo.InvariantCulture));
    }

    private static void AddList(List<string> conditions, Dictionary<string, object?> parameters, string column, string prefix, IReadOnlyList<object>? values)
    {
        if (values is not { Count: > 0 }) return;
        var names = values.Select((_, index) => $"${prefix}{index}").ToList();
        for (var index = 0; index < values.Count; index++) parameters[names[index]] = values[index];
        conditions.Add($"{column} IN ({string.Join(", ", names)})");
    }

    private (long From, long To) ToRange(string? fromDay, string? toDay, int boundary)
    {
        long Start(string day)
        {
            var date = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var offset = timeProvider.LocalTimeZone.GetUtcOffset(date);
            return new DateTimeOffset(date, offset).ToUnixTimeSeconds() + boundary * 3600L;
        }
        var from = fromDay == null ? long.MinValue : Start(fromDay);
        var to = toDay == null ? long.MaxValue : Start(DateTime.ParseExact(toDay, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return (from, to);
    }

    private static bool Overlaps(long start, long end, (long From, long To) range) => end >= range.From && start < range.To;

    /// <summary>按开始时间归属：开始时间在范围内就整局计入。</summary>
    private static long InRangeByStart(UseAppRecord record, (long From, long To) range)
        => record.StartTime >= range.From && record.StartTime < range.To ? record.Duration ?? 0 : 0;

    /// <summary>记录落在范围内的秒数。和渲染层一样按 start + duration 计算。</summary>
    private static long ClipToRange(UseAppRecord record, (long From, long To) range)
    {
        var start = (long)record.StartTime;
        var end = start + (record.Duration ?? 0);
        return Math.Max(0, Math.Min(end, range.To) - Math.Max(start, range.From));
    }

    private static UseAppRecordDto ToDto(UseAppRecord record) => new()
    {
        AppId = record.AppId,
        SteamId = record.SteamId,
        StartTime = record.StartTime,
        EndTime = record.EndTime ?? 0,
        Duration = record.Duration ?? 0,
        IdleDuration = (int)record.IdleDuration
    };

    private static async Task<Dictionary<int, string?>> LookupNamesAsync(AppDbContext db, IReadOnlyList<UseAppRecord> records, CancellationToken cancellationToken)
    {
        var appIds = records.Select(record => record.AppId).Distinct().ToList();
        var apps = await db.SteamAppTable.AsNoTracking()
            .Where(app => appIds.Contains(app.AppId))
            .Select(app => new { app.AppId, app.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return apps.GroupBy(app => app.AppId).ToDictionary(group => group.Key, group => group.First().Name);
    }
}
