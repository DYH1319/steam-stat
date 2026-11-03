using ElectronNet.Hosting;
using SteamStat.Core.Diagnostics;

namespace ElectronNet.Features.Diagnostics;

/// <summary>提交结果；<see cref="Url"/> 为提交位置（如 Issue 链接）。</summary>
internal sealed record CrashReportSubmission(bool Success, string? Url);

/// <summary>
/// 崩溃报告的上报方式。<see cref="RequiresUserAction"/> 为 true 的实现只在用户点击「提交」时调用；
/// 为 false 的实现（例如以后接入 Sentry 兼容的 DSN）会在开启崩溃报告后自动上报新记录。
/// 无论哪种实现，上报内容都只有脱敏后的异常类型、消息、堆栈与版本 / 系统信息。
/// </summary>
internal interface ICrashReportSink
{
    string Id { get; }
    bool RequiresUserAction { get; }
    Task<CrashReportSubmission> SubmitAsync(CrashReport report, CancellationToken cancellationToken);
}

/// <summary>打开预填好的 GitHub Issue，由用户在浏览器中确认后提交。不需要任何服务端。</summary>
internal sealed class GitHubIssueCrashReportSink(IShellLauncher shell) : ICrashReportSink
{
    public string Id => "githubIssue";
    public bool RequiresUserAction => true;

    public async Task<CrashReportSubmission> SubmitAsync(CrashReport report, CancellationToken cancellationToken)
    {
        var url = BuildIssueUrl(report);
        var opened = await shell.OpenExternalAsync(url).WaitAsync(cancellationToken).ConfigureAwait(false);
        return new CrashReportSubmission(opened, url);
    }

    internal static string BuildIssueUrl(CrashReport report)
    {
        var title = $"[Crash] {report.ExceptionType}: {FirstLine(report.Message)}";
        var body = $"""
            ### 崩溃信息 / Crash report

            | | |
            | --- | --- |
            | Steam Stat | {report.AppVersion} ({report.Distribution}) |
            | OS | {report.Os} |
            | Source | {report.Source} / {report.Kind} |
            | Fingerprint | `{report.Fingerprint}` |
            | Occurrences | {report.Count} |

            ### 发生了什么 / What were you doing?

            <!-- 可选：崩溃前在做什么 / Optional: what were you doing before the crash? -->

            ### 堆栈 / Stack trace

            > 已在本机脱敏（账号名、SteamID、用户路径等）。Redacted locally (account names, SteamIDs, user paths).

            ```
            {report.ExceptionType}: {report.Message}
            {report.StackTrace}
            ```
            """;
        // .NET 堆栈与 Windows 检出的源码换行都可能是 \r\n，统一为 \n
        return GitHubIssueLink.Build(title, body.ReplaceLineEndings("\n"), ["crash"]);
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length <= 120 ? line : line[..120] + "…";
    }
}
