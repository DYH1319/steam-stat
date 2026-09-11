using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Http;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamAccessInfrastructureTests
{
    [Test]
    public async Task CmScheduler_BoundsEachAccountOperationAndKeepsPartitionsIndependent()
    {
        var options = new SteamAccessOptions
        {
            CmConcurrencyLimit = 1,
            CmQueueLimit = 1,
            CmOperationTimeout = TimeSpan.FromMinutes(1)
        };
        var monitor = new SteamConnectivityMonitor(options, TimeProvider.System);
        await using var scheduler = new SteamCmOperationScheduler(
            options, monitor, TimeProvider.System, NullLogger<SteamCmOperationScheduler>.Instance);
        var firstGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.RunAsync("alice", "pics", 1, _ => firstGate.Task, CancellationToken.None);
        using var queuedCancellation = new CancellationTokenSource();
        var queued = scheduler.RunAsync("alice", "pics", 1, _ => Task.FromResult(2), queuedCancellation.Token);
        await Task.Delay(20);

        var rejected = () => scheduler.RunAsync("alice", "pics", 1, _ => Task.FromResult(3), CancellationToken.None);
        await rejected.Should().ThrowAsync<SteamCmSchedulerRejectedException>();
        (await scheduler.RunAsync("bob", "pics", 1, _ => Task.FromResult(4), CancellationToken.None)).Should().Be(4);

        queuedCancellation.Cancel();
        await FluentActions.Awaiting(() => queued).Should().ThrowAsync<OperationCanceledException>();
        firstGate.SetResult(1);
        (await first).Should().Be(1);
    }

    [Test]
    public async Task CmScheduler_UsesOperationTimeoutWithoutConvertingCallerCancellation()
    {
        var options = new SteamAccessOptions
        {
            CmConcurrencyLimit = 1,
            CmQueueLimit = 0,
            CmOperationTimeout = TimeSpan.FromMilliseconds(20)
        };
        var monitor = new SteamConnectivityMonitor(options, TimeProvider.System);
        await using var scheduler = new SteamCmOperationScheduler(
            options, monitor, TimeProvider.System, NullLogger<SteamCmOperationScheduler>.Instance);

        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = () => scheduler.RunAsync(
            "alice", "owned-games", 7, _ => operation.Task, CancellationToken.None);
        await timeout.Should().ThrowAsync<TimeoutException>();
        monitor.Current.CmTransport.LastFailureKind.Should().Be(SteamFailureKind.Timeout);
        var whilePending = () => scheduler.RunAsync(
            "alice", "owned-games", 7, _ => Task.FromResult(1), CancellationToken.None);
        await whilePending.Should().ThrowAsync<SteamCmSchedulerRejectedException>();
        operation.SetResult(0);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = () => scheduler.RunAsync("bob", "owned-games", 8, _ => Task.FromResult(1), cancellation.Token);
        await canceled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task CmScheduler_ShutdownCancelsActiveAndQueuedOperations()
    {
        var options = new SteamAccessOptions
        {
            CmConcurrencyLimit = 1,
            CmQueueLimit = 1,
            CmOperationTimeout = TimeSpan.FromMinutes(1),
            CmShutdownTimeout = TimeSpan.FromSeconds(1)
        };
        var monitor = new SteamConnectivityMonitor(options, TimeProvider.System);
        await using var scheduler = new SteamCmOperationScheduler(
            options, monitor, TimeProvider.System, NullLogger<SteamCmOperationScheduler>.Instance);
        var active = scheduler.RunAsync<int>(
            "alice", "pics", 1,
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            },
            CancellationToken.None);
        var queued = scheduler.RunAsync(
            "alice", "pics", 1, _ => Task.FromResult(2), CancellationToken.None);
        await Task.Delay(20);

        await scheduler.DisposeAsync();

        await FluentActions.Awaiting(() => active).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => queued).Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public void ConnectivityMonitor_TracksDependenciesIndependentlyAndExpiresEvidence()
    {
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var options = new SteamAccessOptions { HealthEvidenceLifetime = TimeSpan.FromMinutes(5) };
        var monitor = new SteamConnectivityMonitor(options, time);

        monitor.ReportSuccess(SteamDependency.Store);
        monitor.ReportFailure(
            SteamDependency.SteamWebApi,
            new SteamFailure(SteamFailureKind.RateLimited, "rate_limiter_rejected"),
            isRateLimited: true);

        monitor.Current.Store.State.Should().Be(DependencyHealthState.Healthy);
        monitor.Current.SteamWebApi.Should().BeEquivalentTo(new
        {
            State = DependencyHealthState.Degraded,
            LastFailureKind = (SteamFailureKind?)SteamFailureKind.RateLimited,
            IsRateLimited = true
        });
        monitor.Current.Cdn.State.Should().Be(DependencyHealthState.Unknown);

        time.Advance(TimeSpan.FromMinutes(6));
        monitor.Current.Store.State.Should().Be(DependencyHealthState.Unknown);
        monitor.Current.SteamWebApi.State.Should().Be(DependencyHealthState.Unknown);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
