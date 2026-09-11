using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using SteamKit2;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamGatewayInfrastructureTests
{
    [Test]
    public void GatewayResult_DistinguishesSuccessEmptyFailureAndStaleFallback()
    {
        var empty = SteamGatewayResult<IReadOnlyList<uint>>.Succeeded(
            [], SteamDataSource.Http, SteamFreshness.Fresh, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));
        var failure = SteamGatewayResult<IReadOnlyList<uint>>.Failed(SteamFailureKind.Transient, "http_server_error");
        var stale = SteamGatewayResult<IReadOnlyList<uint>>.Succeeded(
            [730], SteamDataSource.Sqlite, SteamFreshness.Stale, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1), SteamFailureKind.Offline, "http_connection_error");

        empty.IsSuccess.Should().BeTrue();
        empty.Value.Should().BeEmpty();
        failure.IsSuccess.Should().BeFalse();
        failure.Value.Should().BeNull();
        stale.IsSuccess.Should().BeTrue();
        stale.Failure.Should().Be(SteamFailureKind.Offline);
    }

    [Test]
    public void CacheKey_NormalizesStableDimensionsAndRejectsMissingIdentity()
    {
        var key = SteamCacheKey.Create(" App-Metadata ", " PUBLIC ", "730", " SCHINESE ", " DEFAULT ", 1);

        key.Should().BeEquivalentTo(new
        {
            ResourceKind = "app-metadata",
            ScopeId = "public",
            ResourceId = "730",
            Language = "schinese",
            Variant = "default",
            SchemaVersion = 1
        });
        var action = () => SteamCacheKey.Create(" ", "public", "730");
        action.Should().Throw<ArgumentException>();
    }

    [Test]
    public void CachePolicy_UsesDistinctFreshStaleExpiredAndRetentionBoundaries()
    {
        var fetchedAt = DateTimeOffset.UnixEpoch;
        var policy = new SteamCachePolicy(
            TimeSpan.FromHours(1), TimeSpan.FromHours(4), TimeSpan.FromDays(1), 1024, true, true);
        var entry = new SteamResourceCacheEntry(
            SteamCacheKey.Create("app-metadata", "public", "730"), "json-v1", "{}", SteamDataSource.Http,
            fetchedAt, fetchedAt.AddHours(1), fetchedAt.AddDays(1), fetchedAt);

        policy.GetFreshness(entry, fetchedAt.AddHours(1).AddTicks(-1)).Should().Be(SteamFreshness.Fresh);
        policy.GetFreshness(entry, fetchedAt.AddHours(1)).Should().Be(SteamFreshness.Stale);
        policy.GetFreshness(entry, fetchedAt.AddHours(4)).Should().Be(SteamFreshness.Expired);
        policy.ShouldRetain(entry, fetchedAt.AddDays(1).AddTicks(-1)).Should().BeTrue();
        policy.ShouldRetain(entry, fetchedAt.AddDays(1)).Should().BeFalse();
        policy.CanNegativeCache(SteamFailureKind.NotFound).Should().BeTrue();
        Enum.GetValues<SteamFailureKind>().Where(kind => kind != SteamFailureKind.NotFound)
            .Should().OnlyContain(kind => !policy.CanNegativeCache(kind));
    }

    [TestCase(EResult.InvalidPassword, SteamFailureKind.AuthenticationRequired, "steam_authentication_required")]
    [TestCase(EResult.AccessDenied, SteamFailureKind.Forbidden, "steam_access_denied")]
    [TestCase(EResult.AccountNotFound, SteamFailureKind.NotFound, "steam_not_found")]
    [TestCase(EResult.RateLimitExceeded, SteamFailureKind.RateLimited, "steam_rate_limited")]
    [TestCase(EResult.Busy, SteamFailureKind.Transient, "steam_transient")]
    [TestCase(EResult.Timeout, SteamFailureKind.Timeout, "steam_timeout")]
    [TestCase(EResult.Fail, SteamFailureKind.Unknown, "steam_unknown")]
    public void ResultClassifier_ClassifiesSteamResults(EResult result, SteamFailureKind kind, string code)
    {
        new SteamResultClassifier().Classify(result).Should().Be(new SteamFailure(kind, code));
    }

    [TestCase(EResult.AccessDenied)]
    [TestCase(EResult.AccountNotFound)]
    [TestCase(EResult.InvalidPassword)]
    public void ResultClassifier_UsesAuthenticationContextForTerminalLogonResults(EResult result)
    {
        new SteamResultClassifier().Classify(result, SteamResultContext.Authentication)
            .Should().Be(new SteamFailure(SteamFailureKind.AuthenticationRequired, "steam_authentication_required"));
    }

    [TestCase(HttpStatusCode.RequestTimeout, SteamFailureKind.Timeout, "http_timeout")]
    [TestCase(HttpStatusCode.Unauthorized, SteamFailureKind.AuthenticationRequired, "http_authentication_required")]
    [TestCase(HttpStatusCode.Forbidden, SteamFailureKind.Forbidden, "http_forbidden")]
    [TestCase(HttpStatusCode.NotFound, SteamFailureKind.NotFound, "http_not_found")]
    [TestCase(HttpStatusCode.TooManyRequests, SteamFailureKind.RateLimited, "http_rate_limited")]
    [TestCase(HttpStatusCode.ServiceUnavailable, SteamFailureKind.Transient, "http_server_error")]
    [TestCase(HttpStatusCode.BadRequest, SteamFailureKind.Protocol, "http_client_error")]
    public void ResultClassifier_ClassifiesHttpStatus(HttpStatusCode status, SteamFailureKind kind, string code)
    {
        new SteamResultClassifier().Classify(status).Should().Be(new SteamFailure(kind, code));
    }

    [Test]
    public void ResultClassifier_ClassifiesTransportAndInvalidPayloadWithoutLeakingMessages()
    {
        var classifier = new SteamResultClassifier();

        classifier.Classify(new HttpRequestException("secret", new SocketException()))
            .Should().Be(new SteamFailure(SteamFailureKind.Offline, "http_connection_error"));
        classifier.Classify(new JsonException("sensitive response"))
            .Should().Be(new SteamFailure(SteamFailureKind.InvalidData, "invalid_payload"));
    }

    [Test]
    public void ResultClassifier_PropagatesCallerCancellationAndClassifiesIndependentTimeout()
    {
        var classifier = new SteamResultClassifier();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = () => classifier.Classify(new OperationCanceledException(cancellation.Token), cancellation.Token);

        cancelled.Should().Throw<OperationCanceledException>();
        classifier.Classify(new OperationCanceledException(), CancellationToken.None)
            .Should().Be(new SteamFailure(SteamFailureKind.Timeout, "operation_timeout"));
    }

    [Test]
    public async Task Coalescer_RemovesFaultedOperation()
    {
        var coalescer = new SteamRequestCoalescer<string>();

        var failed = () => coalescer.RunAsync<int>(
            "same", _ => Task.FromException<int>(new InvalidOperationException("failure")), CancellationToken.None);

        await failed.Should().ThrowAsync<InvalidOperationException>();
        SpinWait.SpinUntil(() => coalescer.InflightCount == 0, TimeSpan.FromSeconds(1)).Should().BeTrue();
        (await coalescer.RunAsync("same", _ => Task.FromResult(42), CancellationToken.None)).Should().Be(42);
    }

    [Test]
    public async Task Coalescer_SharesOperationButCancelsOnlyIndividualWaiterAndRemovesCompletedTask()
    {
        var coalescer = new SteamRequestCoalescer<string>();
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var firstCaller = new CancellationTokenSource();

        Task<int> Operation(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return source.Task;
        }

        var first = coalescer.RunAsync("same", Operation, CancellationToken.None, firstCaller.Token);
        var second = coalescer.RunAsync("same", Operation, CancellationToken.None);
        firstCaller.Cancel();
        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();
        source.SetResult(42);

        (await second).Should().Be(42);
        calls.Should().Be(1);
        SpinWait.SpinUntil(() => coalescer.InflightCount == 0, TimeSpan.FromSeconds(1)).Should().BeTrue();
        (await coalescer.RunAsync("same", _ => Task.FromResult(43), CancellationToken.None)).Should().Be(43);
        calls.Should().Be(1);
    }
}
