using FluentAssertions;
using SteamKit2;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Login;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class LoginCharacterizationTests
{
    private static readonly EResult[] TerminalResults =
    [
        EResult.InvalidPassword,
        EResult.AccessDenied,
        EResult.Expired,
        EResult.Revoked,
        EResult.InvalidSignature,
        EResult.AccountDisabled,
        EResult.AccountLockedDown,
        EResult.AccountLogonDenied,
        EResult.AccountLoginDeniedNeedTwoFactor,
        EResult.Banned,
        EResult.AccountNotFound
    ];

    [Test]
    public void ReconnectLogonResult_TerminalSetIsFixed()
    {
        TerminalResults.Should().OnlyContain(result => SteamLoginService.IsTerminalLogonResult(result));
    }

    [TestCase(EResult.OK)]
    [TestCase(EResult.Busy)]
    [TestCase(EResult.ServiceUnavailable)]
    [TestCase(EResult.TryAnotherCM)]
    [TestCase(EResult.RateLimitExceeded)]
    [TestCase(EResult.Timeout)]
    [TestCase(EResult.Fail)]
    public void ReconnectLogonResult_NonTerminalSetIsFixed(EResult result)
    {
        SteamLoginService.IsTerminalLogonResult(result).Should().BeFalse();
    }

    [Test]
    public async Task InitialLogin_PublishesReadyBeforeSuccess()
    {
        var observed = new List<string>();
        var eventBus = new RecordingEventBus(observed);

        await SteamSessionEventSequence.PublishLoginSucceededAsync(eventBus, "alice", ProgressRecorder(observed));

        observed.Should().Equal("SteamSessionReady:alice", "SteamLoginProgressChanged:success:alice");
    }

    [Test]
    public async Task Reconnect_PublishesProgressThenReconnectedThenReady()
    {
        var observed = new List<string>();
        var eventBus = new RecordingEventBus(observed);

        await SteamSessionEventSequence.PublishReconnectedAsync(
            eventBus, "alice", ProgressRecorder(observed), CancellationToken.None);

        observed.Should().Equal(
            "SteamLoginProgressChanged:userReconnected:alice",
            "SteamSessionReconnected:alice",
            "SteamSessionReady:alice");
    }

    private static Func<string, SteamLoginProgressData?, Task> ProgressRecorder(List<string> observed) =>
        (type, data) =>
        {
            observed.Add($"SteamLoginProgressChanged:{type}:{data?.AccountName}");
            return Task.CompletedTask;
        };

    private sealed class RecordingEventBus(List<string> observed) : IEventBus
    {
        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
            where TEvent : notnull
        {
            cancellationToken.ThrowIfCancellationRequested();
            observed.Add(message switch
            {
                SteamSessionReady ready => $"SteamSessionReady:{ready.AccountName}",
                SteamSessionEnded ended => $"SteamSessionEnded:{ended.AccountName}",
                SteamSessionDisconnected disconnected => $"SteamSessionDisconnected:{disconnected.AccountName}",
                SteamSessionReconnected reconnected => $"SteamSessionReconnected:{reconnected.AccountName}",
                _ => message.GetType().Name
            });
            return Task.CompletedTask;
        }
    }
}
