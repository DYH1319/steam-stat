using System.Diagnostics;
using System.ServiceProcess;
using ElectronNet.Constants;

namespace ElectronNet.Services;

/// <summary>
/// 本地进程服务
/// </summary>
public static class LocalProcessService
{
    /// <summary>
    /// 从本地进程名称列表获取进程列表（一个进程名称可能对应多个进程）
    /// </summary>
    /// <param name="processNames">进程名称列表</param>
    /// <returns>进程列表</returns>
    public static List<Process> GetProcessesByNames(IEnumerable<string> processNames)
    {
        return processNames.Select(static name =>
        {
            try
            {
                return Process.GetProcessesByName(name);
            }
            catch
            {
                return [];
            }
        }).SelectMany(static processes => processes).ToList();
    }

    /// <summary>
    /// 根据进程对象杀死一个进程
    /// </summary>
    /// <param name="process">进程对象</param>
    /// <returns>对象</returns>
    public static bool KillProcess(Process? process)
    {
        if (process == null)
        {
            return true;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(KillProcess)} fail, name: {process.ProcessName}, id: {process.Id}, ex: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 启动一个进程
    /// </summary>
    /// <param name="filePath">要在进程中运行的应用程序文件路径</param>
    /// <param name="useShellExecute">是否使用操作系统 Shell 启动进程，默认为 false</param>
    /// <param name="arguments">启动进程时传递的命令行参数，默认为 null</param>
    /// <param name="workingDirectory">工作目录，默认为 null；当 useShellExecute = false 时，自动设置为应用程序文件所在的文件夹目录</param>
    /// <param name="environment">环境变量，默认为 null</param>
    /// <returns>若启动成功，返回进程对象；否则返回 null</returns>
    public static Process? StartProcess(string filePath, bool useShellExecute = false, string? arguments = null, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            var pInfo = new ProcessStartInfo(filePath)
            {
                UseShellExecute = useShellExecute
            };

            if (!string.IsNullOrWhiteSpace(arguments)) pInfo.Arguments = arguments;

            if (!string.IsNullOrWhiteSpace(workingDirectory)) pInfo.WorkingDirectory = workingDirectory;
            else if (!useShellExecute && filePath.Contains(Path.DirectorySeparatorChar))
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Exists && !string.IsNullOrWhiteSpace(fileInfo.DirectoryName))
                {
                    pInfo.WorkingDirectory = fileInfo.DirectoryName;
                }
            }

            if (environment != null)
            {
                foreach (var item in environment)
                {
                    pInfo.Environment.Add(item.Key, item.Value);
                }
            }

            return Process.Start(pInfo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(StartProcess)} fail, filePath: {filePath}, ex: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 停止一个 Windows 服务
    /// </summary>
    /// <param name="serviceName"></param>
    /// <returns>是否成功停止服务</returns>
    public static bool StopWindowsService(string serviceName)
    {
        try
        {
            ServiceController service;
            
            try
            {
                service = new ServiceController(serviceName);
            }
            catch
            {
                Console.WriteLine($"{ConsoleLogPrefix.WARN} {serviceName} not found, skipping service stop");
                return true;
            }

            if (service.Status == ServiceControllerStatus.Running)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                Console.WriteLine($"{ConsoleLogPrefix.PROCESS} {serviceName} stopped successfully");
            }
            else
            {
                Console.WriteLine($"{ConsoleLogPrefix.PROCESS} {serviceName} is already stopped (Status: {service.Status})");
            }

            service.Dispose();
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} Timeout waiting for {serviceName} to stop (exceeded 10 seconds)");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} Access denied stopping {serviceName}: {ex.Message}. Please run with administrator privileges.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} Failed to stop {serviceName}: {ex.Message}");
            return false;
        }
    }
}