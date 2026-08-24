using System.Net.NetworkInformation;
using System.Text.Json;
using ElectronNET.API;
using ElectronNet.Constants;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamKitUser = SteamKit2.SteamUser;

namespace ElectronNet.Services;

public static class SteamLoginService
{
    private static SteamClient? _steamClient;
    private static CallbackManager? _manager;
    private static CancellationTokenSource? _cts;
    private static IpcAuthenticator? _authenticator;
    private static TaskCompletionSource<bool>? _connectedTcs;
    private static bool _isLoginInProgress;

    // 已登录的 Steam 会话列表
    private static readonly Dictionary<string, (SteamClient client, CallbackManager manager, CancellationTokenSource cts)> _loggedInSessions = new();

    // 自动重连状态
    private static readonly Dictionary<string, ReconnectState> _reconnectStates = new();

    // 重连尝试次数上限，超过后停止并提示用户手动重新登录
    private const int MAX_RECONNECT_ATTEMPTS = 10;

    private static readonly Random _jitter = new();

    private class ReconnectState
    {
        public string AccountName { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public string? GuardData { get; set; }
        public int RetryCount { get; set; }
        public bool IsUserInitiatedLogout { get; set; }
        public bool IsReconnecting { get; set; }

        /// <summary>
        /// 已确定无法通过重试恢复（凭证失效 / 账号被封禁等），不再重连。
        /// </summary>
        public bool IsTerminated { get; set; }

        public System.Threading.Timer? Timer { get; set; }
    }

    /// <summary>
    /// 判断登录结果是否属于「重试也不会成功」的终止性错误。
    /// 这类错误必须停止自动重连并提示用户重新登录，否则会对一个已失效的
    /// refresh token 永远每 60 秒重试一次。
    /// </summary>
    private static bool IsTerminalLogonResult(EResult result)
    {
        return result switch
        {
            EResult.InvalidPassword => true,
            EResult.AccessDenied => true,
            EResult.Expired => true,
            EResult.Revoked => true,
            EResult.InvalidSignature => true,
            EResult.AccountDisabled => true,
            EResult.AccountLockedDown => true,
            EResult.AccountLogonDenied => true,
            EResult.AccountLoginDeniedNeedTwoFactor => true,
            EResult.Banned => true,
            EResult.AccountNotFound => true,
            _ => false
        };
    }

    /// <summary>
    /// 使用账号密码登录
    /// </summary>
    public static async Task<object> LoginWithCredentials(string username, string password, bool rememberMe)
    {
        if (_isLoginInProgress)
            return new { success = false, error = "Login already in progress", errorCode = "alreadyInProgress" };

        _isLoginInProgress = true;
        try
        {
            SendEvent("connecting");
            await ConnectToSteam();
            SendEvent("authenticating");

            _authenticator = new IpcAuthenticator();

            // 检查是否有保存的 Guard 数据
            string? guardData;
            await using (var db = AppDbContext.Create())
            {
                var existing = db.SteamLoginTokenTable.AsNoTracking()
                    .FirstOrDefault(t => t.AccountName == username);
                guardData = TokenProtectionService.Unprotect(existing?.GuardData);
            }

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

            // 轮询等待认证结果
            var pollResponse = await authSession.PollingWaitForResultAsync(_cts!.Token);

            // 保存 Token
            if (rememberMe)
            {
                await SaveTokens(pollResponse);
            }

            // 保存重连凭据（内存中）
            StoreReconnectCredentials(pollResponse.AccountName, pollResponse.RefreshToken, pollResponse.NewGuardData ?? guardData);

            // Logon to Steam with the access token we have received
            // Note that we are using RefreshToken for logging on here
            var steamUser = _steamClient.GetHandler<SteamKitUser>();
            steamUser?.LogOn(new SteamKitUser.LogOnDetails
            {
                Username = pollResponse.AccountName,
                AccessToken = pollResponse.RefreshToken,
                ShouldRememberPassword = rememberMe
            });

            // 保持登录状态，不 Disconnect
            _loggedInSessions[pollResponse.AccountName] = (_steamClient, _manager!, _cts);

            // 为该会话设置回调，监听断线事件
            SetupSessionCallbacks(pollResponse.AccountName, _manager!, _steamClient!);

            _steamClient = null;
            _manager = null;
            _cts = null;

            SendEvent("success", new
            {
                accountName = pollResponse.AccountName
            });

            _isLoginInProgress = false;
            return new { success = true, accountName = pollResponse.AccountName };
        }
        catch (OperationCanceledException)
        {
            SendEvent("cancelled");
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = "Login cancelled", errorCode = "cancelled" };
        }
        catch (AuthenticationException ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} AuthenticationException: {ex.Message}");
            var errorCode = ex.Result.ToString();
            SendEvent("error", new { message = ex.Message, errorCode });
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = ex.Message, errorCode };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Exception: {ex.Message}");
            var errorCode = GetErrorCodeFromException(ex);
            SendEvent("error", new { message = ex.Message, errorCode });
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = ex.Message, errorCode };
        }
    }

    /// <summary>
    /// 使用二维码登录
    /// </summary>
    public static async Task<object> LoginWithQR(bool rememberMe)
    {
        if (_isLoginInProgress)
            return new { success = false, error = "Login already in progress", errorCode = "alreadyInProgress" };

        _isLoginInProgress = true;
        try
        {
            SendEvent("connecting");
            await ConnectToSteam();
            SendEvent("authenticating");

            var authSession = await _steamClient!.Authentication.BeginAuthSessionViaQRAsync(
                new AuthSessionDetails());

            // Steam 会定期刷新二维码 URL
            authSession.ChallengeURLChanged = () =>
            {
                var qrBase64 = GenerateQrCodeBase64(authSession.ChallengeURL);
                SendEvent("qrCode", new { qrImageBase64 = qrBase64, challengeUrl = authSession.ChallengeURL });
            };

            // 发送初始二维码
            var initialQrBase64 = GenerateQrCodeBase64(authSession.ChallengeURL);
            SendEvent("qrCode", new { qrImageBase64 = initialQrBase64, challengeUrl = authSession.ChallengeURL });

            // 轮询等待用户扫码，使用 CancellationToken 支持取消
            var pollResponse = await authSession.PollingWaitForResultAsync(_cts!.Token);

            // 保存 Token
            if (rememberMe)
            {
                await SaveTokens(pollResponse);
            }

            // 保存重连凭据（内存中）
            StoreReconnectCredentials(pollResponse.AccountName, pollResponse.RefreshToken, pollResponse.NewGuardData);

            // Logon to Steam with the access token we have received
            // Note that we are using RefreshToken for logging on here
            var steamUser = _steamClient.GetHandler<SteamKitUser>();
            steamUser?.LogOn(new SteamKitUser.LogOnDetails
            {
                Username = pollResponse.AccountName,
                AccessToken = pollResponse.RefreshToken,
                ShouldRememberPassword = rememberMe
            });

            // 保持登录状态，不 Disconnect
            _loggedInSessions[pollResponse.AccountName] = (_steamClient, _manager!, _cts);

            // 为该会话设置回调，监听断线事件
            SetupSessionCallbacks(pollResponse.AccountName, _manager!, _steamClient!);

            _steamClient = null;
            _manager = null;
            _cts = null;

            SendEvent("success", new
            {
                accountName = pollResponse.AccountName
            });

            _isLoginInProgress = false;
            return new { success = true, accountName = pollResponse.AccountName };
        }
        catch (OperationCanceledException)
        {
            SendEvent("cancelled");
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = "Login cancelled", errorCode = "cancelled" };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} QR Login Exception: {ex.Message}");
            var errorCode = GetErrorCodeFromException(ex);
            SendEvent("error", new { message = ex.Message, errorCode });
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = ex.Message, errorCode };
        }
    }

    /// <summary>
    /// 使用已保存的 Token 登录（免登录）
    /// </summary>
    public static async Task<object> LoginWithToken(int tokenId)
    {
        if (_isLoginInProgress)
            return new { success = false, error = "Login already in progress", errorCode = "alreadyInProgress" };

        _isLoginInProgress = true;
        try
        {
            SteamLoginToken? savedToken;
            await using (var db = AppDbContext.Create())
            {
                savedToken = await db.SteamLoginTokenTable.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == tokenId);
            }

            if (savedToken == null)
                return new { success = false, error = "Token not found", errorCode = "tokenNotFound" };

            // 解密存储的凭证
            var refreshToken = TokenProtectionService.Unprotect(savedToken.RefreshToken);
            var savedGuardData = TokenProtectionService.Unprotect(savedToken.GuardData);
            if (string.IsNullOrEmpty(refreshToken))
                return new { success = false, error = "Token could not be decrypted", errorCode = "tokenDecryptFailed" };

            SendEvent("connecting");
            await ConnectToSteam();
            SendEvent("authenticating");

            var steamUser = _steamClient!.GetHandler<SteamKitUser>();

            var logonTcs = new TaskCompletionSource<EResult>();

            _manager!.Subscribe<SteamKitUser.LoggedOnCallback>(cb => logonTcs.TrySetResult(cb.Result));

            steamUser?.LogOn(new SteamKitUser.LogOnDetails
            {
                Username = savedToken.AccountName,
                AccessToken = refreshToken,
                ShouldRememberPassword = true
            });

            var logonResult = await logonTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // 不再立即 LogOff，保持登录状态
            if (logonResult == EResult.OK)
            {
                // 保存会话到已登录列表
                _loggedInSessions[savedToken.AccountName] = (_steamClient, _manager, _cts!);

                // 保存重连凭据（内存中）
                StoreReconnectCredentials(savedToken.AccountName, refreshToken, savedGuardData);

                // 为该会话设置回调，监听断线事件
                SetupSessionCallbacks(savedToken.AccountName, _manager!, _steamClient!);

                _steamClient = null;
                _manager = null;
                _cts = null;

                SendEvent("success", new { accountName = savedToken.AccountName });
                return new { success = true, accountName = savedToken.AccountName };
            }

            steamUser?.LogOff();
            Disconnect();

            var errorCode = logonResult.ToString();
            SendEvent("error", new { message = $"Logon failed: {logonResult}", errorCode });
            return new { success = false, error = $"Logon failed: {logonResult}", errorCode };
        }
        catch (TimeoutException)
        {
            SendEvent("error", new { message = "Login timeout", errorCode = "timeout" });
            return new { success = false, error = "Login timeout", errorCode = "timeout" };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Token Login Exception: {ex.Message}");
            var errorCode = GetErrorCodeFromException(ex);
            SendEvent("error", new { message = ex.Message, errorCode });
            return new { success = false, error = ex.Message, errorCode };
        }
        finally
        {
            Disconnect();
            _isLoginInProgress = false;
        }
    }

    /// <summary>
    /// 提交 Steam Guard 验证码
    /// </summary>
    public static void SubmitGuardCode(string code)
    {
        _authenticator?.SubmitCode(code);
    }

    /// <summary>
    /// 从设备确认切换到使用验证码
    /// </summary>
    public static void SwitchToUseCodeLogin()
    {
        _authenticator?.SwitchToUseCode();
    }

    /// <summary>
    /// 确认设备登录（在 Steam 手机 App 上点击确认）
    /// </summary>
    public static void ConfirmDeviceLogin()
    {
        _authenticator?.ConfirmDevice();
    }

    /// <summary>
    /// 取消当前登录
    /// </summary>
    public static void CancelLogin()
    {
        _cts?.Cancel();
        _authenticator?.Cancel();
        Disconnect();
        _isLoginInProgress = false;
    }

    /// <summary>
    /// 获取所有已登录的用户列表
    /// </summary>
    public static List<string> GetLoggedInUsers()
    {
        return _loggedInSessions.Keys.ToList();
    }

    /// <summary>
    /// 根据账户名获取已登录会话
    /// </summary>
    public static (SteamClient client, CallbackManager manager, CancellationTokenSource cts)? GetSessionByAccountName(string accountName)
    {
        if (_loggedInSessions.TryGetValue(accountName, out var session))
        {
            return session;
        }
        return null;
    }

    /// <summary>
    /// 根据账户名获取已登录会话（别名）
    /// </summary>
    public static (SteamClient client, CallbackManager manager, CancellationTokenSource cts)? GetSession(string accountName)
    {
        return GetSessionByAccountName(accountName);
    }

    /// <summary>
    /// 退出指定用户的登录
    /// </summary>
    public static async Task<bool> LogoutUser(string accountName)
    {
        // 标记为用户主动登出，避免触发自动重连
        if (_reconnectStates.TryGetValue(accountName, out var reconnectState))
        {
            reconnectState.IsUserInitiatedLogout = true;
            reconnectState.Timer?.Dispose();
            reconnectState.Timer = null;
        }

        try
        {
            if (!_loggedInSessions.TryGetValue(accountName, out var session))
            {
                return false;
            }

            var steamUser = session.client.GetHandler<SteamKitUser>();
            steamUser?.LogOff();

            // 等待一小段时间让 LogOff 完成
            await Task.Delay(500);

            await session.cts.CancelAsync();
            session.client.Disconnect();

            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} User {accountName} logged out");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Logout failed for {accountName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 退出所有已登录用户
    /// </summary>
    public static async Task LogoutAllUsers()
    {
        var users = _loggedInSessions.Keys.ToList();
        foreach (var user in users)
        {
            await LogoutUser(user);
        }
        Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} All users logged out");
    }

    /// <summary>
    /// 设置指定用户的 Persona 状态
    /// </summary>
    public static bool SetUserPersonaState(string accountName, int personaState)
    {
        try
        {
            if (!_loggedInSessions.TryGetValue(accountName, out var session))
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} User {accountName} not found in logged sessions");
                return false;
            }

            var state = (EPersonaState)personaState;
            session.client.GetHandler<SteamFriends>()?.SetPersonaState(state);
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Set persona state for {accountName} to {state}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} SetUserPersonaState failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取所有已保存的登录 Token（脱敏：不向渲染进程暴露 Token 内容，只返回元信息与过期时间）
    /// </summary>
    public static List<object> GetSavedTokens()
    {
        try
        {
            using var db = AppDbContext.Create();
            return db.SteamLoginTokenTable.AsNoTracking()
                .ToList()
                .Select(t => (object)new
                {
                    id = t.Id,
                    accountName = t.AccountName,
                    createdAt = t.CreatedAt,
                    expiresAt = GetJwtExpiry(TokenProtectionService.Unprotect(t.RefreshToken))
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} GetSavedTokens failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 将数据库中历史遗留的明文凭证升级为加密存储（应用启动时调用一次）
    /// </summary>
    public static async Task EncryptLegacyTokensAsync()
    {
        try
        {
            await using var db = AppDbContext.Create();
            var tokens = await db.SteamLoginTokenTable.ToListAsync();
            var upgraded = 0;

            foreach (var token in tokens)
            {
                if (TokenProtectionService.IsProtected(token.AccessToken)
                    && TokenProtectionService.IsProtected(token.RefreshToken)
                    && (token.GuardData == null || TokenProtectionService.IsProtected(token.GuardData)))
                {
                    continue;
                }

                token.AccessToken = TokenProtectionService.Protect(token.AccessToken) ?? token.AccessToken;
                token.RefreshToken = TokenProtectionService.Protect(token.RefreshToken) ?? token.RefreshToken;
                token.GuardData = TokenProtectionService.Protect(token.GuardData);
                upgraded++;
            }

            if (upgraded > 0)
            {
                await db.SaveChangesAsync();
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Encrypted {upgraded} legacy plaintext token(s)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} EncryptLegacyTokensAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除已保存的登录 Token
    /// </summary>
    public static async Task<bool> DeleteSavedToken(int id)
    {
        try
        {
            await using var db = AppDbContext.Create();
            var token = await db.SteamLoginTokenTable.FindAsync(id);
            if (token == null) return false;
            db.SteamLoginTokenTable.Remove(token);
            await db.SaveChangesAsync();
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Deleted token for {token.AccountName}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} DeleteSavedToken failed: {ex.Message}");
            return false;
        }
    }

    #region Private Methods

    /// <summary>
    /// 连接到 Steam 服务器
    /// </summary>
    private static async Task ConnectToSteam()
    {
        _steamClient = new SteamClient();
        // 注册自定义 Rich Presence 处理器（用于获取好友游戏中的富文本状态）
        _steamClient.AddHandler(new SteamRichPresenceHandler());
        // 注册直接从 PersonaState 报文读取 rich_presence 的处理器
        _steamClient.AddHandler(new PersonaStateRichPresenceHandler());
        // 注册好友 Steam 等级处理器
        _steamClient.AddHandler(new SteamLevelsHandler());
        _manager = new CallbackManager(_steamClient);
        _connectedTcs = new TaskCompletionSource<bool>();
        _cts = new CancellationTokenSource();

        _manager.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Connected to Steam");
            _connectedTcs.TrySetResult(true);
        });

        // 订阅账户信息回调，设置在线状态
        var steamFriends = _steamClient.GetHandler<SteamFriends>();
        _manager.Subscribe<SteamKitUser.AccountInfoCallback>(_ =>
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Account info received, setting persona state to LookingToPlay");
            steamFriends?.SetPersonaState(EPersonaState.LookingToPlay);
        });

        _manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Disconnected from Steam");
            if (!_connectedTcs.Task.IsCompleted)
            {
                _connectedTcs.TrySetResult(false);
            }
        });

        // 在后台线程运行回调循环，使用局部变量引用避免被 null 影响
        var localCts = _cts;
        var localManager = _manager;
        _ = Task.Run(() =>
        {
            while (localCts is { IsCancellationRequested: false })
            {
                try
                {
                    localManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                }
                catch
                {
                    break;
                }
            }
        });

        _steamClient.Connect();

        var connected = await _connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (!connected) throw new Exception("Failed to connect to Steam servers");
    }

    /// <summary>
    /// 断开与 Steam 的连接
    /// </summary>
    private static void Disconnect()
    {
        try
        {
            _cts?.Cancel();
            _steamClient?.Disconnect();
        }
        catch
        {
            // 忽略断开连接时的错误
        }
        finally
        {
            _steamClient = null;
            _manager = null;
            _cts = null;
            _authenticator = null;
            _connectedTcs = null;
        }
    }

    /// <summary>
    /// 为已登录会话设置回调，监听断线事件
    /// </summary>
    private static void SetupSessionCallbacks(string accountName, CallbackManager manager, SteamClient client)
    {
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} User {accountName} disconnected from Steam");

            // 从已登录列表中移除
            _loggedInSessions.Remove(accountName);

            // 清理好友数据
            SteamFriendsService.ClearUserFriendsData(accountName);

            // 通知前端
            SendEvent("userDisconnected", new { accountName });

            // 非用户主动登出时触发自动重连
            if (_reconnectStates.TryGetValue(accountName, out var state) && !state.IsUserInitiatedLogout)
            {
                ScheduleReconnect(accountName, state.RefreshToken, state.GuardData);
            }
        });
    }

    /// <summary>
    /// 保存账号的重连凭据（仅内存，不持久化）
    /// </summary>
    private static void StoreReconnectCredentials(string accountName, string refreshToken, string? guardData)
    {
        if (!_reconnectStates.TryGetValue(accountName, out var state))
        {
            state = new ReconnectState { AccountName = accountName };
            _reconnectStates[accountName] = state;
        }

        state.RefreshToken = refreshToken;
        state.GuardData = guardData;
        state.IsUserInitiatedLogout = false;
        // 用户重新登录成功，重置上一轮的重连计数与终止标记
        state.IsTerminated = false;
        state.RetryCount = 0;

        Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Stored reconnect credentials for {accountName}");
    }

    /// <summary>
    /// 停止某账号的自动重连，并通知前端需要用户手动重新登录。
    /// </summary>
    private static void TerminateReconnect(string accountName, ReconnectState state, string errorCode)
    {
        state.IsTerminated = true;
        state.IsReconnecting = false;
        state.Timer?.Dispose();
        state.Timer = null;
        // 不再保留内存中的凭证
        state.RefreshToken = string.Empty;

        SendEvent("reconnectFailed", new { accountName, errorCode });
    }

    /// <summary>
    /// 使用指数退避调度重连
    /// </summary>
    private static void ScheduleReconnect(string accountName, string refreshToken, string? guardData)
    {
        if (!_reconnectStates.TryGetValue(accountName, out var state))
        {
            state = new ReconnectState { AccountName = accountName };
            _reconnectStates[accountName] = state;
        }

        if (state.IsUserInitiatedLogout || state.IsReconnecting || state.IsTerminated)
        {
            return;
        }

        if (state.RetryCount >= MAX_RECONNECT_ATTEMPTS)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Giving up reconnect for {accountName} after {state.RetryCount} attempts");
            TerminateReconnect(accountName, state, "reconnectAttemptsExhausted");
            return;
        }

        state.RefreshToken = refreshToken;
        state.GuardData = guardData;
        state.IsReconnecting = true;
        state.RetryCount++;

        // 指数退避：5, 10, 20, 40, 60, 60...（最大 60 秒），叠加 ±20% 抖动，
        // 避免多个账号在同一时刻集体重连，形成对 CM 的脉冲式请求。
        var baseDelay = Math.Min(Math.Pow(2, state.RetryCount - 1) * 5, 60);
        double jitterFactor;
        lock (_jitter)
        {
            jitterFactor = 0.8 + _jitter.NextDouble() * 0.4;
        }
        var delaySeconds = baseDelay * jitterFactor;
        Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Scheduling reconnect for {accountName} in {delaySeconds:F0}s (attempt {state.RetryCount}/{MAX_RECONNECT_ATTEMPTS})");

        state.Timer?.Dispose();
        state.Timer = new System.Threading.Timer(
            _ => { _ = ReconnectAsync(accountName); },
            null,
            TimeSpan.FromSeconds(delaySeconds),
            System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 执行自动重连
    /// </summary>
    private static async Task ReconnectAsync(string accountName)
    {
        if (!_reconnectStates.TryGetValue(accountName, out var state) || state.IsUserInitiatedLogout || state.IsTerminated)
        {
            return;
        }

        // 系统层面没有可用网络时不必消耗一次重试机会，等下一次调度即可
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Network unavailable, deferring reconnect for {accountName}");
            state.IsReconnecting = false;
            if (state.RetryCount > 0) state.RetryCount--;
            ScheduleReconnect(accountName, state.RefreshToken, state.GuardData);
            return;
        }

        try
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Reconnecting {accountName}...");

            var client = new SteamClient();
            client.AddHandler(new SteamRichPresenceHandler());
            client.AddHandler(new PersonaStateRichPresenceHandler());
            client.AddHandler(new SteamLevelsHandler());

            var manager = new CallbackManager(client);
            var cts = new CancellationTokenSource();
            var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            manager.Subscribe<SteamClient.ConnectedCallback>(_ =>
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Reconnected to Steam for {accountName}");
                connectedTcs.TrySetResult(true);
            });

            manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Reconnect connection failed for {accountName}");
                connectedTcs.TrySetResult(false);
            });

            var steamFriends = client.GetHandler<SteamFriends>();
            manager.Subscribe<SteamKitUser.AccountInfoCallback>(_ =>
            {
                Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Reconnect account info received for {accountName}");
                steamFriends?.SetPersonaState(EPersonaState.LookingToPlay);
            });

            var localCts = cts;
            var localManager = manager;
            _ = Task.Run(() =>
            {
                while (localCts is { IsCancellationRequested: false })
                {
                    try
                    {
                        localManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                    }
                    catch
                    {
                        break;
                    }
                }
            });

            client.Connect();

            var connected = await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (!connected)
                throw new Exception("Failed to reconnect to Steam servers");

            if (state.IsUserInitiatedLogout)
            {
                cts.Cancel();
                client.Disconnect();
                return;
            }

            // Logon
            var logonTcs = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.Subscribe<SteamKitUser.LoggedOnCallback>(cb => logonTcs.TrySetResult(cb.Result));

            var steamUser = client.GetHandler<SteamKitUser>();
            steamUser?.LogOn(new SteamKitUser.LogOnDetails
            {
                Username = state.AccountName,
                AccessToken = state.RefreshToken,
                ShouldRememberPassword = true
            });

            var logonResult = await logonTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (logonResult != EResult.OK)
            {
                cts.Cancel();
                client.Disconnect();

                // 终止性错误（凭证失效、账号被封等）继续重试只是浪费资源，
                // 必须停下来让用户重新登录。
                if (IsTerminalLogonResult(logonResult))
                {
                    Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Reconnect for {accountName} failed permanently: {logonResult}");
                    state.IsReconnecting = false;
                    TerminateReconnect(accountName, state, logonResult.ToString());
                    return;
                }

                throw new Exception($"Reconnect logon failed for {accountName}: {logonResult}");
            }

            if (state.IsUserInitiatedLogout)
            {
                cts.Cancel();
                client.Disconnect();
                return;
            }

            _loggedInSessions[accountName] = (client, manager, cts);
            SetupSessionCallbacks(accountName, manager, client);

            state.IsReconnecting = false;
            state.RetryCount = 0;
            _reconnectStates[accountName] = state;

            SendEvent("userReconnected", new { accountName });
            _ = Task.Run(() => SteamFriendsService.GetFriendsForUser(accountName));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Reconnect failed for {accountName}: {ex.Message}");
            state.IsReconnecting = false;
            _reconnectStates[accountName] = state;
            ScheduleReconnect(accountName, state.RefreshToken, state.GuardData);
        }
    }

    /// <summary>
    /// 保存登录 Token 到数据库
    /// </summary>
    private static async Task SaveTokens(AuthPollResult pollResponse)
    {
        try
        {
            await using var db = AppDbContext.Create();

            // 按 AccountName 进行 Upsert
            var existing = await db.SteamLoginTokenTable
                .FirstOrDefaultAsync(t => t.AccountName == pollResponse.AccountName);

            // 加密后再持久化，防止数据库文件泄露导致凭证泄露
            var protectedAccessToken = TokenProtectionService.Protect(pollResponse.AccessToken) ?? string.Empty;
            var protectedRefreshToken = TokenProtectionService.Protect(pollResponse.RefreshToken) ?? string.Empty;
            var protectedGuardData = TokenProtectionService.Protect(pollResponse.NewGuardData);

            if (existing != null)
            {
                existing.AccessToken = protectedAccessToken;
                existing.RefreshToken = protectedRefreshToken;
                existing.GuardData = protectedGuardData ?? existing.GuardData;
                existing.CreatedAt = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            else
            {
                db.SteamLoginTokenTable.Add(new SteamLoginToken
                {
                    AccountName = pollResponse.AccountName,
                    AccessToken = protectedAccessToken,
                    RefreshToken = protectedRefreshToken,
                    GuardData = protectedGuardData,
                    CreatedAt = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }

            await db.SaveChangesAsync();
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Token saved for {pollResponse.AccountName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Failed to save token: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析 JWT Token 的过期时间（Unix 秒），解析失败返回 null
    /// </summary>
    private static long? GetJwtExpiry(string? jwt)
    {
        try
        {
            if (string.IsNullOrEmpty(jwt)) return null;
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("exp", out var exp) ? exp.GetInt64() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从异常推断错误码（供前端本地化展示）
    /// </summary>
    private static string GetErrorCodeFromException(Exception ex)
    {
        return ex switch
        {
            TimeoutException => "timeout",
            HttpRequestException => "networkError",
            _ when ex.Message.Contains("connect", StringComparison.OrdinalIgnoreCase) => "connectionFailed",
            _ => "unknown"
        };
    }

    /// <summary>
    /// 生成二维码 Base64 PNG 图片
    /// </summary>
    private static string GenerateQrCodeBase64(string url)
    {
        using var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
        var qrCode = new PngByteQRCode(qrCodeData);
        var pngBytes = qrCode.GetGraphic(10);
        return $"data:image/png;base64,{Convert.ToBase64String(pngBytes)}";
    }

    /// <summary>
    /// 向前端发送登录事件
    /// </summary>
    private static void SendEvent(string type, object? data = null)
    {
        try
        {
            var mainWindow = Program.ElectronMainWindow;
            if (mainWindow == null) return;

            var eventData = new { type, data };
            Electron.IpcMain.Send(mainWindow, "steamLogin:event", eventData);
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Event: {type}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Failed to send event: {ex.Message}");
        }
    }

    #endregion

    /// <summary>
    /// IPC 认证器，用于将 Steam Guard 验证码请求转发到前端
    /// </summary>
    private class IpcAuthenticator : IAuthenticator
    {
        private TaskCompletionSource<string>? _codeTcs;
        private TaskCompletionSource<bool>? _useCodeTcs;
        private readonly CancellationTokenSource _cancelCodeTcs = new();
        private readonly CancellationTokenSource _cancelUseCodeTcs = new();

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            SendEvent("guardCodeNeeded", new { guardType = "device", previousCodeWasIncorrect });
            _codeTcs = new TaskCompletionSource<string>();
            _cancelCodeTcs.Token.Register(() => _codeTcs.TrySetCanceled());
            return _codeTcs.Task;
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            SendEvent("guardCodeNeeded", new { guardType = "email", email, previousCodeWasIncorrect });
            _codeTcs = new TaskCompletionSource<string>();
            _cancelCodeTcs.Token.Register(() => _codeTcs.TrySetCanceled());
            return _codeTcs.Task;
        }

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            SendEvent("deviceConfirmationNeeded");
            _useCodeTcs = new TaskCompletionSource<bool>();
            _cancelUseCodeTcs.Token.Register(() => _useCodeTcs.TrySetCanceled());
            return _useCodeTcs.Task;
        }

        public void SubmitCode(string code)
        {
            _codeTcs?.TrySetResult(code);
        }

        public void SwitchToUseCode()
        {
            _useCodeTcs?.TrySetResult(false);
        }

        public void ConfirmDevice()
        {
            _useCodeTcs?.TrySetResult(true);
        }

        public void Cancel()
        {
            _cancelCodeTcs.Cancel();
            _cancelUseCodeTcs.Cancel();
            _codeTcs?.TrySetCanceled();
            _useCodeTcs?.TrySetCanceled();
        }
    }
}
