namespace SteamStat.Core.Insights;

/// <summary>
/// 游戏股市版：时长是成交量。价格定义为「当天往前七天的时长」，
/// 这样 K 线的开高低收都是真实数字，不是为了好看编出来的。
/// </summary>
internal static class WeeklyMarketBuilder
{
    private const int Weeks = 12;
    private const int Movers = 3;
    private const double MinimumVolumeHours = 0.5;

    internal static WeeklyMarket? Build(InsightArchive archive, InsightPeriod period)
    {
        var to = InsightDays.Parse(period.ToDay);
        var from = to.AddDays(-(Weeks * 7 + 6));
        var daily = new Dictionary<int, double>();
        foreach (var session in archive.SessionsIn(InsightDays.Format(from), period.ToDay))
        {
            var day = InsightDays.Parse(session.Day).DayNumber;
            daily[day] = daily.GetValueOrDefault(day) + InsightMath.Hours(session.Seconds);
        }
        if (daily.Count == 0) return null;

        double Price(int dayNumber)
        {
            double sum = 0;
            for (var offset = 0; offset < 7; offset++) sum += daily.GetValueOrDefault(dayNumber - offset);
            return sum;
        }

        var candles = new List<WeeklyCandle>();
        for (var week = Weeks - 1; week >= 0; week--)
        {
            var end = to.AddDays(-week * 7).DayNumber;
            var start = end - 6;
            var prices = Enumerable.Range(start, 7).Select(Price).ToList();
            var open = Price(start - 1);
            candles.Add(new WeeklyCandle(
                InsightDays.Format(DateOnly.FromDayNumber(start)),
                Round(open),
                Round(Math.Max(open, prices.Max())),
                Round(Math.Min(open, prices.Min())),
                Round(prices[^1]),
                Round(prices[^1])));
        }
        // 只有一两周数据时 K 线和涨跌都没有意义
        if (candles.Count(candle => candle.Volume > 0) < 2) return null;

        var current = PerApp(archive, period.FromDay, period.ToDay);
        var previous = PerApp(archive, InsightDays.Shift(period.FromDay, -7), InsightDays.Shift(period.ToDay, -7));
        var stocks = current.Keys.Union(previous.Keys)
            .Select(appId =>
            {
                var (volume, name) = current.GetValueOrDefault(appId);
                var (before, oldName) = previous.GetValueOrDefault(appId);
                return new WeeklyStock(appId, name ?? oldName ?? $"App {appId}", Round(volume), Round(before), before > 0 ? Round((volume - before) / before, 3) : null);
            })
            .Where(stock => Math.Max(stock.Volume, stock.Previous) >= MinimumVolumeHours)
            .ToList();
        var gainers = stocks
            .Where(stock => stock.Volume > stock.Previous)
            .OrderByDescending(stock => stock.Volume - stock.Previous)
            .ThenBy(stock => stock.AppId)
            .Take(Movers)
            .ToList();
        var losers = stocks
            .Where(stock => stock.Volume < stock.Previous)
            .OrderBy(stock => stock.Volume - stock.Previous)
            .ThenBy(stock => stock.AppId)
            .Take(Movers)
            .ToList();

        var index = candles[^1].Close;
        var last = candles[^2].Close;
        double? change = last > 0 ? Round((index - last) / last, 3) : null;
        return new WeeklyMarket(Commentary(index, change, gainers, losers), index, change, candles, gainers, losers);
    }

    private static Bilingual Commentary(double index, double? change, IReadOnlyList<WeeklyStock> gainers, IReadOnlyList<WeeklyStock> losers)
    {
        var indexText = Say.Number(index);
        var parts = new List<Bilingual>
        {
            change is { } value
                ? Say.Fill(WeeklyCopy.MarketBase, ("index", indexText), ("change", Say.Percent(Math.Abs(value))), ("direction", value >= 0 ? new Bilingual("涨", "up") : new Bilingual("跌", "down")))
                : Say.Fill(WeeklyCopy.MarketFlat, ("index", indexText))
        };
        if (gainers.Count > 0)
        {
            var top = gainers[0];
            parts.Add(Say.Fill(WeeklyCopy.MarketLeader, ("game", Say.Game(top.AppId, top.Name)), ("hours", Say.Hours(top.Volume))));
        }
        if (losers.Count > 0)
        {
            var bottom = losers[0];
            parts.Add(Say.Fill(WeeklyCopy.MarketLaggard, ("game", Say.Game(bottom.AppId, bottom.Name))));
        }
        return Say.Join("", " ", parts);
    }

    private static Dictionary<int, (double Hours, string? Name)> PerApp(InsightArchive archive, string from, string to)
        => archive.SessionsIn(from, to)
            .GroupBy(session => session.AppId)
            .ToDictionary(group => group.Key, group => (InsightMath.Hours(group.Sum(item => item.Seconds)), (string?)group.First().Name));

    private static double Round(double value, int digits = 1) => Math.Round(value, digits);
}
