using System.Diagnostics;
using System.ServiceProcess;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Platform;

namespace SteamStat.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessController(ILogger<WindowsProcessController> logger) : IProcessController
{
    public IReadOnlyList<IProcessHandle> GetProcessesByNames(IEnumerable<string> processNames)
        => processNames.SelectMany(name => GetProcesses(name)).Cast<IProcessHandle>().ToArray();

    public bool StopWindowsService(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            if (service.Status != ServiceControllerStatus.Running) return true;
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            return true;
        }
        catch (System.ServiceProcess.TimeoutException exception)
        {
            logger.LogError(exception, "Timed out waiting for Windows service {ServiceName} to stop", serviceName);
            return false;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to stop Windows service {ServiceName}", serviceName);
            return false;
        }
    }

    public IProcessHandle? StartProcess(string filePath, bool useShellExecute = false, string? arguments = null,
        string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            var startInfo = new ProcessStartInfo(filePath) { UseShellExecute = useShellExecute };
            if (!string.IsNullOrWhiteSpace(arguments)) startInfo.Arguments = arguments;
            if (!string.IsNullOrWhiteSpace(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;
            else if (!useShellExecute && filePath.Contains(Path.DirectorySeparatorChar))
            {
                var file = new FileInfo(filePath);
                if (file.Exists && !string.IsNullOrWhiteSpace(file.DirectoryName)) startInfo.WorkingDirectory = file.DirectoryName;
            }
            if (environment != null)
            {
                foreach (var item in environment) startInfo.Environment.Add(item.Key, item.Value);
            }
            var process = Process.Start(startInfo);
            return process == null ? null : new WindowsProcessHandle(process, logger);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to start process {FilePath}", filePath);
            return null;
        }
    }

    private IEnumerable<WindowsProcessHandle> GetProcesses(string name)
    {
        try
        {
            return Process.GetProcessesByName(name).Select(process => new WindowsProcessHandle(process, logger)).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private sealed class WindowsProcessHandle(Process process, ILogger logger) : IProcessHandle
    {
        public string Name => process.ProcessName;
        public int Id => process.Id;

        public bool KillAndWaitForExit()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit();
                }
                return true;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to kill process {ProcessName} ({ProcessId})", Name, Id);
                return false;
            }
        }

        public void Dispose() => process.Dispose();
    }
}
