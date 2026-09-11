using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Session.Internal;

namespace SteamStat.Core.Steam.Session;

public sealed class SteamSessionManager : ISteamSessionManager
{
    private readonly IEventBus _eventBus;
    private readonly SteamCredentialStore _credentialStore;
    private readonly ISteamConnectionFactory _connectionFactory;
    private readonly INetworkAvailability _network;
    private readonly SteamReconnectPolicy _reconnectPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SteamSessionManager> _logger;
    private readonly ConcurrentDictionary<string, AccountRuntime> _accounts = new();
    private readonly ConcurrentDictionary<int, Task> _backgroundWork = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _attemptLock = new();
    private readonly object _shutdownLock = new();
    private readonly Random _jitter = new();
    private ISteamConnection? _pendingConnection;
    private CancellationTokenSource? _pendingCancellation;
    private Task? _shutdownTask;
    private long _nextGeneration;
    private int _nextWorkId;

    internal SteamSessionManager(
        IEventBus eventBus,
        SteamCredentialStore credentialStore,
        ISteamConnectionFactory connectionFactory,
        INetworkAvailability network,
        SteamReconnectPolicy reconnectPolicy,
        TimeProvider timeProvider,
        ILogger<SteamSessionManager> logger)
    {
        _eventBus = eventBus;
        _credentialStore = credentialStore;
        _connectionFactory = connectionFactory;
        _network = network;
        _reconnectPolicy = reconnectPolicy;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<SteamAuthenticationResult> AuthenticateWithCredentialsAsync(
        string username,
        string password,
        bool persistent,
        string? guardData,
        ISteamGuardInteraction guardInteraction,
        CancellationToken cancellationToken = default)
        => AuthenticateAsync((connection, token) => connection.AuthenticateWithCredentialsAsync(
            username, password, persistent, guardData, guardInteraction, token), cancellationToken);

    public Task<SteamAuthenticationResult> AuthenticateWithQrAsync(
        bool persistent,
        Action<string> challengeChanged,
        CancellationToken cancellationToken = default)
        => AuthenticateAsync((connection, token) => connection.AuthenticateWithQrAsync(
            persistent, challengeChanged, token), cancellationToken);

    private async Task<SteamAuthenticationResult> AuthenticateAsync(
        Func<ISteamConnection, CancellationToken, Task<SteamAuthenticationResult>> authenticate,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
        await CancelLoginAsync().ConfigureAwait(false);
        var connection = _connectionFactory.Create(Interlocked.Increment(ref _nextGeneration));
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        lock (_attemptLock)
        {
            _pendingConnection = connection;
            _pendingCancellation = operationCancellation;
        }
        try
        {
            await connection.ConnectAsync(operationCancellation.Token).ConfigureAwait(false);
            var result = await authenticate(connection, operationCancellation.Token).ConfigureAwait(false);
            lock (_attemptLock)
            {
                if (ReferenceEquals(_pendingConnection, connection)) _pendingCancellation = null;
            }
            operationCancellation.Dispose();
            return result;
        }
        catch
        {
            lock (_attemptLock)
            {
                if (ReferenceEquals(_pendingConnection, connection))
                {
                    _pendingConnection = null;
                    _pendingCancellation = null;
                }
            }
            operationCancellation.Dispose();
            await connection.StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SteamSessionStartResult> StartSessionAsync(
        string accountName,
        string refreshToken,
        string? guardData,
        bool rememberPassword,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var runtime = _accounts.GetOrAdd(accountName, static name => new AccountRuntime(name));
        await runtime.CommandGate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            var hadCurrentSession = GetCurrent(runtime) != null;
            await TransitionAsync(runtime, SteamSessionState.Connecting).ConfigureAwait(false);
            ISteamConnection? connection;
            lock (_attemptLock)
            {
                connection = _pendingConnection;
                _pendingConnection = null;
                _pendingCancellation = null;
            }
            connection ??= _connectionFactory.Create(Interlocked.Increment(ref _nextGeneration));
            lock (runtime.Sync) runtime.Generation = connection.Generation;
            try
            {
                if (!connection.IsConnected)
                    await connection.ConnectAsync(operation.Token).ConfigureAwait(false);
                await TransitionAsync(runtime, SteamSessionState.Authenticating).ConfigureAwait(false);
                var result = await connection.LogOnAsync(
                    accountName, refreshToken, rememberPassword, operation.Token).ConfigureAwait(false);
                if (result != EResult.OK)
                {
                    await connection.StopAsync().ConfigureAwait(false);
                    await TransitionAsync(runtime, hadCurrentSession
                        ? SteamSessionState.Ready
                        : _reconnectPolicy.IsTerminal(result)
                            ? SteamSessionState.ReauthenticationRequired
                            : SteamSessionState.Failed, result.ToString()).ConfigureAwait(false);
                    return new SteamSessionStartResult(false, result.ToString());
                }
                _credentialStore.RememberForSession(accountName, refreshToken, guardData);
                await InstallAsync(runtime, connection, false).ConfigureAwait(false);
                await _eventBus.PublishAsync(new SteamSessionReady(accountName), operation.Token).ConfigureAwait(false);
                return new SteamSessionStartResult(true);
            }
            catch
            {
                await connection.StopAsync().ConfigureAwait(false);
                if (runtime.State.State is SteamSessionState.Connecting or SteamSessionState.Authenticating)
                    await TransitionAsync(runtime, hadCurrentSession
                        ? SteamSessionState.Ready
                        : SteamSessionState.Failed, "session_start_failed").ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            runtime.CommandGate.Release();
        }
    }

    public async Task CancelLoginAsync()
    {
        ISteamConnection? connection;
        CancellationTokenSource? cancellation;
        lock (_attemptLock)
        {
            connection = _pendingConnection;
            cancellation = _pendingCancellation;
            _pendingConnection = null;
            _pendingCancellation = null;
        }
        if (cancellation != null)
        {
            try { await cancellation.CancelAsync().ConfigureAwait(false); } catch { }
        }
        if (connection != null) await connection.StopAsync().ConfigureAwait(false);
    }

    public IReadOnlyList<string> GetLoggedInUsers()
        => _accounts.Where(pair => GetCurrent(pair.Value) != null).Select(pair => pair.Key).ToArray();

    public bool TryGetSession(string accountName, out ISteamSession session)
    {
        if (_accounts.TryGetValue(accountName, out var runtime) && GetCurrent(runtime) is { } current)
        {
            session = current;
            return true;
        }
        session = null!;
        return false;
    }

    public async Task<bool> LogoutUserAsync(string accountName)
    {
        if (!_accounts.TryRemove(accountName, out var runtime)) return false;
        ISteamConnection? connection;
        Task? reconnectTask;
        lock (runtime.Sync)
        {
            runtime.UserLogout = true;
            runtime.Epoch++;
            runtime.Lifetime.Cancel();
            connection = runtime.Current;
            runtime.Current = null;
            reconnectTask = runtime.ReconnectTask;
        }
        await runtime.CommandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await TransitionAsync(runtime, SteamSessionState.Stopping).ConfigureAwait(false);
            if (connection != null)
            {
                try { connection.Client.GetHandler<SteamKit2.SteamUser>()?.LogOff(); } catch { }
                await connection.StopAsync().ConfigureAwait(false);
            }
            if (reconnectTask != null) await IgnoreCancellationAsync(reconnectTask).ConfigureAwait(false);
            await TransitionAsync(runtime, SteamSessionState.Disconnected).ConfigureAwait(false);
            runtime.Lifetime.Dispose();
            _credentialStore.ForgetSession(accountName);
            if (connection != null) await _eventBus.PublishAsync(new SteamSessionEnded(accountName)).ConfigureAwait(false);
            _logger.LogInformation("Steam user {AccountName} logged out", accountName);
            return connection != null;
        }
        finally
        {
            runtime.CommandGate.Release();
        }
    }

    public async Task LogoutAllUsersAsync()
    {
        foreach (var accountName in _accounts.Keys.ToArray()) await LogoutUserAsync(accountName).ConfigureAwait(false);
        _logger.LogInformation("All Steam users logged out");
    }

    public bool SetUserPersonaState(string accountName, int personaState)
    {
        if (!_accounts.TryGetValue(accountName, out var runtime) || GetCurrent(runtime) is not { } connection)
        {
            _logger.LogWarning("Steam user {AccountName} has no logged-in session", accountName);
            return false;
        }
        try
        {
            return connection.SetPersonaState((EPersonaState)personaState);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "SetUserPersonaState failed for {AccountName}", accountName);
            return false;
        }
    }

    private async Task InstallAsync(AccountRuntime runtime, ISteamConnection connection, bool reconnected)
    {
        ISteamConnection? previous;
        lock (runtime.Sync)
        {
            previous = runtime.Current;
            runtime.Epoch++;
            runtime.Lifetime.Cancel();
            runtime.Lifetime.Dispose();
            runtime.Lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            runtime.Current = connection;
            runtime.Generation = connection.Generation;
            runtime.UserLogout = false;
            runtime.AttemptsConsumed = 0;
            connection.Disconnected += OnDisconnected;
            connection.PumpFaulted += OnPumpFaulted;
        }
        if (previous != null && !ReferenceEquals(previous, connection)) await previous.StopAsync().ConfigureAwait(false);
        await TransitionAsync(runtime, SteamSessionState.Ready).ConfigureAwait(false);
        if (!reconnected) return;
        await _eventBus.PublishAsync(new SteamLoginProgressChanged(
            "userReconnected", new SteamLoginProgressData(AccountName: runtime.AccountName))).ConfigureAwait(false);
        await _eventBus.PublishAsync(new SteamSessionReconnected(runtime.AccountName)).ConfigureAwait(false);
        await _eventBus.PublishAsync(new SteamSessionReady(runtime.AccountName)).ConfigureAwait(false);
    }

    private void OnDisconnected(ISteamConnection connection)
        => TrackBackground(HandleUnexpectedEndAsync(connection, null));

    private void OnPumpFaulted(ISteamConnection connection, Exception exception)
        => TrackBackground(HandleUnexpectedEndAsync(connection, exception));

    private async Task HandleUnexpectedEndAsync(ISteamConnection connection, Exception? fault)
    {
        var pair = _accounts.FirstOrDefault(candidate => ReferenceEquals(GetCurrent(candidate.Value), connection));
        if (pair.Value == null) return;
        var runtime = pair.Value;
        lock (runtime.Sync)
        {
            if (!ReferenceEquals(runtime.Current, connection) || runtime.UserLogout) return;
            runtime.Current = null;
        }
        connection.Disconnected -= OnDisconnected;
        connection.PumpFaulted -= OnPumpFaulted;
        await connection.StopAsync().ConfigureAwait(false);
        if (fault != null) _logger.LogWarning(fault, "Steam session callback pump ended for {AccountName}", runtime.AccountName);
        await TransitionAsync(runtime, SteamSessionState.Disconnected, fault == null ? null : "callback_pump_failed").ConfigureAwait(false);
        await _eventBus.PublishAsync(new SteamSessionDisconnected(runtime.AccountName)).ConfigureAwait(false);
        await _eventBus.PublishAsync(new SteamSessionEnded(runtime.AccountName)).ConfigureAwait(false);
        await _eventBus.PublishAsync(new SteamLoginProgressChanged(
            "userDisconnected", new SteamLoginProgressData(AccountName: runtime.AccountName))).ConfigureAwait(false);
        await TransitionAsync(runtime, SteamSessionState.ReconnectWaiting).ConfigureAwait(false);
        StartReconnect(runtime);
    }

    private void StartReconnect(AccountRuntime runtime)
    {
        lock (runtime.Sync)
        {
            if (runtime.UserLogout || runtime.ReconnectTask is { IsCompleted: false }) return;
            var epoch = runtime.Epoch;
            runtime.ReconnectTask = ReconnectLoopAsync(runtime, epoch, runtime.Lifetime.Token);
            TrackBackground(runtime.ReconnectTask);
        }
    }

    private async Task ReconnectLoopAsync(AccountRuntime runtime, long epoch, CancellationToken cancellationToken)
    {
        EResult? previousResult = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_network.IsAvailable)
                {
                    _logger.LogInformation("Network unavailable; pausing reconnect for {AccountName}", runtime.AccountName);
                    await _network.WaitUntilAvailableAsync(cancellationToken).ConfigureAwait(false);
                }
                int nextAttempt;
                lock (runtime.Sync)
                {
                    if (runtime.UserLogout || runtime.Epoch != epoch) return;
                    if (!_reconnectPolicy.CanRetry(runtime.AttemptsConsumed))
                    {
                        nextAttempt = -1;
                    }
                    else
                    {
                        nextAttempt = runtime.AttemptsConsumed + 1;
                    }
                }
                if (nextAttempt < 0)
                {
                    await TransitionAsync(runtime, SteamSessionState.Failed, "reconnectAttemptsExhausted").ConfigureAwait(false);
                    await PublishReconnectFailedAsync(runtime.AccountName, "reconnectAttemptsExhausted").ConfigureAwait(false);
                    return;
                }
                double jitter;
                lock (_jitter) jitter = 0.8 + _jitter.NextDouble() * 0.4;
                var delay = _reconnectPolicy.GetDelay(nextAttempt, previousResult, jitter);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                if (!_network.IsAvailable) continue;
                await runtime.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    lock (runtime.Sync)
                    {
                        if (runtime.UserLogout || runtime.Epoch != epoch || runtime.Current != null) return;
                    }
                    var credential = _credentialStore.FindByAccountName(runtime.AccountName);
                    if (credential == null)
                    {
                        await TransitionAsync(runtime, SteamSessionState.ReauthenticationRequired, "tokenUnavailable").ConfigureAwait(false);
                        await PublishReconnectFailedAsync(runtime.AccountName, "tokenUnavailable").ConfigureAwait(false);
                        return;
                    }
                    ISteamConnection connection;
                    lock (runtime.Sync)
                    {
                        runtime.AttemptsConsumed++;
                        connection = _connectionFactory.Create(Interlocked.Increment(ref _nextGeneration));
                        runtime.Generation = connection.Generation;
                    }
                    await TransitionAsync(runtime, SteamSessionState.Reconnecting).ConfigureAwait(false);
                    try
                    {
                        await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        var result = await connection.LogOnAsync(
                            credential.AccountName, credential.RefreshToken, true, cancellationToken).ConfigureAwait(false);
                        lock (runtime.Sync)
                        {
                            if (runtime.UserLogout || runtime.Epoch != epoch) result = EResult.Cancelled;
                        }
                        if (result == EResult.Cancelled)
                        {
                            await connection.StopAsync().ConfigureAwait(false);
                            return;
                        }
                        if (result == EResult.OK)
                        {
                            await InstallAsync(runtime, connection, true).ConfigureAwait(false);
                            return;
                        }
                        await connection.StopAsync().ConfigureAwait(false);
                        if (_reconnectPolicy.IsTerminal(result))
                        {
                            await TransitionAsync(runtime, SteamSessionState.ReauthenticationRequired, result.ToString()).ConfigureAwait(false);
                            await PublishReconnectFailedAsync(runtime.AccountName, result.ToString()).ConfigureAwait(false);
                            return;
                        }
                        previousResult = result;
                        await TransitionAsync(runtime, SteamSessionState.ReconnectWaiting, result.ToString()).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        await connection.StopAsync().ConfigureAwait(false);
                        return;
                    }
                    catch (Exception exception)
                    {
                        await connection.StopAsync().ConfigureAwait(false);
                        _logger.LogWarning(exception, "Reconnect failed for {AccountName}", runtime.AccountName);
                        previousResult = EResult.ServiceUnavailable;
                        await TransitionAsync(runtime, SteamSessionState.ReconnectWaiting, "reconnectFailed").ConfigureAwait(false);
                    }
                }
                finally
                {
                    runtime.CommandGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task TransitionAsync(AccountRuntime runtime, SteamSessionState state, string? errorCode = null)
    {
        long generation;
        lock (runtime.Sync)
        {
            if (runtime.State.State == state) return;
            runtime.State.TransitionTo(state);
            generation = runtime.Generation;
        }
        await _eventBus.PublishAsync(new SteamSessionStateChanged(
            runtime.AccountName, state, generation, errorCode)).ConfigureAwait(false);
    }

    private Task PublishReconnectFailedAsync(string accountName, string errorCode)
        => _eventBus.PublishAsync(new SteamLoginProgressChanged(
            "reconnectFailed", new SteamLoginProgressData(AccountName: accountName, ErrorCode: errorCode)));

    private static ISteamConnection? GetCurrent(AccountRuntime runtime)
    {
        lock (runtime.Sync) return runtime.Current;
    }

    private void TrackBackground(Task task)
    {
        var id = Interlocked.Increment(ref _nextWorkId);
        _backgroundWork[id] = task;
        _ = task.ContinueWith(completed =>
        {
            _backgroundWork.TryRemove(id, out _);
            if (completed.IsFaulted) _logger.LogError(completed.Exception, "Tracked Steam session work failed");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownLock) return _shutdownTask ??= ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await CancelLoginAsync().ConfigureAwait(false);
        await LogoutAllUsersAsync().ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!_backgroundWork.IsEmpty)
        {
            var work = _backgroundWork.Values.ToArray();
            if (work.Length == 0) break;
            try { await Task.WhenAll(work).WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
            catch (Exception exception) { _logger.LogError(exception, "Failed while draining Steam session work"); }
        }
        _stopping.Dispose();
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private sealed class AccountRuntime(string accountName)
    {
        public string AccountName { get; } = accountName;
        public object Sync { get; } = new();
        public SemaphoreSlim CommandGate { get; } = new(1, 1);
        public SteamSessionStateMachine State { get; } = new();
        public CancellationTokenSource Lifetime { get; set; } = new();
        public ISteamConnection? Current;
        public Task? ReconnectTask;
        public long Epoch;
        public long Generation;
        public int AttemptsConsumed;
        public bool UserLogout;
    }
}
