using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamKit2;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Platform;
using SteamStat.Core.Steam.Session;
using SteamStat.Core.Steam.Session.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamSessionManagerTests
{
    [Test]
    public void StateMachine_AcceptsLegalTransitionsAndRejectsIllegalTransitions()
    {
        var machine = new SteamSessionStateMachine();

        machine.TransitionTo(SteamSessionState.Connecting);
        machine.TransitionTo(SteamSessionState.Authenticating);
        machine.TransitionTo(SteamSessionState.Ready);
        machine.TransitionTo(SteamSessionState.Disconnected);
        machine.TransitionTo(SteamSessionState.ReconnectWaiting);
        machine.TransitionTo(SteamSessionState.Reconnecting);
        machine.TransitionTo(SteamSessionState.ReauthenticationRequired);

        var illegal = () => machine.TransitionTo(SteamSessionState.Ready);
        illegal.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ReconnectPolicy_UsesBoundedExponentialJitterAndTerminalClassification()
    {
        var policy = new SteamReconnectPolicy(new SteamResultClassifier(), maxAttempts: 3);

        policy.GetDelay(1, null, 0.8).Should().Be(TimeSpan.FromSeconds(4));
        policy.GetDelay(2, null, 1.2).Should().Be(TimeSpan.FromSeconds(12));
        policy.GetDelay(10, null, 1).Should().Be(TimeSpan.FromMinutes(1));
        policy.GetDelay(1, EResult.RateLimitExceeded, 1).Should().Be(TimeSpan.FromMinutes(2));
        policy.CanRetry(2).Should().BeTrue();
        policy.CanRetry(3).Should().BeFalse();
        policy.IsTerminal(EResult.Revoked).Should().BeTrue();
        policy.IsTerminal(EResult.Busy).Should().BeFalse();
    }

    [Test]
    public async Task TerminalToken_DoesNotPublishReadyOrScheduleReconnect()
    {
        var fixture = new ManagerFixture(EResult.Revoked);
        await using var manager = fixture.CreateManager();

        var result = await manager.StartSessionAsync("alice", "token", null, true);

        result.Should().Be(new SteamSessionStartResult(false, nameof(EResult.Revoked)));
        fixture.Factory.Created.Should().HaveCount(1);
        fixture.Events.OfType<SteamSessionReady>().Should().BeEmpty();
        fixture.Events.OfType<SteamSessionStateChanged>().Last().State
            .Should().Be(SteamSessionState.ReauthenticationRequired);
    }

    [Test]
    public async Task NetworkPause_DoesNotConsumeAttemptAndRecoveryStartsOneReconnect()
    {
        var fixture = new ManagerFixture(EResult.OK, EResult.OK);
        await using var manager = fixture.CreateManager();
        (await manager.StartSessionAsync("alice", "token", null, true)).Success.Should().BeTrue();
        fixture.Network.SetAvailable(false);

        fixture.Factory.Created[0].RaiseDisconnected();
        await WaitUntilAsync(() => fixture.Events.OfType<SteamSessionEnded>().Any());
        fixture.Events.Where(message => message is SteamSessionDisconnected or SteamSessionEnded)
            .Take(2).Select(message => message.GetType().Name)
            .Should().Equal(nameof(SteamSessionDisconnected), nameof(SteamSessionEnded));
        await Task.Delay(30);
        fixture.Factory.Created.Should().HaveCount(1);

        fixture.Network.SetAvailable(true);
        fixture.Network.SetAvailable(true);
        await WaitUntilAsync(() => fixture.Events.OfType<SteamSessionReconnected>().Any());

        fixture.Factory.Created.Should().HaveCount(2);
        fixture.Factory.Created[1].LogOnCalls.Should().Be(1);
        fixture.Events.Where(message => message is SteamSessionReconnected or SteamSessionReady)
            .TakeLast(2).Select(message => message.GetType().Name)
            .Should().Equal(nameof(SteamSessionReconnected), nameof(SteamSessionReady));
    }

    [Test]
    public async Task MultipleAccounts_AreIndependentAndOldGenerationCannotEndReplacement()
    {
        var fixture = new ManagerFixture(EResult.OK, EResult.OK, EResult.OK);
        await using var manager = fixture.CreateManager();
        await manager.StartSessionAsync("alice", "alice-token", null, true);
        var oldAlice = fixture.Factory.Created[0];
        await manager.StartSessionAsync("bob", "bob-token", null, true);
        await manager.StartSessionAsync("alice", "new-token", null, true);
        var eventsBeforeLateDisconnect = fixture.Events.Count;

        oldAlice.RaiseDisconnected();
        await Task.Delay(30);

        manager.GetLoggedInUsers().Should().BeEquivalentTo("alice", "bob");
        manager.TryGetSession("alice", out var current).Should().BeTrue();
        current.Should().BeSameAs(fixture.Factory.Created[2]);
        fixture.Events.Skip(eventsBeforeLateDisconnect).OfType<SteamSessionEnded>().Should().BeEmpty();
    }

    [Test]
    public async Task Shutdown_IsIdempotentAndStopsAllAccounts()
    {
        var fixture = new ManagerFixture(EResult.OK, EResult.OK);
        var manager = fixture.CreateManager();
        await manager.StartSessionAsync("alice", "alice-token", null, true);
        await manager.StartSessionAsync("bob", "bob-token", null, true);

        var first = manager.ShutdownAsync();
        var second = manager.ShutdownAsync();
        first.Should().BeSameAs(second);
        await first;

        fixture.Factory.Created.Should().OnlyContain(connection => connection.StopCalls == 1);
        manager.GetLoggedInUsers().Should().BeEmpty();
        await manager.DisposeAsync();
    }

    [Test]
    public async Task ActiveLogout_EndsOnceAndNeverReconnects()
    {
        var fixture = new ManagerFixture(EResult.OK, EResult.OK);
        await using var manager = fixture.CreateManager();
        await manager.StartSessionAsync("alice", "token", null, true);

        (await manager.LogoutUserAsync("alice")).Should().BeTrue();
        fixture.Factory.Created[0].RaiseDisconnected();
        await Task.Delay(30);

        fixture.Factory.Created.Should().HaveCount(1);
        fixture.Events.OfType<SteamSessionEnded>().Should().ContainSingle();
    }

    [Test]
    public async Task ConnectionStop_ReturnsOneTaskAndPumpCancellationIsNotFault()
    {
        var connection = new SteamConnection(
            SteamConfiguration.Create(_ => { }), 1, NullLogger<SteamConnection>.Instance);
        var faults = 0;
        connection.PumpFaulted += (_, _) => faults++;

        var first = connection.StopAsync();
        var second = connection.StopAsync();

        first.Should().BeSameAs(second);
        await first;
        faults.Should().Be(0);
    }

    [Test]
    public async Task CredentialStore_ProtectsPersistenceAndDecryptsOnlyWhenLoaded()
    {
        var tokens = new FakeTokenStore();
        var secrets = new PrefixSecretStore();
        var store = new SteamCredentialStore(tokens, secrets, TimeProvider.System, NullLogger<SteamCredentialStore>.Instance);
        var authentication = new SteamAuthenticationResult("alice", "access", "refresh", "guard");

        await store.SaveAsync(authentication);
        store.RememberForSession("alice", "refresh", "guard");

        tokens.Written.Should().NotBeNull();
        tokens.Written!.AccessToken.Should().Be("protected:access");
        tokens.Written.RefreshToken.Should().Be("protected:refresh");
        tokens.Written.GuardData.Should().Be("protected:guard");
        store.FindByAccountName("alice").Should().Be(new SteamCredentialMaterial("alice", "refresh", "guard"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout) await Task.Delay(10);
        condition().Should().BeTrue();
    }

    private sealed class ManagerFixture(params EResult[] results)
    {
        private readonly FakeTokenStore _tokens = new();
        public RecordingEventBus EventBus { get; } = new();
        public FakeConnectionFactory Factory { get; } = new(results);
        public FakeNetworkAvailability Network { get; } = new();
        public IReadOnlyList<object> Events => EventBus.Events;

        public SteamSessionManager CreateManager()
        {
            var credentials = new SteamCredentialStore(
                _tokens, new PrefixSecretStore(), TimeProvider.System, NullLogger<SteamCredentialStore>.Instance);
            return new SteamSessionManager(
                EventBus,
                credentials,
                Factory,
                Network,
                new SteamReconnectPolicy(new SteamResultClassifier(), 3, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
                TimeProvider.System,
                NullLogger<SteamSessionManager>.Instance);
        }
    }

    private sealed class FakeConnectionFactory(IEnumerable<EResult> results) : ISteamConnectionFactory
    {
        private readonly Queue<EResult> _results = new(results);
        public List<FakeConnection> Created { get; } = [];

        public ISteamConnection Create(long generation)
        {
            var connection = new FakeConnection(generation, _results.Count == 0 ? EResult.OK : _results.Dequeue());
            Created.Add(connection);
            return connection;
        }
    }

    private sealed class FakeConnection(long generation, EResult result) : ISteamConnection
    {
        public long Generation { get; } = generation;
        public bool IsConnected { get; private set; }
        public SteamClient Client { get; } = new();
        public CallbackManager Callbacks { get; private set; } = null!;
        public int LogOnCalls { get; private set; }
        public int StopCalls { get; private set; }
        public event Action<ISteamConnection>? Disconnected;
        public event Action<ISteamConnection, Exception>? PumpFaulted;

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = true;
            Callbacks ??= new CallbackManager(Client);
            return Task.CompletedTask;
        }

        public Task<SteamAuthenticationResult> AuthenticateWithCredentialsAsync(
            string username, string password, bool persistent, string? guardData,
            ISteamGuardInteraction guardInteraction, CancellationToken cancellationToken)
            => Task.FromResult(new SteamAuthenticationResult(username, "access", "refresh", guardData));

        public Task<SteamAuthenticationResult> AuthenticateWithQrAsync(
            bool persistent, Action<string> challengeChanged, CancellationToken cancellationToken)
            => Task.FromResult(new SteamAuthenticationResult("qr", "access", "refresh", null));

        public Task<EResult> LogOnAsync(
            string accountName, string refreshToken, bool rememberPassword, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogOnCalls++;
            return Task.FromResult(result);
        }

        public bool SetPersonaState(EPersonaState state) => true;
        public void RaiseDisconnected() => Disconnected?.Invoke(this);
        public void RaisePumpFault(Exception exception) => PumpFaulted?.Invoke(this, exception);

        public Task StopAsync()
        {
            StopCalls++;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new(StopAsync());
    }

    private sealed class FakeNetworkAvailability : INetworkAvailability
    {
        private readonly object _sync = new();
        private TaskCompletionSource _available = Completed();
        public bool IsAvailable { get; private set; } = true;

        public Task WaitUntilAvailableAsync(CancellationToken cancellationToken)
        {
            lock (_sync) return _available.Task.WaitAsync(cancellationToken);
        }

        public void SetAvailable(bool available)
        {
            lock (_sync)
            {
                IsAvailable = available;
                if (available) _available.TrySetResult();
                else if (_available.Task.IsCompleted) _available = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private static TaskCompletionSource Completed()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetResult();
            return completion;
        }
    }

    private sealed class RecordingEventBus : IEventBus
    {
        private readonly ConcurrentQueue<object> _events = new();
        public IReadOnlyList<object> Events => _events.ToArray();
        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : notnull
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class PrefixSecretStore : ISecretStore
    {
        public string? Protect(string? plainText) => plainText == null ? null : $"protected:{plainText}";
        public string? Unprotect(string? protectedText)
            => protectedText?.StartsWith("protected:", StringComparison.Ordinal) == true ? protectedText[10..] : protectedText;
        public bool IsProtected(string? value) => value?.StartsWith("protected:", StringComparison.Ordinal) == true;
    }

    private sealed class FakeTokenStore : ISteamLoginTokenStore
    {
        public SteamLoginTokenWrite? Written { get; private set; }
        public SteamLoginTokenData? FindByAccountName(string accountName)
            => Written?.AccountName == accountName ? ToData(Written) : null;
        public Task<SteamLoginTokenData?> FindByIdAsync(int id, CancellationToken cancellationToken = default)
            => Task.FromResult(Written == null ? null : ToData(Written));
        public IReadOnlyList<SteamLoginTokenData> List() => Written == null ? [] : [ToData(Written)];
        public Task UpsertAsync(SteamLoginTokenWrite token, CancellationToken cancellationToken = default)
        {
            Written = token;
            return Task.CompletedTask;
        }
        public Task<int> EncryptLegacyAsync(ISecretStore secretStore, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
        public Task<SteamLoginTokenData?> DeleteAsync(int id, CancellationToken cancellationToken = default)
            => Task.FromResult<SteamLoginTokenData?>(null);
        private static SteamLoginTokenData ToData(SteamLoginTokenWrite token)
            => new(1, token.AccountName, token.AccessToken, token.RefreshToken, token.GuardData, token.CreatedAt);
    }
}
