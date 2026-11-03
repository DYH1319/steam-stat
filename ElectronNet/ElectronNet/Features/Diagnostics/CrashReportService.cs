using System.Text.Json;
using ElectronNet.Hosting;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Diagnostics;
using SteamStat.Core.Environment;
using SteamStat.Core.Settings;

namespace ElectronNet.Features.Diagnostics;

/// <summary>
/// 崩溃报告（默认关闭）。开启后把 .NET 未处理异常与渲染层未捕获错误记录到 {UserData}/CrashReports，
/// 同一指纹合并计数；只保存脱敏后的异常类型、消息与堆栈。上报交给 <see cref="ICrashReportSink"/>。
/// 关闭时不写任何文件（日志中照常有异常记录）。
/// </summary>
internal sealed class CrashReportService : ICrashReportController
{
    internal const int MaxStoredReports = 50;
    internal const int MaxRecordsPerProcess = 30;
    private const int MaxStackLength = 16_000;
    private const int MaxMessageLength = 2_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly AppEnvironment _environment;
    private readonly AppDistribution _distribution;
    private readonly ICrashReportSink _sink;
    private readonly DiagnosticsRedactionProvider _redaction;
    private readonly ISettingsStore _settingsStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CrashReportService> _logger;
    private bool? _enabled;
    private int _recordedThisProcess;

    public CrashReportService(
        AppEnvironment environment,
        AppDistribution distribution,
        ICrashReportSink sink,
        DiagnosticsRedactionProvider redaction,
        ISettingsStore settingsStore,
        TimeProvider timeProvider,
        ILogger<CrashReportService> logger)
    {
        _environment = environment;
        _distribution = distribution;
        _sink = sink;
        _redaction = redaction;
        _settingsStore = settingsStore;
        _timeProvider = timeProvider;
        _logger = logger;
        _directory = Path.Combine(environment.Paths.UserDataDirectory, "CrashReports");
    }

    public string Directory => _directory;
    public string SinkId => _sink.Id;
    public bool RequiresUserAction => _sink.RequiresUserAction;

    /// <summary>设置未初始化前（启动早期崩溃）直接读取设置文件。</summary>
    public bool Enabled
    {
        get
        {
            lock (_sync)
            {
                if (_enabled is { } enabled) return enabled;
            }
            bool fromSettings;
            try
            {
                fromSettings = _settingsStore.GetSettings().Diagnostics?.CrashReportsEnabled == true;
            }
            catch
            {
                fromSettings = false;
            }
            lock (_sync) return _enabled ??= fromSettings;
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync) _enabled = enabled;
        if (enabled) _ = _redaction.RefreshAsync();
        _logger.LogInformation("Crash reports are {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>记录 .NET 侧异常。在崩溃路径上同步执行，任何失败都被吞掉。</summary>
    public CrashReport? RecordHostException(Exception exception, string kind)
    {
        try
        {
            return Record("host", kind, exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.ToString());
        }
        catch
        {
            return null;
        }
    }

    public CrashReport? RecordRenderer(RendererCrashReportRequest request)
        => Record("renderer", request.Kind, request.ExceptionType, request.Message, request.StackTrace);

    internal CrashReport? Record(string source, string kind, string exceptionType, string message, string stackTrace)
    {
        if (!Enabled) return null;
        if (Interlocked.Increment(ref _recordedThisProcess) > MaxRecordsPerProcess) return null;

        var redactor = _redaction.Current;
        var safeMessage = Truncate(redactor.Redact(message), MaxMessageLength);
        var safeStack = Truncate(redactor.Redact(stackTrace), MaxStackLength);
        var safeType = Truncate(redactor.Redact(exceptionType), 200);
        var fingerprint = CrashReportFingerprint.Compute(safeType, safeStack);
        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        CrashReport report;
        lock (_sync)
        {
            System.IO.Directory.CreateDirectory(_directory);
            var existing = ReadAll().FirstOrDefault(item => item.Fingerprint == fingerprint && item.Status == CrashReportStatuses.Pending);
            report = existing == null
                ? new CrashReport
                {
                    // 非十六进制前缀：避免 Electron.NET 桥把字符串参数按 GUID / 日期重新解析（见 JsonToBoxedPrimitivesConverter）
                    Id = $"crash-{now}-{fingerprint}-{Guid.NewGuid().ToString("N")[..6]}",
                    Fingerprint = fingerprint,
                    Source = source,
                    Kind = kind,
                    ExceptionType = safeType,
                    Message = safeMessage,
                    StackTrace = safeStack,
                    AppVersion = _environment.AppVersion ?? "unknown",
                    Os = PlatformRuntime.OsDescription(),
                    Distribution = _distribution.Kind,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    Count = 1,
                    Status = CrashReportStatuses.Pending
                }
                : existing with { LastSeenAt = now, Count = existing.Count + 1 };
            Write(report);
            Prune();
        }

        if (!_sink.RequiresUserAction && report.Count == 1) _ = SubmitAsync(report.Id, CancellationToken.None);
        return report;
    }

    public IReadOnlyList<CrashReport> List()
    {
        lock (_sync) return ReadAll().OrderByDescending(report => report.LastSeenAt).ToArray();
    }

    public async Task<CrashReportSubmission> SubmitAsync(string id, CancellationToken cancellationToken)
    {
        CrashReport? report;
        lock (_sync) report = ReadAll().FirstOrDefault(item => item.Id == id);
        if (report == null) return new CrashReportSubmission(false, null);

        var submission = await _sink.SubmitAsync(report, cancellationToken).ConfigureAwait(false);
        if (submission.Success)
        {
            lock (_sync)
            {
                Write(report with { Status = CrashReportStatuses.Submitted, SubmittedAt = _timeProvider.GetUtcNow().ToUnixTimeSeconds() });
            }
        }
        return submission;
    }

    /// <summary>忽略一条（或全部）待处理报告。</summary>
    public bool Dismiss(string? id)
    {
        lock (_sync)
        {
            var targets = ReadAll().Where(item => item.Status == CrashReportStatuses.Pending && (id == null || item.Id == id)).ToArray();
            foreach (var report in targets) Write(report with { Status = CrashReportStatuses.Dismissed });
            return targets.Length > 0 || id == null;
        }
    }

    private IEnumerable<CrashReport> ReadAll()
    {
        if (!System.IO.Directory.Exists(_directory)) yield break;
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            CrashReport? report = null;
            try
            {
                report = JsonSerializer.Deserialize<CrashReport>(File.ReadAllText(file));
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
            }
            if (report != null && Path.GetFileNameWithoutExtension(file) == report.Id) yield return report;
        }
    }

    private void Write(CrashReport report)
    {
        var path = Path.Combine(_directory, report.Id + ".json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private void Prune()
    {
        var files = new DirectoryInfo(_directory).GetFiles("*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(MaxStoredReports);
        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}
