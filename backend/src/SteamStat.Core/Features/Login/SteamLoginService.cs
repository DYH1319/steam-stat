using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Platform;
using SteamStat.Core.Sessions;
using SteamKitUser = SteamKit2.SteamUser;

namespace SteamStat.Core.Features.Login;

public sealed class SteamLoginService(
    IEventBus eventBus,
    ISteamLoginTokenStore tokenStore,
    ISecretStore secretStore,
    TimeProvider timeProvider,
    ILogger<SteamLoginService> logger) : ISteamSessionAccessor, IAsyncDisposable
{
    private const int MaxReconnectAttempts = 10;
    private SteamClient? _steamClient;
    private CallbackManager? _manager;
    private CancellationTokenSource? _cts;
    private IpcAuthenticator? _authenticator;
    private TaskCompletionSource<bool>? _connectedTcs;
    private Task? _callbackLoopTask;
    private List<IDisposable>? _attemptSubscriptions;
    private int _isLoginInProgress;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<int, Task> _backgroundWork = new();
    private readonly ConcurrentDictionary<string, SteamSession> _loggedInSessions = new();
    private readonly ConcurrentDictionary<string, ReconnectState> _reconnectStates = new();
    private readonly Random _jitter = new();
    private int _nextWorkId;
    private int _disposed;

    private sealed class ReconnectState
    {
        public string AccountName = string.Empty;
        public string RefreshToken = string.Empty;
        public string? GuardData;
        public int RetryCount;
        public bool IsUserInitiatedLogout;
        public bool IsReconnecting;
        public bool IsTerminated;
        public ITimer? Timer;
    }

    private static bool IsTerminalLogonResult(EResult result) => result is
        EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.Revoked
        or EResult.InvalidSignature or EResult.AccountDisabled or EResult.AccountLockedDown
        or EResult.AccountLogonDenied or EResult.AccountLoginDeniedNeedTwoFactor
        or EResult.Banned or EResult.AccountNotFound;

    public async Task<object> LoginWithCredentials(string username, string password, bool rememberMe)
    {
        if (Interlocked.CompareExchange(ref _isLoginInProgress, 1, 0) != 0)
            return new { success = false, error = "Login already in progress", errorCode = "alreadyInProgress" };
        try
        {
            await SendEventAsync(eventBus, "connecting");
            await ConnectToSteam();
            await SendEventAsync(eventBus, "authenticating");
            _authenticator = new IpcAuthenticator(this, eventBus);
            var guardData = secretStore.Unprotect(tokenStore.FindByAccountName(username)?.GuardData);
            var authSession = await _steamClient!.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = rememberMe,
                    GuardData = guardData,
                    Authenticator = _authenticator,
                    PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient
                });
            var pollResponse = await authSession.PollingWaitForResultAsync(_cts!.Token);
            if (rememberMe) await SaveTokens(pollResponse);
            StoreReconnectCredentials(pollResponse.AccountName, pollResponse.RefreshToken, pollResponse.NewGuardData ?? guardData);
            _steamClient.GetHandler<SteamKitUser>()?.LogOn(new SteamKitUser.LogOnDetails
            {
                Username = pollResponse.AccountName,
                AccessToken = pollResponse.RefreshToken,
                ShouldRememberPassword = rememberMe
            });
            var session = TakeCurrentSession();
            await InstallSessionAsync(pollResponse.AccountName, session, _stopping.Token).ConfigureAwait(false);
            await eventBus.PublishAsync(new SteamSessionReady(pollResponse.AccountName));
            await SendEventAsync(eventBus, "success", new { accountName = pollResponse.AccountName });
            return new { success = true, accountName = pollResponse.AccountName };
        }
        catch (OperationCanceledException)
        {
            await SendEventAsync(eventBus, "cancelled");
            Disconnect();
            return new { success = false, error = "Login cancelled", errorCode = "cancelled" };
        }
        catch (AuthenticationException exception)
        {
            logger.LogWarning(exception, "Steam credential authentication failed");
            var errorCode = exception.Result.ToString();
            await SendEventAsync(eventBus, "error", new { message = exception.Message, errorCode });
            Disconnect();
            return new { success = false, error = exception.Message, errorCode };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Steam credential login failed");
            var errorCode = GetErrorCodeFromException(exception);
            await SendEventAsync(eventBus, "error", new { message = exception.Message, errorCode });
            Disconnect();
            return new { success = false, error = exception.Message, errorCode };
        }
        finally
        {
            DisposeAuthenticator();
            Interlocked.Exchange(ref _isLoginInProgress, 0);
        }
    }

    public async Task<object> LoginWithQR(bool rememberMe)
    {
        if (Interlocked.CompareExchange(ref _isLoginInProgress, 1, 0) != 0)
            return new { success = false, error = "Login already in progress", errorCode = "alreadyInProgress" };
        try
        {
            await SendEventAsync(eventBus, "connecting");
            await ConnectToSteam();
            await SendEventAsync(eventBus, "authenticating");
            var authSession = await _steamClient!.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());
            authSession.ChallengeURLChanged = () => TrackBackground(SendEventAsync(
                eventBus, "qrCode", new { qrImageBase64 = GenerateQrCodeBase64(authSession.ChallengeURL) }));
            await SendEventAsync(eventBus, "qrCode", new { qrImageBase64 = GenerateQrCodeBase64(authSession.ChallengeURL) });
            var pollResponse = await authSession.PollingWaitForResultAsync(_cts!.Token);
            if (rememberMe) await SaveTokens(pollResponse);
            StoreReconnectCredentials(pollResponse.AccountName, pollResponse.RefreshToken, pollResponse.NewGuardData);
            _steamClient.GetHandler<SteamKitUser>()?.LogOn(new SteamKitUser.LogOnDetails
            {
                Username = pollResponse.AccountName,
                AccessToken = pollResponse.RefreshToken,
                ShouldRememberPassword = rememberMe
            });
            var session = TakeCurrentSession();
            await InstallSessionAsync(pollResponse.AccountName, session, _stopping.Token).ConfigureAwait(false);
            await eventBus.PublishAsync(new SteamSessionReady(pollResponse.AccountName));
            await SendEventAsync(eventBus, "success", new { accountName = pollResponse.AccountName });
            return new { success = true, accountName = pollResponse.AccountName };
        }
        catch (OperationCanceledException)
        {
            await SendEventAsync(eventBus, "cancelled");
            Disconnect();
            return new { success = false, error = "Login cancelled", errorCode = "cancelled" };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Steam QR login failed");
            var errorCode = GetErrorCodeFromException(exception);
            await SendEventAsync(eventBus, "error", new { message = exception.Message, errorCode });
            Disconnect();
            return new { success = false, error = exception.Message, errorCode };
        }
        finally
        {
            Interlocked.Exchange(ref _isLoginInProgress, 0);
        }
    }

    public async Task<object> LoginWithToken(int tokenId)
    {
        if (Interlocked.CompareExchange(ref _isLoginInProgress, 1, 0) != 0)
            return new { success = false, error = "Login already in progress", errorCode = "alreadyInProgress" };
        try
        {
            var savedToken = await tokenStore.FindByIdAsync(tokenId);
            if (savedToken == null)
                return new { success = false, error = "Token not found", errorCode = "tokenNotFound" };
            var refreshToken = secretStore.Unprotect(savedToken.RefreshToken);
            var savedGuardData = secretStore.Unprotect(savedToken.GuardData);
            if (string.IsNullOrEmpty(refreshToken))
                return new { success = false, error = "Token could not be decrypted", errorCode = "tokenDecryptFailed" };

            await SendEventAsync(eventBus, "connecting");
            await ConnectToSteam();
            await SendEventAsync(eventBus, "authenticating");
            var logonTcs = new TaskCompletionSource<EResult>();
            _attemptSubscriptions!.Add(_manager!.Subscribe<SteamKitUser.LoggedOnCallback>(callback => logonTcs.TrySetResult(callback.Result)));
            _steamClient!.GetHandler<SteamKitUser>()?.LogOn(new SteamKitUser.LogOnDetails
            {
                Username = savedToken.AccountName,
                AccessToken = refreshToken,
                ShouldRememberPassword = true
            });
            var logonResult = await logonTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (logonResult == EResult.OK)
            {
                StoreReconnectCredentials(savedToken.AccountName, refreshToken, savedGuardData);
                var session = TakeCurrentSession();
                await InstallSessionAsync(savedToken.AccountName, session, _stopping.Token).ConfigureAwait(false);
                await eventBus.PublishAsync(new SteamSessionReady(savedToken.AccountName));
                await SendEventAsync(eventBus, "success", new { accountName = savedToken.AccountName });
                return new { success = true, accountName = savedToken.AccountName };
            }
            _steamClient.GetHandler<SteamKitUser>()?.LogOff();
            Disconnect();
            var errorCode = logonResult.ToString();
            await SendEventAsync(eventBus, "error", new { message = $"Logon failed: {logonResult}", errorCode });
            return new { success = false, error = $"Logon failed: {logonResult}", errorCode };
        }
        catch (TimeoutException)
        {
            await SendEventAsync(eventBus, "error", new { message = "Login timeout", errorCode = "timeout" });
            return new { success = false, error = "Login timeout", errorCode = "timeout" };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Steam token login failed");
            var errorCode = GetErrorCodeFromException(exception);
            await SendEventAsync(eventBus, "error", new { message = exception.Message, errorCode });
            return new { success = false, error = exception.Message, errorCode };
        }
        finally
        {
            Disconnect();
            Interlocked.Exchange(ref _isLoginInProgress, 0);
        }
    }

    public void SubmitGuardCode(string code) => _authenticator?.SubmitCode(code);
    public void SwitchToUseCodeLogin() => _authenticator?.SwitchToUseCode();
    public void ConfirmDeviceLogin() => _authenticator?.ConfirmDevice();

    public void CancelLogin()
    {
        _cts?.Cancel();
        _authenticator?.Cancel();
        Disconnect();
    }

    public IReadOnlyList<string> GetLoggedInUsers() => _loggedInSessions.Keys.ToArray();

    public bool TryGetSession(string accountName, out ISteamSession session)
    {
        if (_loggedInSessions.TryGetValue(accountName, out var value))
        {
            session = value;
            return true;
        }
        session = null!;
        return false;
    }

    private bool TryGetOwnedSession(string accountName, out SteamSession session)
        => _loggedInSessions.TryGetValue(accountName, out session!);

    public async Task<bool> LogoutUser(string accountName)
    {
        if (_reconnectStates.TryGetValue(accountName, out var reconnectState))
        {
            lock (reconnectState)
            {
                reconnectState.IsUserInitiatedLogout = true;
                reconnectState.Timer?.Dispose();
                reconnectState.Timer = null;
            }
        }
        try
        {
            if (!_loggedInSessions.TryRemove(accountName, out var session)) return false;
            session.Client.GetHandler<SteamKitUser>()?.LogOff();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), timeProvider, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            await session.StopAsync(_stopping.Token).ConfigureAwait(false);
            await eventBus.PublishAsync(new SteamSessionEnded(accountName));
            logger.LogInformation("Steam user {AccountName} logged out", accountName);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Logout failed for {AccountName}", accountName);
            return false;
        }
    }

    public async Task LogoutAllUsers()
    {
        foreach (var user in _loggedInSessions.Keys.ToList()) await LogoutUser(user);
        logger.LogInformation("All Steam users logged out");
    }

    public bool SetUserPersonaState(string accountName, int personaState)
    {
        try
        {
            if (!_loggedInSessions.TryGetValue(accountName, out var session))
            {
                logger.LogWarning("Steam user {AccountName} has no logged-in session", accountName);
                return false;
            }
            var state = (EPersonaState)personaState;
            session.Client.GetHandler<SteamFriends>()?.SetPersonaState(state);
            logger.LogDebug("Set persona state for {AccountName} to {State}", accountName, state);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "SetUserPersonaState failed for {AccountName}", accountName);
            return false;
        }
    }

    public List<object> GetSavedTokens()
    {
        try
        {
            return tokenStore.List().Select(token => (object)new
            {
                id = token.Id,
                accountName = token.AccountName,
                createdAt = token.CreatedAt,
                expiresAt = GetJwtExpiry(secretStore.Unprotect(token.RefreshToken))
            }).ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to list saved Steam login tokens");
            return [];
        }
    }

    public async Task EncryptLegacyTokensAsync()
    {
        try
        {
            var upgraded = await tokenStore.EncryptLegacyAsync(secretStore);
            if (upgraded > 0) logger.LogInformation("Encrypted {Count} legacy plaintext Steam token(s)", upgraded);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to encrypt legacy Steam login tokens");
        }
    }

    public async Task<bool> DeleteSavedToken(int id)
    {
        try
        {
            var token = await tokenStore.DeleteAsync(id);
            if (token == null) return false;
            logger.LogInformation("Deleted saved Steam token for {AccountName}", token.AccountName);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete saved Steam login token {TokenId}", id);
            return false;
        }
    }

    private async Task ConnectToSteam()
    {
        _steamClient = CreateSteamClient();
        _manager = new CallbackManager(_steamClient);
        _connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();
        _attemptSubscriptions = [];
        _attemptSubscriptions.Add(_manager.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            logger.LogDebug("Connected to Steam");
            _connectedTcs.TrySetResult(true);
        }));
        var steamFriends = _steamClient.GetHandler<SteamFriends>();
        _attemptSubscriptions.Add(_manager.Subscribe<SteamKitUser.AccountInfoCallback>(_ =>
        {
            logger.LogDebug("Steam account information received; setting persona state");
            steamFriends?.SetPersonaState(EPersonaState.LookingToPlay);
        }));
        _attemptSubscriptions.Add(_manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            logger.LogDebug("Disconnected from Steam during login attempt");
            if (!_connectedTcs.Task.IsCompleted) _connectedTcs.TrySetResult(false);
        }));
        var localCts = _cts;
        var localManager = _manager;
        _callbackLoopTask = Task.Run(() =>
        {
            while (!localCts.IsCancellationRequested)
            {
                try { localManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
                catch { break; }
            }
        });
        _steamClient.Connect();
        if (!await _connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("Failed to connect to Steam servers");
    }

    private SteamClient CreateSteamClient()
    {
        var client = new SteamClient();
        client.AddHandler(new SteamRichPresenceHandler());
        client.AddHandler(new PersonaStateRichPresenceHandler());
        client.AddHandler(new SteamLevelsHandler(logger));
        return client;
    }

    private SteamSession TakeCurrentSession()
    {
        var session = new SteamSession(
            _steamClient ?? throw new InvalidOperationException("No Steam client is connected."),
            _manager ?? throw new InvalidOperationException("No callback manager is available."),
            _cts ?? throw new InvalidOperationException("No callback cancellation source is available."),
            _callbackLoopTask ?? throw new InvalidOperationException("No callback loop is running."),
            _attemptSubscriptions ?? []);
        _steamClient = null;
        _manager = null;
        _cts = null;
        _connectedTcs = null;
        _callbackLoopTask = null;
        _attemptSubscriptions = null;
        return session;
    }

    private async Task InstallSessionAsync(string accountName, SteamSession session, CancellationToken cancellationToken)
    {
        SetupSessionCallbacks(accountName, session);
        while (true)
        {
            if (_loggedInSessions.TryGetValue(accountName, out var previous))
            {
                if (!_loggedInSessions.TryUpdate(accountName, session, previous)) continue;
                await previous.StopAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (_loggedInSessions.TryAdd(accountName, session)) return;
        }
    }

    private void Disconnect()
    {
        var client = _steamClient;
        var cancellation = _cts;
        var callbackLoop = _callbackLoopTask;
        var subscriptions = _attemptSubscriptions;
        _steamClient = null;
        _manager = null;
        _cts = null;
        _connectedTcs = null;
        _callbackLoopTask = null;
        _attemptSubscriptions = null;
        DisposeAuthenticator();
        if (subscriptions != null)
            foreach (var subscription in subscriptions) subscription.Dispose();
        try
        {
            cancellation?.Cancel();
            client?.Disconnect();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to disconnect a Steam login attempt cleanly");
        }
        if (callbackLoop != null && cancellation != null)
            TrackBackground(CompleteAttemptStopAsync(callbackLoop, cancellation));
        else
            cancellation?.Dispose();
    }

    private static async Task CompleteAttemptStopAsync(Task callbackLoop, CancellationTokenSource cancellation)
    {
        try { await callbackLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        finally { cancellation.Dispose(); }
    }

    private void DisposeAuthenticator() => Interlocked.Exchange(ref _authenticator, null)?.Dispose();

    private void SetupSessionCallbacks(string accountName, SteamSession session)
    {
        session.AddSubscription(session.Callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            logger.LogInformation("Steam user {AccountName} disconnected", accountName);
            if (!((ICollection<KeyValuePair<string, SteamSession>>)_loggedInSessions)
                    .Remove(new KeyValuePair<string, SteamSession>(accountName, session))) return;
            TrackBackground(session.StopAsync(_stopping.Token));
            TrackBackground(eventBus.PublishAsync(new SteamSessionDisconnected(accountName), _stopping.Token));
            TrackBackground(eventBus.PublishAsync(new SteamSessionEnded(accountName), _stopping.Token));
            TrackBackground(SendEventAsync(eventBus, "userDisconnected", new { accountName }));
            if (_reconnectStates.TryGetValue(accountName, out var state))
            {
                string refreshToken;
                string? guardData;
                bool shouldReconnect;
                lock (state)
                {
                    shouldReconnect = !state.IsUserInitiatedLogout && !state.IsTerminated;
                    refreshToken = state.RefreshToken;
                    guardData = state.GuardData;
                }
                if (shouldReconnect) ScheduleReconnect(eventBus, accountName, refreshToken, guardData);
            }
        }));
    }

    private void StoreReconnectCredentials(string accountName, string refreshToken, string? guardData)
    {
        var state = _reconnectStates.GetOrAdd(accountName, name => new ReconnectState { AccountName = name });
        lock (state)
        {
            state.RefreshToken = refreshToken;
            state.GuardData = guardData;
            state.IsUserInitiatedLogout = false;
            state.IsTerminated = false;
            state.IsReconnecting = false;
            state.RetryCount = 0;
            state.Timer?.Dispose();
            state.Timer = null;
        }
        logger.LogDebug("Stored reconnect credentials for {AccountName}", accountName);
    }

    private void TerminateReconnect(IEventBus targetEventBus, string accountName, ReconnectState state, string errorCode)
    {
        lock (state)
        {
            state.IsTerminated = true;
            state.IsReconnecting = false;
            state.Timer?.Dispose();
            state.Timer = null;
            state.RefreshToken = string.Empty;
            state.GuardData = null;
        }
        TrackBackground(SendEventAsync(targetEventBus, "reconnectFailed", new { accountName, errorCode }));
    }

    private void ScheduleReconnect(IEventBus targetEventBus, string accountName, string refreshToken, string? guardData)
    {
        var state = _reconnectStates.GetOrAdd(accountName, name => new ReconnectState { AccountName = name });
        lock (state)
        {
            if (state.IsUserInitiatedLogout || state.IsReconnecting || state.IsTerminated) return;
            if (state.RetryCount >= MaxReconnectAttempts)
            {
                logger.LogWarning("Giving up reconnect for {AccountName} after {Count} attempts", accountName, state.RetryCount);
                TerminateReconnect(targetEventBus, accountName, state, "reconnectAttemptsExhausted");
                return;
            }
            state.RefreshToken = refreshToken;
            state.GuardData = guardData;
            state.IsReconnecting = true;
            state.RetryCount++;
            var baseDelay = Math.Min(Math.Pow(2, state.RetryCount - 1) * 5, 60);
            double jitterFactor;
            lock (_jitter) jitterFactor = 0.8 + _jitter.NextDouble() * 0.4;
            var delay = TimeSpan.FromSeconds(baseDelay * jitterFactor);
            logger.LogInformation("Scheduling reconnect for {AccountName} in {Delay} (attempt {Attempt}/{Maximum})",
                accountName, delay, state.RetryCount, MaxReconnectAttempts);
            state.Timer?.Dispose();
            state.Timer = timeProvider.CreateTimer(
                _ => TrackBackground(ReconnectAsync(targetEventBus, accountName)), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ReconnectAsync(IEventBus targetEventBus, string accountName)
    {
        if (!_reconnectStates.TryGetValue(accountName, out var state)) return;
        string logonAccountName;
        string refreshToken;
        string? guardData;
        lock (state)
        {
            if (state.IsUserInitiatedLogout || state.IsTerminated) return;
            logonAccountName = state.AccountName;
            refreshToken = state.RefreshToken;
            guardData = state.GuardData;
        }
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            logger.LogInformation("Network unavailable; deferring reconnect for {AccountName}", accountName);
            lock (state)
            {
                state.IsReconnecting = false;
                if (state.RetryCount > 0) state.RetryCount--;
                refreshToken = state.RefreshToken;
                guardData = state.GuardData;
            }
            ScheduleReconnect(targetEventBus, accountName, refreshToken, guardData);
            return;
        }

        SteamSession? pendingSession = null;
        try
        {
            logger.LogInformation("Reconnecting Steam user {AccountName}", accountName);
            var client = CreateSteamClient();
            var manager = new CallbackManager(client);
            var cancellation = new CancellationTokenSource();
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscriptions = new List<IDisposable>
            {
                manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult(true)),
                manager.Subscribe<SteamClient.DisconnectedCallback>(_ => connected.TrySetResult(false))
            };
            var steamFriends = client.GetHandler<SteamFriends>();
            subscriptions.Add(manager.Subscribe<SteamKitUser.AccountInfoCallback>(_ =>
                steamFriends?.SetPersonaState(EPersonaState.LookingToPlay)));
            var callbackLoop = Task.Run(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    try { manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
                    catch { break; }
                }
            });
            pendingSession = new SteamSession(client, manager, cancellation, callbackLoop, subscriptions);
            client.Connect();
            if (!await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), _stopping.Token).ConfigureAwait(false))
                throw new InvalidOperationException("Failed to reconnect to Steam servers");
            lock (state)
            {
                if (state.IsUserInitiatedLogout || state.IsTerminated) return;
                logonAccountName = state.AccountName;
                refreshToken = state.RefreshToken;
            }
            var loggedOn = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingSession.AddSubscription(manager.Subscribe<SteamKitUser.LoggedOnCallback>(callback => loggedOn.TrySetResult(callback.Result)));
            client.GetHandler<SteamKitUser>()?.LogOn(new SteamKitUser.LogOnDetails
            {
                Username = logonAccountName,
                AccessToken = refreshToken,
                ShouldRememberPassword = true
            });
            var result = await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), _stopping.Token).ConfigureAwait(false);
            if (result != EResult.OK)
            {
                if (IsTerminalLogonResult(result))
                {
                    logger.LogWarning("Reconnect for {AccountName} failed permanently: {Result}", accountName, result);
                    TerminateReconnect(targetEventBus, accountName, state, result.ToString());
                    return;
                }
                throw new InvalidOperationException($"Reconnect logon failed for {accountName}: {result}");
            }
            lock (state)
            {
                if (state.IsUserInitiatedLogout || state.IsTerminated) return;
                state.IsReconnecting = false;
                state.RetryCount = 0;
            }
            var installedSession = pendingSession;
            await InstallSessionAsync(accountName, installedSession, _stopping.Token).ConfigureAwait(false);
            pendingSession = null;
            if (!_loggedInSessions.TryGetValue(accountName, out var current) || !ReferenceEquals(current, installedSession)) return;
            await SendEventAsync(targetEventBus, "userReconnected", new { accountName });
            await targetEventBus.PublishAsync(new SteamSessionReconnected(accountName), _stopping.Token);
            await targetEventBus.PublishAsync(new SteamSessionReady(accountName), _stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Reconnect failed for {AccountName}", accountName);
            bool shouldRetry;
            lock (state)
            {
                state.IsReconnecting = false;
                refreshToken = state.RefreshToken;
                guardData = state.GuardData;
                shouldRetry = !state.IsUserInitiatedLogout && !state.IsTerminated;
            }
            if (shouldRetry) ScheduleReconnect(targetEventBus, accountName, refreshToken, guardData);
        }
        finally
        {
            if (pendingSession != null) await pendingSession.StopAsync(_stopping.Token).ConfigureAwait(false);
        }
    }

    private async Task SaveTokens(AuthPollResult response)
    {
        try
        {
            await tokenStore.UpsertAsync(new SteamLoginTokenWrite(
                response.AccountName,
                secretStore.Protect(response.AccessToken) ?? string.Empty,
                secretStore.Protect(response.RefreshToken) ?? string.Empty,
                secretStore.Protect(response.NewGuardData),
                (int)timeProvider.GetUtcNow().ToUnixTimeSeconds()));
            logger.LogInformation("Saved Steam login token for {AccountName}", response.AccountName);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save Steam login token");
        }
    }

    private static long? GetJwtExpiry(string? jwt)
    {
        try
        {
            if (string.IsNullOrEmpty(jwt)) return null;
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("exp", out var expiry) ? expiry.GetInt64() : null;
        }
        catch { return null; }
    }

    private static string GetErrorCodeFromException(Exception exception) => exception switch
    {
        TimeoutException => "timeout",
        HttpRequestException => "networkError",
        _ when exception.Message.Contains("connect", StringComparison.OrdinalIgnoreCase) => "connectionFailed",
        _ => "unknown"
    };

    private static string GenerateQrCodeBase64(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
        var code = new PngByteQRCode(data);
        return $"data:image/png;base64,{Convert.ToBase64String(code.GetGraphic(10))}";
    }

    private async Task SendEventAsync(IEventBus targetEventBus, string type, object? data = null)
    {
        await targetEventBus.PublishAsync(new SteamLoginProgressChanged(type, data));
        logger.LogDebug("Steam login event: {EventType}", type);
    }

    private void TrackBackground(Task task)
    {
        var id = Interlocked.Increment(ref _nextWorkId);
        _backgroundWork[id] = task;
        _ = task.ContinueWith(completed =>
        {
            _backgroundWork.TryRemove(id, out _);
            if (completed.IsFaulted) logger.LogError(completed.Exception, "Tracked Steam login callback work failed");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task DrainBackgroundWorkAsync(CancellationToken cancellationToken)
    {
        while (!_backgroundWork.IsEmpty)
        {
            var work = _backgroundWork.Values.ToArray();
            if (work.Length == 0) break;
            try { await Task.WhenAll(work).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Failed while draining Steam login callback work"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelLogin();
        foreach (var state in _reconnectStates.Values)
        {
            lock (state)
            {
                state.IsUserInitiatedLogout = true;
                state.Timer?.Dispose();
                state.Timer = null;
            }
        }
        await LogoutAllUsers().ConfigureAwait(false);
        await _stopping.CancelAsync();
        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await DrainBackgroundWorkAsync(drainTimeout.Token).ConfigureAwait(false);
        _reconnectStates.Clear();
        _stopping.Dispose();
    }

    private sealed class SteamSession(
        SteamClient client,
        CallbackManager callbacks,
        CancellationTokenSource cancellation,
        Task callbackLoop,
        IEnumerable<IDisposable> subscriptions) : ISteamSession, IAsyncDisposable
    {
        private readonly object _lifecycleLock = new();
        private readonly List<IDisposable> _subscriptions = subscriptions.ToList();
        private Task? _stopTask;
        public SteamClient Client { get; } = client;
        public CallbackManager Callbacks { get; } = callbacks;

        public void AddSubscription(IDisposable subscription)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            lock (_lifecycleLock)
            {
                if (_stopTask == null)
                {
                    _subscriptions.Add(subscription);
                    return;
                }
            }
            subscription.Dispose();
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Task stopTask;
            lock (_lifecycleLock) stopTask = _stopTask ??= StopCoreAsync();
            return stopTask.WaitAsync(cancellationToken);
        }

        private async Task StopCoreAsync()
        {
            IDisposable[] subscriptions;
            lock (_lifecycleLock)
            {
                subscriptions = _subscriptions.ToArray();
                _subscriptions.Clear();
            }
            foreach (var subscription in subscriptions)
            {
                try { subscription.Dispose(); } catch { }
            }
            try { await cancellation.CancelAsync().ConfigureAwait(false); } catch { }
            try { Client.Disconnect(); } catch { }
            try { await callbackLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
            finally { cancellation.Dispose(); }
        }

        public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    }

    private sealed class IpcAuthenticator(SteamLoginService owner, IEventBus targetEventBus) : IAuthenticator, IDisposable
    {
        private TaskCompletionSource<string>? _codeTcs;
        private TaskCompletionSource<bool>? _useCodeTcs;
        private readonly CancellationTokenSource _cancellation = new();
        private int _disposed;

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            => WaitForCodeAsync("device", null, previousCodeWasIncorrect);
        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            => WaitForCodeAsync("email", email, previousCodeWasIncorrect);

        private async Task<string> WaitForCodeAsync(string guardType, string? email, bool previousCodeWasIncorrect)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _codeTcs = completion;
            using var registration = _cancellation.Token.Register(() => completion.TrySetCanceled(_cancellation.Token));
            await owner.SendEventAsync(targetEventBus, "guardCodeNeeded", new { guardType, email, previousCodeWasIncorrect });
            return await completion.Task.ConfigureAwait(false);
        }

        public async Task<bool> AcceptDeviceConfirmationAsync()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _useCodeTcs = completion;
            using var registration = _cancellation.Token.Register(() => completion.TrySetCanceled(_cancellation.Token));
            await owner.SendEventAsync(targetEventBus, "deviceConfirmationNeeded");
            return await completion.Task.ConfigureAwait(false);
        }

        public void SubmitCode(string code) => _codeTcs?.TrySetResult(code);
        public void SwitchToUseCode() => _useCodeTcs?.TrySetResult(false);
        public void ConfirmDevice() => _useCodeTcs?.TrySetResult(true);
        public void Cancel()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _cancellation.Cancel();
            _codeTcs?.TrySetCanceled(_cancellation.Token);
            _useCodeTcs?.TrySetCanceled(_cancellation.Token);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cancellation.Cancel();
            _codeTcs?.TrySetCanceled(_cancellation.Token);
            _useCodeTcs?.TrySetCanceled(_cancellation.Token);
            _codeTcs = null;
            _useCodeTcs = null;
            _cancellation.Dispose();
        }
    }
}
