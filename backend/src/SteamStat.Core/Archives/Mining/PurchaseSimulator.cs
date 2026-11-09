namespace SteamStat.Core.Archives.Mining;

public sealed record SimulatorTarget(
    int AppId,
    string Name,
    IReadOnlyList<int> TagIds,
    IReadOnlyList<int> CategoryIds,
    IReadOnlyList<string> Developers,
    string? Franchise,
    long? PriceCents);

/// <summary>库里的一款游戏。Dropped 是生命周期判定（手动状态优先），Hours 是累计时长。</summary>
public sealed record SimulatorGame(
    int AppId,
    string Name,
    IReadOnlyList<int> TagIds,
    IReadOnlyList<string> Developers,
    string? Franchise,
    double Hours,
    bool Dropped);

public sealed record SimulatorNeighbor(int AppId, string Name, double Similarity, double Hours, bool Dropped);

/// <summary>
/// 模拟结果。样本不够时 Enough 是 false，只给出相似的游戏，不给预测：
/// 三款游戏算出来的中位数没有意义，第四节的显著性门槛在这里同样适用。
/// </summary>
public sealed record SimulatorResult(
    bool Enough,
    int Candidates,
    double? MedianHours,
    double? LowHours,
    double? HighHours,
    double? DropProbability,
    double? NeverPlayedProbability,
    double? CostPerHourCents,
    IReadOnlyList<SimulatorNeighbor> Neighbors);

/// <summary>
/// 购前模拟：用库里相似的游戏预测这款你会玩多久。相似度是加权 Jaccard，靠前的标签权重大，
/// 同开发商、同系列再加分。不是模型，只是把你自己的历史翻出来，所以界面要列出它依据了哪些游戏。
/// </summary>
public static class PurchaseSimulator
{
    public const int MinNeighbors = 5;
    private const int MaxNeighbors = 12;
    private const double MinSimilarity = 0.2;
    private const double NeverPlayedHours = 0.5;

    public static SimulatorResult Simulate(SimulatorTarget target, IReadOnlyCollection<SimulatorGame> library)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(library);
        var weights = TagWeights(target.TagIds);
        var neighbors = library
            .Where(game => game.AppId != target.AppId && game.TagIds.Count > 0)
            .Select(game => new SimulatorNeighbor(game.AppId, game.Name, Similarity(weights, target, game), game.Hours, game.Dropped))
            .Where(neighbor => neighbor.Similarity >= MinSimilarity)
            .OrderByDescending(neighbor => neighbor.Similarity)
            .ThenByDescending(neighbor => neighbor.Hours)
            .ToArray();
        var picked = neighbors.Take(MaxNeighbors).ToArray();
        if (picked.Length < MinNeighbors)
            return new SimulatorResult(false, neighbors.Length, null, null, null, null, null, null, picked);

        var median = WeightedQuantile(picked, 0.5);
        var total = picked.Sum(neighbor => neighbor.Similarity);
        var never = picked.Where(neighbor => neighbor.Hours < NeverPlayedHours).Sum(neighbor => neighbor.Similarity) / total;
        var dropped = picked.Where(neighbor => neighbor.Dropped).Sum(neighbor => neighbor.Similarity) / total;
        return new SimulatorResult(
            true,
            neighbors.Length,
            Math.Round(median, 1),
            Math.Round(WeightedQuantile(picked, 0.25), 1),
            Math.Round(WeightedQuantile(picked, 0.75), 1),
            Math.Round(dropped, 3),
            Math.Round(never, 3),
            target.PriceCents is { } price && median > 0 ? Math.Round(price / median, 1) : null,
            picked);
    }

    /// <summary>商店按票数排序标签，第一个的权重是第十二个的三倍多。</summary>
    private static Dictionary<int, double> TagWeights(IReadOnlyList<int> tagIds)
    {
        var weights = new Dictionary<int, double>();
        for (var index = 0; index < tagIds.Count; index++) weights.TryAdd(tagIds[index], 1 / (1 + index / 5.0));
        return weights;
    }

    private static double Similarity(Dictionary<int, double> weights, SimulatorTarget target, SimulatorGame game)
    {
        if (weights.Count == 0) return 0;
        var other = TagWeights(game.TagIds);
        double intersection = 0, union = 0;
        foreach (var tag in weights.Keys.Union(other.Keys))
        {
            var left = weights.GetValueOrDefault(tag);
            var right = other.GetValueOrDefault(tag);
            intersection += Math.Min(left, right);
            union += Math.Max(left, right);
        }
        var score = union == 0 ? 0 : intersection / union;
        if (target.Developers.Intersect(game.Developers, StringComparer.OrdinalIgnoreCase).Any()) score += 0.08;
        if (target.Franchise != null && string.Equals(target.Franchise, game.Franchise, StringComparison.OrdinalIgnoreCase)) score += 0.12;
        return Math.Min(1, Math.Round(score, 3));
    }

    private static double WeightedQuantile(IReadOnlyCollection<SimulatorNeighbor> neighbors, double quantile)
    {
        var ordered = neighbors.OrderBy(neighbor => neighbor.Hours).ToArray();
        var threshold = neighbors.Sum(neighbor => neighbor.Similarity) * quantile;
        double running = 0;
        foreach (var neighbor in ordered)
        {
            running += neighbor.Similarity;
            if (running >= threshold) return neighbor.Hours;
        }
        return ordered[^1].Hours;
    }
}
