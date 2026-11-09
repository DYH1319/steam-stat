using System.Text.Json;
using ElectronNet.Features.Semantic;
using ElectronNet.Hosting;
using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 语义视图层的安装、口径同步和查询入口。Host 内部（封存包、指标字典）和以后的外部接口都经这里读数据。
/// </summary>
internal sealed class SemanticLayerService(
    IAppPaths appPaths,
    ISettingsStore settingsStore,
    ILogger<SemanticLayerService> logger) : IEventHandler<AppSettingsChanged>
{
    /// <summary>界面本身能看到全部数据，隐私分级只约束外部调用方。</summary>
    internal static readonly IReadOnlySet<string> AllTiers = SemanticTier.All.ToHashSet();

    private readonly SemaphoreSlim _configGate = new(1, 1);

    /// <summary>启动时、迁移之后调用。</summary>
    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile, pooling: false));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SemanticSchema.InstallAsync(connection, settingsStore.GetSettings(), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Installed {ViewCount} semantic views (contract v{Version})", SemanticSchema.Views.Count, SemanticSchema.ContractVersion);
    }

    /// <summary>分界点、隐藏游戏、清洗规则变化后，视图要马上用新口径。</summary>
    public async Task HandleAsync(AppSettingsChanged message, CancellationToken cancellationToken)
    {
        await _configGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile, pooling: false));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await SemanticSchema.WriteConfigAsync(connection, settingsStore.GetSettings(), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to apply analytics settings to the semantic layer");
        }
        finally
        {
            _configGate.Release();
        }
    }

    internal Task<SemanticQueryResult> QueryAsync(
        string sql,
        IReadOnlySet<string>? tiers = null,
        IReadOnlyDictionary<string, object?>? parameters = null,
        int maxRows = SemanticQueryGateway.DefaultMaxRows,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => SemanticQueryGateway.ExecuteAsync(appPaths.DatabaseFile, sql, tiers ?? AllTiers, parameters, maxRows, timeout, cancellationToken);

    public async Task<SemanticQueryResultDto> QueryForIpcAsync(SemanticQueryRequest request, CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(request.Sql, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new SemanticQueryResultDto
        {
            Status = result.Failure switch
            {
                SemanticQueryFailure.None => "ok",
                SemanticQueryFailure.Denied => "denied",
                SemanticQueryFailure.Timeout => "timeout",
                _ => "invalid"
            },
            Columns = result.Columns,
            RowsJson = JsonSerializer.Serialize(result.Rows),
            RowCount = result.Rows.Count,
            Truncated = result.Truncated,
            ElapsedMilliseconds = result.ElapsedMilliseconds
        };
    }

    public async Task<SemanticCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var views = new List<SemanticViewDto>();
        await using (var connection = new SqliteConnection(SqliteConnectionStrings.Create(appPaths.DatabaseFile, pooling: false, readOnly: true)))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            foreach (var view in SemanticSchema.Views)
                views.Add(new SemanticViewDto
                {
                    Name = view.Name,
                    Tier = view.Tier,
                    Description = SemanticCatalog.ViewDescriptions[view.Name],
                    Columns = await DescribeColumnsAsync(connection, view.Name, cancellationToken).ConfigureAwait(false)
                });
        }

        var metrics = new List<MetricDefinitionDto>(MetricCatalog.Metrics.Count);
        foreach (var metric in MetricCatalog.Metrics)
        {
            var result = await QueryAsync(metric.Sql, cancellationToken: cancellationToken).ConfigureAwait(false);
            double? value = null;
            if (result.Failure == SemanticQueryFailure.None && result.Rows is [{ Length: > 0 } row, ..])
                value = row[0] == null ? 0 : Convert.ToDouble(row[0]);
            else if (result.Failure != SemanticQueryFailure.None)
                logger.LogWarning("Metric {Metric} failed with {Failure}", metric.Id, result.Failure);
            metrics.Add(metric with { Value = value });
        }
        return new SemanticCatalogDto { ContractVersion = SemanticSchema.ContractVersion, Views = views, Metrics = metrics };
    }

    private static async Task<IReadOnlyList<SemanticColumnDto>> DescribeColumnsAsync(SqliteConnection connection, string view, CancellationToken cancellationToken)
    {
        var columns = new List<SemanticColumnDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({SqliteSchema.Quote(view)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            columns.Add(new SemanticColumnDto
            {
                Name = name,
                Type = reader.IsDBNull(2) || reader.GetString(2).Length == 0 ? "ANY" : reader.GetString(2),
                Description = SemanticCatalog.DescribeColumn(view, name) ?? new BilingualTextDto(name, name)
            });
        }
        return columns;
    }
}
