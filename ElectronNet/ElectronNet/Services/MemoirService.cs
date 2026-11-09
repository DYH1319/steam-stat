using ElectronNet.Features.Insights;
using ElectronNet.Features.Memoir;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Insights;
using SteamStat.Core.Memoir;

namespace ElectronNet.Services;

/// <summary>
/// 回忆录的读取入口：时光机、人生章节、史前考古。读一次档案算完，缓存两分钟：
/// 拖动时光机的日期拨盘时每一格都是一次请求，不能每次都把十年档案重读一遍。
/// </summary>
internal sealed class MemoirService(
    SemanticLayerService semanticLayer,
    InsightService insights,
    IMemoirCatalog catalog,
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<MemoirService> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

    private readonly SemanticInsightDataSource _data = new(semanticLayer);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Loaded? _cached;

    internal sealed record Loaded(
        string Key,
        DateTimeOffset At,
        InsightScope Scope,
        MemoirArchive Archive,
        IReadOnlyList<AppPlaytimeTimeline> Timelines,
        IReadOnlyList<MemoirMonth> Months,
        IReadOnlyList<LifeChapter> Chapters);

    /// <summary>人生事件变了：章节要重新切。</summary>
    public void Invalidate() => _cached = null;

    internal async Task<Loaded> LoadAsync(IReadOnlyList<string>? steamIds, CancellationToken cancellationToken = default)
    {
        var key = string.Join(',', (steamIds ?? []).Order(StringComparer.Ordinal));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (_cached is { } cached && cached.Key == key && now - cached.At < CacheLifetime) return cached;
            var started = timeProvider.GetTimestamp();
            var scope = InsightScope.Of(steamIds);
            // 好友记录可能有上百万条，时光机只读需要的那一天，这里不读
            var insight = await insights.LoadAsync(scope, now.ToUnixTimeSeconds(), cancellationToken).ConfigureAwait(false);
            var archive = await MemoirArchiveLoader.LoadAsync(insight, _data, catalog, scope, cancellationToken).ConfigureAwait(false);
            var timelines = PlaytimeTimelines.Build(archive);
            var months = MemoirMonths.Build(archive, timelines);
            var chapters = LifeChapters.Build(archive, months);
            logger.LogInformation("Loaded the memoir archive ({Sessions} sessions, {Traces} traces, {Months} months) in {Elapsed} ms",
                insight.Sessions.Count, archive.Traces.Count, months.Count, (int)timeProvider.GetElapsedTime(started).TotalMilliseconds);
            _cached = new Loaded(key, now, scope, archive, timelines, months, chapters);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TimeMachineDto> GetTimeMachineAsync(TimeMachineRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var archive = loaded.Archive;
        var options = archive.Options;
        var day = Math.Min(request.Day, MemoirDays.ToEpoch(options.LogicalDate(archive.Now)));
        var date = MemoirDays.FromEpoch(day);
        var start = MemoirDays.StartOf(options, date);
        var end = MemoirDays.EndOf(options, date);
        var plays = await MemoirArchiveLoader.LoadFriendPlaysAsync(_data, loaded.Scope, start, end, cancellationToken).ConfigureAwait(false);
        var snapshot = TimeMachine.At(archive, loaded.Timelines, loaded.Chapters, plays, day);
        var icons = await IconsAsync(snapshot.Achievements.DayUnlocks.Concat(snapshot.Achievements.Recent).Concat(snapshot.Achievements.Rarest), cancellationToken).ConfigureAwait(false);
        return MemoirDtoMapper.ToDto(snapshot, archive, icons);
    }

    public async Task<MemoirChaptersDto> GetChaptersAsync(ArchiveScopeRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var archive = loaded.Archive;
        var appIds = loaded.Months.SelectMany(month => month.Games.Keys).Concat(loaded.Chapters.SelectMany(chapter => chapter.TopGames.Select(game => game.AppId))).Distinct();
        return new MemoirChaptersDto
        {
            Months = loaded.Months.Select(month => new MemoirMonthDto
            {
                Month = month.Month,
                Seconds = month.Seconds,
                Evidence = month.Evidence.Id(),
                Games = month.Games.OrderByDescending(entry => entry.Value).Take(8).Select(entry => new MemoirMonthGameDto { AppId = entry.Key, Seconds = entry.Value }).ToList()
            }).ToList(),
            Chapters = loaded.Chapters.Select(chapter => MemoirDtoMapper.ToDto(chapter, archive)).ToList(),
            Events = archive.LifeEvents.Select(MemoirDtoMapper.ToDto).ToList(),
            Names = MemoirDtoMapper.Names(archive, appIds),
            TrackingStartMonth = archive.TrackingStart > 0 ? MemoirDays.Month(archive.Options.LogicalDate(archive.TrackingStart)) : null
        };
    }

    public async Task<MemoirPrehistoryDto> GetPrehistoryAsync(ArchiveScopeRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        var archive = loaded.Archive;
        var options = archive.Options;
        var report = Prehistory.Build(archive, loaded.Timelines);
        var appIds = report.FirstPlays.Select(item => item.AppId)
            .Concat(report.Untracked.Select(item => item.AppId))
            .Concat(report.Calendar.SelectMany(day => day.AppIds))
            .Concat(archive.YearReviews.SelectMany(review => review.Games.Select(game => game.AppId).Concat(review.Months.SelectMany(month => month.Games.Select(game => game.AppId)))));
        return new MemoirPrehistoryDto
        {
            Today = options.DayOf(archive.Now),
            TrackingStartDay = report.TrackingStart is { } tracking ? options.DayOf(tracking) : null,
            FirstEvidenceDay = report.FirstEvidence is { } first ? options.DayOf(first) : null,
            FirstPlays = report.FirstPlays.Select(item => new MemoirFirstPlayDto { AppId = item.AppId, Name = archive.NameOf(item.AppId), FirstPlayed = item.FirstPlayed, BeforeTracking = item.BeforeTracking }).ToList(),
            Untracked = report.Untracked.Select(item => new MemoirUntrackedDto
            {
                AppId = item.AppId,
                Name = archive.NameOf(item.AppId),
                OfficialSeconds = item.OfficialSeconds,
                RecordedSeconds = item.RecordedSeconds,
                UntrackedSeconds = item.UntrackedSeconds,
                BaselineAt = item.BaselineAt,
                Evidence = item.Evidence.Id()
            }).ToList(),
            Calendar = report.Calendar.Select(day => new MemoirTraceDayDto { Day = day.Day, AppIds = day.AppIds, Kinds = day.Kinds.Select(kind => kind.Id()).ToList(), Count = day.Count }).ToList(),
            TraceCounts = report.TraceCounts.Select(item => new MemoirTraceCountDto { Kind = item.Kind.Id(), Total = item.Total, BeforeTracking = item.BeforeTracking }).ToList(),
            YearReviews = archive.YearReviews.Select(review => MemoirDtoMapper.ToDto(review, archive)).ToList(),
            Names = MemoirDtoMapper.Names(archive, appIds)
        };
    }

    /// <summary>成就图标只在归档表里有，洞察档案没有带它。</summary>
    private async Task<IReadOnlyDictionary<(int, string), string>> IconsAsync(IEnumerable<InsightUnlock> unlocks, CancellationToken cancellationToken)
    {
        var wanted = unlocks.Select(unlock => (unlock.AppId, unlock.InternalName)).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<(int, string), string>();
        var appIds = wanted.Select(item => (long)item.AppId).Distinct().ToList();
        var names = wanted.Select(item => item.InternalName).Distinct().ToList();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AchievementUnlockArchiveTable.AsNoTracking()
            .Where(row => appIds.Contains(row.AppId) && names.Contains(row.InternalName) && row.Icon != "")
            .Select(row => new { row.AppId, row.InternalName, row.Icon })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var icons = new Dictionary<(int, string), string>();
        foreach (var row in rows) icons.TryAdd(((int)row.AppId, row.InternalName), row.Icon);
        return icons;
    }
}
