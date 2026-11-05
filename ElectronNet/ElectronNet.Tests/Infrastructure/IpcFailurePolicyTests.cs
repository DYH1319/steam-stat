using System.Net.Http;
using ElectronNet.Infrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Http;
using SteamKit2;
using SteamStat.Core.Sync;

namespace ElectronNet.Tests.Infrastructure;

[TestFixture]
public sealed class IpcFailurePolicyTests
{
    private static readonly IpcInvoke<IpcNoRequest, int> Endpoint = new("test:channel", "testGet");
    private static readonly IpcInvoke<IpcNoRequest, int> OtherEndpoint = new("test:other", "otherGet");

    private static IEnumerable<TestCaseData> Failures()
    {
        yield return new TestCaseData(new OperationCanceledException(), IpcFailureCodes.Cancelled).SetName("user cancellation");
        yield return new TestCaseData(new TaskCanceledException(), IpcFailureCodes.Cancelled).SetName("task cancellation");
        yield return new TestCaseData(new HttpRequestException("down"), IpcFailureCodes.Unavailable).SetName("HTTP failure");
        yield return new TestCaseData(new TimeoutException(), IpcFailureCodes.Unavailable).SetName("timeout");
        yield return new TestCaseData(new TaskCanceledException("timed out", new TimeoutException()), IpcFailureCodes.Unavailable).SetName("HttpClient timeout is not a cancellation");
        yield return new TestCaseData(new SyncStorageException(SyncStorageErrorKind.Network, "down"), IpcFailureCodes.Unavailable).SetName("sync storage");
        yield return new TestCaseData(new SteamRateLimiterRejectedException("quota"), IpcFailureCodes.Unavailable).SetName("Steam quota");
        yield return new TestCaseData(new AsyncJobFailedException(), IpcFailureCodes.Unavailable).SetName("Steam CM job failure");
        yield return new TestCaseData(new InvalidOperationException("wrapped", new HttpRequestException("down")), IpcFailureCodes.Unavailable).SetName("wrapped network failure");
        yield return new TestCaseData(new IpcRequestBindingException("bad"), IpcFailureCodes.InvalidRequest).SetName("contract violation");
        yield return new TestCaseData(new InvalidOperationException("bug"), IpcFailureCodes.Internal).SetName("unknown exception");
        yield return new TestCaseData(new SqliteException("database is locked", 5), IpcFailureCodes.Internal).SetName("database error");
        yield return new TestCaseData(new IOException("disk"), IpcFailureCodes.Internal).SetName("file error");
    }

    [TestCaseSource(nameof(Failures))]
    public void Classify_SortsFailures(Exception exception, string code)
        => IpcFailurePolicy.Classify(exception).Should().Be(code);

    [TestCaseSource(nameof(Failures))]
    public void Run_RejectsWithOnlyTheCode(Exception exception, string code)
    {
        var (policy, _) = CreatePolicy();

        var rejection = () => policy.Run(Endpoint, () => throw exception);

        rejection.Should().Throw<IpcFailureException>().Which.Message.Should().Be(IpcFailureCodes.MessagePrefix + code);
    }

    [Test]
    public void Run_NeverSendsTheExceptionTextToTheRenderer()
    {
        var (policy, _) = CreatePolicy();

        var rejection = () => policy.Run(Endpoint, () => throw new IOException("/home/someone/.config/secret.db"));

        rejection.Should().Throw<IpcFailureException>().Which.Message.Should().NotContain("someone");
    }

    [Test]
    public async Task RunAsync_RejectsFailuresThrownAfterAnAwait()
    {
        var (policy, recorder) = CreatePolicy();

        var rejection = () => policy.RunAsync(Endpoint, async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("bug");
        });

        await rejection.Should().ThrowAsync<IpcFailureException>();
        recorder.Failures.Should().ContainSingle();
    }

    [Test]
    public void Run_ReturnsTheResultWithoutReporting()
    {
        var (policy, recorder) = CreatePolicy();

        policy.Run(Endpoint, () => 42).Should().Be(42);

        recorder.Failures.Should().BeEmpty();
    }

    private static IEnumerable<Exception> Defects()
    {
        yield return new InvalidOperationException("bug");
        yield return new IpcRequestBindingException("bad");
    }

    [TestCaseSource(nameof(Defects))]
    public void Run_ReportsDefects(Exception exception)
    {
        var (policy, recorder) = CreatePolicy();

        Rejected(policy, Endpoint, exception);

        recorder.Failures.Should().ContainSingle().Which.Should().Be(("testGet", exception));
    }

    [Test]
    public void Run_DoesNotReportCancellationsOrUnavailableServices()
    {
        var (policy, recorder) = CreatePolicy();

        Rejected(policy, Endpoint, new OperationCanceledException());
        Rejected(policy, Endpoint, new HttpRequestException("down"));

        recorder.Failures.Should().BeEmpty();
    }

    [Test]
    public void Run_ReportsARepeatingFailureOnce()
    {
        var (policy, recorder) = CreatePolicy();

        for (var i = 0; i < 100; i++) Rejected(policy, Endpoint, new InvalidOperationException("bug"));

        recorder.Failures.Should().ContainSingle();
    }

    [Test]
    public void Run_StillReportsOtherDefectsWhileOneKeepsFailing()
    {
        var (policy, recorder) = CreatePolicy();

        for (var i = 0; i < 100; i++) Rejected(policy, Endpoint, new InvalidOperationException("poll"));
        Rejected(policy, Endpoint, new IOException("different type"));
        Rejected(policy, OtherEndpoint, new InvalidOperationException("different endpoint"));

        recorder.Failures.Should().HaveCount(3);
    }

    private static void Rejected(IpcFailurePolicy policy, IIpcEndpointDescriptor endpoint, Exception exception)
    {
        var rejection = () => policy.Run(endpoint, () => throw exception);
        rejection.Should().Throw<IpcFailureException>();
    }

    private static (IpcFailurePolicy Policy, FakeRecorder Recorder) CreatePolicy()
    {
        var recorder = new FakeRecorder();
        return (new IpcFailurePolicy(recorder, NullLogger<IpcFailurePolicy>.Instance), recorder);
    }

    private sealed class FakeRecorder : IIpcFailureRecorder
    {
        public List<(string Endpoint, Exception Exception)> Failures { get; } = [];

        public void RecordIpcFailure(string endpoint, Exception exception) => Failures.Add((endpoint, exception));
    }
}
