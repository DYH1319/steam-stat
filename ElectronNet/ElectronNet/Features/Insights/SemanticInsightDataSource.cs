using ElectronNet.Features.Semantic;
using ElectronNet.Services;
using SteamStat.Core.Insights;

namespace ElectronNet.Features.Insights;

/// <summary>洞察检测器的数据入口，只能读语义视图，所以隐藏游戏和口径设置自动生效。</summary>
internal sealed class SemanticInsightDataSource(SemanticLayerService semanticLayer) : IInsightDataSource
{
    public async Task<IReadOnlyList<object?[]>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var result = await semanticLayer.QueryAsync(sql, parameters: parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Failure == SemanticQueryFailure.None
            ? result.Rows
            : throw new InvalidOperationException($"Insight query failed: {result.Failure}");
    }
}
