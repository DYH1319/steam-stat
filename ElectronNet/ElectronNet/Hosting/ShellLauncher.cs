using ElectronNET.API;
using Microsoft.Extensions.Logging;

namespace ElectronNet.Hosting;

/// <summary>交给系统处理的外部操作（默认浏览器打开链接、在文件管理器中显示文件）。URL 与路径由 Host 生成，不来自渲染层。</summary>
internal interface IShellLauncher
{
    Task<bool> OpenExternalAsync(string url);
    Task<bool> ShowItemInFolderAsync(string fullPath);
}

internal sealed class ElectronShellLauncher(ILogger<ElectronShellLauncher> logger) : IShellLauncher
{
    public async Task<bool> OpenExternalAsync(string url)
    {
        try
        {
            await Electron.Shell.OpenExternalAsync(url).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to open an external URL");
            return false;
        }
    }

    public async Task<bool> ShowItemInFolderAsync(string fullPath)
    {
        try
        {
            await Electron.Shell.ShowItemInFolderAsync(fullPath).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to reveal a file in the file manager");
            return false;
        }
    }
}
