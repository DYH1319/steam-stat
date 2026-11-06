using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElectronNet.Features.Semantic;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 年度封存包：每年一份开放格式的档案，生成后只读。放在 {UserData}/Sealed，已存在的年份绝不覆盖。
/// 新年后自动生成上一年；已封存的文件被改动或丢失时由档案体检提示。
/// </summary>
internal sealed class ArchiveSealService(
    IAppPaths appPaths,
    SemanticLayerService semanticLayer,
    ISettingsStore settingsStore,
    AppEnvironment environment,
    IShellLauncher shell,
    TimeProvider timeProvider,
    ILogger<ArchiveSealService> logger) : IDisposable
{
    private static readonly TimeSpan AutoSealDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(2);
    private const int MaxRows = 2_000_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private string Directory_ => Path.Combine(appPaths.UserDataDirectory, "Sealed");
    private string RegistryFile => Path.Combine(Directory_, "seals.json");
    private string FileOf(int year) => Path.Combine(Directory_, $"steam-stat-archive-{year}.zip");

    private sealed class SealRecord
    {
        [JsonPropertyName("year")] public int Year { get; init; }
        [JsonPropertyName("sealedAt")] public long SealedAt { get; init; }
        [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
        [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = string.Empty;
        [JsonPropertyName("sessionCount")] public int SessionCount { get; init; }
    }

    public void StartSchedule() => _ = RunAutoSealAsync();

    private async Task RunAutoSealAsync()
    {
        try
        {
            await Task.Delay(AutoSealDelay, timeProvider, _lifetime.Token).ConfigureAwait(false);
            if (settingsStore.GetSettings().Archive?.AnnualSealEnabled != true) return;
            foreach (var year in await GetAvailableYearsAsync(_lifetime.Token).ConfigureAwait(false))
                await CreateAsync(year, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Automatic archive sealing failed");
        }
    }

    private int CurrentLogicalYear()
    {
        var boundary = settingsStore.GetSettings().Analytics?.DayBoundaryHour ?? 4;
        return TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone).AddHours(-boundary).Year;
    }

    private async Task<IReadOnlyList<int>> GetAvailableYearsAsync(CancellationToken cancellationToken)
    {
        var result = await semanticLayer.QueryAsync(
            "SELECT DISTINCT CAST(substr(logical_day, 1, 4) AS INTEGER) FROM v_session ORDER BY 1",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Failure != SemanticQueryFailure.None) return [];
        var sealedYears = ReadRegistry().Select(record => record.Year).ToHashSet();
        var current = CurrentLogicalYear();
        return result.Rows
            .Select(row => Convert.ToInt32(row[0], CultureInfo.InvariantCulture))
            .Where(year => year < current && !sealedYears.Contains(year))
            .ToList();
    }

    public async Task<ArchiveSealOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var seals = new List<ArchiveSealDto>();
        foreach (var record in ReadRegistry().OrderByDescending(item => item.Year))
            seals.Add(ToDto(record, await IsIntactAsync(record, cancellationToken).ConfigureAwait(false)));
        return new ArchiveSealOverviewDto
        {
            Seals = seals,
            AvailableYears = await GetAvailableYearsAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    public async Task<ArchiveSealResultDto> CreateAsync(int year, CancellationToken cancellationToken = default)
    {
        if (year >= CurrentLogicalYear()) return new ArchiveSealResultDto { Status = "notFinished" };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = FileOf(year) + ".tmp";
        try
        {
            var registry = ReadRegistry();
            var existing = registry.FirstOrDefault(record => record.Year == year);
            if (existing != null || File.Exists(FileOf(year)))
                return new ArchiveSealResultDto { Status = "exists", Seal = existing == null ? null : ToDto(existing, true) };

            Directory.CreateDirectory(Directory_);
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var scope = new OpenArchiveScope($"{year}-01-01", $"{year}-12-31", year.ToString(CultureInfo.InvariantCulture));
            var metadata = new OpenArchiveMetadata(
                environment.AppVersion ?? "0.0.0",
                now,
                timeProvider.LocalTimeZone.Id,
                settingsStore.GetSettings().Analytics?.DayBoundaryHour ?? 4);

            OpenArchiveSummary summary;
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                summary = await OpenArchiveExporter.WriteAsync(stream, scope, metadata, QueryAsync, cancellationToken).ConfigureAwait(false);
            if (summary.SessionCount == 0)
            {
                File.Delete(temporary);
                return new ArchiveSealResultDto { Status = "empty" };
            }

            File.Move(temporary, FileOf(year));
            File.SetAttributes(FileOf(year), File.GetAttributes(FileOf(year)) | FileAttributes.ReadOnly);
            var record = new SealRecord
            {
                Year = year,
                SealedAt = now,
                SizeBytes = new FileInfo(FileOf(year)).Length,
                Sha256 = await HashAsync(FileOf(year), cancellationToken).ConfigureAwait(false),
                AppVersion = metadata.AppVersion,
                SessionCount = summary.SessionCount
            };
            await WriteRegistryAsync([.. registry, record], cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Sealed the {Year} archive ({Sessions} sessions)", year, summary.SessionCount);
            return new ArchiveSealResultDto { Status = "sealed", Seal = ToDto(record, true) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Failed to seal the {Year} archive", year);
            return new ArchiveSealResultDto { Status = "failed" };
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    public async Task<bool> RevealAsync(int year)
        => File.Exists(FileOf(year)) && await shell.ShowItemInFolderAsync(FileOf(year)).ConfigureAwait(false);

    private Task<SemanticQueryResult> QueryAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        => SemanticQueryGateway.ExecuteAsync(
            appPaths.DatabaseFile, sql, SemanticLayerService.AllTiers, parameters, MaxRows, QueryTimeout, cancellationToken);

    private async Task<bool> IsIntactAsync(SealRecord record, CancellationToken cancellationToken)
    {
        var file = FileOf(record.Year);
        if (!File.Exists(file) || new FileInfo(file).Length != record.SizeBytes) return false;
        return string.Equals(await HashAsync(file, cancellationToken).ConfigureAwait(false), record.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> HashAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static ArchiveSealDto ToDto(SealRecord record, bool intact) => new()
    {
        Year = record.Year,
        SealedAt = record.SealedAt,
        SizeBytes = record.SizeBytes,
        Sha256 = record.Sha256,
        AppVersion = record.AppVersion,
        SessionCount = record.SessionCount,
        Intact = intact
    };

    private List<SealRecord> ReadRegistry()
    {
        try
        {
            return File.Exists(RegistryFile)
                ? JsonSerializer.Deserialize<List<SealRecord>>(File.ReadAllText(RegistryFile)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            logger.LogWarning(exception, "Failed to read the seal registry");
            return [];
        }
    }

    private async Task WriteRegistryAsync(IReadOnlyList<SealRecord> records, CancellationToken cancellationToken)
    {
        var temporary = RegistryFile + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, RegistryFile, overwrite: true);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
