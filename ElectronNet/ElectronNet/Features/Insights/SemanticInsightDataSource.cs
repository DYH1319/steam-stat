using ElectronNet.Features.Semantic;
using ElectronNet.Services;
using SteamStat.Core.Insights;

namespace ElectronNet.Features.Insights;

/// <summary>洞察的数据入口，只能读语义视图，所以隐藏游戏和口径设置自动生效。</summary>
internal sealed class SemanticInsightDataSource(SemanticLayerService semanticLayer) : IInsightDataSource
{
    /// <summary>界面查询的默认上限是 5000 行。洞察要读完整档案，十年压测里最大的一张有一百多万行。</summary>
    private const int MaxRows = 5_000_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<IReadOnlyList<object?[]>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var result = await semanticLayer.QueryAsync(sql, parameters: parameters, maxRows: MaxRows, timeout: Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Failure == SemanticQueryFailure.None
            ? result.Rows
            : throw new InvalidOperationException($"Insight query failed: {result.Failure}");
    }
}
