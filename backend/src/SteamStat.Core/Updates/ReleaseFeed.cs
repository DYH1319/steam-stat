using System.Xml;
using System.Xml.Linq;
using SteamStat.Core.Settings;

namespace SteamStat.Core.Updates;

/// <summary>GitHub Releases Atom 订阅（releases.atom）中的一个已发布版本。草稿不会出现在订阅中。</summary>
public sealed record ReleaseFeedEntry(
    SemanticVersion Version,
    string Tag,
    string Title,
    string PageUrl,
    DateTimeOffset? UpdatedAt,
    string? NotesHtml);

/// <summary>
/// 解析 releases.atom 并按更新通道挑选新版本。用于无法自动安装更新的发行形态
/// （便携版、tar.gz、包管理器安装）：只提示有新版本，下载交给浏览器或包管理器。
/// </summary>
public static class ReleaseFeed
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    public static IReadOnlyList<ReleaseFeedEntry> Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException)
        {
            return [];
        }

        var entries = new List<ReleaseFeedEntry>();
        foreach (var entry in document.Root?.Elements(Atom + "entry") ?? [])
        {
            var link = entry.Elements(Atom + "link").FirstOrDefault(element => (string?)element.Attribute("rel") is null or "alternate");
            var href = (string?)link?.Attribute("href");
            if (href == null) continue;
            var marker = href.LastIndexOf("/releases/tag/", StringComparison.Ordinal);
            if (marker < 0) continue;
            var tag = Uri.UnescapeDataString(href[(marker + "/releases/tag/".Length)..]);
            if (!SemanticVersion.TryParse(tag, out var version)) continue;

            DateTimeOffset? updatedAt = DateTimeOffset.TryParse((string?)entry.Element(Atom + "updated"), out var updated) ? updated : null;
            entries.Add(new ReleaseFeedEntry(
                version,
                tag,
                ((string?)entry.Element(Atom + "title"))?.Trim() is { Length: > 0 } title ? title : tag,
                href,
                updatedAt,
                (string?)entry.Element(Atom + "content")));
        }
        return entries;
    }

    /// <summary>
    /// 通道内比当前版本新的最高版本。stable 只看正式版；beta 同时接收正式版与 beta / rc 预发布（不接收 alpha）。
    /// </summary>
    public static ReleaseFeedEntry? SelectUpdate(IEnumerable<ReleaseFeedEntry> entries, string channel, SemanticVersion current)
        => entries
            .Where(entry => IsAllowed(entry.Version, channel))
            .Where(entry => entry.Version > current)
            .OrderByDescending(entry => entry.Version)
            .FirstOrDefault();

    public static bool IsAllowed(SemanticVersion version, string channel)
        => !version.IsPrerelease
           || (channel == UpdateChannels.Beta && !string.Equals(version.PrereleaseChannel, "alpha", StringComparison.OrdinalIgnoreCase));
}
