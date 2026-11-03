using ElectronNET.API;
using ElectronNET.API.Entities;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Services;

/// <summary>
/// 系统通知：渲染层决定何时通知（周报/里程碑/异常），Host 负责弹出原生通知；
/// 点击通知时显示主窗口并导航到通知关联的页面。
/// </summary>
internal sealed class DesktopNotificationService(
    TrayService trayService,
    ILogger<DesktopNotificationService> logger)
{
    internal void Show(NotificationShowRequest request)
    {
        try
        {
            var path = TrayService.IsAppPath(request.Path) ? request.Path : null;
            var options = new NotificationOptions(request.Title, request.Body)
            {
                OnClick = () => _ = trayService.NavigateAsync(path)
            };
            var iconPath = TrayService.IconPath();
            if (iconPath != null) options.Icon = iconPath;
            Electron.Notification.Show(options);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show a desktop notification");
        }
    }
}
