using Microsoft.Extensions.Logging;
using QRCoder;
using SteamKit2.Authentication;
using SteamStat.Core.Events;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Features.Login;

public sealed class SteamLoginService(
    IEventBus eventBus,
    SteamCredentialStore credentialStore,
    ISteamSessionManager sessionManager,
    ILogger<SteamLoginService> logger) : IAsyncDisposable
{
    private readonly object _qrEventLock = new();
    private CancellationTokenSource? _loginCancellation;
    private IpcAuthenticator? _authenticator;
    private Task _qrEvents = Task.CompletedTask;
    private int _isLoginInProgress;
    private int _disposed;

    public Task<SteamLoginResult> LoginWithCredentials(string username, string password, bool rememberMe)
        => RunLoginAsync(async cancellationToken =>
        {
            await SendEventAsync("connecting").ConfigureAwait(false);
            _authenticator = new IpcAuthenticator(eventBus);
            var guardData = credentialStore.LoadGuardData(username);
            await SendEventAsync("authenticating").ConfigureAwait(false);
            var authentication = await sessionManager.AuthenticateWithCredentialsAsync(
                username, password, rememberMe, guardData, _authenticator, cancellationToken).ConfigureAwait(false);
            if (rememberMe) await credentialStore.SaveAsync(authentication, cancellationToken).ConfigureAwait(false);
            return await StartAuthenticatedSessionAsync(authentication, rememberMe, cancellationToken).ConfigureAwait(false);
        }, "Steam credential login failed");

    public Task<SteamLoginResult> LoginWithQR(bool rememberMe)
        => RunLoginAsync(async cancellationToken =>
        {
            await SendEventAsync("connecting").ConfigureAwait(false);
            await SendEventAsync("authenticating").ConfigureAwait(false);
            var authentication = await sessionManager.AuthenticateWithQrAsync(
                rememberMe, QueueQrCodeEvent, cancellationToken).ConfigureAwait(false);
            if (rememberMe) await credentialStore.SaveAsync(authentication, cancellationToken).ConfigureAwait(false);
            return await StartAuthenticatedSessionAsync(authentication, rememberMe, cancellationToken).ConfigureAwait(false);
        }, "Steam QR login failed");

    public Task<SteamLoginResult> LoginWithToken(int tokenId)
        => RunLoginAsync(async cancellationToken =>
        {
            var lookup = await credentialStore.LoadByIdAsync(tokenId, cancellationToken).ConfigureAwait(false);
            if (!lookup.Found)
                return new SteamLoginResult(false, Error: "Token not found", ErrorCode: "tokenNotFound");
            if (lookup.Credential is not { } credential)
                return new SteamLoginResult(false, Error: "Token could not be decrypted", ErrorCode: "tokenDecryptFailed");
            await SendEventAsync("connecting").ConfigureAwait(false);
            await SendEventAsync("authenticating").ConfigureAwait(false);
            var start = await sessionManager.StartSessionAsync(
                credential.AccountName, credential.RefreshToken, credential.GuardData, true, cancellationToken).ConfigureAwait(false);
            if (!start.Success)
            {
                await SendEventAsync("error", new SteamLoginProgressData(
                    Message: $"Logon failed: {start.ErrorCode}", ErrorCode: start.ErrorCode)).ConfigureAwait(false);
                return new SteamLoginResult(false, Error: $"Logon failed: {start.ErrorCode}", ErrorCode: start.ErrorCode);
            }
            await SendEventAsync("success", new SteamLoginProgressData(AccountName: credential.AccountName)).ConfigureAwait(false);
            return new SteamLoginResult(true, AccountName: credential.AccountName);
        }, "Steam token login failed");

    public void SubmitGuardCode(string code) => _authenticator?.SubmitCode(code);
    public void SwitchToUseCodeLogin() => _authenticator?.SwitchToUseCode();
    public void ConfirmDeviceLogin() => _authenticator?.ConfirmDevice();

    public void CancelLogin()
    {
        _loginCancellation?.Cancel();
        _authenticator?.Cancel();
        _ = sessionManager.CancelLoginAsync();
    }

    public IReadOnlyList<string> GetLoggedInUsers() => sessionManager.GetLoggedInUsers();
    public Task<bool> LogoutUser(string accountName) => sessionManager.LogoutUserAsync(accountName);
    public Task LogoutAllUsers() => sessionManager.LogoutAllUsersAsync();
    public bool SetUserPersonaState(string accountName, int personaState)
        => sessionManager.SetUserPersonaState(accountName, personaState);

    public IReadOnlyList<SteamLoginTokenSummary> GetSavedTokens()
    {
        try { return credentialStore.List(); }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to list saved Steam login tokens");
            return [];
        }
    }

    public async Task EncryptLegacyTokensAsync()
    {
        try { await credentialStore.EncryptLegacyAsync().ConfigureAwait(false); }
        catch (Exception exception) { logger.LogError(exception, "Failed to encrypt legacy Steam login tokens"); }
    }

    public async Task<bool> DeleteSavedToken(int id)
    {
        try { return await credentialStore.DeleteAsync(id).ConfigureAwait(false); }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete saved Steam login token {TokenId}", id);
            return false;
        }
    }

    private async Task<SteamLoginResult> RunLoginAsync(
        Func<CancellationToken, Task<SteamLoginResult>> login,
        string failureMessage)
    {
        if (Interlocked.CompareExchange(ref _isLoginInProgress, 1, 0) != 0)
            return new SteamLoginResult(false, Error: "Login already in progress", ErrorCode: "alreadyInProgress");
        var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        try
        {
            return await login(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendEventAsync("cancelled").ConfigureAwait(false);
            return new SteamLoginResult(false, Error: "Login cancelled", ErrorCode: "cancelled");
        }
        catch (AuthenticationException exception)
        {
            logger.LogWarning(exception, failureMessage);
            var errorCode = exception.Result.ToString();
            await SendEventAsync("error", new SteamLoginProgressData(
                Message: exception.Message, ErrorCode: errorCode)).ConfigureAwait(false);
            return new SteamLoginResult(false, Error: exception.Message, ErrorCode: errorCode);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, failureMessage);
            var errorCode = GetErrorCodeFromException(exception);
            await SendEventAsync("error", new SteamLoginProgressData(
                Message: exception.Message, ErrorCode: errorCode)).ConfigureAwait(false);
            return new SteamLoginResult(false, Error: exception.Message, ErrorCode: errorCode);
        }
        finally
        {
            if (ReferenceEquals(_loginCancellation, cancellation)) _loginCancellation = null;
            cancellation.Dispose();
            Interlocked.Exchange(ref _authenticator, null)?.Dispose();
            Interlocked.Exchange(ref _isLoginInProgress, 0);
        }
    }

    private async Task<SteamLoginResult> StartAuthenticatedSessionAsync(
        SteamAuthenticationResult authentication,
        bool rememberMe,
        CancellationToken cancellationToken)
    {
        var start = await sessionManager.StartSessionAsync(
            authentication.AccountName,
            authentication.RefreshToken,
            authentication.GuardData,
            rememberMe,
            cancellationToken).ConfigureAwait(false);
        if (!start.Success)
        {
            await SendEventAsync("error", new SteamLoginProgressData(
                Message: $"Logon failed: {start.ErrorCode}", ErrorCode: start.ErrorCode)).ConfigureAwait(false);
            return new SteamLoginResult(false, Error: $"Logon failed: {start.ErrorCode}", ErrorCode: start.ErrorCode);
        }
        await SendEventAsync("success", new SteamLoginProgressData(AccountName: authentication.AccountName)).ConfigureAwait(false);
        return new SteamLoginResult(true, AccountName: authentication.AccountName);
    }

    private void QueueQrCodeEvent(string url)
    {
        lock (_qrEventLock)
        {
            _qrEvents = _qrEvents.ContinueWith(
                _ => SendEventAsync("qrCode", new SteamLoginProgressData(QrImageBase64: GenerateQrCodeBase64(url))),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
        }
    }

    private Task SendEventAsync(string type, SteamLoginProgressData? data = null)
    {
        logger.LogDebug("Steam login event: {EventType}", type);
        return eventBus.PublishAsync(new SteamLoginProgressChanged(type, data));
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelLogin();
        await sessionManager.ShutdownAsync().ConfigureAwait(false);
        Task qrEvents;
        lock (_qrEventLock) qrEvents = _qrEvents;
        try { await qrEvents.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        Interlocked.Exchange(ref _authenticator, null)?.Dispose();
        _loginCancellation?.Dispose();
    }

    private sealed class IpcAuthenticator(IEventBus targetEventBus) : ISteamGuardInteraction, IDisposable
    {
        private TaskCompletionSource<string>? _codeCompletion;
        private TaskCompletionSource<bool>? _confirmationCompletion;
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
            _codeCompletion = completion;
            using var registration = _cancellation.Token.Register(() => completion.TrySetCanceled(_cancellation.Token));
            await targetEventBus.PublishAsync(new SteamLoginProgressChanged(
                "guardCodeNeeded", new SteamLoginProgressData(guardType, email, previousCodeWasIncorrect))).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }

        public async Task<bool> AcceptDeviceConfirmationAsync()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _confirmationCompletion = completion;
            using var registration = _cancellation.Token.Register(() => completion.TrySetCanceled(_cancellation.Token));
            await targetEventBus.PublishAsync(new SteamLoginProgressChanged("deviceConfirmationNeeded")).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }

        public void SubmitCode(string code) => _codeCompletion?.TrySetResult(code);
        public void SwitchToUseCode() => _confirmationCompletion?.TrySetResult(false);
        public void ConfirmDevice() => _confirmationCompletion?.TrySetResult(true);

        public void Cancel()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _cancellation.Cancel();
            _codeCompletion?.TrySetCanceled(_cancellation.Token);
            _confirmationCompletion?.TrySetCanceled(_cancellation.Token);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cancellation.Cancel();
            _codeCompletion?.TrySetCanceled(_cancellation.Token);
            _confirmationCompletion?.TrySetCanceled(_cancellation.Token);
            _codeCompletion = null;
            _confirmationCompletion = null;
            _cancellation.Dispose();
        }
    }
}

public sealed record SteamLoginResult(
    bool Success,
    string? AccountName = null,
    string? Error = null,
    string? ErrorCode = null);

public sealed record SteamLoginTokenSummary(
    int Id,
    string AccountName,
    int CreatedAt,
    long? ExpiresAt);
