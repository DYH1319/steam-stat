using ElectronNET.API;
using ElectronNet.Constants;
using ElectronNet.Hosting;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;

namespace ElectronNet.Services;

public static class UpdateService
{
    private static Timer? _timer;
    private static bool _isRunning;

    private static bool IsChecking;
    private static bool IsDownloading;
    private static readonly int CheckUpdateInterval = 3 * 60; // 每 3 小时检查更新

    public static bool AutoUpdateEnabled
    {
        get;
        internal set
        {
            field = value;
            UpdateAutoUpdater();
        }
    }

    /// <summary>
    /// 获取更新相关状态
    /// </summary>
    public static async Task<UpdaterStatusDto> GetStatus()
    {
        return new UpdaterStatusDto(
            AutoUpdateEnabled,
            IsChecking,
            IsDownloading,
            CheckUpdateInterval,
            await Electron.App.GetVersionAsync());
    }

    /// <summary>
    /// 初始化自动更新
    /// </summary>
    public static void InitAutoUpdater(IEventBus eventBus)
    {
        try
        {
            var autoUpdater = Electron.AutoUpdater;

            // 配置自动更新器
            autoUpdater.AutoDownload = false;
            autoUpdater.AutoInstallOnAppQuit = true;
            autoUpdater.AllowPrerelease = false;
            autoUpdater.AllowDowngrade = false;
            autoUpdater.FullChangelog = true;

            // 正在检查更新事件
            autoUpdater.OnCheckingForUpdate += () =>
            {
                IsChecking = true;
                _ = SendUpdaterEvent(eventBus, "checking-for-update");
                Console.WriteLine($"{ConsoleLogPrefix.UPDATER} 正在检查更新...");
            };

            // 检查到更新事件
            autoUpdater.OnUpdateAvailable += info =>
            {
                IsChecking = false;
                _ = SendUpdaterEvent(eventBus, "update-available", new UpdaterEventDataDto
                {
                    Files = info.Files.Select(file => new UpdaterFileDto
                    {
                        Url = file.Url,
                        Size = file.Size,
                        BlockMapSize = file.BlockMapSize,
                        Sha512 = file.Sha512,
                        IsAdminRightsRequired = file.IsAdminRightsRequired
                    }).ToArray(),
                    Version = info.Version,
                    ReleaseDate = info.ReleaseDate,
                    ReleaseName = info.ReleaseName,
                    ReleaseNotes = info.ReleaseNotes.Select(note => new UpdaterReleaseNoteDto(note.Version, note.Note)).ToArray(),
                    StagingPercentage = info.StagingPercentage
                });
                Console.WriteLine($"{ConsoleLogPrefix.UPDATER} 发现新版本：{info.Version}");
                // TODO
            };

            // 没有检查到更新事件
            autoUpdater.OnUpdateNotAvailable += info =>
            {
                IsChecking = false;
                _ = SendUpdaterVersionEvent(eventBus, "update-not-available", info.Version);
                Console.WriteLine($"{ConsoleLogPrefix.UPDATER} 当前已是最新版本：{info.Version}");
            };

            // 下载进度事件
            autoUpdater.OnDownloadProgress += info =>
            {
                IsDownloading = true;
                _ = SendUpdaterEvent(eventBus, "download-progress", new UpdaterEventDataDto
                {
                    Progress = info.Progress,
                    Percent = info.Percent,
                    BytesPerSecond = info.BytesPerSecond,
                    Transferred = info.Transferred,
                    Total = info.Total
                });
            };

            // 下载完成事件
            autoUpdater.OnUpdateDownloaded += info =>
            {
                IsDownloading = false;
                _ = SendUpdaterVersionEvent(eventBus, "update-downloaded", info.Version);
                Console.WriteLine($"{ConsoleLogPrefix.UPDATER} 更新下载完成：{info.Version}");
            };

            // 更新错误事件
            autoUpdater.OnError += error =>
            {
                IsChecking = false;
                IsDownloading = false;
                _ = SendUpdaterEvent(eventBus, "error", new UpdaterEventDataDto { Message = error });
                Console.WriteLine($"{ConsoleLogPrefix.ERROR} Updater Error：{error}");
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(InitAutoUpdater)} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 发送自动更新器事件到渲染端
    /// </summary>
    /// <param name="updaterEvent">自动更新器事件名称</param>
    /// <param name="data">数据</param>
    private static Task SendUpdaterEvent(IEventBus eventBus, string updaterEvent, UpdaterEventDataDto? data = null)
        => eventBus.PublishAsync(new UpdaterStateChanged(new UpdaterEventDto
        {
            UpdaterEvent = updaterEvent,
            Data = data == null ? null : new UpdaterDetailsEventPayload(data)
        }));

    private static Task SendUpdaterVersionEvent(IEventBus eventBus, string updaterEvent, string version)
        => eventBus.PublishAsync(new UpdaterStateChanged(new UpdaterEventDto
        {
            UpdaterEvent = updaterEvent,
            Data = new UpdaterVersionEventPayload(version)
        }));

    /// <summary>
    /// 更新自动更新器
    /// </summary>
    private static void UpdateAutoUpdater()
    {
        try
        {
            // 停止计时器
            if (_isRunning)
            {
                _timer?.Dispose();
                _timer = null;
                _isRunning = false;
            }
            // 如果启动了自动检测更新
            if (AutoUpdateEnabled)
            {
                if (_isRunning) return;
                _isRunning = true;
                // 延迟 5 秒开启计时器，每间隔 CheckUpdateInterval 分钟执行一次
                _timer = new Timer(_ => Task.Run(CheckForUpdate), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(CheckUpdateInterval));
                Console.WriteLine($"{ConsoleLogPrefix.UPDATER} 已启动自动更新");
            }
            else
            {
                Console.WriteLine($"{ConsoleLogPrefix.UPDATER} 已关闭自动更新");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(UpdateAutoUpdater)} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查更新
    /// </summary>
    public static void CheckForUpdate()
    {
        if (IsChecking) return;
        _ = Electron.AutoUpdater.CheckForUpdatesAsync();
    }

    /// <summary>
    /// 下载更新
    /// </summary>
    public static void DownloadUpdate()
    {
        if (IsDownloading) return;
        _ = Electron.AutoUpdater.DownloadUpdateAsync();
    }

    /// <summary>
    /// 退出并安装更新
    /// </summary>
    public static void QuitAndInstall()
    {
        Electron.AutoUpdater.QuitAndInstall(false, true);
    }
}
