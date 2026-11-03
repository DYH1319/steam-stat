using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Platform;

namespace SteamStat.Platform.Linux;

[SupportedOSPlatform("linux")]
internal sealed partial class LinuxProcessController(
    LinuxSystemContext context,
    ILogger<LinuxProcessController> logger) : IProcessController
{
    private const int SigTerm = 15;
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(5);

    private readonly ProcScanner _proc = new(context.ProcRoot);

    /// <summary>按 /proc/&lt;pid&gt;/comm 精确匹配（.NET 的 ProcessName 在 Linux 上对长名称与脚本的处理不稳定）。</summary>
    public IReadOnlyList<IProcessHandle> GetProcessesByNames(IEnumerable<string> processNames)
    {
        var names = processNames.ToHashSet(StringComparer.Ordinal);
        var handles = new List<IProcessHandle>();
        foreach (var pid in _proc.FindByComm(names))
        {
            try
            {
                handles.Add(new LinuxProcessHandle(Process.GetProcessById(pid), _proc.ReadComm(pid) ?? string.Empty, logger));
            }
            catch (ArgumentException)
            {
                // 进程在扫描与打开之间退出
            }
        }
        return handles;
    }

    /// <summary>Linux 上 Steam 没有系统服务进程，无需处理。</summary>
    public bool StopWindowsService(string serviceName) => true;

    public IProcessHandle? StartProcess(string filePath, bool useShellExecute = false, string? arguments = null,
        string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            var startInfo = new ProcessStartInfo(filePath) { UseShellExecute = useShellExecute };
            if (!string.IsNullOrWhiteSpace(arguments)) startInfo.Arguments = arguments;
            if (!string.IsNullOrWhiteSpace(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;
            else if (Path.IsPathRooted(filePath) && Path.GetDirectoryName(filePath) is { Length: > 0 } directory && Directory.Exists(directory))
                startInfo.WorkingDirectory = directory;
            if (environment != null)
            {
                foreach (var item in environment) startInfo.Environment[item.Key] = item.Value;
            }
            var process = Process.Start(startInfo);
            return process == null ? null : new LinuxProcessHandle(process, Path.GetFileName(filePath), logger);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to start process {FilePath}", filePath);
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    private sealed class LinuxProcessHandle(Process process, string name, ILogger logger) : IProcessHandle
    {
        public string Name => name;
        public int Id => process.Id;

        /// <summary>先发 SIGTERM 让 Steam 正常保存状态，超时再强制结束。</summary>
        public bool KillAndWaitForExit()
        {
            try
            {
                if (process.HasExited) return true;
                if (Kill(process.Id, SigTerm) == 0 && process.WaitForExit(GracefulExitTimeout)) return true;
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(GracefulExitTimeout);
                }
                return true;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to stop process {ProcessName} ({ProcessId})", Name, Id);
                return false;
            }
        }

        public void Dispose() => process.Dispose();
    }
}
