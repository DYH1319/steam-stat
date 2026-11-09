using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SteamStat.Core.Diagnostics;

namespace SteamStat.Core.Memoir;

public sealed record StaticSiteOptions(
    int? Year,
    string? Title,
    string Identity,
    string? Alias,
    IReadOnlySet<string> Sections,
    bool MaskNames,
    IReadOnlySet<int> Excluded,
    bool RoundHours,
    bool MonthPrecision,
    bool Covers,
    bool LifeEvents,
    bool Credit,
    string Language,
    string Theme,
    string? Accent);

public sealed record StaticSiteResult(string Html, string Json, int Games, IReadOnlyList<string> Warnings);

/// <summary>
/// 自包含静态站：一个不带脚本、不依赖任何服务的 HTML 文件，放到 GitHub Pages 就是公开主页。
/// <para>
/// 打码在生成数据时就做完，页面和 site.json 里根本没有被隐去的内容：隐藏游戏（语义视图已经剔除）、
/// 额外排除的游戏、游戏名打码（连同封面和成就名）、身份、时长取整、日期只到月。好友数据永远不进来。
/// </para>
/// </summary>
public static partial class StaticSiteBuilder
{
    public static readonly IReadOnlyList<string> AllSections = ["overview", "years", "heatmap", "games", "chapters", "achievements", "prehistory"];

    private const string CoverHost = "https://shared.akamai.steamstatic.com";
    private const int TopGames = 15;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    // 下面这些记录是 site.json 的结构，也是页面上全部的内容
    internal sealed record SiteYear(int Year, double Hours, bool Inferred);
    internal sealed record SiteDay(string Day, double Hours);
    internal sealed record SiteMonth(string Month, double Hours, bool Inferred);
    internal sealed record SiteGame(int Rank, string Name, int? AppId, double Hours, double Share);
    internal sealed record SiteChapter(string From, string To, string Title, double Hours, IReadOnlyList<string> Games, IReadOnlyList<string> Events, bool Inferred);
    internal sealed record SiteRare(string? Name, string? Game, double Percent);
    internal sealed record SiteAchievements(int Unlocked, int Completed, IReadOnlyList<SiteRare> Rarest);
    internal sealed record SitePrehistory(string? TrackingSince, string? FirstGame, string? FirstPlayed, int TraceDays, double UntrackedHours);
    internal sealed record SiteTotals(double Hours, int Games, int Achievements, int Completed, int Years);

    internal sealed record SiteData(
        string Title,
        string? Owner,
        string Language,
        string Generated,
        string? From,
        string? To,
        SiteTotals Totals,
        IReadOnlyList<SiteYear>? Years,
        IReadOnlyList<SiteDay>? Heatmap,
        IReadOnlyList<SiteMonth>? Months,
        IReadOnlyList<SiteGame>? Games,
        IReadOnlyList<SiteChapter>? Chapters,
        SiteAchievements? Achievements,
        SitePrehistory? Prehistory);

    public static StaticSiteResult Build(
        MemoirArchive archive,
        IReadOnlyList<AppPlaytimeTimeline> timelines,
        IReadOnlyList<MemoirMonth> months,
        IReadOnlyList<LifeChapter> chapters,
        StaticSiteOptions options,
        string? personaName)
    {
        var data = Collect(archive, timelines, months, chapters, options, personaName);
        var warnings = new List<string>();
        if (data.Totals.Hours <= 0 && data.Totals.Achievements == 0) warnings.Add("noData");
        if (options.Identity == "real" && data.Owner != null) warnings.Add("realName");
        if (!options.MonthPrecision && options.Sections.Contains("heatmap")) warnings.Add("exactDates");
        if (!options.RoundHours) warnings.Add("exactHours");
        if (options.Covers && !options.MaskNames && options.Sections.Contains("games")) warnings.Add("covers");
        return new StaticSiteResult(Render(data, options), JsonSerializer.Serialize(data, JsonOptions), data.Games?.Count ?? 0, warnings);
    }

    internal static SiteData Collect(
        MemoirArchive archive,
        IReadOnlyList<AppPlaytimeTimeline> timelines,
        IReadOnlyList<MemoirMonth> months,
        IReadOnlyList<LifeChapter> chapters,
        StaticSiteOptions options,
        string? personaName)
    {
        var zh = options.Language != "en";
        var settings = archive.Options;
        var excluded = options.Excluded;
        var year = options.Year;
        bool InYear(string month) => year == null || month.StartsWith(year.Value.ToString(Invariant) + "-", StringComparison.Ordinal);

        // 排除的游戏从每个月里扣掉，总数跟着变
        var scoped = months
            .Where(month => InYear(month.Month))
            .Select(month =>
            {
                var games = month.Games.Where(entry => !excluded.Contains(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value);
                var removed = month.Games.Where(entry => excluded.Contains(entry.Key)).Sum(entry => entry.Value);
                return month with { Seconds = Math.Max(0, month.Seconds - removed), Games = games };
            })
            .ToList();
        var sessions = archive.Insight.Sessions.Where(session => !excluded.Contains(session.AppId) && InYear(MemoirDays.Month(session.Day))).ToList();

        // 游戏排行：全部时间用官方累计时长（包括安装之前），单年用这一年的逐月数据
        var totals = year == null
            ? timelines.Where(timeline => !excluded.Contains(timeline.AppId))
                .GroupBy(timeline => timeline.AppId)
                .ToDictionary(group => group.Key, group => group.Sum(timeline => timeline.At(archive.Now).Value))
            : scoped.SelectMany(month => month.Games).GroupBy(entry => entry.Key).ToDictionary(group => group.Key, group => group.Sum(entry => entry.Value));
        var ranked = totals.Where(entry => entry.Value >= 60).OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key).ToList();
        var mask = ranked.Select((entry, index) => (entry.Key, index + 1)).ToDictionary(item => item.Key, item => item.Item2);
        string GameName(int appId)
        {
            if (!options.MaskNames) return archive.NameOf(appId);
            var number = mask.TryGetValue(appId, out var rank) ? rank : mask.Count + 1;
            return zh ? $"游戏 #{number}" : $"Game #{number}";
        }

        var totalSeconds = year == null ? ranked.Sum(entry => entry.Value) : scoped.Sum(month => month.Seconds);
        var gameCount = ranked.Count;
        var unlocks = archive.Insight.Unlocks.Where(unlock => !excluded.Contains(unlock.AppId) && (year == null || unlock.Day.StartsWith(year.Value.ToString(Invariant), StringComparison.Ordinal))).ToList();
        var completed = archive.Insight.Progress.Where(item => !excluded.Contains(item.AppId) && item.Total > 0 && item.Unlocked >= item.Total).Select(item => item.AppId).Distinct().Count();
        var from = scoped.FirstOrDefault(month => month.Seconds > 0)?.Month;
        var to = scoped.LastOrDefault(month => month.Seconds > 0)?.Month;

        var owner = options.Identity switch
        {
            "real" => string.IsNullOrWhiteSpace(personaName) ? null : personaName!.Trim(),
            "alias" => string.IsNullOrWhiteSpace(options.Alias) ? null : options.Alias!.Trim(),
            _ => null
        };
        var title = !string.IsNullOrWhiteSpace(options.Title)
            ? options.Title!.Trim()
            : owner != null
                ? zh ? $"{owner} 的游戏档案" : $"{owner}'s Gaming Archive"
                : zh ? "游戏档案" : "Gaming Archive";
        var today = settings.LogicalDate(archive.Now);
        string Date(DateOnly date) => options.MonthPrecision ? MemoirDays.Month(date) : date.ToString("yyyy-MM-dd", Invariant);

        IReadOnlyList<SiteYear>? years = null;
        if (options.Sections.Contains("years"))
            years = scoped.GroupBy(month => int.Parse(month.Month[..4], Invariant))
                .Select(group => new SiteYear(group.Key, Hours(group.Sum(month => month.Seconds)), group.Sum(month => month.Evidence >= MemoirEvidence.Inferred ? month.Seconds : 0) * 2 > group.Sum(month => month.Seconds)))
                .Where(item => item.Hours > 0)
                .OrderBy(item => item.Year)
                .ToList();

        IReadOnlyList<SiteDay>? heatmap = null;
        IReadOnlyList<SiteMonth>? monthBars = null;
        if (options.Sections.Contains("heatmap"))
        {
            if (options.MonthPrecision)
            {
                var last = scoped.TakeLast(year == null ? 24 : 12).ToList();
                monthBars = last.Select(month => new SiteMonth(month.Month, Hours(month.Seconds), month.Evidence >= MemoirEvidence.Inferred)).ToList();
            }
            else
            {
                var end = year == null ? today : new DateOnly(year.Value, 12, 31) < today ? new DateOnly(year.Value, 12, 31) : today;
                var start = year == null ? end.AddDays(-364) : new DateOnly(year.Value, 1, 1);
                var byDay = sessions.GroupBy(session => session.Day).ToDictionary(group => group.Key, group => group.Sum(session => session.Seconds));
                var days = new List<SiteDay>();
                for (var date = start; date <= end; date = date.AddDays(1))
                {
                    var key = date.ToString("yyyy-MM-dd", Invariant);
                    days.Add(new SiteDay(key, Hours(byDay.GetValueOrDefault(key))));
                }
                heatmap = days;
            }
        }

        IReadOnlyList<SiteGame>? games = null;
        if (options.Sections.Contains("games"))
            games = ranked.Take(TopGames)
                .Select((entry, index) => new SiteGame(index + 1, GameName(entry.Key), options.MaskNames ? null : entry.Key, Hours(entry.Value), totalSeconds > 0 ? Math.Round((double)entry.Value / totalSeconds, 4) : 0))
                .ToList();

        IReadOnlyList<SiteChapter>? siteChapters = null;
        if (options.Sections.Contains("chapters"))
            siteChapters = chapters
                .Where(chapter => year == null || string.CompareOrdinal(chapter.ToMonth, $"{year}-01") >= 0 && string.CompareOrdinal(chapter.FromMonth, $"{year}-12") <= 0)
                .Select(chapter =>
                {
                    var leads = chapter.AppIds.Where(appId => !excluded.Contains(appId)).ToList();
                    var events = options.LifeEvents
                        ? archive.LifeEvents
                            .Where(item => chapter.Contains(MemoirDays.Month(MemoirDays.FromEpoch(item.StartDay))))
                            .Select(item => item.Title)
                            .ToList()
                        : [];
                    return new SiteChapter(
                        chapter.FromMonth,
                        chapter.ToMonth,
                        ChapterTitle(chapter.Kind, leads, GameName, zh),
                        Hours(chapter.Seconds - chapter.TopGames.Where(game => excluded.Contains(game.AppId)).Sum(game => game.Seconds)),
                        chapter.TopGames.Where(game => !excluded.Contains(game.AppId)).Take(3).Select(game => GameName(game.AppId)).ToList(),
                        events,
                        chapter.Evidence >= MemoirEvidence.Inferred);
                })
                .ToList();

        SiteAchievements? achievements = null;
        if (options.Sections.Contains("achievements"))
            achievements = new SiteAchievements(
                unlocks.Count,
                completed,
                unlocks.Where(unlock => unlock.GlobalPercent is > 0)
                    .OrderBy(unlock => unlock.GlobalPercent)
                    .Take(5)
                    // 成就名会暴露是哪款游戏，打码时一起去掉
                    .Select(unlock => new SiteRare(options.MaskNames ? null : unlock.Name, GameName(unlock.AppId), Math.Round(unlock.GlobalPercent!.Value, 2)))
                    .ToList());

        SitePrehistory? prehistory = null;
        if (options.Sections.Contains("prehistory") && year == null)
        {
            var firstPlay = archive.FirstPlays.Where(item => !excluded.Contains(item.AppId)).MinBy(item => item.FirstPlayed);
            var tracking = archive.TrackingStart;
            var traceDays = archive.Traces.Where(trace => (tracking == 0 || trace.Time < tracking) && !excluded.Contains(trace.AppId)).Select(trace => settings.DayOf(trace.Time)).Distinct().Count();
            var untracked = timelines.Where(timeline => !excluded.Contains(timeline.AppId)).Sum(timeline => timeline.Untracked);
            prehistory = new SitePrehistory(
                tracking > 0 ? Date(settings.LogicalDate(tracking)) : null,
                firstPlay == null ? null : GameName(firstPlay.AppId),
                firstPlay == null ? null : Date(settings.LogicalDate(firstPlay.FirstPlayed)),
                traceDays,
                Hours(untracked));
        }

        return new SiteData(
            title,
            owner,
            zh ? "zh" : "en",
            Date(today),
            from,
            to,
            new SiteTotals(Hours(totalSeconds), gameCount, unlocks.Count, completed, scoped.Where(month => month.Seconds > 0).Select(month => month.Month[..4]).Distinct().Count()),
            years,
            heatmap,
            monthBars,
            games,
            siteChapters,
            achievements,
            prehistory);

        double Hours(long seconds) => Math.Round(seconds / 3600.0, 1);
    }

    internal static string ChapterTitle(string kind, IReadOnlyList<int> leads, Func<int, string> name, bool zh)
    {
        string Lead(int index) => index < leads.Count ? name(leads[index]) : zh ? "某款游戏" : "a game";
        return kind switch
        {
            "quiet" => zh ? "沉寂期" : "The Quiet Spell",
            "era" => zh ? $"《{Lead(0)}》时代" : $"The {Lead(0)} Era",
            "duo" => zh ? $"《{Lead(0)}》与《{Lead(1)}》" : $"{Lead(0)} & {Lead(1)}",
            "sampler" => zh ? "尝鲜期" : "The Sampler",
            "variety" => zh ? "百花齐放" : "A Bit of Everything",
            _ => zh ? $"以《{Lead(0)}》为主" : $"Mostly {Lead(0)}"
        };
    }

    // ---------------------------------------------------------------------
    // 渲染：没有脚本，没有外部样式；封面是唯一可能的外部资源，由 CSP 限定在 Steam CDN
    // ---------------------------------------------------------------------

    internal static string Render(SiteData data, StaticSiteOptions options)
    {
        var zh = data.Language == "zh";
        string T(string chinese, string english) => zh ? chinese : english;
        string H(double hours) => FormatHours(hours, options.RoundHours, zh);
        var accent = NormalizeAccent(options.Accent);
        var html = new StringBuilder(32 * 1024);
        var images = options.Covers && !options.MaskNames ? $" img-src {CoverHost};" : string.Empty;
        html.Append("<!doctype html>\n<html lang=\"").Append(zh ? "zh-CN" : "en").Append("\"");
        if (options.Theme != "auto") html.Append(" data-theme=\"").Append(options.Theme == "dark" ? "dark" : "light").Append('"');
        html.Append(">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline';").Append(images).Append("\">\n");
        html.Append("<meta name=\"referrer\" content=\"no-referrer\">\n<meta name=\"generator\" content=\"Steam Stat\">\n");
        html.Append("<title>").Append(E(data.Title)).Append("</title>\n<style>\n").Append(Styles(accent)).Append("</style>\n</head>\n<body>\n<main>\n");

        html.Append("<header class=\"hero\"><h1>").Append(E(data.Title)).Append("</h1><p class=\"muted\">");
        if (data.From != null) html.Append(E(data.From)).Append(" — ").Append(E(data.To ?? data.From)).Append(" · ");
        html.Append(T("生成于 ", "Generated ")).Append(E(data.Generated)).Append("</p></header>\n");

        if (options.Sections.Contains("overview"))
        {
            html.Append("<section class=\"stats\">");
            Stat(html, H(data.Totals.Hours), T("总时长", "Played"));
            Stat(html, data.Totals.Games.ToString(Invariant), T("玩过的游戏", "Games"));
            Stat(html, data.Totals.Achievements.ToString(Invariant), T("解锁的成就", "Achievements"));
            Stat(html, data.Totals.Completed.ToString(Invariant), T("全成就", "Completed"));
            Stat(html, data.Totals.Years.ToString(Invariant), T("年", "Years"));
            html.Append("</section>\n");
        }

        if (data.Years is { Count: > 0 } years)
        {
            Section(html, T("每年的时长", "Hours per year"));
            html.Append(BarChart(years.Select(item => (item.Year.ToString(Invariant), item.Hours, item.Inferred)).ToList(), H));
            if (years.Any(item => item.Inferred)) html.Append("<p class=\"note\"><span class=\"swatch hatched\"></span>").Append(T("斜线表示推算：安装 Steam Stat 之前的时长由官方数据和游玩痕迹推算。", "Hatched bars are estimates: playtime before Steam Stat was installed is inferred from official data and traces.")).Append("</p>");
            html.Append("</section>\n");
        }

        if (data.Heatmap is { Count: > 0 } heatmap)
        {
            Section(html, T("每天", "Every day"));
            html.Append(Heatmap(heatmap, H));
            html.Append("</section>\n");
        }
        else if (data.Months is { Count: > 0 } monthBars)
        {
            Section(html, T("每月", "Every month"));
            html.Append(BarChart(monthBars.Select(item => (item.Month, item.Hours, item.Inferred)).ToList(), H));
            html.Append("</section>\n");
        }

        if (data.Games is { Count: > 0 } games)
        {
            Section(html, T("玩得最多", "Most played"));
            html.Append("<ol class=\"games\">");
            foreach (var game in games)
            {
                html.Append("<li>");
                if (options.Covers && game.AppId is { } appId)
                    html.Append("<img src=\"").Append(CoverHost).Append("/store_item_assets/steam/apps/").Append(appId.ToString(Invariant)).Append("/header.jpg\" alt=\"\" loading=\"lazy\" width=\"120\" height=\"56\">");
                else html.Append("<span class=\"rank\">").Append(game.Rank.ToString(Invariant)).Append("</span>");
                html.Append("<div class=\"grow\"><div class=\"row\"><strong>").Append(E(game.Name)).Append("</strong><span class=\"muted num\">").Append(E(H(game.Hours))).Append("</span></div>");
                html.Append("<div class=\"bar\"><i style=\"width:").Append(Math.Max(1, Math.Round(game.Share / Math.Max(1e-9, games[0].Share) * 100)).ToString(Invariant)).Append("%\"></i></div></div></li>");
            }
            html.Append("</ol></section>\n");
        }

        if (data.Chapters is { Count: > 0 } chapters)
        {
            Section(html, T("人生章节", "Chapters"));
            html.Append("<ol class=\"chapters\">");
            foreach (var chapter in chapters)
            {
                html.Append("<li><div class=\"when muted\">").Append(E(chapter.From)).Append(" — ").Append(E(chapter.To)).Append("</div><h3>").Append(E(chapter.Title));
                if (chapter.Inferred) html.Append(" <span class=\"tag\">").Append(T("推算", "estimated")).Append("</span>");
                html.Append("</h3><p class=\"muted\">").Append(E(H(chapter.Hours)));
                if (chapter.Games.Count > 0) html.Append(" · ").Append(E(string.Join(T("、", ", "), chapter.Games)));
                html.Append("</p>");
                foreach (var title in chapter.Events) html.Append("<p class=\"event\">").Append(E(title)).Append("</p>");
                html.Append("</li>");
            }
            html.Append("</ol></section>\n");
        }

        if (data.Achievements is { } achievements && achievements.Unlocked > 0)
        {
            Section(html, T("成就", "Achievements"));
            html.Append("<p>").Append(zh ? $"解锁了 {achievements.Unlocked} 个成就，{achievements.Completed} 款游戏全成就。" : $"{achievements.Unlocked} achievements unlocked, {achievements.Completed} games completed.").Append("</p>");
            if (achievements.Rarest.Count > 0)
            {
                html.Append("<h3>").Append(T("最稀有的", "Rarest")).Append("</h3><ul class=\"rare\">");
                foreach (var rare in achievements.Rarest)
                {
                    html.Append("<li><span class=\"num\">").Append(rare.Percent.ToString("0.##", Invariant)).Append("%</span> ");
                    html.Append(E(rare.Name ?? T("一个成就", "An achievement")));
                    if (rare.Game != null) html.Append(" <span class=\"muted\">· ").Append(E(rare.Game)).Append("</span>");
                    html.Append("</li>");
                }
                html.Append("</ul>");
            }
            html.Append("</section>\n");
        }

        if (data.Prehistory is { } prehistory && (prehistory.FirstGame != null || prehistory.TraceDays > 0))
        {
            Section(html, T("史前", "Before the record"));
            html.Append("<p>");
            if (prehistory.FirstGame != null && prehistory.FirstPlayed != null)
                html.Append(E(zh ? $"Steam 记得的第一款游戏是 {prehistory.FirstGame}，{prehistory.FirstPlayed} 第一次打开。" : $"The first game Steam remembers is {prehistory.FirstGame}, first played on {prehistory.FirstPlayed}."));
            if (prehistory.TrackingSince != null)
                html.Append(' ').Append(E(zh ? $"逐局记录从 {prehistory.TrackingSince} 开始；在那之前有 {prehistory.TraceDays} 天留下了游玩痕迹，约 {H(prehistory.UntrackedHours)} 没有逐局记录。" : $"Session-by-session records start on {prehistory.TrackingSince}; before that, {prehistory.TraceDays} days left traces of play and about {H(prehistory.UntrackedHours)} has no session record."));
            html.Append("</p></section>\n");
        }

        html.Append("</main>\n<footer class=\"muted\">");
        html.Append(T("由档案自动生成，不含好友数据。", "Generated from a personal archive. No friend data included."));
        if (options.Credit)
            html.Append(" · <a href=\"").Append(ProjectLinks.RepositoryUrl).Append("\" rel=\"noopener\">Steam Stat</a>");
        html.Append("</footer>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void Section(StringBuilder html, string title) => html.Append("<section><h2>").Append(E(title)).Append("</h2>");

    private static void Stat(StringBuilder html, string value, string label)
        => html.Append("<div><strong class=\"num\">").Append(E(value)).Append("</strong><span class=\"muted\">").Append(E(label)).Append("</span></div>");

    private static string BarChart(IReadOnlyList<(string Label, double Hours, bool Inferred)> bars, Func<double, string> format)
    {
        const int height = 160, labelSpace = 22, top = 18;
        var width = Math.Max(320, bars.Count * 44);
        var max = Math.Max(1, bars.Max(bar => bar.Hours));
        var step = (double)width / bars.Count;
        var svg = new StringBuilder();
        svg.Append("<svg class=\"chart\" viewBox=\"0 0 ").Append(width.ToString(Invariant)).Append(' ').Append((height + labelSpace + top).ToString(Invariant)).Append("\" role=\"img\">");
        svg.Append("<defs><pattern id=\"hatch\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><rect width=\"6\" height=\"6\" class=\"hatch-bg\"/><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\" class=\"hatch-line\"/></pattern></defs>");
        for (var index = 0; index < bars.Count; index++)
        {
            var (label, hours, inferred) = bars[index];
            var barHeight = Math.Max(hours > 0 ? 2 : 0, hours / max * height);
            var x = index * step + step * 0.18;
            var y = top + height - barHeight;
            svg.Append("<g><title>").Append(E($"{label}: {format(hours)}")).Append("</title>");
            svg.Append("<rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(y)).Append("\" width=\"").Append(F(step * 0.64)).Append("\" height=\"").Append(F(barHeight)).Append("\" rx=\"3\" class=\"").Append(inferred ? "bar-inferred" : "bar-solid").Append("\"/>");
            svg.Append("<text x=\"").Append(F(x + step * 0.32)).Append("\" y=\"").Append(F(top + height + 15)).Append("\" class=\"axis\">").Append(E(label.Length > 4 ? label[2..] : label)).Append("</text></g>");
        }
        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string Heatmap(IReadOnlyList<SiteDay> days, Func<double, string> format)
    {
        const int cell = 11, gap = 2;
        var first = DateOnly.ParseExact(days[0].Day, "yyyy-MM-dd", Invariant);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var weeks = (days.Count + offset + 6) / 7;
        var max = Math.Max(0.5, days.Max(day => day.Hours));
        var svg = new StringBuilder();
        svg.Append("<svg class=\"chart heat\" viewBox=\"0 0 ").Append((weeks * (cell + gap)).ToString(Invariant)).Append(' ').Append((7 * (cell + gap)).ToString(Invariant)).Append("\" role=\"img\">");
        for (var index = 0; index < days.Count; index++)
        {
            var position = index + offset;
            var level = days[index].Hours <= 0 ? 0 : Math.Min(4, 1 + (int)(days[index].Hours / max * 3.999));
            svg.Append("<rect x=\"").Append((position / 7 * (cell + gap)).ToString(Invariant)).Append("\" y=\"").Append((position % 7 * (cell + gap)).ToString(Invariant))
                .Append("\" width=\"").Append(cell.ToString(Invariant)).Append("\" height=\"").Append(cell.ToString(Invariant)).Append("\" rx=\"2\" class=\"l").Append(level.ToString(Invariant)).Append("\"><title>")
                .Append(E($"{days[index].Day}: {format(days[index].Hours)}")).Append("</title></rect>");
        }
        svg.Append("</svg>");
        return svg.ToString();
    }

    internal static string FormatHours(double hours, bool rounded, bool zh)
    {
        string unit = zh ? " 小时" : " h";
        if (rounded)
        {
            if (hours <= 0) return "0" + unit;
            if (hours < 10) return (zh ? "不到 10" : "<10") + unit;
            return (Math.Round(hours / 10) * 10).ToString("#,0", Invariant) + unit;
        }
        return (hours >= 100 ? hours.ToString("#,0", Invariant) : hours.ToString("0.#", Invariant)) + unit;
    }

    internal static string NormalizeAccent(string? accent)
        => accent != null && AccentPattern().IsMatch(accent) ? accent : "#3b82f6";

    private static string Styles(string accent) => """
        :root{--accent:__ACCENT__;--bg:#fafafa;--card:#fff;--text:#18181b;--muted:#71717a;--line:#e4e4e7;--h0:#ebedf0}
        @media (prefers-color-scheme:dark){:root:not([data-theme=light]){--bg:#0b0b0e;--card:#16161a;--text:#f4f4f5;--muted:#a1a1aa;--line:#27272a;--h0:#202024}}
        :root[data-theme=dark]{--bg:#0b0b0e;--card:#16161a;--text:#f4f4f5;--muted:#a1a1aa;--line:#27272a;--h0:#202024}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.6 system-ui,-apple-system,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif}
        main{max-width:860px;margin:0 auto;padding:48px 20px 24px}h1{font-size:34px;line-height:1.2;margin:0 0 6px}h2{font-size:18px;margin:0 0 14px}h3{font-size:15px;margin:10px 0 4px}
        section{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:20px;margin:16px 0}.hero{margin-bottom:24px;border-left:4px solid var(--accent);padding-left:16px}
        .muted{color:var(--muted)}.num{font-variant-numeric:tabular-nums}.note{font-size:12px;color:var(--muted);margin:8px 0 0}
        .stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:12px}.stats div{display:flex;flex-direction:column}.stats strong{font-size:26px;color:var(--accent)}
        .chart{width:100%;height:auto;display:block}.axis{font-size:11px;fill:var(--muted);text-anchor:middle}.bar-solid{fill:var(--accent)}.bar-inferred{fill:url(#hatch)}
        .hatch-bg{fill:color-mix(in srgb,var(--accent) 25%,transparent)}.hatch-line{stroke:var(--accent);stroke-width:3}.swatch{display:inline-block;width:12px;height:12px;border-radius:3px;margin-right:6px;vertical-align:-2px}
        .hatched{background:repeating-linear-gradient(45deg,var(--accent) 0 2px,color-mix(in srgb,var(--accent) 25%,transparent) 2px 6px)}
        .heat .l0{fill:var(--h0)}.heat .l1{fill:color-mix(in srgb,var(--accent) 30%,var(--h0))}.heat .l2{fill:color-mix(in srgb,var(--accent) 55%,var(--h0))}.heat .l3{fill:color-mix(in srgb,var(--accent) 78%,var(--h0))}.heat .l4{fill:var(--accent)}
        ol,ul{margin:0;padding:0;list-style:none}.games li{display:flex;gap:12px;align-items:center;padding:8px 0;border-top:1px solid var(--line)}.games li:first-child{border-top:0}
        .games img{width:120px;height:56px;object-fit:cover;border-radius:6px;flex:none;background:var(--h0)}.rank{width:28px;text-align:center;color:var(--muted);font-weight:600;flex:none}
        .grow{flex:1;min-width:0}.row{display:flex;justify-content:space-between;gap:12px}.row strong{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .bar{height:6px;border-radius:3px;background:var(--h0);margin-top:6px;overflow:hidden}.bar i{display:block;height:100%;background:var(--accent);border-radius:3px}
        .chapters li{position:relative;padding:0 0 16px 20px;border-left:2px solid var(--line);margin-left:6px}.chapters li::before{content:"";position:absolute;left:-7px;top:6px;width:12px;height:12px;border-radius:50%;background:var(--accent)}
        .chapters p{margin:2px 0}.when{font-size:12px}.event{font-size:13px}.event::before{content:"◆ ";color:var(--accent)}
        .tag{font-size:11px;font-weight:500;border:1px dashed var(--muted);color:var(--muted);border-radius:4px;padding:0 5px;vertical-align:2px}
        .rare li{padding:4px 0}.rare .num{display:inline-block;min-width:56px;color:var(--accent);font-weight:600}
        footer{text-align:center;font-size:12px;padding:12px 20px 40px}footer a{color:inherit}
        """.Replace("__ACCENT__", accent, StringComparison.Ordinal);

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static string F(double value) => value.ToString("0.#", Invariant);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex AccentPattern();
}
