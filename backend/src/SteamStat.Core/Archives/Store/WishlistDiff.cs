namespace SteamStat.Core.Archives.Store;

public sealed record WishlistEntry(int AppId, long? AddedAt, int? Priority);

/// <summary>还在愿望单上的记录。</summary>
public sealed record ActiveWishlistItem(int AppId, long AddedAt);

public sealed record WishlistChanges(
    IReadOnlyList<WishlistEntry> Added,
    IReadOnlyList<int> Removed,
    IReadOnlyList<WishlistEntry> Updated);

/// <summary>
/// 比较数据库里的愿望单和这次取到的列表。
/// IWishlistService 对私密资料和空愿望单都返回 {"response":{}}，分不出来（线上实测），
/// 所以空结果绝不当成「全部移除」，除非这次请求带了登录令牌、而且是连续第二次为空。
/// </summary>
public static class WishlistDiff
{
    public const int EmptyRepliesToTrust = 2;

    public static WishlistChanges Compute(
        IReadOnlyCollection<ActiveWishlistItem> active,
        IReadOnlyCollection<WishlistEntry> fetched,
        bool authenticated,
        int emptyReplies)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(fetched);
        if (fetched.Count == 0 && active.Count > 0 && !(authenticated && emptyReplies >= EmptyRepliesToTrust))
            return new WishlistChanges([], [], []);

        var current = active.GroupBy(item => item.AppId).ToDictionary(group => group.Key, group => group.Max(item => item.AddedAt));
        var seen = fetched.Select(item => item.AppId).ToHashSet();
        var added = new List<WishlistEntry>();
        var updated = new List<WishlistEntry>();
        var removed = current.Keys.Where(appId => !seen.Contains(appId)).ToList();
        foreach (var item in fetched)
        {
            if (!current.TryGetValue(item.AppId, out var addedAt))
            {
                added.Add(item);
            }
            else if (item.AddedAt is { } date && date != addedAt)
            {
                // 两次检查之间移除又加回来了：旧的一段结束，新的一段开始
                removed.Add(item.AppId);
                added.Add(item);
            }
            else
            {
                updated.Add(item);
            }
        }
        return new WishlistChanges(added, removed, updated);
    }
}
