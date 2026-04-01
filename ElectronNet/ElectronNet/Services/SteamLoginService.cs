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

    /// <summary>
    /// 使用账号密码登录
    /// </summary>
    public static async Task<object> LoginWithCredentials(string username, string password, bool rememberMe)
    {
        if (_isLoginInProgress)
            return new { success = false, error = "Login already in progress" };

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
                guardData = existing?.GuardData;
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
        catch (TaskCanceledException)
        {
            SendEvent("cancelled");
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = "Login cancelled" };
        }
        catch (AuthenticationException ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} AuthenticationException: {ex.Message}");
            SendEvent("error", new { message = ex.Message });
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = ex.Message };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Exception: {ex.Message}");
            SendEvent("error", new { message = ex.Message });
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = ex.Message };
        }
    }

    /// <summary>
    /// 使用二维码登录
    /// </summary>
    public static async Task<object> LoginWithQR(bool rememberMe)
    {
        if (_isLoginInProgress)
            return new { success = false, error = "Login already in progress" };

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
        catch (TaskCanceledException)
        {
            SendEvent("cancelled");
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = "Login cancelled" };
        }
        catch (OperationCanceledException)
        {
            SendEvent("cancelled");
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = "Login cancelled" };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} QR Login Exception: {ex.Message}");
            SendEvent("error", new { message = ex.Message });
            Disconnect();
            _isLoginInProgress = false;
            return new { success = false, error = ex.Message };
        }
    }

    /// <summary>
    /// 使用已保存的 Token 登录（免登录）
    /// </summary>
    public static async Task<object> LoginWithToken(int tokenId)
    {
        if (_isLoginInProgress)
            return new { success = false, error = "Login already in progress" };

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
                return new { success = false, error = "Token not found" };

            SendEvent("connecting");
            await ConnectToSteam();
            SendEvent("authenticating");

            var steamUser = _steamClient!.GetHandler<SteamKitUser>();

            var logonTcs = new TaskCompletionSource<object>();

            _manager!.Subscribe<SteamKitUser.LoggedOnCallback>(cb =>
            {
                if (cb.Result == EResult.OK)
                {
                    logonTcs.TrySetResult(new { success = true, accountName = savedToken.AccountName });
                }
                else
                {
                    logonTcs.TrySetResult(new { success = false, error = $"Logon failed: {cb.Result}" });
                }
            });

            steamUser?.LogOn(new SteamKitUser.LogOnDetails
            {
                Username = savedToken.AccountName,
                AccessToken = savedToken.RefreshToken,
                ShouldRememberPassword = true
            });

            var result = await logonTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // 不再立即 LogOff，保持登录状态
            var resultObj = result as dynamic;
            if (resultObj.success == true)
            {
                // 保存会话到已登录列表
                _loggedInSessions[savedToken.AccountName] = (_steamClient, _manager, _cts!);

                // 为该会话设置回调，监听断线事件
                SetupSessionCallbacks(savedToken.AccountName, _manager!, _steamClient!);

                _steamClient = null;
                _manager = null;
                _cts = null;
            }
            else
            {
                steamUser?.LogOff();
                Disconnect();
            }

            SendEvent("success", new { accountName = savedToken.AccountName });
            return result;
        }
        catch (TimeoutException)
        {
            SendEvent("error", new { message = "Login timeout" });
            return new { success = false, error = "Login timeout" };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} Token Login Exception: {ex.Message}");
            SendEvent("error", new { message = ex.Message });
            return new { success = false, error = ex.Message };
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
    /// 退出指定用户的登录
    /// </summary>
    public static async Task<bool> LogoutUser(string accountName)
    {
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

            _loggedInSessions.Remove(accountName);
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
    /// 获取所有已保存的登录 Token
    /// </summary>
    public static List<SteamLoginToken> GetSavedTokens()
    {
        try
        {
            using var db = AppDbContext.Create();
            return db.SteamLoginTokenTable.AsNoTracking().ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_LOGIN} GetSavedTokens failed: {ex.Message}");
            return [];
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
                    localManager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
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

            // 通知前端
            SendEvent("userDisconnected", new { accountName });
        });
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

            if (existing != null)
            {
                existing.AccessToken = pollResponse.AccessToken;
                existing.RefreshToken = pollResponse.RefreshToken;
                existing.GuardData = pollResponse.NewGuardData ?? existing.GuardData;
                existing.CreatedAt = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            else
            {
                db.SteamLoginTokenTable.Add(new SteamLoginToken
                {
                    AccountName = pollResponse.AccountName,
                    AccessToken = pollResponse.AccessToken,
                    RefreshToken = pollResponse.RefreshToken,
                    GuardData = pollResponse.NewGuardData,
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
