using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

internal sealed record WeeklyIssuePublished(WeeklyIssueSummaryDto Issue);

/// <summary>封存包只需要读一年的期号，不需要整个出版社。</summary>
internal interface IWeeklyYearSource
{
    Task<IReadOnlyList<WeeklyPayload>> GetYearAsync(int year, CancellationToken cancellationToken = default);
}

/// <summary>
/// 档案周刊的出版社：每周一份，遇到大事出号外。生成后内容冻结，之后只有已读时间会变。
/// 这张表不参与同步：好友版的内容只有本机有，两台设备各自出刊。
/// </summary>
internal sealed class WeeklyService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    InsightService insights,
    SessionTagService sessionTags,
    ISettingsStore settingsStore,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<WeeklyService> logger) : IEventHandler<UsageSessionEnded>, IWeeklyYearSource, IDisposable
{
    internal static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SessionEndDelay = TimeSpan.FromSeconds(60);
    /// <summary>预报要看八周，再多一周是为了第一周的边界。</summary>
    private const long FriendWindowSeconds = 9 * 7 * 86400L;
    /// <summary>号外只报最近发生的事。</summary>
    private const long ExtraWindowSeconds = 48 * 3600;
    private const int MaxExtrasPerDay = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _timerLock = new();
    private ITimer? _schedule;
    private ITimer? _sessionTimer;
    private (int? LastSessionEnd, int Unlocks)? _lastSeen;
    private int _disposed;

    public void StartSchedule()
        => _schedule ??= timeProvider.CreateTimer(_ => _ = RunGuardedAsync(), null, InitialDelay, TickInterval);

    /// <summary>破纪录和里程碑要等一局结束才知道，「立即」就是这时候。</summary>
    public Task HandleAsync(UsageSessionEnded message, CancellationToken cancellationToken)
    {
        lock (_timerLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
            _sessionTimer?.Dispose();
            _sessionTimer = timeProvider.CreateTimer(_ => _ = RunGuardedAsync(), null, SessionEndDelay, Timeout.InfiniteTimeSpan);
        }
        return Task.CompletedTask;
    }

    private async Task RunGuardedAsync()
    {
        try
        {
            await RunOnceAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Weekly issue run failed");
        }
    }

    /// <summary>周刊没单独选账号时覆盖「我」，「我」也没确认时覆盖全部账号。</summary>
    private InsightScope Scope
    {
        get
        {
            var settings = settingsStore.GetSettings();
            return InsightScope.Of(settings.Weekly?.SteamIds is { Count: > 0 } chosen ? chosen : AccountSettings.MineOf(settings));
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.GetSettings().Weekly;
        if (settings?.Enabled != true) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var period = insights.Options().LastFullWeek(now);
            var due = !await ExistsAsync("weekly", period.FromDay, cancellationToken).ConfigureAwait(false);
            var extras = settings.ExtraEnabled == true && await ArchiveChangedAsync(cancellationToken).ConfigureAwait(false);
            if (!due && !extras) return;

            await sessionTags.RetagAsync(cancellationToken).ConfigureAwait(false);
            var archive = await insights.LoadAsync(Scope, now - FriendWindowSeconds, cancellationToken).ConfigureAwait(false);
            if (archive.Sessions.Count == 0) return;

            if (due) await PublishWeeklyAsync(archive, period, cancellationToken).ConfigureAwait(false);
            if (extras) await PublishExtrasAsync(archive, settings, now, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>每小时醒一次，但大多数时候什么都没发生，不值得读一遍档案。</summary>
    private async Task<bool> ArchiveChangedAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var signature = (
            await db.UseAppRecordTable.AsNoTracking().MaxAsync(record => record.EndTime, cancellationToken).ConfigureAwait(false),
            await db.AchievementUnlockArchiveTable.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false));
        if (signature == _lastSeen) return false;
        _lastSeen = signature;
        return true;
    }

    private async Task PublishWeeklyAsync(InsightArchive archive, InsightPeriod period, CancellationToken cancellationToken)
    {
        var quiet = !archive.SessionsIn(period).Any();
        // 连续没有游戏时只出一期「本周无事发生」，不然应用常驻托盘时每周都来打扰一次
        if (quiet && await LatestWeeklyWasQuietAsync(cancellationToken).ConfigureAwait(false)) return;
        var payload = WeeklyComposer.Compose(archive, period, InsightEngine.CreateDefault().Run(archive, period));
        await SaveAsync(payload, period.FromDay, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> LatestWeeklyWasQuietAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var payload = await db.WeeklyIssueTable.AsNoTracking()
            .Where(issue => issue.Kind == "weekly")
            .OrderByDescending(issue => issue.ToDay)
            .Select(issue => issue.Payload)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (payload == null) return false;
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("quiet", out var quiet) && quiet.ValueKind == JsonValueKind.True;
    }

    private async Task PublishExtrasAsync(InsightArchive archive, WeeklySettings settings, long now, CancellationToken cancellationToken)
    {
        // 第一次运行只建立基线，不把历史里的里程碑翻出来
        if (settings.BaselineAt == null)
        {
            await settingsStore.UpdateAsync(new AppSettings { Weekly = new WeeklySettings { BaselineAt = now } }, cancellationToken).ConfigureAwait(false);
            return;
        }
        var since = Math.Max(settings.BaselineAt.Value, now - ExtraWindowSeconds);
        var today = archive.Options.DayOf(now);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var published = await db.WeeklyIssueTable.AsNoTracking().CountAsync(issue => issue.Kind == "extra" && issue.ToDay == today, cancellationToken).ConfigureAwait(false);
        foreach (var item in WeeklyExtras.Detect(archive, since))
        {
            if (published >= MaxExtrasPerDay) break;
            if (await ExistsAsync("extra", item.Key, cancellationToken).ConfigureAwait(false)) continue;
            await SaveAsync(WeeklyComposer.ComposeExtra(archive, item), item.Key, cancellationToken).ConfigureAwait(false);
            published++;
        }
    }

    /// <summary>补印往期。内容按现在的档案算，所以标记为补印，和当时生成的区分开。</summary>
    public async Task<WeeklyBackfillResultDto> BackfillAsync(int anchorTime, CancellationToken cancellationToken = default)
    {
        if (settingsStore.GetSettings().Weekly?.Enabled != true) return new WeeklyBackfillResultDto { Status = "disabled" };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var options = insights.Options();
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var start = options.WeekOf(options.LogicalDate(anchorTime));
            var period = new InsightPeriod(InsightDays.Format(start), InsightDays.Format(start.AddDays(6)));
            if (string.CompareOrdinal(period.ToDay, options.DayOf(now)) >= 0) return new WeeklyBackfillResultDto { Status = "notFinished" };

            await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                var existing = await db.WeeklyIssueTable.AsNoTracking()
                    .FirstOrDefaultAsync(issue => issue.Kind == "weekly" && issue.IssueKey == period.FromDay, cancellationToken)
                    .ConfigureAwait(false);
                if (existing != null) return new WeeklyBackfillResultDto { Status = "exists", Issue = ToSummary(existing) };
            }

            await sessionTags.RetagAsync(cancellationToken).ConfigureAwait(false);
            var friendsFrom = Math.Min(now, anchorTime) - FriendWindowSeconds;
            var archive = await insights.LoadAsync(Scope, friendsFrom, cancellationToken).ConfigureAwait(false);
            if (!archive.SessionsIn(period).Any()) return new WeeklyBackfillResultDto { Status = "empty" };

            var payload = WeeklyComposer.Compose(archive, period, InsightEngine.CreateDefault().Run(archive, period), backfilled: true);
            var summary = await SaveAsync(payload, period.FromDay, cancellationToken, announce: false).ConfigureAwait(false);
            return new WeeklyBackfillResultDto { Status = "created", Issue = summary };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ExistsAsync(string kind, string key, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.WeeklyIssueTable.AsNoTracking().AnyAsync(issue => issue.Kind == kind && issue.IssueKey == key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WeeklyIssueSummaryDto> SaveAsync(WeeklyPayload payload, string key, CancellationToken cancellationToken, bool announce = true)
    {
        var issue = new WeeklyIssue
        {
            Kind = payload.Kind,
            IssueKey = key,
            Number = payload.Number,
            FromDay = payload.FromDay,
            ToDay = payload.ToDay,
            GeneratedAt = payload.GeneratedAt,
            Backfilled = payload.Backfilled,
            Payload = JsonSerializer.Serialize(payload, PayloadOptions)
        };
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            db.WeeklyIssueTable.Add(issue);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        logger.LogInformation("Published {Kind} issue {Number} for {FromDay}", issue.Kind, issue.Number, issue.FromDay);
        var summary = ToSummary(issue);
        if (announce) await eventBus.PublishAsync(new WeeklyIssuePublished(summary), cancellationToken).ConfigureAwait(false);
        return summary;
    }

    public async Task<IReadOnlyList<WeeklyIssueSummaryDto>> ListAsync(WeeklyIssuesQueryRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.WeeklyIssueTable.AsNoTracking();
        if (request.BeforeId is { } beforeId) query = query.Where(issue => issue.Id < beforeId);
        var issues = await query
            .OrderByDescending(issue => issue.ToDay)
            .ThenByDescending(issue => issue.Id)
            .Take(request.Limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return issues.Select(ToSummary).ToList();
    }

    public async Task<WeeklyIssueDto?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var issue = await db.WeeklyIssueTable.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
        return issue == null ? null : new WeeklyIssueDto { Summary = ToSummary(issue), Payload = issue.Payload };
    }

    public async Task<bool> MarkReadAsync(int id, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var changed = await db.WeeklyIssueTable
            .Where(issue => issue.Id == id && issue.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(issue => issue.ReadAt, now), cancellationToken)
            .ConfigureAwait(false);
        return changed > 0;
    }

    /// <summary>这一年的全部期号，给年度合订本。解析不了的旧期号跳过，不影响封存。</summary>
    public async Task<IReadOnlyList<WeeklyPayload>> GetYearAsync(int year, CancellationToken cancellationToken = default)
    {
        var prefix = year.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-";
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var payloads = await db.WeeklyIssueTable.AsNoTracking()
            .Where(issue => issue.ToDay.StartsWith(prefix))
            .OrderBy(issue => issue.ToDay)
            .ThenBy(issue => issue.Id)
            .Select(issue => issue.Payload)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var issues = new List<WeeklyPayload>(payloads.Count);
        foreach (var payload in payloads)
        {
            try
            {
                if (JsonSerializer.Deserialize<WeeklyPayload>(payload, PayloadOptions) is { } issue) issues.Add(issue);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Skipped a weekly issue that could not be read");
            }
        }
        return issues;
    }

    private static WeeklyIssueSummaryDto ToSummary(WeeklyIssue issue)
    {
        var headline = new BilingualTextDto(string.Empty, string.Empty);
        int? appId = null;
        try
        {
            using var document = JsonDocument.Parse(issue.Payload);
            if (document.RootElement.TryGetProperty("headline", out var story))
            {
                if (story.TryGetProperty("title", out var title))
                    headline = new BilingualTextDto(title.GetProperty("zh").GetString() ?? "", title.GetProperty("en").GetString() ?? "");
                if (story.TryGetProperty("appId", out var app) && app.TryGetInt32(out var value)) appId = value;
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // 标题读不出来也要能在过刊馆里看到这一期
        }
        return new WeeklyIssueSummaryDto
        {
            Id = issue.Id,
            Kind = issue.Kind,
            Number = issue.Number,
            FromDay = issue.FromDay,
            ToDay = issue.ToDay,
            GeneratedAt = (int)issue.GeneratedAt,
            ReadAt = (int?)issue.ReadAt,
            Backfilled = issue.Backfilled,
            Headline = headline,
            AppId = appId
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_timerLock)
        {
            _schedule?.Dispose();
            _sessionTimer?.Dispose();
        }
        _gate.Dispose();
    }
}
