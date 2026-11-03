using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamStat.Core.Diagnostics;

public static class CrashReportStatuses
{
    public const string Pending = "pending";
    public const string Submitted = "submitted";
    public const string Dismissed = "dismissed";
}

/// <summary>
/// 一条本地崩溃报告。只含异常类型、消息与堆栈（均已脱敏）以及版本 / 系统信息，不含日志与用户数据。
/// 相同指纹的异常合并为一条并累计次数。
/// </summary>
public sealed record CrashReport
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("fingerprint")] public required string Fingerprint { get; init; }
    /// <summary>host（.NET 进程）| renderer（界面）。</summary>
    [JsonPropertyName("source")] public required string Source { get; init; }
    /// <summary>unhandled | unobservedTask | fatal | rendererError | unhandledRejection。</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("exceptionType")] public required string ExceptionType { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("stackTrace")] public required string StackTrace { get; init; }
    [JsonPropertyName("appVersion")] public required string AppVersion { get; init; }
    [JsonPropertyName("os")] public required string Os { get; init; }
    [JsonPropertyName("distribution")] public required string Distribution { get; init; }
    [JsonPropertyName("firstSeenAt")] public required long FirstSeenAt { get; init; }
    [JsonPropertyName("lastSeenAt")] public required long LastSeenAt { get; init; }
    [JsonPropertyName("count")] public required int Count { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("submittedAt")] public long? SubmittedAt { get; init; }
}

public static partial class CrashReportFingerprint
{
    /// <summary>
    /// 指纹 = 异常类型 + 去掉行号 / 列号 / 地址 / 文件路径后的前 12 帧。同一缺陷在不同路径、不同构建下得到相同指纹。
    /// </summary>
    public static string Compute(string exceptionType, string stackTrace)
    {
        var frames = (stackTrace ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(frame => frame.Length > 0)
            .Take(12);
        var input = exceptionType + "\n" + string.Join('\n', frames);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }

    private static string Normalize(string frame)
    {
        var value = FileLocationPattern().Replace(frame, string.Empty);
        value = UrlLocationPattern().Replace(value, "$1");
        value = HexAddressPattern().Replace(value, "0x?");
        return value.Trim();
    }

    // .NET：" in C:\src\Foo.cs:line 42"
    [GeneratedRegex(@"\s+in\s+.+:line\s+\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex FileLocationPattern();

    // JS："at foo (http://localhost:9000/src/a.ts?t=1:10:5)" → 只保留函数名
    [GeneratedRegex(@"^(at\s+[^\s(]+)\s*\(.*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex UrlLocationPattern();

    [GeneratedRegex(@"0x[0-9a-fA-F]+", RegexOptions.CultureInvariant)]
    private static partial Regex HexAddressPattern();
}
