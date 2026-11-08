using System.Globalization;
using System.Text.Json;
using ElectronNet.Features.Memoir;
using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Memoir;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

internal sealed record PreviouslyOnReady(PreviouslyOnDto Recap);

internal sealed record TimeCapsuleReady(TimeCapsuleDto Capsule);

/// <summary>
/// 回忆录里用户写下的东西和由游玩触发的东西：人生事件、时光胶囊、回坑「上回说到」。
/// 人生事件和胶囊参与同步；胶囊的正文到期前不发给渲染层，到期由启动游戏或每小时的检查触发。
/// </summary>
internal sealed class MemoirJournalService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    MemoirService memoir,
    IAppNameResolver appNames,
    ISettingsStore settingsStore,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<MemoirJournalService> logger) : IEventHandler<UsageSessionStarted>, IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions ContextOptions = new(JsonSerializerDefaults.Web);
    private const int MaxNotes = 3;
    private const int MaxScreenshots = 4;
    private const int MaxUnlocks = 8;

    private readonly SemaphoreSlim _capsuleGate = new(1, 1);
    private ITimer? _timer;

    public void StartSchedule()
        => _timer ??= timeProvider.CreateTimer(_ => _ = CheckGuardedAsync(), null, InitialDelay, TickInterval);

    private long Now => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    #region 人生事件

    public async Task<IReadOnlyList<LifeEventDto>> GetLifeEventsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.LifeEventTable.AsNoTracking().OrderBy(row => row.StartDay).ThenBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    public async Task<LifeEventDto> SaveLifeEventAsync(LifeEventSaveRequest request, CancellationToken cancellationToken = default)
    {
        var title = UserText(request.Title, 80);
        if (title.Length == 0) throw new ArgumentException("A life event needs a title.", nameof(request));
        var note = UserText(request.Note, 2000);
        int? endDay = request.EndDay is { } end && end > request.StartDay ? end : null;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LifeEvent row;
        if (request.Id is { } id)
        {
            row = await db.LifeEventTable.FirstOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
                  ?? throw new KeyNotFoundException("The life event no longer exists.");
        }
        else
        {
            row = new LifeEvent { CreatedAt = Now };
            db.LifeEventTable.Add(row);
        }
        row.Title = title;
        row.Note = note.Length == 0 ? null : note;
        row.Kind = request.Kind;
        row.StartDay = request.StartDay;
        row.EndDay = endDay;
        row.Boundary = request.Boundary;
        row.UpdatedAt = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        memoir.Invalidate();
        return ToDto(row);
    }

    public async Task<bool> DeleteLifeEventAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await db.LifeEventTable.Where(row => row.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        memoir.Invalidate();
        return deleted > 0;
    }

    private static LifeEventDto ToDto(LifeEvent row)
        => MemoirDtoMapper.ToDto(new MemoirLifeEvent(row.Id, row.Title, row.Note, row.Kind, row.StartDay, row.EndDay, row.Boundary, row.UpdatedAt));

    #endregion

    #region 时光胶囊

    public async Task<IReadOnlyList<TimeCapsuleDto>> GetCapsulesAsync(CancellationToken cancellationToken = default)
    {
        await CheckCapsulesAsync(null, announce: true, cancellationToken).ConfigureAwait(false);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.TimeCapsuleTable.AsNoTracking().OrderBy(row => row.OpenAt).ThenBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    public async Task<TimeCapsuleDto> CreateCapsuleAsync(TimeCapsuleCreateRequest request, CancellationToken cancellationToken = default)
    {
        var now = Now;
        var message = UserText(request.Message, int.MaxValue);
        var label = UserText(request.Label, 80);
        if (TimeCapsuleRules.Validate(message, request.OpenAt, now) is { } problem)
            throw new ArgumentException($"The capsule cannot be sealed: {problem}.", nameof(request));
        var context = await ContextAsync(request.AppId, cancellationToken).ConfigureAwait(false);
        var row = new TimeCapsule
        {
            Label = label.Length == 0 ? null : label,
            Message = message,
            CreatedAt = now,
            OpenAt = request.OpenAt,
            AppId = request.AppId,
            Context = context == null ? null : JsonSerializer.Serialize(context, ContextOptions),
            UpdatedAt = now
        };
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.TimeCapsuleTable.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    /// <summary>开启一颗已经到期的胶囊。没到期时返回 null：正文绝不提前发出去。</summary>
    public async Task<TimeCapsuleDto?> OpenCapsuleAsync(int id, CancellationToken cancellationToken = default)
    {
        await CheckCapsulesAsync(null, announce: false, cancellationToken).ConfigureAwait(false);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.TimeCapsuleTable.FirstOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
        if (row?.ReadyAt == null) return null;
        if (row.OpenedAt == null)
        {
            row.OpenedAt = Now;
            row.UpdatedAt = Now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return ToDto(row);
    }

    public async Task<bool> DeleteCapsuleAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.TimeCapsuleTable.Where(row => row.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private async Task CheckGuardedAsync()
    {
        try
        {
            await CheckCapsulesAsync(null, announce: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Time capsule check failed");
        }
    }

    /// <summary>
    /// 把条件满足的胶囊标为可开启。绑定游戏的要在开启日期之后启动过这款游戏：
    /// 看会话表，所以别的设备上的游玩同步过来以后也算。
    /// </summary>
    internal async Task<IReadOnlyList<int>> CheckCapsulesAsync(int? launchedAppId, bool announce, CancellationToken cancellationToken = default)
    {
        await _capsuleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<TimeCapsule> ready = [];
        try
        {
            var now = Now;
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var pending = await db.TimeCapsuleTable.Where(row => row.ReadyAt == null && row.OpenAt <= now).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var capsule in pending)
            {
                long? lastLaunch = null;
                if (capsule.AppId is { } appId)
                    lastLaunch = launchedAppId == appId
                        ? now
                        : await db.UseAppRecordTable.AsNoTracking().Where(record => record.AppId == appId && record.StartTime >= capsule.OpenAt)
                            .MaxAsync(record => (long?)record.StartTime, cancellationToken).ConfigureAwait(false);
                if (!TimeCapsuleRules.IsReady(capsule.OpenAt, capsule.AppId, now, lastLaunch)) continue;
                capsule.ReadyAt = now;
                capsule.UpdatedAt = now;
                ready.Add(capsule);
            }
            if (ready.Count > 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _capsuleGate.Release();
        }
        if (announce)
            foreach (var capsule in ready)
                await eventBus.PublishAsync(new TimeCapsuleReady(ToDto(capsule)), cancellationToken).ConfigureAwait(false);
        return ready.Select(capsule => capsule.Id).ToList();
    }

    /// <summary>封存时的档案摘要。开启时和那时的自己对比，所以要现在记下来。</summary>
    private async Task<TimeCapsuleContextDto?> ContextAsync(int? appId, CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await memoir.LoadAsync(AccountSettings.MineOf(settingsStore.GetSettings()), cancellationToken).ConfigureAwait(false);
            var archive = loaded.Archive;
            var now = archive.Now;
            var top = archive.Insight.Sessions.Where(session => session.Start >= now - 30 * 86400L)
                .GroupBy(session => session.AppId)
                .Select(group => (AppId: group.Key, Seconds: group.Sum(session => session.Seconds)))
                .OrderByDescending(item => item.Seconds)
                .FirstOrDefault();
            return new TimeCapsuleContextDto
            {
                TotalSeconds = loaded.Timelines.Sum(timeline => timeline.At(now).Value),
                Games = loaded.Timelines.Where(timeline => timeline.At(now).Value > 0).Select(timeline => timeline.AppId).Distinct().Count(),
                Achievements = archive.Insight.Unlocks.Count,
                TopAppId = top.AppId > 0 ? top.AppId : null,
                TopAppName = top.AppId > 0 ? archive.NameOf(top.AppId) : null,
                AppSeconds = appId is { } id ? loaded.Timelines.Where(timeline => timeline.AppId == id).Sum(timeline => timeline.At(now).Value) : null
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 摘要只是锦上添花，没有它胶囊照样能封存
            logger.LogWarning(exception, "Could not snapshot the archive for a time capsule");
            return null;
        }
    }

    private TimeCapsuleDto ToDto(TimeCapsule row)
    {
        var ready = row.ReadyAt != null;
        TimeCapsuleContextDto? context = null;
        if (ready && !string.IsNullOrEmpty(row.Context))
        {
            try
            {
                context = JsonSerializer.Deserialize<TimeCapsuleContextDto>(row.Context, ContextOptions);
            }
            catch (JsonException)
            {
                context = null;
            }
        }
        return new TimeCapsuleDto
        {
            Id = row.Id,
            Label = row.Label,
            CreatedAt = row.CreatedAt,
            OpenAt = row.OpenAt,
            AppId = row.AppId,
            AppName = row.AppId is { } appId ? NameOf(appId) : null,
            State = row.OpenedAt != null ? "opened" : ready ? "ready" : "sealed",
            ReadyAt = row.ReadyAt,
            OpenedAt = row.OpenedAt,
            Message = ready ? row.Message : null,
            Context = context
        };
    }

    #endregion

    #region 上回说到

    /// <summary>启动时只记下这件事，查询放到后台：记录会话的任务不能被它拖慢。</summary>
    public Task HandleAsync(UsageSessionStarted message, CancellationToken cancellationToken)
    {
        _ = Task.Run(() => OnStartedAsync(message));
        return Task.CompletedTask;
    }

    internal async Task OnStartedAsync(UsageSessionStarted message)
    {
        try
        {
            var capsules = await CheckCapsulesAsync(message.AppId, announce: true).ConfigureAwait(false);
            var settings = settingsStore.GetSettings().Memoir;
            if (settings?.PreviouslyOnEnabled == false) return;
            var threshold = settings?.PreviouslyOnDays ?? 90;
            await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var previous = await db.UseAppRecordTable.AsNoTracking()
                .Where(record => record.AppId == message.AppId && record.SteamId == message.SteamId && record.Id != message.RecordId
                                 && record.EndTime != null && record.Duration > 0 && record.StartTime < message.StartTime)
                .OrderByDescending(record => record.EndTime)
                .FirstOrDefaultAsync().ConfigureAwait(false);
            if (previous?.EndTime is not { } previousEnd) return;
            if (PreviouslyOnRules.GapDays(previousEnd, message.StartTime, threshold) is not { } gap) return;
            var recap = await BuildAsync(message.AppId, [message.SteamId], previous, gap, true, capsules, message.RecordId).ConfigureAwait(false);
            logger.LogInformation("Previously on app {AppId} after {GapDays} days", message.AppId, gap);
            await eventBus.PublishAsync(new PreviouslyOnReady(recap)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to prepare the previously-on recap");
        }
    }

    /// <summary>在详情页手动查看：不管隔了多久，最后一次游玩之前是什么样。</summary>
    public async Task<PreviouslyOnDto?> GetPreviouslyOnAsync(PreviouslyOnRequest request, CancellationToken cancellationToken = default)
    {
        var scope = request.SteamIds is { Count: > 0 } steamIds ? steamIds : null;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.UseAppRecordTable.AsNoTracking().Where(record => record.AppId == request.AppId && record.EndTime != null && record.Duration > 0);
        if (scope != null) query = query.Where(record => scope.Contains(record.SteamId));
        var previous = await query.OrderByDescending(record => record.EndTime).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (previous?.EndTime is not { } end) return null;
        var gap = (int)Math.Max(0, (Now - end) / 86400);
        return await BuildAsync(request.AppId, scope, previous, gap, false, [], null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PreviouslyOnDto> BuildAsync(
        int appId,
        IReadOnlyList<string>? steamIds,
        UseAppRecord previous,
        int gapDays,
        bool live,
        IReadOnlyList<int> capsules,
        int? currentRecordId,
        CancellationToken cancellationToken = default)
    {
        var lastEnd = previous.EndTime ?? previous.StartTime;
        var periodStart = lastEnd - PreviouslyOnRules.PeriodSeconds;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await db.SteamUserTable.AsNoTracking()
            .Where(user => steamIds == null || steamIds.Contains(user.SteamId))
            .Select(user => user.AccountName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var longAppId = (long)appId;

        var records = await db.UseAppRecordTable.AsNoTracking()
            .Where(record => record.AppId == appId && record.EndTime != null && record.Duration > 0 && (steamIds == null || steamIds.Contains(record.SteamId)) && record.Id != currentRecordId)
            .Select(record => new { record.StartTime, record.Duration, record.Note })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var recorded = records.Sum(record => (long)(record.Duration ?? 0));
        var official = await db.PlaytimeSnapshotTable.AsNoTracking()
            .Where(row => row.AppId == appId && (steamIds == null || steamIds.Contains(row.SteamId)))
            .GroupBy(row => row.SteamId)
            .Select(group => group.Max(row => row.PlaytimeMinutes))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var period = records.Where(record => record.StartTime >= periodStart && record.StartTime <= lastEnd).ToList();

        var notes = records.Where(record => !string.IsNullOrWhiteSpace(record.Note) && record.StartTime <= lastEnd)
            .OrderByDescending(record => record.StartTime)
            .Take(MaxNotes)
            .Select(record => new PreviouslyOnNoteDto { Text = record.Note!.Trim(), Time = record.StartTime, Source = "session" })
            .ToList();
        var meta = await db.LibraryGameMetaTable.AsNoTracking()
            .Where(row => row.AppId == longAppId && accounts.Contains(row.AccountName))
            .OrderByDescending(row => row.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(meta?.Note)) notes.Insert(0, new PreviouslyOnNoteDto { Text = meta.Note.Trim(), Time = meta.UpdatedAt, Source = "library" });

        var screenshots = await db.ScreenshotArchiveTable.AsNoTracking()
            .Where(row => row.AppId == longAppId && row.MissingSince == null && row.TakenAt <= lastEnd + 3600 && (steamIds == null || steamIds.Contains(row.SteamId)))
            .OrderByDescending(row => row.TakenAt)
            .Take(MaxScreenshots)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var unlockQuery = db.AchievementUnlockArchiveTable.AsNoTracking()
            .Where(row => row.AppId == longAppId && row.UnlockTime > 0 && row.UnlockTime <= lastEnd + 3600 && (steamIds == null || steamIds.Contains(row.SteamId)));
        var unlocks = await unlockQuery.Where(row => row.UnlockTime >= periodStart).OrderByDescending(row => row.UnlockTime).Take(MaxUnlocks).ToListAsync(cancellationToken).ConfigureAwait(false);
        // 那段时间什么都没解锁时，给最后解锁的几个：知道自己当时卡在哪里也是「上回说到」
        if (unlocks.Count == 0) unlocks = await unlockQuery.OrderByDescending(row => row.UnlockTime).Take(4).ToListAsync(cancellationToken).ConfigureAwait(false);
        var progress = await db.AchievementGameArchiveTable.AsNoTracking()
            .Where(row => row.AppId == longAppId && accounts.Contains(row.AccountName) && row.Total > 0)
            .OrderByDescending(row => row.Unlocked)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var presence = await db.RichPresenceRecordTable.AsNoTracking()
            .Where(row => row.IsSelf && row.AppId == appId && row.ObservedAt <= lastEnd + 60 && row.DisplayText != null && row.DisplayText != "" && (steamIds == null || steamIds.Contains(row.SteamId)))
            .OrderByDescending(row => row.ObservedAt)
            .Select(row => new { row.DisplayText, row.ObservedAt })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var since = new List<PreviouslyOnChangeDto>();
        var updates = await db.AppBuildTable.AsNoTracking().Where(row => row.AppId == appId && !row.IsBaseline && row.ObservedAt > lastEnd).Select(row => row.ObservedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (updates.Count > 0) since.Add(new PreviouslyOnChangeDto { Kind = "update", Count = updates.Count, Time = updates.Max() });
        var added = await db.AchievementSchemaChangeTable.AsNoTracking().Where(row => row.AppId == longAppId && row.Kind == "added" && row.DetectedAt > lastEnd).Select(row => row.DetectedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (added.Count > 0) since.Add(new PreviouslyOnChangeDto { Kind = "achievements", Count = added.Count, Time = added.Max() });
        var dlcIds = await db.AchievementSetArchiveTable.AsNoTracking().Where(row => row.AppId == longAppId && row.DlcAppId != null).Select(row => row.DlcAppId!.Value).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (dlcIds.Count > 0)
        {
            var dlcs = await db.LibraryAcquisitionTable.AsNoTracking().Where(row => dlcIds.Contains(row.AppId) && row.PackageId > 0 && row.AcquiredAt > lastEnd).Select(row => row.AcquiredAt).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (dlcs.Count > 0) since.Add(new PreviouslyOnChangeDto { Kind = "dlc", Count = dlcs.Count, Time = dlcs.Max() });
        }

        return new PreviouslyOnDto
        {
            AppId = appId,
            Name = NameOf(appId),
            GapDays = gapDays,
            LastPlayedAt = lastEnd,
            TotalSeconds = Math.Max(recorded, official.Sum(minutes => (long)minutes * 60)),
            LastSessionSeconds = previous.Duration ?? 0,
            PeriodSeconds = period.Sum(record => (long)(record.Duration ?? 0)),
            PeriodSessions = period.Count,
            Notes = notes,
            Status = meta?.Status,
            Screenshots = screenshots.Select(ScreenshotArchiveService.ToDto).ToList(),
            Unlocks = unlocks.Select(row => new MemoirUnlockDto
            {
                AppId = appId,
                AppName = NameOf(appId),
                InternalName = row.InternalName,
                Name = string.IsNullOrWhiteSpace(row.Name) ? row.InternalName : row.Name,
                Icon = string.IsNullOrEmpty(row.Icon) ? null : row.Icon,
                Time = row.UnlockTime,
                GlobalPercent = row.GlobalPercent
            }).ToList(),
            Progress = progress == null ? null : new PreviouslyOnProgressDto { Unlocked = progress.Unlocked, Total = progress.Total },
            Presence = presence == null ? null : new PreviouslyOnPresenceDto { Text = presence.DisplayText!, Time = presence.ObservedAt },
            Since = since,
            Capsules = capsules,
            Live = live,
            GeneratedAt = Now
        };
    }

    #endregion

    /// <summary>渲染层把用户写的文字编码成「s1:」再传，桥接层不会把「12:30」「2024-05-01」改写成时间。</summary>
    internal static string UserText(string? value, int maxLength)
    {
        var text = (ElectronNet.Infrastructure.IpcSecret.Decode(value) ?? value ?? string.Empty).Trim();
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private string NameOf(int appId)
    {
        var name = appNames.GetCachedName((uint)appId);
        return AppNameHelper.IsPlaceholder(name) ? string.Create(CultureInfo.InvariantCulture, $"App {appId}") : name!;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _capsuleGate.Dispose();
    }
}
