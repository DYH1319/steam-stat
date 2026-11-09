using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ElectronNet.Features.Semantic;
using SteamStat.Core.Insights;

namespace ElectronNet.Features.Insights;

/// <summary>
/// 年度合订本：一年的周刊合订成不依赖脚本的静态页，并入年度封存包。
/// 封存包的规矩这里同样适用：隐藏的游戏不出现，好友的内容不进包（那是别人的数据）。
/// 周刊是冻结的，生成时可能还没隐藏，所以要在这里重新过滤一遍。
/// </summary>
internal static class WeeklyBoundVolume
{
    internal const string Directory = "weekly";

    internal static IReadOnlyList<OpenArchiveFile> Build(IReadOnlyList<WeeklyPayload> issues, int year, string language, IReadOnlySet<int> hidden)
    {
        if (issues.Count == 0) return [];
        var clean = issues.Select(issue => Sanitize(issue, hidden)).ToList();
        return
        [
            new OpenArchiveFile($"{Directory}/index.html", Encoding.UTF8.GetBytes(ToHtml(clean, year, language, hidden))),
            new OpenArchiveFile($"{Directory}/issues.json", Encoding.UTF8.GetBytes(ToJson(clean, hidden)))
        ];
    }

    internal static WeeklyPayload Sanitize(WeeklyPayload issue, IReadOnlySet<int> hidden)
    {
        WeeklyStory Hide(WeeklyStory story) => story.AppId is { } appId && hidden.Contains(appId) ? story with { AppId = null } : story;
        var market = issue.Market == null ? null : issue.Market with
        {
            Gainers = issue.Market.Gainers.Where(stock => !hidden.Contains(stock.AppId)).ToList(),
            Losers = issue.Market.Losers.Where(stock => !hidden.Contains(stock.AppId)).ToList()
        };
        return issue with
        {
            Headline = Hide(issue.Headline),
            Briefs = issue.Briefs.Select(Hide).ToList(),
            Friends = null,
            Obituaries = issue.Obituaries.Where(item => !hidden.Contains(item.AppId)).ToList(),
            Market = market,
            Forecast = issue.Forecast == null ? null : issue.Forecast with { Friend = null },
            Classifieds = issue.Classifieds.Where(item => !hidden.Contains(item.AppId)).ToList()
        };
    }

    private static string Plain(string text, string language, IReadOnlySet<int> hidden)
        => Say.Resolve(
            text,
            (appId, name) => hidden.Contains(appId) ? (language == "zh" ? "某款游戏" : "an unnamed game") : name,
            (_, _) => language == "zh" ? "一位好友" : "a friend");

    private static string Pick(Bilingual text, string language, IReadOnlySet<int> hidden)
        => Plain(language == "zh" ? text.Zh : text.En, language, hidden);

    private static string ToJson(IReadOnlyList<WeeklyPayload> issues, IReadOnlySet<int> hidden)
    {
        var json = JsonSerializer.Serialize(issues, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        return Say.Resolve(json, (appId, name) => hidden.Contains(appId) ? "(hidden)" : name, (_, _) => "(friend)");
    }

    private static string ToHtml(IReadOnlyList<WeeklyPayload> issues, int year, string language, IReadOnlySet<int> hidden)
    {
        var zh = language == "zh";
        string Encode(string text) => WebUtility.HtmlEncode(text);
        string Text(Bilingual text) => Encode(Pick(text, language, hidden));
        var html = new StringBuilder();
        var title = zh ? $"档案周刊 {year} 年度合订本" : $"The Archive Weekly: {year} Bound Volume";
        html.Append($$"""
            <!doctype html>
            <html lang="{{(zh ? "zh-CN" : "en")}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{Encode(title)}}</title>
            <style>
            :root { color-scheme: light dark; --ink: #1c1a17; --muted: #6f675c; --paper: #f7f3ea; --line: #cfc7b8; }
            @media (prefers-color-scheme: dark) { :root { --ink: #e9e4da; --muted: #a49c8f; --paper: #16140f; --line: #3a352c; } }
            * { box-sizing: border-box; }
            body { margin: 0 auto; max-width: 780px; padding: 32px 16px 64px; background: var(--paper); color: var(--ink); font: 16px/1.7 Georgia, "Noto Serif SC", "Songti SC", serif; }
            h1 { margin: 0; font-size: 2.4rem; text-align: center; border-bottom: 3px double var(--line); padding-bottom: .6rem; }
            article { margin-top: 2.6rem; padding-top: 1.2rem; border-top: 1px solid var(--line); }
            .meta { color: var(--muted); font-size: .85rem; }
            h2 { margin: .3rem 0 .4rem; font-size: 1.5rem; line-height: 1.3; }
            h3 { margin: 1.2rem 0 .2rem; font-size: .95rem; letter-spacing: .08em; color: var(--muted); }
            p { margin: .3rem 0; }
            </style>
            </head>
            <body>
            <h1>{{Encode(title)}}</h1>
            <p class="meta">{{Encode(zh ? $"共 {issues.Count} 期。隐藏的游戏和好友的内容不在其中。" : $"{issues.Count} issues. Hidden games and friends' activity are left out.")}}</p>
            """);
        foreach (var issue in issues)
        {
            var label = issue.Kind == "extra" ? (zh ? "号外" : "Extra") : zh ? $"第 {issue.Number} 期" : $"Issue {issue.Number}";
            html.Append($"""
                <article>
                <p class="meta">{Encode(label)} · {Encode(issue.FromDay)} ~ {Encode(issue.ToDay)}</p>
                <h2>{Text(issue.Headline.Title)}</h2>
                <p>{Text(issue.Headline.Body)}</p>
                """);
            foreach (var brief in issue.Briefs)
                html.Append($"<h3>{Text(brief.Title)}</h3><p>{Text(brief.Body)}</p>");
            if (issue.Schedule != null) html.Append($"<h3>{Encode(zh ? "作息版" : "Hours")}</h3><p>{Text(issue.Schedule.Text)}</p>");
            if (issue.Market != null) html.Append($"<h3>{Encode(zh ? "游戏股市版" : "Markets")}</h3><p>{Text(issue.Market.Commentary)}</p>");
            if (issue.Obituaries.Count > 0)
            {
                html.Append($"<h3>{Encode(zh ? "讣告" : "Obituaries")}</h3>");
                foreach (var item in issue.Obituaries) html.Append($"<p>{Text(item.Text)}</p>");
            }
            if (issue.Forecast != null) html.Append($"<h3>{Encode(zh ? "下周预报" : "Forecast")}</h3><p>{Text(issue.Forecast.Summary)}</p>");
            if (issue.Classifieds.Count > 0)
            {
                html.Append($"<h3>{Encode(zh ? "分类广告" : "Classifieds")}</h3>");
                foreach (var item in issue.Classifieds) html.Append($"<p>{Text(item.Text)}</p>");
            }
            html.Append("</article>");
        }
        html.Append("</body></html>");
        return html.ToString();
    }
}
