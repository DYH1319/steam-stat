using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Gateway.Internal;
using SteamKitUser = SteamKit2.SteamUser;

namespace SteamStat.Core.Steam.Session.Internal;

internal interface ISteamConnection : ISteamSession, IAsyncDisposable
{
    bool IsConnected { get; }
    event Action<ISteamConnection>? Disconnected;
    event Action<ISteamConnection, Exception>? PumpFaulted;
    Task ConnectAsync(CancellationToken cancellationToken);
    Task<SteamAuthenticationResult> AuthenticateWithCredentialsAsync(
        string username,
        string password,
        bool persistent,
        string? guardData,
        ISteamGuardInteraction guardInteraction,
        CancellationToken cancellationToken);
    Task<SteamAuthenticationResult> AuthenticateWithQrAsync(
        bool persistent,
        Action<string> challengeChanged,
        CancellationToken cancellationToken);
    Task<EResult> LogOnAsync(string accountName, string refreshToken, bool rememberPassword, CancellationToken cancellationToken);
    bool SetPersonaState(EPersonaState state);
    Task StopAsync();
}

internal interface ISteamConnectionFactory
{
    ISteamConnection Create(long generation);
}

internal sealed class SteamConnectionFactory(ILoggerFactory loggerFactory) : ISteamConnectionFactory
{
    private readonly SteamConfiguration _configuration = SteamConfiguration.Create(_ => { });

    public ISteamConnection Create(long generation)
        => new SteamConnection(_configuration, generation, loggerFactory.CreateLogger<SteamConnection>());
}

internal sealed class SteamConnection : ISteamConnection
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private readonly object _lifecycleLock = new();
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ILogger<SteamConnection> _logger;
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pumpTask;
    private Task? _stopTask;
    private bool _isConnected;
    private bool _stopping;

    public SteamConnection(SteamConfiguration configuration, long generation, ILogger<SteamConnection> logger)
    {
        Generation = generation;
        _logger = logger;
        Client = new SteamClient(configuration);
        Client.AddHandler(new SteamRichPresenceHandler());
        Client.AddHandler(new PersonaStateRichPresenceHandler());
        Client.AddHandler(new SteamLevelsHandler(logger));
        Callbacks = new CallbackManager(Client);
        _subscriptions.Add(Callbacks.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            _isConnected = true;
            _connected.TrySetResult(true);
        }));
        _subscriptions.Add(Callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => OnDisconnected()));
        var friends = Client.GetHandler<SteamFriends>();
        _subscriptions.Add(Callbacks.Subscribe<SteamKitUser.AccountInfoCallback>(_ =>
            friends?.SetPersonaState(EPersonaState.LookingToPlay)));
        _pumpTask = PumpCallbacksAsync();
    }

    public long Generation { get; }
    public bool IsConnected => Volatile.Read(ref _isConnected);
    public SteamClient Client { get; }
    public CallbackManager Callbacks { get; }
    public event Action<ISteamConnection>? Disconnected;
    public event Action<ISteamConnection, Exception>? PumpFaulted;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        Client.Connect();
        if (!await _connected.Task.WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Failed to connect to Steam servers.");
    }

    public async Task<SteamAuthenticationResult> AuthenticateWithCredentialsAsync(
        string username,
        string password,
        bool persistent,
        string? guardData,
        ISteamGuardInteraction guardInteraction,
        CancellationToken cancellationToken)
    {
        using var authenticator = new GuardInteractionAdapter(guardInteraction);
        var session = await Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username,
            Password = password,
            IsPersistentSession = persistent,
            GuardData = guardData,
            Authenticator = authenticator,
            PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient
        }).ConfigureAwait(false);
        var result = await session.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);
        return new SteamAuthenticationResult(
            result.AccountName, result.AccessToken, result.RefreshToken, result.NewGuardData ?? guardData);
    }

    public async Task<SteamAuthenticationResult> AuthenticateWithQrAsync(
        bool persistent,
        Action<string> challengeChanged,
        CancellationToken cancellationToken)
    {
        var session = await Client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
        {
            IsPersistentSession = persistent,
            PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient
        }).ConfigureAwait(false);
        session.ChallengeURLChanged = () => challengeChanged(session.ChallengeURL);
        challengeChanged(session.ChallengeURL);
        var result = await session.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);
        return new SteamAuthenticationResult(
            result.AccountName, result.AccessToken, result.RefreshToken, result.NewGuardData);
    }

    public async Task<EResult> LogOnAsync(
        string accountName,
        string refreshToken,
        bool rememberPassword,
        CancellationToken cancellationToken)
    {
        var loggedOn = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = AddSubscription(
            Callbacks.Subscribe<SteamKitUser.LoggedOnCallback>(callback => loggedOn.TrySetResult(callback.Result)));
        void OnConnectionLost(ISteamConnection _) => disconnected.TrySetResult();
        Disconnected += OnConnectionLost;
        try
        {
            Client.GetHandler<SteamKitUser>()?.LogOn(new SteamKitUser.LogOnDetails
            {
                Username = accountName,
                AccessToken = refreshToken,
                ShouldRememberPassword = rememberPassword
            });
            var completed = await Task.WhenAny(loggedOn.Task, disconnected.Task)
                .WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
            if (completed == disconnected.Task) return EResult.ServiceUnavailable;
            return await loggedOn.Task.ConfigureAwait(false);
        }
        finally
        {
            Disconnected -= OnConnectionLost;
        }
    }

    public bool SetPersonaState(EPersonaState state)
    {
        var friends = Client.GetHandler<SteamFriends>();
        if (friends == null) return false;
        friends.SetPersonaState(state);
        return true;
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock) return _stopTask ??= StopCoreAsync();
    }

    private IDisposable AddSubscription(IDisposable subscription)
    {
        lock (_lifecycleLock)
        {
            if (!_stopping)
            {
                _subscriptions.Add(subscription);
                return new SubscriptionLease(this, subscription);
            }
        }
        subscription.Dispose();
        return EmptyDisposable.Instance;
    }

    private void RemoveSubscription(IDisposable subscription)
    {
        lock (_lifecycleLock) _subscriptions.Remove(subscription);
        subscription.Dispose();
    }

    private void OnDisconnected()
    {
        Volatile.Write(ref _isConnected, false);
        _connected.TrySetResult(false);
        lock (_lifecycleLock)
        {
            if (_stopping) return;
        }
        Disconnected?.Invoke(this);
    }

    private async Task PumpCallbacksAsync()
    {
        try
        {
            while (!_pumpCancellation.IsCancellationRequested)
                await Callbacks.RunWaitCallbackAsync(_pumpCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_pumpCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Steam callback pump failed for generation {Generation}", Generation);
            PumpFaulted?.Invoke(this, exception);
        }
    }

    private async Task StopCoreAsync()
    {
        IDisposable[] subscriptions;
        lock (_lifecycleLock)
        {
            _stopping = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions)
        {
            try { subscription.Dispose(); } catch { }
        }
        try { await _pumpCancellation.CancelAsync().ConfigureAwait(false); } catch { }
        try { Client.Disconnect(); } catch { }
        Volatile.Write(ref _isConnected, false);
        try { await _pumpTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        _pumpCancellation.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class GuardInteractionAdapter(ISteamGuardInteraction interaction) : IAuthenticator, IDisposable
    {
        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            => interaction.GetDeviceCodeAsync(previousCodeWasIncorrect);
        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            => interaction.GetEmailCodeAsync(email, previousCodeWasIncorrect);
        public Task<bool> AcceptDeviceConfirmationAsync()
            => interaction.AcceptDeviceConfirmationAsync();
        public void Dispose() { }
    }

    private sealed class SubscriptionLease(SteamConnection owner, IDisposable subscription) : IDisposable
    {
        private IDisposable? _subscription = subscription;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _subscription, null);
            if (value != null) owner.RemoveSubscription(value);
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
