using System.Security.Cryptography;
using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Features.DataTransfer;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Features;

namespace ElectronNet.Services;

/// <summary>
/// 数据导出：表格（CSV / xlsx）与完整 JSON。
/// 保存位置由用户在系统对话框里选择；先写临时文件，完成后再改名，失败或取消不会留下半个文件。
/// 只读数据库，不导出 Steam 凭证和资源缓存。
/// </summary>
internal sealed class DataExportService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IMainWindowAccessor mainWindowAccessor,
    IShellLauncher shell,
    RevealRegistry revealRegistry,
    AppEnvironment environment,
    DeviceService deviceService,
    TimeProvider timeProvider,
    ILogger<DataExportService> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DataExportResultDto> ExportAsync(DataExportRequest request, IProgress<TaskProgressUpdate>? progress = null)
    {
        var datasets = NormalizeDatasets(request);
        if (datasets == null) return Failed("invalidDatasets");

        var target = await PickTargetAsync(request.Format, datasets).ConfigureAwait(false);
        if (target == null) return new DataExportResultDto { Status = "cancelled", SizeBytes = 0, Tables = [] };
        return await ExportToAsync(request, target, progress).ConfigureAwait(false);
    }

    /// <summary>
    /// 写到指定位置：json / xlsx / 单个 csv 为文件路径；多个 csv 时为目录（每个数据集一个文件）。
    /// </summary>
    internal async Task<DataExportResultDto> ExportToAsync(DataExportRequest request, string target, IProgress<TaskProgressUpdate>? progress = null)
    {
        var datasets = NormalizeDatasets(request);
        if (datasets == null) return Failed("invalidDatasets");
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return Failed("busy");
        try
        {
            var filter = new ExportFilter(request.From, request.To);
            var timeZone = timeProvider.LocalTimeZone;
            IReadOnlyList<ExportTableResult> tables;
            long size;
            switch (request.Format)
            {
                case "json":
                    tables = await WriteFileAsync(target, stream => WriteJsonAsync(stream, progress)).ConfigureAwait(false);
                    size = new FileInfo(target).Length;
                    break;
                case "xlsx":
                    tables = await WriteFileAsync(target, async stream =>
                    {
                        await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
                        var lookups = await ExportDatasets.Lookups.LoadAsync(db, CancellationToken.None).ConfigureAwait(false);
                        var list = datasets.Select(id => ExportDatasets.Create(id, db, lookups, filter)).ToList();
                        return await XlsxWorkbookWriter.WriteAsync(stream, list, timeZone, progress).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    size = new FileInfo(target).Length;
                    break;
                default:
                    (tables, size) = await WriteCsvAsync(target, datasets, filter, timeZone, progress).ConfigureAwait(false);
                    break;
            }

            var exportId = revealRegistry.Register(target);
            logger.LogInformation("Exported {Format} data ({Tables} tables, {Size} bytes)", request.Format, tables.Count, size);
            return new DataExportResultDto
            {
                Status = "completed",
                ExportId = exportId,
                Path = target,
                SizeBytes = size,
                Tables = tables.Select(table => new DataExportTableDto { Id = table.Id, Rows = table.Rows, Truncated = table.Truncated }).ToList()
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to write the export");
            return Failed("io");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Data export failed");
            return Failed("failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RevealAsync(string exportId)
    {
        var path = revealRegistry.Resolve(exportId);
        if (path == null) return false;
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        return await shell.ShowItemInFolderAsync(path).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ExportTableResult>> WriteJsonAsync(Stream stream, IProgress<TaskProgressUpdate>? progress)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var migration = (await db.Database.GetAppliedMigrationsAsync().ConfigureAwait(false)).LastOrDefault();
        var deviceId = deviceService.CurrentDeviceId;
        await db.Database.OpenConnectionAsync().ConfigureAwait(false);
        try
        {
            var metadata = new DatabaseJsonMetadata(
                environment.AppVersion ?? "0.0.0",
                timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                migration,
                timeProvider.LocalTimeZone.Id,
                deviceId);
            return await DatabaseJsonExporter.WriteAsync(stream, db.Database.GetDbConnection(), metadata, progress).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<(IReadOnlyList<ExportTableResult> Tables, long Size)> WriteCsvAsync(
        string target, IReadOnlyList<string> datasets, ExportFilter filter, TimeZoneInfo timeZone, IProgress<TaskProgressUpdate>? progress)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var lookups = await ExportDatasets.Lookups.LoadAsync(db, CancellationToken.None).ConfigureAwait(false);
        if (datasets.Count == 1)
        {
            var single = await WriteFileAsync(target, async stream =>
                (IReadOnlyList<ExportTableResult>)[await CsvTableWriter.WriteAsync(stream, ExportDatasets.Create(datasets[0], db, lookups, filter), timeZone).ConfigureAwait(false)])
                .ConfigureAwait(false);
            return (single, new FileInfo(target).Length);
        }

        Directory.CreateDirectory(target);
        var results = new List<ExportTableResult>();
        long size = 0;
        foreach (var id in datasets)
        {
            progress?.Report(new TaskProgressUpdate("tables", results.Count, datasets.Count));
            var file = Path.Combine(target, $"{id}.csv");
            var result = await WriteFileAsync(file, async stream =>
                (IReadOnlyList<ExportTableResult>)[await CsvTableWriter.WriteAsync(stream, ExportDatasets.Create(id, db, lookups, filter), timeZone).ConfigureAwait(false)])
                .ConfigureAwait(false);
            results.AddRange(result);
            size += new FileInfo(file).Length;
        }
        return (results, size);
    }

    /// <summary>先写同目录下的临时文件，成功后覆盖目标；失败时删除临时文件。</summary>
    private static async Task<IReadOnlyList<ExportTableResult>> WriteFileAsync(string path, Func<Stream, Task<IReadOnlyList<ExportTableResult>>> write)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
        try
        {
            IReadOnlyList<ExportTableResult> results;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                results = await write(stream).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
            return results;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<string?> PickTargetAsync(string format, IReadOnlyList<string> datasets)
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return null;
        var stamp = timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        try
        {
            if (format == "csv" && datasets.Count > 1)
            {
                var folders = await Electron.Dialog.ShowOpenDialogAsync(snapshot.Window, new OpenDialogOptions
                {
                    DefaultPath = documents,
                    Properties = [OpenDialogProperty.openDirectory, OpenDialogProperty.createDirectory]
                }).ConfigureAwait(false);
                return folders is { Length: > 0 } ? UniqueDirectory(Path.Combine(folders[0], $"steam-stat-export-{stamp}")) : null;
            }

            var (name, filterName) = format switch
            {
                "json" => ($"steam-stat-export-{stamp}.json", "JSON"),
                "xlsx" => ($"steam-stat-export-{stamp}.xlsx", "Excel"),
                _ => ($"steam-stat-{datasets[0]}-{stamp}.csv", "CSV")
            };
            var path = await Electron.Dialog.ShowSaveDialogAsync(snapshot.Window, new SaveDialogOptions
            {
                DefaultPath = string.IsNullOrEmpty(documents) ? name : Path.Combine(documents, name),
                Filters = [new FileFilter { Name = filterName, Extensions = [format] }]
            }).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Path.HasExtension(path) ? path : $"{path}.{format}";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show the export location dialog");
            return null;
        }
    }

    private static string UniqueDirectory(string path)
    {
        var candidate = path;
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++) candidate = $"{path}-{suffix}";
        return candidate;
    }

    /// <summary>表格导出至少一个数据集且全部已知；JSON 总是导出全部表。</summary>
    internal static IReadOnlyList<string>? NormalizeDatasets(DataExportRequest request)
    {
        if (request.Format == "json") return ExportDatasets.All;
        var requested = request.Datasets ?? [];
        if (requested.Count == 0 || requested.Any(id => !ExportDatasets.All.Contains(id))) return null;
        // 按固定顺序输出，去重
        return ExportDatasets.All.Where(requested.Contains).ToList();
    }

    private static DataExportResultDto Failed(string error)
        => new() { Status = "failed", SizeBytes = 0, Tables = [], Error = error };

    public void Dispose() => _gate.Dispose();
}
