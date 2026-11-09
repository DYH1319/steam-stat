namespace SteamStat.Core.Insights;

/// <summary>奇观榜的一项。Value 的含义看榜：价格、未碰的天数或时长。</summary>
public sealed record SpectacleItem(int AppId, string Name, double Value, string? Currency = null, long? Time = null, int PlaytimeMinutes = 0);

public sealed record SpectacleBoard(IReadOnlyList<SpectacleItem> PriciestUnplayed, IReadOnlyList<SpectacleItem> LongestUntouched, IReadOnlyList<SpectacleItem> Delisted);

/// <summary>库存奇观榜：最贵的未启动游戏、最久没碰的老游戏、库里已下架的游戏。</summary>
public static class SpectacleAnalyzer
{
    private const int BoardSize = 10;

    public static SpectacleBoard Build(InsightArchive archive)
    {
        var manual = archive.Statuses.GroupBy(item => item.AppId).ToDictionary(group => group.Key, group => group.First());
        var lastSession = archive.Sessions.GroupBy(session => session.AppId).ToDictionary(group => group.Key, group => group.Max(session => session.End));

        // 不同币种不能比较：用户录入的价格优先展示，排序统一按美区标价，没有标价的排在后面
        var priciest = archive.Owned
            .Where(game => game.PlaytimeMinutes == 0 && !lastSession.ContainsKey(game.AppId))
            .Select(game => (Game: game, Cents: archive.FactsOf(game.AppId)?.PriceCents, Manual: manual.GetValueOrDefault(game.AppId)))
            .Where(item => item.Cents is > 0 || item.Manual?.Price is > 0)
            .OrderByDescending(item => item.Cents ?? 0)
            .ThenByDescending(item => item.Manual?.Price ?? 0)
            .Take(BoardSize)
            .Select(item => item.Manual?.Price is > 0
                ? new SpectacleItem(item.Game.AppId, item.Game.Name, item.Manual.Price.Value, item.Manual.Currency)
                : new SpectacleItem(item.Game.AppId, item.Game.Name, item.Cents!.Value / 100.0, "USD"))
            .ToList();

        var untouched = archive.Owned
            .Select(game => (Game: game, Last: Math.Max(game.LastPlayed, lastSession.GetValueOrDefault(game.AppId))))
            .Where(item => item.Last > 0 && item.Game.PlaytimeMinutes > 0)
            .OrderBy(item => item.Last)
            .Take(BoardSize)
            .Select(item => new SpectacleItem(item.Game.AppId, item.Game.Name, (archive.Now - item.Last) / 86400, null, item.Last, item.Game.PlaytimeMinutes))
            .ToList();

        var delisted = archive.Owned
            .Where(game => archive.FactsOf(game.AppId)?.Delisted == true)
            .OrderByDescending(game => game.PlaytimeMinutes)
            .Take(BoardSize)
            .Select(game => new SpectacleItem(game.AppId, game.Name, game.PlaytimeMinutes / 60.0, null, archive.FactsOf(game.AppId)?.DelistedAt, game.PlaytimeMinutes))
            .ToList();

        return new SpectacleBoard(priciest, untouched, delisted);
    }
}
