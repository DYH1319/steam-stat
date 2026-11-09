using ElectronNet.Features.Insights;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>洞察的入口：读档案、跑检测器、组装洞察页需要的内容。</summary>
internal sealed class InsightService(
    SemanticLayerService semanticLayer,
    IInsightCatalog catalog,
    ISettingsStore settingsStore,
    TimeProvider timeProvider,
    ILogger<InsightService> logger)
{
    private const int DefaultPeriodDays = 30;
    private const int ComebackDays = 365;
    private const int MaxCrashes = 50;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly SemanticInsightDataSource _data = new(semanticLayer);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Key, DateTimeOffset At, InsightReportDto Report)? _cached;

    internal InsightOptions Options()
    {
        var settings = settingsStore.GetSettings();
        return new InsightOptions(
            settings.Analytics?.DayBoundaryHour ?? 4,
            settings.Analytics?.WeekStart ?? 1,
            settings.Analytics?.PauseDays ?? 30,
            settings.Analytics?.AbandonDays ?? 90,
            timeProvider.LocalTimeZone,
            settings.Language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == false ? "en" : "zh");
    }

    internal Task<InsightArchive> LoadAsync(InsightScope scope, long friendsFrom, CancellationToken cancellationToken = default)
        => InsightArchiveLoader.LoadAsync(_data, catalog, Options(), scope, timeProvider.GetUtcNow().ToUnixTimeSeconds(), friendsFrom, cancellationToken);

    public async Task<InsightReportDto> GetReportAsync(InsightReportRequest request, CancellationToken cancellationToken = default)
    {
        var days = request.PeriodDays ?? DefaultPeriodDays;
        var key = $"{days}|{string.Join(',', (request.SteamIds ?? []).Order(StringComparer.Ordinal))}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (_cached is { } cached && cached.Key == key && now - cached.At < CacheLifetime) return cached.Report;

            var started = timeProvider.GetTimestamp();
            // 和好友最久的一夜要看全部记录
            var archive = await LoadAsync(InsightScope.Of(request.SteamIds), 0, cancellationToken).ConfigureAwait(false);
            var report = Build(archive, days);
            logger.LogInformation("Built the insight report from {Sessions} sessions in {Elapsed} ms", archive.Sessions.Count, (int)timeProvider.GetElapsedTime(started).TotalMilliseconds);
            _cached = (key, now, report);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static InsightReportDto Build(InsightArchive archive, int periodDays)
    {
        var options = archive.Options;
        var toDay = options.DayOf(archive.Now);
        var period = new InsightPeriod(InsightDays.Shift(toDay, -(periodDays - 1)), toDay);
        var names = archive.Sessions.GroupBy(session => session.AppId).ToDictionary(group => group.Key, group => group.First().Name);
        var board = SpectacleAnalyzer.Build(archive);
        return new InsightReportDto
        {
            GeneratedAt = (int)archive.Now,
            FromDay = period.FromDay,
            ToDay = period.ToDay,
            Insights = InsightEngine.CreateDefault().Run(archive, period).Select(ToDto).ToList(),
            Personas = PersonaAnalyzer.ByYear(archive).Select(ToDto).ToList(),
            Career = CareerAnalyzer.Build(archive).Select(item => new CareerHighlightDto
            {
                Kind = item.Kind,
                Time = (int)item.Time,
                Day = item.Day,
                AppId = item.AppId,
                Text = ToDto(item.Text)
            }).ToList(),
            Spectacle = new SpectacleBoardDto
            {
                PriciestUnplayed = board.PriciestUnplayed.Select(ToDto).ToList(),
                LongestUntouched = board.LongestUntouched.Select(ToDto).ToList(),
                Delisted = board.Delisted.Select(ToDto).ToList()
            },
            Comebacks = ComebackAnalysis.Find(archive, InsightDays.Shift(toDay, -(ComebackDays - 1)), toDay).Select(item => new ComebackDto
            {
                AppId = item.AppId,
                Name = item.Name,
                Start = (int)item.Start,
                Day = item.Day,
                GapDays = item.GapDays,
                Seconds = (int)item.SecondsAfter,
                Causes = item.Causes.Select(cause => new ComebackCauseDto
                {
                    Kind = cause.Kind,
                    DaysBefore = cause.DaysBefore,
                    Name = cause.Name == null ? null : ToDto(cause.Name)
                }).ToList()
            }).ToList(),
            Crashes = archive.Crashes.OrderByDescending(item => item.Start).Take(MaxCrashes).Select(item => new CrashBurstDto
            {
                AppId = item.AppId,
                Name = names.GetValueOrDefault(item.AppId) ?? $"App {item.AppId}",
                Start = (int)item.Start,
                End = (int)item.End,
                Attempts = item.Attempts,
                Seconds = (int)item.Seconds,
                AfterUpdate = item.AfterUpdate,
                BuildId = item.BuildId
            }).ToList()
        };
    }

    private static BilingualTextDto ToDto(Bilingual text) => new(text.Zh, text.En);

    private static InsightItemDto ToDto(Insight insight) => new()
    {
        Id = insight.Id,
        Kind = insight.Kind.ToString().ToLowerInvariant(),
        FromDay = insight.FromDay,
        ToDay = insight.ToDay,
        Text = ToDto(InsightCopy.Describe(insight)),
        SampleSize = insight.SampleSize,
        Confidence = Math.Round(insight.Confidence, 2),
        Evidence = insight.Evidence.FirstOrDefault()?.Level ?? InsightEvidenceLevel.Derived,
        Views = insight.Evidence.Select(item => item.View).Distinct().ToList(),
        AppId = insight.AppId
    };

    private static PersonaYearDto ToDto(PlayerPersona persona) => new()
    {
        Year = persona.Year,
        Code = persona.Code,
        Name = ToDto(persona.Name),
        Summary = ToDto(persona.Summary),
        Axes = persona.Axes.Select(axis => new PersonaAxisDto { Id = axis.Id, Score = Math.Round(axis.Score, 3), Letter = axis.Letter, Value = Math.Round(axis.Value, 3) }).ToList(),
        Title = ToDto(persona.Title.Text),
        TitleReasons = persona.Title.Reasons.Select(ToDto).ToList(),
        Sessions = persona.Sessions,
        Hours = Math.Round(persona.Hours, 1)
    };

    private static SpectacleItemDto ToDto(SpectacleItem item) => new()
    {
        AppId = item.AppId,
        Name = item.Name,
        Value = Math.Round(item.Value, 2),
        Currency = item.Currency,
        Time = (int?)item.Time,
        PlaytimeMinutes = item.PlaytimeMinutes
    };
}
