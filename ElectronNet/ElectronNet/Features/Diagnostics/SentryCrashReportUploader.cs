using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Diagnostics;
using SteamStat.Core.Environment;

namespace ElectronNet.Features.Diagnostics;

/// <summary>
/// 自动上报（开启崩溃报告后在后台发送）。与 <see cref="ICrashReportSink"/>（用户手动提交 GitHub Issue）并存。
/// </summary>
internal interface ICrashReportUploader
{
    /// <summary>构建时注入了 DSN（或设置了环境变量）才为 true；未配置时什么都不发送。</summary>
    bool IsConfigured { get; }

    /// <summary>上传一条已脱敏的报告，成功时返回远端事件 ID。</summary>
    Task<string?> UploadAsync(CrashReport report, CancellationToken cancellationToken);
}

/// <summary>解析后的 Sentry DSN：<c>{scheme}://{publicKey}@{host}/{pathPrefix}{projectId}</c>。</summary>
internal sealed record SentryDsn(string Raw, string PublicKey, Uri EnvelopeUri)
{
    internal const string EnvironmentVariable = "STEAM_STAT_SENTRY_DSN";
    internal const string AssemblyMetadataKey = "SentryDsn";

    public static SentryDsn? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.UserInfo)) return null;
        var publicKey = uri.UserInfo.Split(':', 2)[0];
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (publicKey.Length == 0 || segments.Length == 0 || !segments[^1].All(char.IsAsciiDigit)) return null;
        var prefix = string.Concat(segments[..^1].Select(segment => segment + "/"));
        var envelope = new UriBuilder(uri.Scheme, uri.Host, uri.Port, $"/{prefix}api/{segments[^1]}/envelope/").Uri;
        return new SentryDsn(value.Trim(), publicKey, envelope);
    }

    /// <summary>环境变量（开发调试）优先，其次是发行构建通过 <c>-p:SentryDsn=...</c> 写入的程序集元数据。</summary>
    public static SentryDsn? Resolve()
        => TryParse(System.Environment.GetEnvironmentVariable(EnvironmentVariable))
           ?? TryParse(typeof(SentryDsn).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
               .FirstOrDefault(attribute => attribute.Key == AssemblyMetadataKey)?.Value);
}

/// <summary>
/// 以 Sentry envelope 协议直接上报，不使用 Sentry SDK：SDK 会自动采集面包屑、设备名、IP 等，
/// 这里只发送本地已脱敏的 <see cref="CrashReport"/> 字段，并要求服务端不推断 IP。
/// </summary>
internal sealed partial class SentryCrashReportUploader : ICrashReportUploader
{
    internal const string HttpClientName = "SteamStat.Sentry";
    private const int MaxFrames = 100;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SentryCrashReportUploader> _logger;
    private readonly SentryDsn? _dsn;

    public SentryCrashReportUploader(
        IHttpClientFactory httpClientFactory,
        AppEnvironment environment,
        TimeProvider timeProvider,
        ILogger<SentryCrashReportUploader> logger)
        : this(httpClientFactory, environment, timeProvider, logger, SentryDsn.Resolve())
    {
    }

    internal SentryCrashReportUploader(
        IHttpClientFactory httpClientFactory,
        AppEnvironment environment,
        TimeProvider timeProvider,
        ILogger<SentryCrashReportUploader> logger,
        SentryDsn? dsn)
    {
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _timeProvider = timeProvider;
        _logger = logger;
        _dsn = dsn;
    }

    public bool IsConfigured => _dsn != null;

    public async Task<string?> UploadAsync(CrashReport report, CancellationToken cancellationToken)
    {
        if (_dsn == null) return null;
        var eventId = Guid.NewGuid().ToString("N");
        var body = BuildEnvelope(report, eventId, _timeProvider.GetUtcNow(), _dsn, _environment);

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _dsn.EnvelopeUri)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
        request.Headers.TryAddWithoutValidation(
            "X-Sentry-Auth",
            $"Sentry sentry_version=7, sentry_key={_dsn.PublicKey}, sentry_client={ClientName(_environment)}");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Sentry rejected crash report {Fingerprint} with HTTP {StatusCode}", report.Fingerprint, (int)response.StatusCode);
            return null;
        }
        return eventId;
    }

    internal static byte[] BuildEnvelope(CrashReport report, string eventId, DateTimeOffset sentAt, SentryDsn dsn, AppEnvironment environment)
    {
        var payload = Encoding.UTF8.GetBytes(BuildEvent(report, eventId, environment).ToJsonString());
        var envelopeHeader = new JsonObject
        {
            ["event_id"] = eventId,
            ["sent_at"] = sentAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["dsn"] = dsn.Raw
        };
        var itemHeader = new JsonObject
        {
            ["type"] = "event",
            ["content_type"] = "application/json",
            ["length"] = payload.Length
        };
        using var stream = new MemoryStream();
        stream.Write(Encoding.UTF8.GetBytes(envelopeHeader.ToJsonString() + "\n" + itemHeader.ToJsonString() + "\n"));
        stream.Write(payload);
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    internal static JsonObject BuildEvent(CrashReport report, string eventId, AppEnvironment environment)
    {
        var host = report.Source == "host";
        var exception = new JsonObject
        {
            ["type"] = report.ExceptionType,
            ["value"] = report.Message,
            ["mechanism"] = new JsonObject { ["type"] = report.Kind, ["handled"] = false }
        };
        var frames = ParseFrames(report.StackTrace, host);
        if (frames.Count > 0) exception["stacktrace"] = new JsonObject { ["frames"] = frames };

        var contexts = new JsonObject
        {
            ["os"] = new JsonObject { ["raw_description"] = report.Os },
            ["app"] = new JsonObject { ["app_version"] = report.AppVersion }
        };
        if (host) contexts["runtime"] = new JsonObject { ["name"] = ".NET", ["version"] = System.Environment.Version.ToString() };

        return new JsonObject
        {
            ["event_id"] = eventId,
            ["timestamp"] = report.FirstSeenAt,
            ["platform"] = host ? "csharp" : "javascript",
            ["level"] = report.Kind is "unhandled" or "fatal" ? "fatal" : "error",
            ["logger"] = report.Source,
            ["release"] = $"steam-stat@{report.AppVersion}",
            ["environment"] = EnvironmentName(report.AppVersion, environment),
            // 与本地指纹一致：Sentry 中的分组 = 崩溃报告列表中的一条
            ["fingerprint"] = new JsonArray(report.Fingerprint),
            ["tags"] = new JsonObject
            {
                ["source"] = report.Source,
                ["kind"] = report.Kind,
                ["distribution"] = report.Distribution,
                ["crash_fingerprint"] = report.Fingerprint
            },
            ["contexts"] = contexts,
            ["exception"] = new JsonObject { ["values"] = new JsonArray(exception) },
            // 解析不出的帧（内部异常分隔行等）以原文保留
            ["extra"] = new JsonObject { ["stack_trace"] = report.StackTrace },
            ["sdk"] = new JsonObject
            {
                ["name"] = "steam-stat.crash-report",
                ["version"] = report.AppVersion,
                ["settings"] = new JsonObject { ["infer_ip"] = "never" }
            }
        };
    }

    /// <summary>Sentry 要求帧按调用顺序排列（出错的帧在最后），而 .NET / V8 堆栈最近的帧在前。</summary>
    internal static JsonArray ParseFrames(string stackTrace, bool host)
    {
        var frames = new List<JsonObject>();
        foreach (var raw in (stackTrace ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("at ", StringComparison.Ordinal)) continue;
            var frame = host ? ParseDotNetFrame(line) : ParseJavaScriptFrame(line);
            if (frame != null) frames.Add(frame);
            if (frames.Count >= MaxFrames) break;
        }
        frames.Reverse();
        return new JsonArray([.. frames]);
    }

    private static JsonObject? ParseDotNetFrame(string line)
    {
        var match = DotNetFramePattern().Match(line);
        if (!match.Success) return null;
        var function = match.Groups["function"].Value;
        var frame = new JsonObject
        {
            ["function"] = function,
            ["in_app"] = function.StartsWith("SteamStat.", StringComparison.Ordinal) || function.StartsWith("ElectronNet.", StringComparison.Ordinal)
        };
        if (match.Groups["file"].Success)
        {
            frame["filename"] = match.Groups["file"].Value;
            frame["lineno"] = int.Parse(match.Groups["line"].Value);
        }
        return frame;
    }

    private static JsonObject? ParseJavaScriptFrame(string line)
    {
        var match = JavaScriptFramePattern().Match(line);
        if (!match.Success) return new JsonObject { ["function"] = line[3..].Trim() };
        var file = match.Groups["file"].Value;
        var frame = new JsonObject
        {
            ["abs_path"] = file,
            ["filename"] = file,
            ["lineno"] = int.Parse(match.Groups["line"].Value),
            ["colno"] = int.Parse(match.Groups["column"].Value),
            ["in_app"] = !file.Contains("node_modules", StringComparison.Ordinal)
        };
        if (match.Groups["function"].Success) frame["function"] = match.Groups["function"].Value;
        return frame;
    }

    private static string EnvironmentName(string appVersion, AppEnvironment environment)
        => environment.IsDevelopment ? "development" : appVersion.Contains('-') ? "beta" : "production";

    private static string ClientName(AppEnvironment environment) => $"steam-stat/{environment.AppVersion ?? "unknown"}";

    // "at Namespace.Type.Method(Args) in /path/File.cs:line 42"
    [GeneratedRegex(@"^at (?<function>.+?)(?: in (?<file>.+):line (?<line>\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex DotNetFramePattern();

    // "at fn (file:///app/dist/a.js:10:5)" / "at file:///app/dist/a.js:10:5" / "at async fn (...)"
    [GeneratedRegex(@"^at (?:(?<function>.+?) \()?(?<file>[^()\s]+?):(?<line>\d+):(?<column>\d+)\)?$", RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptFramePattern();
}
