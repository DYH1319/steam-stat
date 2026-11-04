using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Diagnostics;
using SteamStat.Core.Events;
using SteamStat.Core.Settings;
using SteamStat.Core.Updates;

namespace ElectronNet.Services;

internal interface IElectronAutoUpdater
{
    bool AutoDownload { set; }
    bool AutoInstallOnAppQuit { set; }
    bool AllowPrerelease { set; }
    bool AllowDowngrade { set; }
    bool FullChangelog { set; }
    /// <summary>electron-updater 的更新通道（latest / beta），决定读取哪个 *.yml。</summary>
    string Channel { set; }
    event Action? CheckingForUpdate;
    event Action<UpdateInfo>? UpdateAvailable;
    event Action<UpdateInfo>? UpdateNotAvailable;
    event Action<ProgressInfo>? DownloadProgress;
    event Action<UpdateInfo>? UpdateDownloaded;
    event Action<string>? Error;
    Task<string> GetVersionAsync();
    Task CheckForUpdatesAsync();
    Task DownloadUpdateAsync();
    void QuitAndInstall();
}

internal sealed class ElectronAutoUpdater(SteamStat.Core.Environment.AppEnvironment environment) : IElectronAutoUpdater
{
    private AutoUpdater Updater => Electron.AutoUpdater;

    public bool AutoDownload { set => Updater.AutoDownload = value; }
    public bool AutoInstallOnAppQuit { set => Updater.AutoInstallOnAppQuit = value; }
    public bool AllowPrerelease { set => Updater.AllowPrerelease = value; }
    public bool AllowDowngrade { set => Updater.AllowDowngrade = value; }
    public bool FullChangelog { set => Updater.FullChangelog = value; }
    public string Channel { set => Updater.SetChannel = value; }
    public event Action? CheckingForUpdate
    {
        add => Updater.OnCheckingForUpdate += value;
        remove => Updater.OnCheckingForUpdate -= value;
    }
    public event Action<UpdateInfo>? UpdateAvailable
    {
        add => Updater.OnUpdateAvailable += value;
        remove => Updater.OnUpdateAvailable -= value;
    }
    public event Action<UpdateInfo>? UpdateNotAvailable
    {
        add => Updater.OnUpdateNotAvailable += value;
        remove => Updater.OnUpdateNotAvailable -= value;
    }
    public event Action<ProgressInfo>? DownloadProgress
    {
        add => Updater.OnDownloadProgress += value;
        remove => Updater.OnDownloadProgress -= value;
    }
    public event Action<UpdateInfo>? UpdateDownloaded
    {
        add => Updater.OnUpdateDownloaded += value;
        remove => Updater.OnUpdateDownloaded -= value;
    }
    public event Action<string>? Error
    {
        add => Updater.OnError += value;
        remove => Updater.OnError -= value;
    }

    /// <summary>与 csproj &lt;Version&gt; 一致的应用版本；开发模式下 Electron 只能报告自身版本。</summary>
    public Task<string> GetVersionAsync()
        => environment.AppVersion is { } version ? Task.FromResult(version) : Electron.App.GetVersionAsync();
    public async Task CheckForUpdatesAsync() => await Updater.CheckForUpdatesAsync();
    public async Task DownloadUpdateAsync() => await Updater.DownloadUpdateAsync();
    public void QuitAndInstall() => Updater.QuitAndInstall(false, true);
}

/// <summary>
/// 应用更新。按发行形态分两条路径：
/// <list type="bullet">
/// <item>auto（安装版 / AppImage）：electron-updater 读取公开仓库 Releases 的 latest*.yml / beta*.yml，应用内下载安装。</item>
/// <item>notify / managed（便携版、tar.gz、包管理器）：读取 releases.atom 判断新版本，「下载」打开发布页或提示包管理器命令。</item>
/// </list>
/// 两条路径都遵守更新通道：stable 只接收正式版，beta 同时接收 beta / rc 预发布。
/// </summary>
internal sealed class UpdateService : BackgroundService
{
    private static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(3);
    private readonly object _sync = new();
    private readonly HashSet<Task> _operations = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly IElectronAutoUpdater _autoUpdater;
    private readonly IEventBus _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UpdateService> _logger;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _checkInterval;
    private readonly AppDistribution _distribution;
    private readonly IReleaseFeedClient? _releaseFeed;
    private readonly IShellLauncher? _shell;
    private CancellationTokenSource _scheduleChanged = new();
    private string _channel = UpdateChannels.Stable;
    private string? _releasePageUrl;
    private bool _autoUpdateEnabled;
    private bool _isChecking;
    private bool _isDownloading;
    private bool _eventsRegistered;
    private int _disposed;

    public UpdateService(
        IElectronAutoUpdater autoUpdater,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<UpdateService> logger,
        AppDistribution distribution,
        IReleaseFeedClient releaseFeed,
        IShellLauncher shell)
        : this(autoUpdater, eventBus, timeProvider, logger, DefaultStartupDelay, DefaultCheckInterval, distribution, releaseFeed, shell)
    {
    }

    internal UpdateService(
        IElectronAutoUpdater autoUpdater,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<UpdateService> logger,
        TimeSpan startupDelay,
        TimeSpan checkInterval,
        AppDistribution? distribution = null,
        IReleaseFeedClient? releaseFeed = null,
        IShellLauncher? shell = null)
    {
        _autoUpdater = autoUpdater;
        _eventBus = eventBus;
        _timeProvider = timeProvider;
        _logger = logger;
        _startupDelay = startupDelay;
        _checkInterval = checkInterval;
        _distribution = distribution ?? AppDistribution.FromKind("installer", null, null);
        _releaseFeed = releaseFeed;
        _shell = shell;
    }

    private bool UsesElectronUpdater => _distribution.UpdateMode == AppUpdateMode.Auto;

    public string Channel
    {
        get
        {
            lock (_sync) return _channel;
        }
    }

    public bool AutoUpdateEnabled
    {
        get
        {
            lock (_sync) return _autoUpdateEnabled;
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        RegisterEvents();
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        SignalScheduleChanged();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        UnregisterEvents();
        await DrainOperationsAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SetAutoUpdateEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_autoUpdateEnabled == enabled) return;
            _autoUpdateEnabled = enabled;
        }
        SignalScheduleChanged();
        _logger.LogInformation("Automatic update checks are {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>切换更新通道；对 electron-updater 立即生效（下次检查读取对应的 yml）。</summary>
    public void SetChannel(string channel)
    {
        var normalized = UpdateChannels.Normalize(channel);
        lock (_sync)
        {
            if (_channel == normalized) return;
            _channel = normalized;
            _releasePageUrl = null;
        }
        if (_eventsRegistered) ApplyChannelToElectronUpdater(normalized);
        _logger.LogInformation("Update channel set to {Channel}", normalized);
    }

    public async Task<UpdaterStatusDto> GetStatus()
    {
        bool enabled;
        bool checking;
        bool downloading;
        string channel;
        string? releasePageUrl;
        lock (_sync)
        {
            enabled = _autoUpdateEnabled;
            checking = _isChecking;
            downloading = _isDownloading;
            channel = _channel;
            releasePageUrl = _releasePageUrl;
        }
        return new UpdaterStatusDto
        {
            AutoUpdateEnabled = enabled,
            IsChecking = checking,
            IsDownloading = downloading,
            CheckUpdateInterval = (int)_checkInterval.TotalMinutes,
            CurrentVersion = await _autoUpdater.GetVersionAsync(),
            Channel = channel,
            UpdateMode = _distribution.UpdateModeName,
            Distribution = _distribution.Kind,
            ManagedUpdateCommand = _distribution.ManagedUpdateCommand,
            ReleasePageUrl = releasePageUrl
        };
    }

    public void CheckForUpdate()
    {
        lock (_sync)
        {
            if (_isChecking) return;
            _isChecking = true;
        }
        TrackOperation(CheckForUpdateAsync(_stopping.Token));
    }

    public void DownloadUpdate()
    {
        if (!UsesElectronUpdater)
        {
            // 无法应用内安装：打开新版本的发布页，由用户下载或交给包管理器
            string url;
            lock (_sync) url = _releasePageUrl ?? ProjectLinks.ReleasesUrl;
            if (_shell != null) TrackOperation(_shell.OpenExternalAsync(url));
            return;
        }
        lock (_sync)
        {
            if (_isDownloading) return;
            _isDownloading = true;
        }
        TrackOperation(DownloadUpdateAsync(_stopping.Token));
    }

    public void QuitAndInstall()
    {
        if (UsesElectronUpdater) _autoUpdater.QuitAndInstall();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool enabled;
            CancellationTokenSource iteration;
            lock (_sync)
            {
                enabled = _autoUpdateEnabled;
                iteration = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _scheduleChanged.Token);
            }

            using (iteration)
            try
            {
                if (!enabled)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, iteration.Token).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(_startupDelay, _timeProvider, iteration.Token).ConfigureAwait(false);
                CheckForUpdate();

                using var timer = new PeriodicTimer(_checkInterval, _timeProvider);
                while (await timer.WaitForNextTickAsync(iteration.Token).ConfigureAwait(false))
                    CheckForUpdate();
            }
            catch (OperationCanceledException) when (iteration.IsCancellationRequested)
            {
            }
        }
    }

    private void RegisterEvents()
    {
        if (_eventsRegistered) return;
        _autoUpdater.AutoDownload = false;
        _autoUpdater.AutoInstallOnAppQuit = UsesElectronUpdater;
        _autoUpdater.FullChangelog = true;
        ApplyChannelToElectronUpdater(Channel);
        _autoUpdater.CheckingForUpdate += OnCheckingForUpdate;
        _autoUpdater.UpdateAvailable += OnUpdateAvailable;
        _autoUpdater.UpdateNotAvailable += OnUpdateNotAvailable;
        _autoUpdater.DownloadProgress += OnDownloadProgress;
        _autoUpdater.UpdateDownloaded += OnUpdateDownloaded;
        _autoUpdater.Error += OnError;
        _eventsRegistered = true;
    }

    /// <summary>
    /// beta：allowPrerelease 后 GitHub provider 会从 releases.atom 取最新的 beta / 正式版并读取 beta*.yml（缺失时回退 latest*.yml）。
    /// 设置 channel 会让 electron-updater 打开 allowDowngrade，必须随后关掉，否则从 beta 切回 stable 会降级到旧正式版。
    /// </summary>
    private void ApplyChannelToElectronUpdater(string channel)
    {
        if (!UsesElectronUpdater) return;
        try
        {
            var beta = channel == UpdateChannels.Beta;
            _autoUpdater.AllowPrerelease = beta;
            _autoUpdater.Channel = beta ? "beta" : "latest";
            _autoUpdater.AllowDowngrade = false;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to apply the update channel to electron-updater");
        }
    }

    private void UnregisterEvents()
    {
        if (!_eventsRegistered) return;
        try
        {
            _autoUpdater.CheckingForUpdate -= OnCheckingForUpdate;
            _autoUpdater.UpdateAvailable -= OnUpdateAvailable;
            _autoUpdater.UpdateNotAvailable -= OnUpdateNotAvailable;
            _autoUpdater.DownloadProgress -= OnDownloadProgress;
            _autoUpdater.UpdateDownloaded -= OnUpdateDownloaded;
            _autoUpdater.Error -= OnError;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Electron updater events could not be unregistered because the runtime is no longer available");
        }
        finally
        {
            _eventsRegistered = false;
        }
    }

    private void OnCheckingForUpdate()
    {
        lock (_sync) _isChecking = true;
        PublishUpdaterEvent("checking-for-update");
        _logger.LogInformation("Checking for application updates");
    }

    private void OnUpdateAvailable(UpdateInfo info)
    {
        lock (_sync) _isChecking = false;
        PublishUpdaterEvent("update-available", new UpdaterEventDataDto
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
        _logger.LogInformation("Application update {Version} is available", info.Version);
    }

    private void OnUpdateNotAvailable(UpdateInfo info)
    {
        lock (_sync) _isChecking = false;
        PublishUpdaterVersionEvent("update-not-available", info.Version);
        _logger.LogInformation("Application is up to date at version {Version}", info.Version);
    }

    private void OnDownloadProgress(ProgressInfo info)
    {
        lock (_sync) _isDownloading = true;
        PublishUpdaterEvent("download-progress", new UpdaterEventDataDto
        {
            Progress = info.Progress,
            Percent = info.Percent,
            BytesPerSecond = info.BytesPerSecond,
            Transferred = info.Transferred,
            Total = info.Total
        });
    }

    private void OnUpdateDownloaded(UpdateInfo info)
    {
        lock (_sync) _isDownloading = false;
        PublishUpdaterVersionEvent("update-downloaded", info.Version);
        _logger.LogInformation("Application update {Version} was downloaded", info.Version);
    }

    private void OnError(string error)
    {
        lock (_sync)
        {
            _isChecking = false;
            _isDownloading = false;
        }
        PublishUpdaterEvent("error", new UpdaterEventDataDto { Message = error });
        _logger.LogError("Application updater reported an error");
    }

    private void PublishUpdaterEvent(string updaterEvent, UpdaterEventDataDto? data = null)
        => TrackOperation(_eventBus.PublishAsync(new UpdaterStateChanged(new UpdaterEventDto
        {
            UpdaterEvent = updaterEvent,
            Data = data == null ? null : new UpdaterDetailsEventPayload(data)
        }), _stopping.Token));

    private void PublishUpdaterVersionEvent(string updaterEvent, string version)
        => TrackOperation(_eventBus.PublishAsync(new UpdaterStateChanged(new UpdaterEventDto
        {
            UpdaterEvent = updaterEvent,
            Data = new UpdaterVersionEventPayload(version)
        }), _stopping.Token));

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        if (!UsesElectronUpdater)
        {
            await CheckReleaseFeedAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            await _autoUpdater.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync) _isChecking = false;
        }
        catch (Exception exception)
        {
            lock (_sync) _isChecking = false;
            _logger.LogError(exception, "Failed to check for application updates");
        }
    }

    /// <summary>notify / managed 模式：按通道比较 releases.atom 中的版本，结果以与 electron-updater 相同的事件发布。</summary>
    private async Task CheckReleaseFeedAsync(CancellationToken cancellationToken)
    {
        if (_releaseFeed == null)
        {
            lock (_sync) _isChecking = false;
            return;
        }

        OnCheckingForUpdate();
        try
        {
            var currentText = await _autoUpdater.GetVersionAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!SemanticVersion.TryParse(currentText, out var current))
                throw new InvalidOperationException($"Current version '{currentText}' is not a semantic version");

            var entries = await _releaseFeed.GetAsync(cancellationToken).ConfigureAwait(false);
            var update = ReleaseFeed.SelectUpdate(entries, Channel, current);
            if (update == null)
            {
                lock (_sync)
                {
                    _isChecking = false;
                    _releasePageUrl = null;
                }
                PublishUpdaterVersionEvent("update-not-available", current.ToString());
                _logger.LogInformation("Application is up to date at version {Version} ({Channel} channel)", current, Channel);
                return;
            }

            lock (_sync)
            {
                _isChecking = false;
                _releasePageUrl = update.PageUrl;
            }
            PublishUpdaterEvent("update-available", new UpdaterEventDataDto
            {
                Version = update.Version.ToString(),
                ReleaseName = update.Title,
                ReleaseDate = update.UpdatedAt?.ToString("O"),
                ReleaseNotes = update.NotesHtml == null ? [] : [new UpdaterReleaseNoteDto(update.Version.ToString(), update.NotesHtml)],
                Files = []
            });
            _logger.LogInformation("Application update {Version} is available on the {Channel} channel", update.Version, Channel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync) _isChecking = false;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to check the release feed for updates");
            OnError(exception is HttpRequestException ? "network" : exception.Message);
        }
    }

    private async Task DownloadUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _autoUpdater.DownloadUpdateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync) _isDownloading = false;
        }
        catch (Exception exception)
        {
            lock (_sync) _isDownloading = false;
            _logger.LogError(exception, "Failed to download the application update");
        }
    }

    private void TrackOperation(Task operation)
    {
        lock (_sync) _operations.Add(operation);
        _ = operation.ContinueWith(completed =>
        {
            lock (_sync) _operations.Remove(completed);
            if (completed.IsFaulted) _logger.LogError(completed.Exception, "Updater background operation failed");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task DrainOperationsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] operations;
            lock (_sync) operations = [.. _operations];
            if (operations.Length == 0) return;
            try
            {
                await Task.WhenAll(operations).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
            }
        }
    }

    private void SignalScheduleChanged()
    {
        CancellationTokenSource previous;
        lock (_sync)
        {
            previous = _scheduleChanged;
            _scheduleChanged = new CancellationTokenSource();
        }
        previous.Cancel();
        previous.Dispose();
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        UnregisterEvents();
        _stopping.Cancel();
        CancellationTokenSource schedule;
        lock (_sync) schedule = _scheduleChanged;
        schedule.Cancel();
        schedule.Dispose();
        _stopping.Dispose();
        base.Dispose();
    }
}
