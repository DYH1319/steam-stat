using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElectronNet.Hosting;
using ElectronNet.Services;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Diagnostics;
using SteamStat.Core.Environment;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Features.Diagnostics;

/// <summary>
/// 应用内反馈：运行环境概要、脱敏诊断包（日志 + 系统信息 + 崩溃报告）与预填 GitHub Issue。
/// 诊断包只写到 {UserData}/Diagnostics，由用户自行拖进 Issue；应用不会上传任何文件。
/// </summary>
internal sealed partial class DiagnosticsService(
    AppEnvironment environment,
    AppDistribution distribution,
    ISettingsStore settingsStore,
    ISecretStore secretStore,
    ISteamInstallLocator installLocator,
    LocalDataSourceService localDataSourceService,
    CrashReportService crashReportService,
    DiagnosticsRedactionProvider redactionProvider,
    IElectronAutoUpdater autoUpdater,
    IShellLauncher shell,
    TimeProvider timeProvider,
    ILogger<DiagnosticsService> logger)
{
    internal const int KeepBundles = 5;
    private const long MaxLogBytes = 20L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string BundleDirectory => Path.Combine(environment.Paths.UserDataDirectory, "Diagnostics");

    public async Task<AppInfoDto> GetAppInfoAsync()
        => new()
        {
            Version = await GetVersionAsync().ConfigureAwait(false),
            Platform = PlatformRuntime.PlatformName,
            Arch = PlatformRuntime.Arch,
            OsDescription = PlatformRuntime.OsDescription(),
            Distribution = distribution.Kind,
            UpdateMode = distribution.UpdateModeName,
            ManagedUpdateCommand = distribution.ManagedUpdateCommand,
            PortableData = distribution.PortableData,
            DataDirectory = environment.Paths.UserDataDirectory,
            SecureCredentialStorage = secretStore.CanPersist,
            SessionType = PlatformRuntime.SessionType(),
            RepositoryUrl = ProjectLinks.RepositoryUrl,
            WindowMaterialSupported = WindowMaterials.Supported
        };

    public async Task<DiagnosticsBundleDto> CreateBundleAsync(DiagnosticsBundleRequest request, CancellationToken cancellationToken = default)
    {
        var redactor = await redactionProvider.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetLocalNow();
        Directory.CreateDirectory(BundleDirectory);
        var fileName = $"steam-stat-diagnostics-{now:yyyyMMdd-HHmmss}.zip";
        var path = Path.Combine(BundleDirectory, fileName);
        var entries = new List<DiagnosticsBundleEntryDto>();
        var redactions = 0;

        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            void AddText(string name, string content)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
                entries.Add(new DiagnosticsBundleEntryDto { Name = name, SizeBytes = Encoding.UTF8.GetByteCount(content) });
            }

            AddText("README.txt", Readme(now));

            var system = redactor.Redact(JsonSerializer.Serialize(await CollectSystemInfoAsync(cancellationToken).ConfigureAwait(false), JsonOptions), out var systemRedactions);
            redactions += systemRedactions;
            AddText("system.json", system);

            foreach (var log in SelectLogFiles(request.LogDays ?? 3))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = RedactLog(log, redactor, out var logRedactions);
                redactions += logRedactions;
                AddText($"logs/{log.Name}", content);
            }

            foreach (var report in crashReportService.List())
                AddText($"crash-reports/{report.Id}.json", JsonSerializer.Serialize(report, JsonOptions));
        }

        PruneBundles();
        var info = new FileInfo(path);
        logger.LogInformation("Created diagnostics bundle with {EntryCount} entries and {RedactionCount} redactions", entries.Count, redactions);
        return new DiagnosticsBundleDto
        {
            FileName = fileName,
            FilePath = path,
            SizeBytes = info.Length,
            CreatedAt = now.ToUnixTimeSeconds(),
            Entries = entries,
            RedactionCount = redactions
        };
    }

    /// <summary>只接受本服务生成的文件名，路径由 Host 拼接。</summary>
    public async Task<bool> RevealBundleAsync(string fileName)
    {
        if (!BundleFileNamePattern().IsMatch(fileName)) return false;
        var path = Path.Combine(BundleDirectory, fileName);
        return File.Exists(path) && await shell.ShowItemInFolderAsync(path).ConfigureAwait(false);
    }

    public async Task<bool> OpenIssueAsync(FeedbackIssueRequest request)
    {
        var english = request.Language == "en-US";
        var info = request.IncludeSystemInfo == false ? null : await GetAppInfoAsync().ConfigureAwait(false);
        var redactor = redactionProvider.Current;
        var (title, body, labels) = BuildIssue(request, info, english);
        return await shell.OpenExternalAsync(GitHubIssueLink.Build(redactor.Redact(title), redactor.Redact(body), labels)).ConfigureAwait(false);
    }

    internal static (string Title, string Body, IReadOnlyCollection<string> Labels) BuildIssue(FeedbackIssueRequest request, AppInfoDto? info, bool english)
    {
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var (prefix, labels, sections) = request.Kind switch
        {
            "feature" => ("[Feature] ", new[] { "enhancement" }, english
                ? new[] { ("What would you like?", description), ("Why / use case", null) }
                : new[] { ("需求描述", description), ("使用场景", null) }),
            "question" => ("[Question] ", new[] { "question" }, english
                ? new[] { ("Question", description) }
                : new[] { ("问题", description) }),
            _ => ("[Bug] ", new[] { "bug" }, english
                ? new[] { ("What happened?", description), ("Steps to reproduce", "1. \n2. \n3. "), ("Expected behavior", null) }
                : new[] { ("问题描述", description), ("复现步骤", "1. \n2. \n3. "), ("期望结果", null) })
        };

        // 统一用 \n：AppendLine 在 Windows 上写 \r\n，会让 Issue 正文随平台变化
        var body = new StringBuilder();
        foreach (var (heading, content) in sections)
        {
            body.Append("### ").Append(heading).Append('\n').Append('\n');
            body.Append(content ?? "<!-- -->").Append('\n').Append('\n');
        }

        if (info != null)
        {
            body.Append("### ").Append(english ? "Environment" : "运行环境").Append('\n').Append('\n');
            body.Append($"- Steam Stat: {info.Version} ({info.Distribution}, {info.UpdateMode})").Append('\n');
            body.Append($"- OS: {info.OsDescription}").Append('\n');
            body.Append($"- Platform: {info.Platform} {info.Arch}{(info.SessionType == null ? string.Empty : $" ({info.SessionType})")}").Append('\n');
            body.Append('\n');
        }

        if (request.Kind == "bug")
        {
            body.Append("### ").Append(english ? "Diagnostics" : "诊断包").Append('\n').Append('\n');
            body.Append(english
                ? "<!-- Optional: generate a diagnostics bundle in Feedback & Support and drag the zip here. It is redacted locally. -->"
                : "<!-- 可选：在「反馈与支持」页生成诊断包，把 zip 文件拖到这里。诊断包已在本机脱敏。 -->").Append('\n');
        }

        var title = prefix + (request.Title?.Trim() is { Length: > 0 } custom ? custom : string.Empty);
        return (title, body.ToString().TrimEnd() + "\n", labels);
    }

    private async Task<object> CollectSystemInfoAsync(CancellationToken cancellationToken)
    {
        var settings = settingsStore.GetSettings();
        var info = await GetAppInfoAsync().ConfigureAwait(false);
        string? electron = null, chrome = null;
        try
        {
            var versions = await ElectronNET.API.Electron.Process.VersionsAsync.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            electron = versions.Electron;
            chrome = versions.Chrome;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Electron 不可用（测试或关闭阶段）时省略
        }

        SteamLocalDataSourcesDto? dataSources = null;
        try
        {
            dataSources = await localDataSourceService.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Data source health could not be collected for the diagnostics bundle");
        }

        string? steamPath = null;
        try
        {
            steamPath = installLocator.ReadSteamRegistry().SteamPath;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Steam path could not be read for the diagnostics bundle");
        }

        return new
        {
            GeneratedAt = timeProvider.GetUtcNow(),
            App = info,
            Runtime = new
            {
                DotNet = RuntimeInformation.FrameworkDescription,
                Electron = electron,
                Chrome = chrome,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                environment.Locale,
                environment.IsDevelopment,
                UptimeMinutes = (int)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMinutes,
                Desktop = OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") : null
            },
            Steam = new
            {
                Detected = !string.IsNullOrWhiteSpace(steamPath),
                Path = steamPath
            },
            Settings = new
            {
                settings.Language,
                settings.AutoUpdate,
                settings.UpdateChannel,
                settings.AutoStart,
                settings.SilentStart,
                settings.CloseAction,
                Monitoring = settings.UpdateAppRunningStatusJob,
                settings.SessionTracking?.AfkEnabled,
                settings.SessionTracking?.AfkThresholdMinutes,
                ExcludedAppCount = settings.SessionTracking?.ExcludedAppIds?.Count ?? 0,
                ProxyMode = settings.Network?.ProxyMode,
                // 只保留协议，不含代理主机与端口
                ProxyScheme = settings.Network?.ProxyUrl is { Length: > 0 } proxy && Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri) ? proxyUri.Scheme : null,
                settings.Network?.CnOptimized,
                settings.Archive?.SaveBackupEnabled,
                settings.Archive?.ScreenshotIndexEnabled,
                settings.Diagnostics?.CrashReportsEnabled
            },
            Database = DatabaseSummary(),
            DataSources = dataSources?.Sources.Select(source => new { source.Id, source.Status, source.Error, source.ItemCount })
        };
    }

    private object DatabaseSummary()
    {
        try
        {
            var file = new FileInfo(environment.Paths.DatabaseFile);
            return new { Exists = file.Exists, SizeBytes = file.Exists ? file.Length : 0 };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new { Exists = false, SizeBytes = 0L };
        }
    }

    private IEnumerable<FileInfo> SelectLogFiles(int days)
    {
        var directory = new DirectoryInfo(environment.Paths.LogsDirectory);
        if (!directory.Exists) yield break;
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 14));
        long total = 0;
        foreach (var file in directory.GetFiles("steam-stat-*.log").Where(file => file.LastWriteTimeUtc >= since).OrderByDescending(file => file.LastWriteTimeUtc))
        {
            if (total + file.Length > MaxLogBytes && total > 0) yield break;
            total += file.Length;
            yield return file;
        }
    }

    private static string RedactLog(FileInfo file, DiagnosticsRedactor redactor, out int redactions)
    {
        redactions = 0;
        // Serilog 持有当前日志文件，必须以共享读写方式打开
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var builder = new StringBuilder((int)Math.Min(file.Length, int.MaxValue / 2));
        while (reader.ReadLine() is { } line)
        {
            builder.AppendLine(redactor.Redact(line, out var count));
            redactions += count;
        }
        return builder.ToString();
    }

    private void PruneBundles()
    {
        foreach (var file in new DirectoryInfo(BundleDirectory).GetFiles("steam-stat-diagnostics-*.zip")
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(KeepBundles))
        {
            try
            {
                file.Delete();
            }
            catch (IOException exception)
            {
                logger.LogDebug(exception, "Failed to delete an old diagnostics bundle");
            }
        }
    }

    private async Task<string> GetVersionAsync()
    {
        if (environment.AppVersion != null) return environment.AppVersion;
        try
        {
            return await autoUpdater.GetVersionAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return environment.AppVersion ?? "unknown";
        }
    }

    private static string Readme(DateTimeOffset now) => $"""
        Steam Stat diagnostics bundle / 诊断包
        Generated / 生成时间: {now:yyyy-MM-dd HH:mm:ss zzz}

        This archive was created on your computer and is never uploaded automatically.
        本压缩包在你的电脑上生成，应用不会自动上传。

        Contents / 内容:
          system.json      app version, platform, distribution, non-sensitive settings, data source health
                           版本、平台、发行形态、不敏感的设置项、数据源健康状态
          logs/            recent application logs / 最近的应用日志
          crash-reports/   local crash reports (only if crash reports are enabled) / 本地崩溃报告（仅开启时）

        Redacted / 已脱敏: your home directory and user name, Steam account and persona names,
        SteamID64 values, userdata account ids, e-mail addresses, tokens and proxy credentials.
        家目录与系统用户名、Steam 账号名与昵称、SteamID64、userdata 账号 ID、邮箱、令牌与代理凭据。
        Not included / 不包含: the database, saved credentials, screenshots or save files.
        数据库、已保存的登录凭证、截图与存档。

        Please review the files before attaching them to a GitHub issue.
        附加到 GitHub Issue 之前请自行检查一遍。
        """;

    [GeneratedRegex(@"^steam-stat-diagnostics-\d{8}-\d{6}\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex BundleFileNamePattern();
}
