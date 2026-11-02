using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace ElectronNet.Helpers;

/// <summary>
/// 应用名兜底：历史上名称未知时会写入「App {appId}」占位名，读取时用 SteamApp 表里的真实名称修正。
/// </summary>
internal static partial class AppNameHelper
{
    [GeneratedRegex(@"^App \d+$")]
    private static partial Regex PlaceholderPattern();

    public static bool IsPlaceholder(string? name) => string.IsNullOrWhiteSpace(name) || PlaceholderPattern().IsMatch(name);

    /// <summary>批量查询 SteamApp 表中已知的应用名（只返回非空名称）。</summary>
    public static Dictionary<long, string> LookupNames(AppDbContext db, IEnumerable<long> appIds)
    {
        var ids = appIds.Where(id => id is > 0 and <= int.MaxValue).Select(id => (int)id).Distinct().ToList();
        if (ids.Count == 0) return [];
        return db.SteamAppTable.AsNoTracking()
            .Where(app => ids.Contains(app.AppId) && app.Name != null && app.Name != string.Empty)
            .Select(app => new { app.AppId, app.Name })
            .AsEnumerable()
            .GroupBy(app => (long)app.AppId)
            .ToDictionary(group => group.Key, group => group.First().Name!);
    }
}
