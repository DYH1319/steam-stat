using ElectronNET.API.Entities;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Events;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class UpdateServiceTests
{
    [Test]
    public async Task EnabledService_UsesPeriodicScheduleAndStopsCleanly()
    {
        var updater = new FakeAutoUpdater();
        using var service = new UpdateService(
            updater,
            new RecordingEventBus(),
            TimeProvider.System,
            NullLogger<UpdateService>.Instance,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        service.SetAutoUpdateEnabled(true);

        await WaitUntilAsync(() => updater.CheckCount >= 2);
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        updater.CheckCount.Should().BeGreaterThanOrEqualTo(2);
        updater.RemovedHandlerCount.Should().Be(6);
    }

    [Test]
    public async Task RapidScheduleChanges_DoNotRaceWithDisposedTokens()
    {
        using var service = new UpdateService(
            new FakeAutoUpdater(),
            new RecordingEventBus(),
            TimeProvider.System,
            NullLogger<UpdateService>.Instance,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1));

        await service.StartAsync(CancellationToken.None);
        for (var index = 0; index < 100; index++) service.SetAutoUpdateEnabled(index % 2 == 0);

        var stop = () => service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        await stop.Should().NotThrowAsync();
    }

    [Test]
    public async Task StopAsync_ToleratesElectronRuntimeBecomingUnavailableBeforeUnregistration()
    {
        var updater = new FakeAutoUpdater { ThrowWhenRemovingHandler = true };
        using var service = new UpdateService(
            updater,
            new RecordingEventBus(),
            TimeProvider.System,
            NullLogger<UpdateService>.Instance,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1));

        await service.StartAsync(CancellationToken.None);

        var stop = () => service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        await stop.Should().NotThrowAsync();
    }

    [Test]
    public async Task StopAsync_DrainsCanceledEventPublicationsWithoutFailing()
    {
        var events = new CancelingEventBus();
        using var service = new UpdateService(
            new FakeAutoUpdater(),
            events,
            TimeProvider.System,
            NullLogger<UpdateService>.Instance,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1));

        await service.StartAsync(CancellationToken.None);
        service.CheckForUpdate();
        await events.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var stop = () => service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        await stop.Should().NotThrowAsync();
    }

    [Test]
    public async Task StopAsync_CancelsAndWaitsForTrackedUpdateOperation()
    {
        var updater = new FakeAutoUpdater { PendingCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = new UpdateService(
            updater,
            new RecordingEventBus(),
            TimeProvider.System,
            NullLogger<UpdateService>.Instance,
            TimeSpan.Zero,
            TimeSpan.FromHours(1));

        await service.StartAsync(CancellationToken.None);
        service.SetAutoUpdateEnabled(true);
        await WaitUntilAsync(() => updater.CheckCount == 1);

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        updater.PendingCheck.Task.IsCompleted.Should().BeFalse();
        (await service.GetStatus()).IsChecking.Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class RecordingEventBus : IEventBus
    {
        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : notnull
            => Task.CompletedTask;
    }

    private sealed class CancelingEventBus : IEventBus
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : notnull
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FakeAutoUpdater : IElectronAutoUpdater
    {
        private Action? _checkingForUpdate;
        private Action<UpdateInfo>? _updateAvailable;
        private Action<UpdateInfo>? _updateNotAvailable;
        private Action<ProgressInfo>? _downloadProgress;
        private Action<UpdateInfo>? _updateDownloaded;
        private Action<string>? _error;

        public int CheckCount { get; private set; }
        public int RemovedHandlerCount { get; private set; }
        public bool ThrowWhenRemovingHandler { get; init; }
        public TaskCompletionSource? PendingCheck { get; init; }
        public bool AutoDownload { private get; set; }
        public bool AutoInstallOnAppQuit { private get; set; }
        public bool AllowPrerelease { private get; set; }
        public bool AllowDowngrade { private get; set; }
        public bool FullChangelog { private get; set; }
        public event Action? CheckingForUpdate
        {
            add => _checkingForUpdate += value;
            remove
            {
                if (ThrowWhenRemovingHandler) throw new InvalidOperationException("Runtime stopped");
                _checkingForUpdate -= value;
                RemovedHandlerCount++;
            }
        }
        public event Action<UpdateInfo>? UpdateAvailable
        {
            add => _updateAvailable += value;
            remove { _updateAvailable -= value; RemovedHandlerCount++; }
        }
        public event Action<UpdateInfo>? UpdateNotAvailable
        {
            add => _updateNotAvailable += value;
            remove { _updateNotAvailable -= value; RemovedHandlerCount++; }
        }
        public event Action<ProgressInfo>? DownloadProgress
        {
            add => _downloadProgress += value;
            remove { _downloadProgress -= value; RemovedHandlerCount++; }
        }
        public event Action<UpdateInfo>? UpdateDownloaded
        {
            add => _updateDownloaded += value;
            remove { _updateDownloaded -= value; RemovedHandlerCount++; }
        }
        public event Action<string>? Error
        {
            add => _error += value;
            remove { _error -= value; RemovedHandlerCount++; }
        }

        public Task<string> GetVersionAsync() => Task.FromResult("1.0.0");

        public async Task CheckForUpdatesAsync()
        {
            CheckCount++;
            _checkingForUpdate?.Invoke();
            if (PendingCheck != null)
            {
                await PendingCheck.Task;
                return;
            }
            _updateNotAvailable?.Invoke(new UpdateInfo
            {
                Version = "1.0.0",
                ReleaseDate = string.Empty,
                ReleaseName = string.Empty
            });
        }

        public Task DownloadUpdateAsync() => Task.CompletedTask;
        public void QuitAndInstall() { }
    }
}
