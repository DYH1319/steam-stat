using ElectronNET.API.Entities;
using ElectronNet.Hosting;
using ElectronNet.Services;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Updates;
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

    [Test]
    public async Task BetaChannel_EnablesPrereleaseWithoutAllowingDowngrade()
    {
        var updater = new FakeAutoUpdater();
        using var service = new UpdateService(updater, new RecordingEventBus(), TimeProvider.System,
            NullLogger<UpdateService>.Instance, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        await service.StartAsync(CancellationToken.None);

        updater.Channel.Should().Be("latest");
        updater.AllowPrerelease.Should().BeFalse();

        service.SetChannel("beta");
        updater.AllowPrerelease.Should().BeTrue();
        updater.Channel.Should().Be("beta");
        updater.AllowDowngrade.Should().BeFalse();
        (await service.GetStatus()).Channel.Should().Be("beta");

        service.SetChannel("stable");
        updater.AllowPrerelease.Should().BeFalse();
        updater.Channel.Should().Be("latest");
        updater.AllowDowngrade.Should().BeFalse();
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task NotifyMode_ChecksReleaseFeedAndOpensReleasePageInsteadOfDownloading()
    {
        var updater = new FakeAutoUpdater { Version = "4.0.0" };
        var events = new RecordingEventBus();
        var shell = new RecordingShell();
        var feed = new FakeReleaseFeed(
            Entry("v4.1.0-beta.1"),
            Entry("v4.0.2"));
        using var service = new UpdateService(updater, events, TimeProvider.System, NullLogger<UpdateService>.Instance,
            TimeSpan.FromHours(1), TimeSpan.FromHours(1), AppDistribution.FromKind("portable", null, null), feed, shell);
        await service.StartAsync(CancellationToken.None);

        service.CheckForUpdate();
        await WaitUntilAsync(() => events.Updater.Any(e => e.UpdaterEvent == "update-available"));

        var available = events.Updater.Single(e => e.UpdaterEvent == "update-available");
        ((UpdaterDetailsEventPayload)available.Data!).Value.Version.Should().Be("4.0.2");
        updater.CheckCount.Should().Be(0, "portable builds cannot be installed by electron-updater");
        var status = await service.GetStatus();
        status.UpdateMode.Should().Be("notify");
        status.ReleasePageUrl.Should().Be("https://github.com/DYH1319/steam-stat/releases/tag/v4.0.2");

        service.DownloadUpdate();
        await WaitUntilAsync(() => shell.Opened.Count == 1);
        shell.Opened.Should().Equal("https://github.com/DYH1319/steam-stat/releases/tag/v4.0.2");
        updater.DownloadCount.Should().Be(0);

        service.SetChannel("beta");
        service.CheckForUpdate();
        await WaitUntilAsync(() => events.Updater.Count(e => e.UpdaterEvent == "update-available") == 2);
        ((UpdaterDetailsEventPayload)events.Updater.Last(e => e.UpdaterEvent == "update-available").Data!).Value.Version.Should().Be("4.1.0-beta.1");
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ManagedMode_WithoutNewerRelease_ReportsUpToDate()
    {
        var events = new RecordingEventBus();
        using var service = new UpdateService(new FakeAutoUpdater { Version = "4.0.2" }, events, TimeProvider.System,
            NullLogger<UpdateService>.Instance, TimeSpan.FromHours(1), TimeSpan.FromHours(1),
            AppDistribution.FromKind("scoop", null, null), new FakeReleaseFeed(Entry("v4.0.2")), new RecordingShell());
        await service.StartAsync(CancellationToken.None);

        service.CheckForUpdate();
        await WaitUntilAsync(() => events.Updater.Any(e => e.UpdaterEvent == "update-not-available"));

        var status = await service.GetStatus();
        status.UpdateMode.Should().Be("managed");
        status.ManagedUpdateCommand.Should().Be("scoop update steam-stat");
        status.IsChecking.Should().BeFalse();
        await service.StopAsync(CancellationToken.None);
    }

    private static ReleaseFeedEntry Entry(string tag)
        => new(SemanticVersion.Parse(tag), tag, tag, $"https://github.com/DYH1319/steam-stat/releases/tag/{tag}", null, "<p>notes</p>");

    private const string MissingLinuxMetadataError =
        "Error: Cannot find latest-linux.yml in the latest release artifacts (https://github.com/DYH1319/steam-stat/releases/download/v4.0.0/latest-linux.yml): HttpError: 404";

    [Test]
    public async Task MissingPlatformMetadata_IsReportedAsNoUpdateAvailable()
    {
        var updater = new FakeAutoUpdater { Version = "4.1.0" };
        var events = new RecordingEventBus();
        using var service = new UpdateService(
            updater, events, TimeProvider.System, NullLogger<UpdateService>.Instance,
            TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        await service.StartAsync(CancellationToken.None);

        updater.RaiseError(MissingLinuxMetadataError);

        await WaitUntilAsync(() => events.Updater.Count > 0);
        events.Updater.Should().ContainSingle();
        events.Updater[0].UpdaterEvent.Should().Be("update-not-available");
        events.Updater[0].Data.Should().BeOfType<UpdaterVersionEventPayload>()
            .Which.Should().Be(new UpdaterVersionEventPayload("4.1.0"));
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task OtherUpdaterErrors_AreStillReported()
    {
        var updater = new FakeAutoUpdater();
        var events = new RecordingEventBus();
        using var service = new UpdateService(
            updater, events, TimeProvider.System, NullLogger<UpdateService>.Instance,
            TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        await service.StartAsync(CancellationToken.None);

        updater.RaiseError("Error: net::ERR_CONNECTION_RESET");

        await WaitUntilAsync(() => events.Updater.Count > 0);
        events.Updater.Should().ContainSingle().Which.UpdaterEvent.Should().Be("error");
        await service.StopAsync(CancellationToken.None);
    }

    [TestCase(MissingLinuxMetadataError, true)]
    [TestCase("Error: Cannot find beta.yml in the latest release artifacts (https://example/beta.yml): HttpError: 404", true)]
    [TestCase("Error: Cannot find latest-linux.yml in the latest release artifacts (https://example): HttpError: 503", false)]
    [TestCase("Error: net::ERR_CONNECTION_RESET", false)]
    [TestCase(null, false)]
    public void IsMissingPlatformMetadata_MatchesOnlyMissingChannelFiles(string? error, bool expected)
        => UpdateService.IsMissingPlatformMetadata(error).Should().Be(expected);

    private sealed class FakeReleaseFeed(params ReleaseFeedEntry[] entries) : IReleaseFeedClient
    {
        public Task<IReadOnlyList<ReleaseFeedEntry>> GetAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ReleaseFeedEntry>>(entries);
    }

    private sealed class RecordingShell : IShellLauncher
    {
        public List<string> Opened { get; } = [];

        public Task<bool> OpenExternalAsync(string url)
        {
            lock (Opened) Opened.Add(url);
            return Task.FromResult(true);
        }

        public Task<bool> ShowItemInFolderAsync(string fullPath) => Task.FromResult(true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class RecordingEventBus : IEventBus
    {
        private readonly List<UpdaterEventDto> _updater = [];

        public IReadOnlyList<UpdaterEventDto> Updater
        {
            get
            {
                lock (_updater) return _updater.ToArray();
            }
        }

        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : notnull
        {
            if (message is UpdaterStateChanged changed)
                lock (_updater) _updater.Add(changed.Event);
            return Task.CompletedTask;
        }
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
        public bool AllowPrerelease { get; set; }
        public bool AllowDowngrade { get; set; }
        public bool FullChangelog { private get; set; }
        public string Channel { get; set; } = string.Empty;
        public string Version { get; init; } = "1.0.0";
        public int DownloadCount { get; private set; }
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

        public Task<string> GetVersionAsync() => Task.FromResult(Version);

        public void RaiseError(string error) => _error?.Invoke(error);

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

        public Task DownloadUpdateAsync()
        {
            DownloadCount++;
            return Task.CompletedTask;
        }
        public void QuitAndInstall() { }
    }
}
