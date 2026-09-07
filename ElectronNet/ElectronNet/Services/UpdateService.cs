using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;

namespace ElectronNet.Services;

internal interface IElectronAutoUpdater
{
    bool AutoDownload { set; }
    bool AutoInstallOnAppQuit { set; }
    bool AllowPrerelease { set; }
    bool AllowDowngrade { set; }
    bool FullChangelog { set; }
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

internal sealed class ElectronAutoUpdater : IElectronAutoUpdater
{
    private AutoUpdater Updater => Electron.AutoUpdater;

    public bool AutoDownload { set => Updater.AutoDownload = value; }
    public bool AutoInstallOnAppQuit { set => Updater.AutoInstallOnAppQuit = value; }
    public bool AllowPrerelease { set => Updater.AllowPrerelease = value; }
    public bool AllowDowngrade { set => Updater.AllowDowngrade = value; }
    public bool FullChangelog { set => Updater.FullChangelog = value; }
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

    public Task<string> GetVersionAsync() => Electron.App.GetVersionAsync();
    public async Task CheckForUpdatesAsync() => await Updater.CheckForUpdatesAsync();
    public async Task DownloadUpdateAsync() => await Updater.DownloadUpdateAsync();
    public void QuitAndInstall() => Updater.QuitAndInstall(false, true);
}

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
    private CancellationTokenSource _scheduleChanged = new();
    private bool _autoUpdateEnabled;
    private bool _isChecking;
    private bool _isDownloading;
    private bool _eventsRegistered;
    private int _disposed;

    public UpdateService(
        IElectronAutoUpdater autoUpdater,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<UpdateService> logger)
        : this(autoUpdater, eventBus, timeProvider, logger, DefaultStartupDelay, DefaultCheckInterval)
    {
    }

    internal UpdateService(
        IElectronAutoUpdater autoUpdater,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<UpdateService> logger,
        TimeSpan startupDelay,
        TimeSpan checkInterval)
    {
        _autoUpdater = autoUpdater;
        _eventBus = eventBus;
        _timeProvider = timeProvider;
        _logger = logger;
        _startupDelay = startupDelay;
        _checkInterval = checkInterval;
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

    public async Task<UpdaterStatusDto> GetStatus()
    {
        bool enabled;
        bool checking;
        bool downloading;
        lock (_sync)
        {
            enabled = _autoUpdateEnabled;
            checking = _isChecking;
            downloading = _isDownloading;
        }
        return new UpdaterStatusDto(enabled, checking, downloading, (int)_checkInterval.TotalMinutes, await _autoUpdater.GetVersionAsync());
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
        lock (_sync)
        {
            if (_isDownloading) return;
            _isDownloading = true;
        }
        TrackOperation(DownloadUpdateAsync(_stopping.Token));
    }

    public void QuitAndInstall() => _autoUpdater.QuitAndInstall();

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
        _autoUpdater.AutoInstallOnAppQuit = true;
        _autoUpdater.AllowPrerelease = false;
        _autoUpdater.AllowDowngrade = false;
        _autoUpdater.FullChangelog = true;
        _autoUpdater.CheckingForUpdate += OnCheckingForUpdate;
        _autoUpdater.UpdateAvailable += OnUpdateAvailable;
        _autoUpdater.UpdateNotAvailable += OnUpdateNotAvailable;
        _autoUpdater.DownloadProgress += OnDownloadProgress;
        _autoUpdater.UpdateDownloaded += OnUpdateDownloaded;
        _autoUpdater.Error += OnError;
        _eventsRegistered = true;
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
