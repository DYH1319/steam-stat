using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace SteamStat.Core.Archives.Web;

public sealed record CommunityReview(
    string ReviewId,
    int AppId,
    bool Recommended,
    double? HoursTotal,
    double? HoursAtReview,
    long? PostedAt,
    long? EditedAt,
    string Text,
    int Helpful,
    int Funny);

public sealed record ReviewPage(WebPageState State, IReadOnlyList<CommunityReview> Reviews, int Total);

/// <summary>收藏的工坊物品或自己写的指南。</summary>
public sealed record CommunityFile(string PublishedFileId, int AppId, string Title, string? Description, string? Author, string? PreviewUrl);

public sealed record FilePage(WebPageState State, IReadOnlyList<CommunityFile> Files, int Total);

public sealed record CommunityComment(string CommentId, string? AuthorSteamId, string AuthorName, long PostedAt, string Text);

public sealed record CommentPage(WebPageState State, IReadOnlyList<CommunityComment> Comments, int Total);

/// <summary>
/// 解析社区页面。结构来自线上实测（2026-11-06），请求固定用英文。
/// 评测列表没有 ID 字段，用投票按钮里的推荐 ID；赞踩用图标判断，不依赖文字。
/// </summary>
public static partial class CommunityParsers
{
    public const int ParserVersion = 1;
    private const ulong SteamIdBase = 76561197960265728;

    public static ReviewPage ParseReviews(string html, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = new HtmlParser().ParseDocument(html);
        if (IsSignIn(document)) return new ReviewPage(WebPageState.SignedOut, [], 0);
        var boxes = document.QuerySelectorAll(".review_box");
        if (boxes.Length == 0)
            return new ReviewPage(HasProfileChrome(document) ? WebPageState.Ok : WebPageState.LayoutChanged, [], 0);

        var reviews = new List<CommunityReview>(boxes.Length);
        foreach (var box in boxes)
        {
            var review = ParseReview(box, capturedAt);
            if (review != null) reviews.Add(review);
        }
        return reviews.Count == 0
            ? new ReviewPage(WebPageState.LayoutChanged, [], 0)
            : new ReviewPage(WebPageState.Ok, reviews, ReadTotal(document));
    }

    private static CommunityReview? ParseReview(IElement box, DateTimeOffset capturedAt)
    {
        var link = box.QuerySelector(".leftcol a[href*='/app/']") ?? box.QuerySelector("a[href*='/app/']");
        var appMatch = link == null ? Match.Empty : AppPath().Match(link.GetAttribute("href") ?? string.Empty);
        if (!appMatch.Success || !int.TryParse(appMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId)) return null;

        var recommendationId = RecommendationId().Match(box.InnerHtml);
        var thumb = box.QuerySelector(".thumb img")?.GetAttribute("src") ?? string.Empty;
        var title = Text(box.QuerySelector(".title"));
        var recommended = thumb.Contains("thumbsDown", StringComparison.OrdinalIgnoreCase)
            ? false
            : thumb.Contains("thumbsUp", StringComparison.OrdinalIgnoreCase) || !title.StartsWith("Not", StringComparison.OrdinalIgnoreCase);

        var hoursText = Text(box.QuerySelector(".hours"));
        var numbers = Hours().Matches(hoursText);
        double? hoursTotal = numbers.Count > 0 ? ParseNumber(numbers[0].Groups[1].Value) : null;
        double? hoursAtReview = numbers.Count > 1 ? ParseNumber(numbers[1].Groups[1].Value) : null;

        var posted = Text(box.QuerySelector(".posted"));
        var header = Text(box.QuerySelector(".header"));
        var content = box.QuerySelector(".content");
        return new CommunityReview(
            recommendationId.Success ? recommendationId.Groups[1].Value : $"app:{appId}",
            appId,
            recommended,
            hoursTotal,
            hoursAtReview,
            ReadDate(Posted().Match(posted), capturedAt),
            ReadDate(Edited().Match(posted), capturedAt),
            content == null ? string.Empty : MultilineText(content),
            ReadCount(Helpful().Match(header)),
            ReadCount(Funny().Match(header)));
    }

    /// <summary>收藏和指南共用：两种列表的条目都是带 data-publishedfileid 的 a.ugc。</summary>
    public static FilePage ParseFiles(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = new HtmlParser().ParseDocument(html);
        if (IsSignIn(document)) return new FilePage(WebPageState.SignedOut, [], 0);
        var anchors = document.QuerySelectorAll("a.ugc[data-publishedfileid]");
        if (anchors.Length == 0)
            return new FilePage(HasProfileChrome(document) ? WebPageState.Ok : WebPageState.LayoutChanged, [], 0);

        var files = new Dictionary<string, CommunityFile>(StringComparer.Ordinal);
        foreach (var anchor in anchors)
        {
            var id = anchor.GetAttribute("data-publishedfileid");
            if (string.IsNullOrEmpty(id) || files.ContainsKey(id)) continue;
            // 收藏的标题在 a.ugc 外面，指南的在里面，所以往外找到第一个带标题的祖先
            var container = anchor;
            while (container != null && container.QuerySelector(".workshopItemTitle") == null) container = container.ParentElement;
            var title = Text(container?.QuerySelector(".workshopItemTitle"));
            if (title.Length == 0) continue;
            _ = int.TryParse(anchor.GetAttribute("data-appid"), NumberStyles.None, CultureInfo.InvariantCulture, out var appId);
            var preview = (container?.QuerySelector("img.workshopItemPreviewImage") ?? anchor.QuerySelector("img"))?.GetAttribute("src");
            var description = Text(container?.QuerySelector(".workshopItemShortDesc"));
            var author = Text(container?.QuerySelector(".workshopItemAuthorName"));
            files[id] = new CommunityFile(id, appId, title, description.Length == 0 ? null : description, author.Length == 0 ? null : author, preview);
        }
        return files.Count == 0
            ? new FilePage(WebPageState.LayoutChanged, [], 0)
            : new FilePage(WebPageState.Ok, files.Values.ToArray(), ReadTotal(document));
    }

    /// <summary>留言接口：{ success, total_count, comments_html }。</summary>
    public static CommentPage ParseComments(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new CommentPage(WebPageState.LayoutChanged, [], 0);
        }
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                return new CommentPage(WebPageState.LayoutChanged, [], 0);
            var total = root.TryGetProperty("total_count", out var count) && count.TryGetInt32(out var value) ? value : 0;
            var html = root.TryGetProperty("comments_html", out var content) ? content.GetString() : null;
            if (string.IsNullOrWhiteSpace(html)) return new CommentPage(WebPageState.Ok, [], total);

            var document = new HtmlParser().ParseDocument(html);
            var comments = new List<CommunityComment>();
            foreach (var element in document.QuerySelectorAll(".commentthread_comment"))
            {
                var id = element.Id?.StartsWith("comment_", StringComparison.Ordinal) == true ? element.Id["comment_".Length..] : null;
                var timestamp = element.QuerySelector(".commentthread_comment_timestamp[data-timestamp]")?.GetAttribute("data-timestamp");
                if (id == null || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var postedAt)) continue;
                var author = element.QuerySelector("a.commentthread_author_link");
                var textNode = element.QuerySelector(".commentthread_comment_text");
                comments.Add(new CommunityComment(
                    id,
                    ToSteamId(author?.GetAttribute("data-miniprofile")),
                    Text(author),
                    postedAt,
                    textNode == null ? string.Empty : MultilineText(textNode)));
            }
            return comments.Count == 0 && total > 0
                ? new CommentPage(WebPageState.LayoutChanged, [], total)
                : new CommentPage(WebPageState.Ok, comments, total);
        }
    }

    private static string? ToSteamId(string? accountId)
        => ulong.TryParse(accountId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? (SteamIdBase + id).ToString(CultureInfo.InvariantCulture)
            : null;

    /// <summary>「Showing 1-10 of 12 entries」。</summary>
    private static int ReadTotal(IDocument document)
    {
        var match = Total().Match(Text(document.QuerySelector(".workshopBrowsePagingInfo")));
        return match.Success && int.TryParse(match.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var total) ? total : 0;
    }

    /// <summary>今年的日期不写年份：「Posted 24 November.」，年份来自抓取时间。</summary>
    private static long? ReadDate(Match match, DateTimeOffset capturedAt)
    {
        if (!match.Success) return null;
        var text = match.Groups[1].Value.Trim().TrimEnd('.').Replace("@", " ", StringComparison.Ordinal).Trim();
        var withYear = text.Contains(',', StringComparison.Ordinal) ? text : $"{text}, {capturedAt.UtcDateTime.Year.ToString(CultureInfo.InvariantCulture)}";
        string[] formats = ["d MMMM, yyyy", "MMMM d, yyyy", "d MMM, yyyy", "MMM d, yyyy"];
        return DateTime.TryParseExact(withYear, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? new DateTimeOffset(date.Date, TimeSpan.Zero).ToUnixTimeSeconds()
            : null;
    }

    private static int ReadCount(Match match)
        => match.Success && int.TryParse(match.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double? ParseNumber(string value)
        => double.TryParse(value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static bool IsSignIn(IDocument document)
        => document.Title?.Trim().Equals("Sign In", StringComparison.OrdinalIgnoreCase) == true
           || document.QuerySelector("#login_btn_signin, form[name='logon']") != null;

    /// <summary>空列表也是正常页面：有资料页外框或翻页区就不是改版。</summary>
    private static bool HasProfileChrome(IDocument document)
        => document.QuerySelector(".profile_small_header_bg, .profile_small_header_texture, .workshopBrowsePaging, #leftContents, .no_items, .profile_private_info") != null;

    private static string MultilineText(IElement element)
    {
        var clone = (IElement)element.Clone();
        foreach (var br in clone.QuerySelectorAll("br").ToArray()) br.Replace(clone.Owner!.CreateTextNode("\n"));
        var lines = clone.TextContent.Split('\n').Select(line => Whitespace().Replace(line, " ").Trim());
        return string.Join('\n', lines).Trim();
    }

    private static string Text(IElement? element) => element == null ? string.Empty : Whitespace().Replace(element.TextContent, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"/app/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex AppPath();

    [GeneratedRegex(@"RecommendationVoteUpBtn(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex RecommendationId();

    [GeneratedRegex(@"([\d,.]+)\s*hrs?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Hours();

    [GeneratedRegex(@"Posted\s+([^.]+?(?:,\s*\d{4})?)\s*\.", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Posted();

    [GeneratedRegex(@"(?:Last edited|Updated)\s+([^.]+?(?:,\s*\d{4})?)\s*\.", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Edited();

    [GeneratedRegex(@"([\d,]+)\s+(?:person|people)\s+found this review helpful", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Helpful();

    [GeneratedRegex(@"([\d,]+)\s+(?:person|people)\s+found this review funny", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Funny();

    [GeneratedRegex(@"of\s+([\d,]+)\s+entries", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Total();
}
