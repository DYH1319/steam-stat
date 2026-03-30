using System.Text.Json;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Discovery;
using SteamKit2.Internal;

namespace ElectronNet.Tests.SteamKit2;

[TestFixture]
public class SteamUserTests
{
    [Test]
    public void AuthenticationLogOn()
    {
        // var tcs = new TaskCompletionSource<SteamID>();

        // create our steamclient instance
        var steamClient = new SteamClient();
        // create the callback manager which will route callbacks to function calls
        var manager = new CallbackManager(steamClient);
        // get the steamuser handler, which is used for logging on after successfully connecting
        var steamUser = steamClient.GetHandler<SteamUser>();

        // register a few callbacks we're interested in
        // these are registered upon creation to a callback manager, which will then route the callbacks
        // to the functions specified
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        // manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        // {
        //     if (cb.Result == EResult.OK)
        //     {
        //         TestContext.Progress.WriteLine($"LoggedOn, SteamID: {cb.ClientSteamID?.ConvertToUInt64()}");
        //         tcs.SetResult(cb.ClientSteamID ?? new SteamID("-1"));
        //     }
        //     else
        //     {
        //         tcs.SetException(new Exception(cb.Result.ToString()));
        //     }
        // });

        var isRunning = true;

        TestContext.Progress.WriteLine("Connecting to Steam...");

        // initiate the connection
        steamClient.Connect();

        // create our callback handling loop
        while (isRunning)
        {
            // in order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        // 跑回调循环（关键）
        // _ = Task.Run(() =>
        // {
        //     while (!tcs.Task.IsCompleted)
        //     {
        //         manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        //     }
        // });
        // var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Assert.IsNotNull(result);

        return;

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            try
            {
                TestContext.Progress.WriteLine("Connected to Steam! Logging in '{0}'...", steamUser);

                const bool shouldRememberPassword = true;
                string? previouslyStoredGuardData = null;

                // Begin authenticating via credentials
                var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = "xxx",
                    Password = "xxxxxx",
                    IsPersistentSession = shouldRememberPassword,

                    // See NewGuardData comment below
                    GuardData = previouslyStoredGuardData,

                    // <see cref="UserConsoleAuthenticator"/> is the default authenticator implemention provided by SteamKit
                    // for ease of use which blocks the thread and asks for user input to enter the code.
                    // However, if you require special handling (e.g. you have the TOTP secret and can generate codes on the fly),
                    // you can implement your own <see cref="SteamKit2.Authentication.IAuthenticator"/>.
                    Authenticator = new UserConsoleAuthenticator()
                });

                // Starting polling Steam for authentication response
                var pollResponse = await authSession.PollingWaitForResultAsync();

                if (pollResponse.NewGuardData != null)
                {
                    // When using certain two factor methods (such as email 2fa), guard data may be provided by Steam
                    // for use in future authentication sessions to avoid triggering 2FA again (this works similarly to the old sentry file system).
                    // Do note that this guard data is also a JWT token and has an expiration date.
                    previouslyStoredGuardData = pollResponse.NewGuardData;
                }

                // Logon to Steam with the access token we have received
                // Note that we are using RefreshToken for logging on here
                steamUser?.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken,
                    ShouldRememberPassword = shouldRememberPassword
                });

                // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
            }
            catch (Exception e)
            {
                TestContext.Progress.WriteLine(e);
            }
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            TestContext.Progress.WriteLine("Disconnected from Steam");

            isRunning = false;
        }

        void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                TestContext.Progress.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                isRunning = false;
                return;
            }

            TestContext.Progress.WriteLine("Successfully logged on!");

            // at this point, we'd be able to perform actions on Steam

            // for this sample we'll just log off
            steamUser?.LogOff();
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            TestContext.Progress.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        // This is simply showing how to parse JWT, this is not required to log in to Steam
        void ParseJsonWebToken(string token, string name)
        {
            // You can use a JWT library to do the parsing for you
            var tokenComponents = token.Split('.');

            // Fix up base64url to normal base64
            var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

            if (base64.Length % 4 != 0)
            {
                base64 += new string('=', 4 - base64.Length % 4);
            }

            var payloadBytes = Convert.FromBase64String(base64);

            // Payload can be parsed as JSON, and then fields such expiration date, scope, etc. can be accessed
            var payload = JsonDocument.Parse(payloadBytes);

            // For brevity, we will simply output formatted JSON to console
            var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            TestContext.Progress.WriteLine($"{name}: {formatted}");
            TestContext.Progress.WriteLine();
        }
    }

    [Test]
    public void AuthenticationWithQrCodeLogOn()
    {
        // create our steamclient instance
        var steamClient = new SteamClient();
        // create the callback manager which will route callbacks to function calls
        var manager = new CallbackManager(steamClient);
        // get the steamuser handler, which is used for logging on after successfully connecting
        var steamUser = steamClient.GetHandler<SteamUser>();

        // register a few callbacks we're interested in
        // these are registered upon creation to a callback manager, which will then route the callbacks
        // to the functions specified
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        var isRunning = true;

        TestContext.Progress.WriteLine("Connecting to Steam...");

        // initiate the connection
        steamClient.Connect();

        // create our callback handling loop
        while (isRunning)
        {
            // in order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        return;

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            try
            {
                TestContext.Progress.WriteLine("Connected to Steam! Logging in '{0}'...", steamUser);

                // Start an authentication session by requesting a link
                var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());

                // Steam will periodically refresh the challenge url, this callback allows you to draw a new qr code
                authSession.ChallengeURLChanged = () =>
                {
                    TestContext.Progress.WriteLine();
                    TestContext.Progress.WriteLine("Steam has refreshed the challenge url");

                    DrawQRCode(authSession);
                };

                // Draw current qr right away
                DrawQRCode(authSession);

                // Starting polling Steam for authentication response
                // This response is later used to log on to Steam after connecting
                var pollResponse = await authSession.PollingWaitForResultAsync();

                await TestContext.Progress.WriteLineAsync($"Logging in as '{pollResponse.AccountName}'...");

                // Logon to Steam with the access token we have received
                steamUser?.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken
                });

                // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
            }
            catch (Exception e)
            {
                TestContext.Progress.WriteLine(e);
            }
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            TestContext.Progress.WriteLine("Disconnected from Steam");

            isRunning = false;
        }

        void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                TestContext.Progress.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                isRunning = false;
                return;
            }

            TestContext.Progress.WriteLine("Successfully logged on!");

            // at this point, we'd be able to perform actions on Steam

            // for this sample we'll just log off
            steamUser?.LogOff();
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            TestContext.Progress.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        // This is simply showing how to parse JWT, this is not required to log in to Steam
        void ParseJsonWebToken(string token, string name)
        {
            // You can use a JWT library to do the parsing for you
            var tokenComponents = token.Split('.');

            // Fix up base64url to normal base64
            var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

            if (base64.Length % 4 != 0)
            {
                base64 += new string('=', 4 - base64.Length % 4);
            }

            var payloadBytes = Convert.FromBase64String(base64);

            // Payload can be parsed as JSON, and then fields such expiration date, scope, etc. can be accessed
            var payload = JsonDocument.Parse(payloadBytes);

            // For brevity, we will simply output formatted JSON to console
            var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            TestContext.Progress.WriteLine($"{name}: {formatted}");
            TestContext.Progress.WriteLine();
        }

        void DrawQRCode(QrAuthSession authSession)
        {
            TestContext.Progress.WriteLine($"Challenge URL: {authSession.ChallengeURL}");
            TestContext.Progress.WriteLine();

            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetGraphic(1, drawQuietZones: false);

            TestContext.Progress.WriteLine("Use the Steam Mobile App to sign in via QR code:");
            TestContext.Progress.WriteLine(qrCodeAsAsciiArt);
        }
    }

    [Test]
    public void WebCookie()
    {
        var isRunning = true;
        var accessToken = string.Empty;
        var refreshToken = string.Empty;

        // create our steamclient instance
        var steamClient = new SteamClient();
        // create the callback manager which will route callbacks to function calls
        var manager = new CallbackManager(steamClient);
        // get the steamuser handler, which is used for logging on after successfully connecting
        var steamUser = steamClient.GetHandler<SteamUser>();

        // register a few callbacks we're interested in
        // these are registered upon creation to a callback manager, which will then route the callbacks
        // to the functions specified
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        TestContext.Progress.WriteLine("Connecting to Steam...");

        // initiate the connection
        steamClient.Connect();

        // create our callback handling loop
        while (isRunning)
        {
            // in order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        return;

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            try
            {
                TestContext.Progress.WriteLine("Connected to Steam! Logging in '{0}'...", steamUser);

                // Start an authentication session by requesting a link
                var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());

                // Steam will periodically refresh the challenge url, this callback allows you to draw a new qr code
                authSession.ChallengeURLChanged = () =>
                {
                    TestContext.Progress.WriteLine();
                    TestContext.Progress.WriteLine("Steam has refreshed the challenge url");

                    DrawQRCode(authSession);
                };

                // Draw current qr right away
                DrawQRCode(authSession);

                // Starting polling Steam for authentication response
                // This response is later used to log on to Steam after connecting
                var pollResponse = await authSession.PollingWaitForResultAsync();

                await TestContext.Progress.WriteLineAsync($"Logging in as '{pollResponse.AccountName}'...");

                // Logon to Steam with the access token we have received
                steamUser?.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken
                });

                // AccessToken can be used as the steamLoginSecure cookie
                // RefreshToken is required to generate new access tokens
                accessToken = pollResponse.AccessToken;
                refreshToken = pollResponse.RefreshToken;

                // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
            }
            catch (Exception e)
            {
                TestContext.Progress.WriteLine(e);
            }
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            TestContext.Progress.WriteLine("Disconnected from Steam");

            isRunning = false;
        }

        async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            try
            {
                if (callback.Result != EResult.OK)
                {
                    TestContext.Progress.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                    isRunning = false;
                    return;
                }

                await TestContext.Progress.WriteLineAsync("Successfully logged on!");

                // at this point, we'd be able to perform actions on Steam
                // This is how you concatenate the cookie, you can set it on the Steam domains, and it should work
                // but actual usage of this will be left as an exercise for the reader
                var steamLoginSecure = $"{callback.ClientSteamID}||{accessToken}";

                await TestContext.Progress.WriteLineAsync($"Steam Login Secure: {steamLoginSecure}");
                await TestContext.Progress.WriteLineAsync($"Steam Access Token: {accessToken}");
                await TestContext.Progress.WriteLineAsync($"Steam Refresh Token: {refreshToken}");

                // The access token expires in 24 hours (at the time of writing) so you will have to renew it.
                // Parse this token with a JWT library to get the expiration date and set up a timer to renew it.
                // To renew you will have to call this:
                // When allowRenewal is set to true, Steam may return new RefreshToken
                var newTokens = await steamClient.Authentication.GenerateAccessTokenForAppAsync(callback.ClientSteamID!, refreshToken, allowRenewal: false);

                accessToken = newTokens.AccessToken;

                if (!string.IsNullOrEmpty(newTokens.RefreshToken))
                {
                    refreshToken = newTokens.RefreshToken;
                }

                // Do not forget to update steamLoginSecure with the new accessToken!
                steamLoginSecure = $"{callback.ClientSteamID}||{accessToken}";

                await TestContext.Progress.WriteLineAsync($"Steam Login Secure: {steamLoginSecure}");
                await TestContext.Progress.WriteLineAsync($"Steam Access Token: {accessToken}");
                await TestContext.Progress.WriteLineAsync($"Steam Refresh Token: {refreshToken}");

                // for this sample we'll just log off
                steamUser?.LogOff();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            TestContext.Progress.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        // This is simply showing how to parse JWT, this is not required to log in to Steam
        void ParseJsonWebToken(string token, string name)
        {
            // You can use a JWT library to do the parsing for you
            var tokenComponents = token.Split('.');

            // Fix up base64url to normal base64
            var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

            if (base64.Length % 4 != 0)
            {
                base64 += new string('=', 4 - base64.Length % 4);
            }

            var payloadBytes = Convert.FromBase64String(base64);

            // Payload can be parsed as JSON, and then fields such expiration date, scope, etc. can be accessed
            var payload = JsonDocument.Parse(payloadBytes);

            // For brevity, we will simply output formatted JSON to console
            var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            TestContext.Progress.WriteLine($"{name}: {formatted}");
            TestContext.Progress.WriteLine();
        }

        void DrawQRCode(QrAuthSession authSession)
        {
            TestContext.Progress.WriteLine($"Challenge URL: {authSession.ChallengeURL}");
            TestContext.Progress.WriteLine();

            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetGraphic(1, drawQuietZones: false);

            TestContext.Progress.WriteLine("Use the Steam Mobile App to sign in via QR code:");
            TestContext.Progress.WriteLine(qrCodeAsAsciiArt);
        }
    }

    [Test]
    public void ServerList()
    {
        var cellid = 0u;

        // if we've previously connected and saved our cellid, load it.
        if (File.Exists("cellid.txt"))
        {
            if (!uint.TryParse(File.ReadAllText("cellid.txt"), out cellid))
            {
                TestContext.Progress.WriteLine("Error parsing cellid from cellid.txt. Continuing with cellid 0.");
                cellid = 0;
            }
            else
            {
                TestContext.Progress.WriteLine($"Using persisted cell ID {cellid}");
            }
        }

        var configuration = SteamConfiguration.Create(b => b
            .WithCellID(cellid)
            .WithServerListProvider(new FileStorageServerListProvider("servers_list.bin"))
        );

        // create our steamclient instance
        var steamClient = new SteamClient(configuration);
        // create the callback manager which will route callbacks to function calls
        var manager = new CallbackManager(steamClient);
        // get the steamuser handler, which is used for logging on after successfully connecting
        var steamUser = steamClient.GetHandler<SteamUser>();

        // register a few callbacks we're interested in
        // these are registered upon creation to a callback manager, which will then route the callbacks
        // to the functions specified
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;

            TestContext.Progress.WriteLine($"Received {e.SpecialKey}, disconnecting...");
            steamUser?.LogOff();
        };

        var isRunning = true;

        TestContext.Progress.WriteLine("Connecting to Steam...");

        // initiate the connection
        steamClient.Connect();

        // create our callback handling loop
        while (isRunning)
        {
            // in order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        return;

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            try
            {
                TestContext.Progress.WriteLine("Connected to Steam! Logging in '{0}'...", steamUser);

                // Start an authentication session by requesting a link
                var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());

                // Steam will periodically refresh the challenge url, this callback allows you to draw a new qr code
                authSession.ChallengeURLChanged = () =>
                {
                    TestContext.Progress.WriteLine();
                    TestContext.Progress.WriteLine("Steam has refreshed the challenge url");

                    DrawQRCode(authSession);
                };

                // Draw current qr right away
                DrawQRCode(authSession);

                // Starting polling Steam for authentication response
                // This response is later used to log on to Steam after connecting
                var pollResponse = await authSession.PollingWaitForResultAsync();

                await TestContext.Progress.WriteLineAsync($"Logging in as '{pollResponse.AccountName}'...");

                // Logon to Steam with the access token we have received
                steamUser?.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken
                });

                // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
            }
            catch (Exception e)
            {
                TestContext.Progress.WriteLine(e);
            }
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            TestContext.Progress.WriteLine("Disconnected from Steam");

            isRunning = false;
        }

        void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                TestContext.Progress.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                isRunning = false;
                return;
            }

            TestContext.Progress.WriteLine("Successfully logged on!");

            // save the current cellid somewhere. if we lose our saved server list, we can use this when retrieving
            // servers from the Steam Directory.
            File.WriteAllText("cellid.txt", callback.CellID.ToString());
            TestContext.Progress.WriteLine("Successfully logged on! Press Ctrl+C to log off...");
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            TestContext.Progress.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        // This is simply showing how to parse JWT, this is not required to log in to Steam
        void ParseJsonWebToken(string token, string name)
        {
            // You can use a JWT library to do the parsing for you
            var tokenComponents = token.Split('.');

            // Fix up base64url to normal base64
            var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

            if (base64.Length % 4 != 0)
            {
                base64 += new string('=', 4 - base64.Length % 4);
            }

            var payloadBytes = Convert.FromBase64String(base64);

            // Payload can be parsed as JSON, and then fields such expiration date, scope, etc. can be accessed
            var payload = JsonDocument.Parse(payloadBytes);

            // For brevity, we will simply output formatted JSON to console
            var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            TestContext.Progress.WriteLine($"{name}: {formatted}");
            TestContext.Progress.WriteLine();
        }

        void DrawQRCode(QrAuthSession authSession)
        {
            TestContext.Progress.WriteLine($"Challenge URL: {authSession.ChallengeURL}");
            TestContext.Progress.WriteLine();

            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetGraphic(1, drawQuietZones: false);

            TestContext.Progress.WriteLine("Use the Steam Mobile App to sign in via QR code:");
            TestContext.Progress.WriteLine(qrCodeAsAsciiArt);
        }
    }

    [Test]
    public void Extending()
    {
        // create our steamclient instance
        var steamClient = new SteamClient();

        // add our custom handler to our steamclient
        steamClient.AddHandler(new MyHandler());

        // create the callback manager which will route callbacks to function calls
        var manager = new CallbackManager(steamClient);
        // get the steamuser handler, which is used for logging on after successfully connecting
        var steamUser = steamClient.GetHandler<SteamUser>();

        // register a few callbacks we're interested in
        // these are registered upon creation to a callback manager, which will then route the callbacks
        // to the functions specified
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        // handle our own custom callback
        manager.Subscribe<MyHandler.MyCallback>(OnMyCallback);

        var isRunning = true;

        TestContext.Progress.WriteLine("Connecting to Steam...");

        // initiate the connection
        steamClient.Connect();

        // create our callback handling loop
        while (isRunning)
        {
            // in order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        return;

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            try
            {
                TestContext.Progress.WriteLine("Connected to Steam! Logging in '{0}'...", steamUser);

                // Start an authentication session by requesting a link
                var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());

                // Steam will periodically refresh the challenge url, this callback allows you to draw a new qr code
                authSession.ChallengeURLChanged = () =>
                {
                    TestContext.Progress.WriteLine();
                    TestContext.Progress.WriteLine("Steam has refreshed the challenge url");

                    DrawQRCode(authSession);
                };

                // Draw current qr right away
                DrawQRCode(authSession);

                // Starting polling Steam for authentication response
                // This response is later used to log on to Steam after connecting
                var pollResponse = await authSession.PollingWaitForResultAsync();

                await TestContext.Progress.WriteLineAsync($"Logging in as '{pollResponse.AccountName}'...");

                // Logon to Steam with the access token we have received
                steamUser?.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken
                });

                // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
            }
            catch (Exception e)
            {
                TestContext.Progress.WriteLine(e);
            }
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            TestContext.Progress.WriteLine("Disconnected from Steam");

            isRunning = false;
        }

        void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                TestContext.Progress.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                isRunning = false;
                return;
            }

            TestContext.Progress.WriteLine("Successfully logged on!");

            // at this point, we'd be able to perform actions on Steam

            // for this sample we'll just log off
            steamUser?.LogOff();
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            TestContext.Progress.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        void OnMyCallback(MyHandler.MyCallback callback)
        {
            // this will be called when our custom callback gets posted
            TestContext.Progress.WriteLine("OnMyCallback: {0}", callback.Result);
        }

        // This is simply showing how to parse JWT, this is not required to log in to Steam
        void ParseJsonWebToken(string token, string name)
        {
            // You can use a JWT library to do the parsing for you
            var tokenComponents = token.Split('.');

            // Fix up base64url to normal base64
            var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

            if (base64.Length % 4 != 0)
            {
                base64 += new string('=', 4 - base64.Length % 4);
            }

            var payloadBytes = Convert.FromBase64String(base64);

            // Payload can be parsed as JSON, and then fields such expiration date, scope, etc. can be accessed
            var payload = JsonDocument.Parse(payloadBytes);

            // For brevity, we will simply output formatted JSON to console
            var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            TestContext.Progress.WriteLine($"{name}: {formatted}");
            TestContext.Progress.WriteLine();
        }

        void DrawQRCode(QrAuthSession authSession)
        {
            TestContext.Progress.WriteLine($"Challenge URL: {authSession.ChallengeURL}");
            TestContext.Progress.WriteLine();

            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetGraphic(1, drawQuietZones: false);

            TestContext.Progress.WriteLine("Use the Steam Mobile App to sign in via QR code:");
            TestContext.Progress.WriteLine(qrCodeAsAsciiArt);
        }
    }

    [Test]
    public void DebugLogSample()
    {
        // install our debug listeners for this example

        // install an instance of our custom listener
        // see the bottom of this file for how it is defined.
        DebugLog.AddListener(new MyListener());

        // install a listener as an anonymous method
        // this call is commented as it would be redundant to install a second listener that also displays messages to the console
        // DebugLog.AddListener((category, msg) => TestContext.Progress.WriteLine("AnonymousMethod - {0}: {1}", category, msg));

        // Enable DebugLog in release builds
        DebugLog.Enabled = true;

        // create our steamclient instance
        var steamClient = new SteamClient();

        // uncomment this if you'd like to dump raw sent and received packets
        // that can be opened for analysis in NetHookAnalyzer
        // NOTE: dumps may contain sensitive data (such as your Steam password)
        steamClient.DebugNetworkListener = new NetHookNetworkListener();

        // create the callback manager which will route callbacks to function calls
        var manager = new CallbackManager(steamClient);
        // get the steamuser handler, which is used for logging on after successfully connecting
        var steamUser = steamClient.GetHandler<SteamUser>();

        // register a few callbacks we're interested in
        // these are registered upon creation to a callback manager, which will then route the callbacks
        // to the functions specified
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        var isRunning = true;

        TestContext.Progress.WriteLine("Connecting to Steam...");

        // initiate the connection
        steamClient.Connect();

        // create our callback handling loop
        while (isRunning)
        {
            // in order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        return;

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            try
            {
                TestContext.Progress.WriteLine("Connected to Steam! Logging in '{0}'...", steamUser);

                // Start an authentication session by requesting a link
                var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());

                // Steam will periodically refresh the challenge url, this callback allows you to draw a new qr code
                authSession.ChallengeURLChanged = () =>
                {
                    TestContext.Progress.WriteLine();
                    TestContext.Progress.WriteLine("Steam has refreshed the challenge url");

                    DrawQRCode(authSession);
                };

                // Draw current qr right away
                DrawQRCode(authSession);

                // Starting polling Steam for authentication response
                // This response is later used to log on to Steam after connecting
                var pollResponse = await authSession.PollingWaitForResultAsync();

                await TestContext.Progress.WriteLineAsync($"Logging in as '{pollResponse.AccountName}'...");

                // Logon to Steam with the access token we have received
                steamUser?.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken
                });

                // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
            }
            catch (Exception e)
            {
                TestContext.Progress.WriteLine(e);
            }
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            TestContext.Progress.WriteLine("Disconnected from Steam");

            isRunning = false;
        }

        void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                TestContext.Progress.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                isRunning = false;
                return;
            }

            TestContext.Progress.WriteLine("Successfully logged on!");

            // at this point, we'd be able to perform actions on Steam

            // for this sample we'll just log off
            steamUser?.LogOff();
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            TestContext.Progress.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        // This is simply showing how to parse JWT, this is not required to log in to Steam
        void ParseJsonWebToken(string token, string name)
        {
            // You can use a JWT library to do the parsing for you
            var tokenComponents = token.Split('.');

            // Fix up base64url to normal base64
            var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

            if (base64.Length % 4 != 0)
            {
                base64 += new string('=', 4 - base64.Length % 4);
            }

            var payloadBytes = Convert.FromBase64String(base64);

            // Payload can be parsed as JSON, and then fields such expiration date, scope, etc. can be accessed
            var payload = JsonDocument.Parse(payloadBytes);

            // For brevity, we will simply output formatted JSON to console
            var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            TestContext.Progress.WriteLine($"{name}: {formatted}");
            TestContext.Progress.WriteLine();
        }

        void DrawQRCode(QrAuthSession authSession)
        {
            TestContext.Progress.WriteLine($"Challenge URL: {authSession.ChallengeURL}");
            TestContext.Progress.WriteLine();

            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetGraphic(1, drawQuietZones: false);

            TestContext.Progress.WriteLine("Use the Steam Mobile App to sign in via QR code:");
            TestContext.Progress.WriteLine(qrCodeAsAsciiArt);
        }
    }

    [Test]
    public void AsyncJobs()
    {
        // create our steamclient instance
        var steamClient = new SteamClient();
        // create the callback manager which will route callbacks to function calls
        var manager = new CallbackManager(steamClient);
        // get the steamuser handler, which is used for logging on after successfully connecting
        var steamUser = steamClient.GetHandler<SteamUser>();
        // get our steamapps handler, we'll use this as an example of how async jobs can be handled
        var steamApps = steamClient.GetHandler<SteamApps>();
        if (steamApps == null) return;

        // register a few callbacks we're interested in
        // these are registered upon creation to a callback manager, which will then route the callbacks
        // to the functions specified
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        // notice that we're not subscribing to the SteamApps.PICSProductInfoCallback callback here (or other SteamApps callbacks)
        // since this sample is using the async job directly, we no longer need to subscribe to the callback.
        // however, if we still wish to use callbacks (or have existing code which subscribes to callbacks, they will
        // continue to operate alongside direct async job handling. (i.e.: steamclient will still post callbacks for
        // any async jobs that are completed)

        var isRunning = true;

        TestContext.Progress.WriteLine("Connecting to Steam...");

        // initiate the connection
        steamClient.Connect();

        // create our callback handling loop
        while (isRunning)
        {
            // in order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        return;

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            try
            {
                TestContext.Progress.WriteLine("Connected to Steam! Logging in '{0}'...", steamUser);

                // Start an authentication session by requesting a link
                var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());

                // Steam will periodically refresh the challenge url, this callback allows you to draw a new qr code
                authSession.ChallengeURLChanged = () =>
                {
                    TestContext.Progress.WriteLine();
                    TestContext.Progress.WriteLine("Steam has refreshed the challenge url");

                    DrawQRCode(authSession);
                };

                // Draw current qr right away
                DrawQRCode(authSession);

                // Starting polling Steam for authentication response
                // This response is later used to log on to Steam after connecting
                var pollResponse = await authSession.PollingWaitForResultAsync();

                await TestContext.Progress.WriteLineAsync($"Logging in as '{pollResponse.AccountName}'...");

                // Logon to Steam with the access token we have received
                steamUser?.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken
                });

                // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
            }
            catch (Exception e)
            {
                TestContext.Progress.WriteLine(e);
            }
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            TestContext.Progress.WriteLine("Disconnected from Steam");

            isRunning = false;
        }

        async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            try
            {
                if (callback.Result != EResult.OK)
                {
                    TestContext.Progress.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                    isRunning = false;
                    return;
                }

                await TestContext.Progress.WriteLineAsync("Successfully logged on!");

                // at this point, we'd be able to perform actions on Steam

                // in this sample, we'll simply do a few async requests to acquire information about Counter-Strike: Global Offensive

                const uint appid = 730;
                const uint depotid = 731;

                // first, we'll request a depot decryption key for CSGO's common files depot (731)
                var depotJob = steamApps.GetDepotDecryptionKey(depotid, appid);

                // at this point, this request is now in-flight to the steam server, so we'll use te async/await pattern to wait for a response
                // the await pattern allows this code to resume once the Steam servers have replied to the request.
                // if Steam does not reply to the request in a timely fashion (controlled by the `Timeout` field on the AsyncJob object), the underlying
                // task for this job will be canceled, and TaskCanceledException will be thrown.
                // additionally, if Steam encounters a remote failure and is unable to process your request, the job will be faulted and an AsyncJobFailedException
                // will be thrown.
                SteamApps.DepotKeyCallback depotKey = await depotJob;

                if (depotKey.Result == EResult.OK)
                {
                    await TestContext.Progress.WriteLineAsync($"Got our depot key: {BitConverter.ToString(depotKey.DepotKey)}");
                }
                else
                {
                    await TestContext.Progress.WriteLineAsync("Unable to request depot key!");
                }

                // now request the access token
                var accessTokenJob = steamApps.PICSGetAccessTokens(appid, package: null);
                var accessTokenResult = await accessTokenJob;

                // Get the access token, if the app is owned it may be a long value, or 0 if it is public.
                // If the request was denied, the appid will be listed in AppTokensDenied.
                accessTokenResult.AppTokens.TryGetValue(appid, out var accessToken);

                // create a request product info request struct
                var request = new SteamApps.PICSRequest(appid, accessToken);

                // now request some product info
                var productJob = steamApps.PICSGetProductInfo(app: request, package: null, metaDataOnly: false);

                // note that with some requests, Steam can return multiple results, so these jobs don't return the callback object directly, but rather
                // a result set that could contain multiple callback objects if Steam gives us multiple results
                AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet resultSet = await productJob;

                if (resultSet.Complete)
                {
                    // the request fully completed, we can handle the data
                    SteamApps.PICSProductInfoCallback? productInfo = resultSet.Results?.First();

                    // ... do something with our product info
                }
                else if (resultSet.Failed)
                {
                    // the request partially completed, and then Steam encountered a remote failure. for async jobs with only a single result (such as
                    // GetDepotDecryptionKey), this would normally throw an AsyncJobFailedException. but since Steam had given us a partial set of callbacks
                    // we get to decide what to do with the data

                    // keep in mind that if Steam immediately fails to provide any data, or times out while waiting for the first result, an
                    // AsyncJobFailedException or TaskCanceledException will be thrown

                    // the result set might not have our data, so we need to test to see if we have results for our request
                    SteamApps.PICSProductInfoCallback? productInfo = resultSet.Results?.FirstOrDefault(prodCallback => prodCallback.Apps.ContainsKey(appid));

                    if (productInfo != null)
                    {
                        // we were lucky and Steam gave us the info we requested before failing
                    }
                    else
                    {
                        // bad luck
                    }
                }
                else
                {
                    // the request partially completed, but then we timed out. essentially the same as the previous case, but Steam didn't explicitly fail.

                    // we still need to check our result set to see if we have our data
                    SteamApps.PICSProductInfoCallback? productInfo = resultSet.Results?.FirstOrDefault(prodCallback => prodCallback.Apps.ContainsKey(appid));

                    if (productInfo != null)
                    {
                        // we were lucky and Steam gave us the info we requested before timing out
                    }
                    else
                    {
                        // bad luck
                    }
                }

                // lastly, if you're unable to use the async/await pattern (older VS/compiler, etc.) you can still directly access the TPL Task associated
                // with the async job by calling `ToTask()`
                var depotTask = steamApps.GetDepotDecryptionKey(depotid, appid).ToTask();

                // set up a continuation for when this task completes
                var ignored = depotTask.ContinueWith(task =>
                {
                    depotKey = task.Result;

                    // do something with the depot key

                    // we're finished with this sample, drop out of the callback loop
                    isRunning = false;
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            TestContext.Progress.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        // This is simply showing how to parse JWT, this is not required to log in to Steam
        void ParseJsonWebToken(string token, string name)
        {
            // You can use a JWT library to do the parsing for you
            var tokenComponents = token.Split('.');

            // Fix up base64url to normal base64
            var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

            if (base64.Length % 4 != 0)
            {
                base64 += new string('=', 4 - base64.Length % 4);
            }

            var payloadBytes = Convert.FromBase64String(base64);

            // Payload can be parsed as JSON, and then fields such expiration date, scope, etc. can be accessed
            var payload = JsonDocument.Parse(payloadBytes);

            // For brevity, we will simply output formatted JSON to console
            var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            TestContext.Progress.WriteLine($"{name}: {formatted}");
            TestContext.Progress.WriteLine();
        }

        void DrawQRCode(QrAuthSession authSession)
        {
            TestContext.Progress.WriteLine($"Challenge URL: {authSession.ChallengeURL}");
            TestContext.Progress.WriteLine();

            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetGraphic(1, drawQuietZones: false);

            TestContext.Progress.WriteLine("Use the Steam Mobile App to sign in via QR code:");
            TestContext.Progress.WriteLine(qrCodeAsAsciiArt);
        }
    }

    [Test]
    public void SteamUnifiedMessages()
    {
        // create our steamclient instance
        var steamClient = new SteamClient();
        // create the callback manager which will route callbacks to function calls
        var manager = new CallbackManager(steamClient);
        // get the steamuser handler, which is used for logging on after successfully connecting
        var steamUser = steamClient.GetHandler<SteamUser>();
        // get the steam unified messages handler, which is used for sending and receiving responses from the unified service api
        var steamUnifiedMessages = steamClient.GetHandler<SteamUnifiedMessages>();
        if (steamUnifiedMessages == null) return;

        // we also want to create our local service interface, which will help us build requests to the unified api 
        var playerService = steamUnifiedMessages.CreateService<Player>();

        // register a few callbacks we're interested in
        // these are registered upon creation to a callback manager, which will then route the callbacks
        // to the functions specified
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        // subscribe to incoming messages from the GameNotificationsClient service
        manager.SubscribeServiceNotification<GameNotificationsClient, CGameNotifications_OnNotificationsRequested_Notification>(OnGameStartedNotification);

        var isRunning = true;

        TestContext.Progress.WriteLine("Connecting to Steam...");

        // initiate the connection
        steamClient.Connect();

        // create our callback handling loop
        while (isRunning)
        {
            // in order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        return;

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            try
            {
                TestContext.Progress.WriteLine("Connected to Steam! Logging in '{0}'...", steamUser);

                // Start an authentication session by requesting a link
                var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());

                // Steam will periodically refresh the challenge url, this callback allows you to draw a new qr code
                authSession.ChallengeURLChanged = () =>
                {
                    TestContext.Progress.WriteLine();
                    TestContext.Progress.WriteLine("Steam has refreshed the challenge url");

                    DrawQRCode(authSession);
                };

                // Draw current qr right away
                DrawQRCode(authSession);

                // Starting polling Steam for authentication response
                // This response is later used to log on to Steam after connecting
                var pollResponse = await authSession.PollingWaitForResultAsync();

                await TestContext.Progress.WriteLineAsync($"Logging in as '{pollResponse.AccountName}'...");

                // Logon to Steam with the access token we have received
                steamUser?.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken
                });

                // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
            }
            catch (Exception e)
            {
                TestContext.Progress.WriteLine(e);
            }
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            TestContext.Progress.WriteLine("Disconnected from Steam");

            isRunning = false;
        }

        async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            try
            {
                if (callback.Result != EResult.OK)
                {
                    TestContext.Progress.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                    isRunning = false;
                    return;
                }

                await TestContext.Progress.WriteLineAsync("Successfully logged on!");

                // now that we're logged onto Steam, lets query the IPlayer service for our badge levels

                // first, build our request object, these are autogenerated and can normally be found in the SteamKit2.Internal namespace
                var req = new CPlayer_GetGameBadgeLevels_Request
                {
                    // we want to know our 730 (CS2) badge level
                    appid = 730
                };

                // now let's send the request and await for the response
                var response = await playerService.GetGameBadgeLevels(req);

                // alternatively, the request can be made using SteamUnifiedMessages directly, but then you must build the service request name manually
                // the name format is in the form of <Service>.<Method>#<Version>
                // response = await steamUnifiedMessages.SendMessage<CPlayer_GetGameBadgeLevels_Request, CPlayer_GetGameBadgeLevels_Response>("Player.GetGameBadgeLevels#1", req);

                if (response.Result != EResult.OK)
                {
                    await TestContext.Progress.WriteLineAsync($"Unified service request failed with {response.Result}");
                    return;
                }

                await TestContext.Progress.WriteLineAsync($"Our player level is {response.Body.player_level}");

                foreach (var badge in response.Body.badges)
                {
                    await TestContext.Progress.WriteLineAsync($"Badge series {badge.series} is level {badge.level}");
                }
                
                // now that we've completed our task, lets log off after a few seconds to receive possible notifications
                await Task.Delay(30000);
                steamUser?.LogOff();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            TestContext.Progress.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        void OnGameStartedNotification(SteamUnifiedMessages.ServiceMethodNotification<CGameNotifications_OnNotificationsRequested_Notification> notification)
        {
            TestContext.Progress.WriteLine($"User with id {notification.Body.steamid} started the game:");
            TestContext.Progress.WriteLine(notification.Body.appid);
        }

        // This is simply showing how to parse JWT, this is not required to log in to Steam
        void ParseJsonWebToken(string token, string name)
        {
            // You can use a JWT library to do the parsing for you
            var tokenComponents = token.Split('.');

            // Fix up base64url to normal base64
            var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

            if (base64.Length % 4 != 0)
            {
                base64 += new string('=', 4 - base64.Length % 4);
            }

            var payloadBytes = Convert.FromBase64String(base64);

            // Payload can be parsed as JSON, and then fields such expiration date, scope, etc. can be accessed
            var payload = JsonDocument.Parse(payloadBytes);

            // For brevity, we will simply output formatted JSON to console
            var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            TestContext.Progress.WriteLine($"{name}: {formatted}");
            TestContext.Progress.WriteLine();
        }

        void DrawQRCode(QrAuthSession authSession)
        {
            TestContext.Progress.WriteLine($"Challenge URL: {authSession.ChallengeURL}");
            TestContext.Progress.WriteLine();

            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetGraphic(1, drawQuietZones: false);

            TestContext.Progress.WriteLine("Use the Steam Mobile App to sign in via QR code:");
            TestContext.Progress.WriteLine(qrCodeAsAsciiArt);
        }
    }

    [Test]
    public void SteamFriends()
    {
        // create our steamclient instance
        var configuration = SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.Tcp));
        var steamClient = new SteamClient(configuration);
        // create the callback manager which will route callbacks to function calls
        var manager = new CallbackManager(steamClient);

        // get the steamuser handler, which is used for logging on after successfully connecting
        var steamUser = steamClient.GetHandler<SteamUser>();
        if (steamUser == null) return;
        // get the steam friends handler, which is used for interacting with friends on the network after logging on
        var steamFriends = steamClient.GetHandler<SteamFriends>();
        if (steamFriends == null) return;

        // register a few callbacks we're interested in
        // these are registered upon creation to a callback manager, which will then route the callbacks
        // to the functions specified
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        // we use the following callbacks for friends related activities
        manager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
        manager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
        manager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);
        manager.Subscribe<SteamFriends.FriendAddedCallback>(OnFriendAdded);
        manager.Subscribe<SteamFriends.ChatMsgCallback>(OnChatMsg);

        var isRunning = true;

        TestContext.Progress.WriteLine("Connecting to Steam...");

        // initiate the connection
        steamClient.Connect();

        // create our callback handling loop
        while (isRunning)
        {
            // in order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        return;

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            try
            {
                TestContext.Progress.WriteLine("Connected to Steam! Logging in '{0}'...", steamUser);

                // Start an authentication session by requesting a link
                var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                {
                    PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient
                });

                // Steam will periodically refresh the challenge url, this callback allows you to draw a new qr code
                authSession.ChallengeURLChanged = () =>
                {
                    TestContext.Progress.WriteLine();
                    TestContext.Progress.WriteLine("Steam has refreshed the challenge url");

                    DrawQRCode(authSession);
                };

                // Draw current qr right away
                DrawQRCode(authSession);

                // Starting polling Steam for authentication response
                // This response is later used to log on to Steam after connecting
                var pollResponse = await authSession.PollingWaitForResultAsync();

                await TestContext.Progress.WriteLineAsync($"Logging in as '{pollResponse.AccountName}'...");

                // Logon to Steam with the access token we have received
                steamUser.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken
                });

                // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
            }
            catch (Exception e)
            {
                TestContext.Progress.WriteLine(e);
            }
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            TestContext.Progress.WriteLine("Disconnected from Steam");

            isRunning = false;
        }

        void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                TestContext.Progress.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                isRunning = false;
                return;
            }

            TestContext.Progress.WriteLine("Successfully logged on!");

            // at this point, we'd be able to perform actions on Steam

            // for this sample we wait for other callbacks to perform logic
            // steamUser?.LogOff();
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            TestContext.Progress.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            // before being able to interact with friends, you must wait for the account info callback
            // this callback is posted shortly after a successful logon

            // at this point, we can go online on friends, so let's do that
            steamFriends.SetPersonaState(EPersonaState.LookingToPlay);
            TestContext.Progress.WriteLine($"Logged on from: {callback.Country}");
        }

        void OnFriendsList(SteamFriends.FriendsListCallback callback)
        {
            // at this point, the client has received its friends list

            var friendCount = steamFriends.GetFriendCount();

            TestContext.Progress.WriteLine("We have {0} friends", friendCount);

            for (var x = 0; x < friendCount; x++)
            {
                // steamids identify objects that exist on the steam network, such as friends, as an example
                SteamID steamIdFriend = steamFriends.GetFriendByIndex(x);

                // we'll just display the STEAM_ rendered version
                TestContext.Progress.WriteLine("Friend: {0}", steamIdFriend.Render());
            }

            // we can also iterate over our friendslist to accept or decline any pending invites

            foreach (var friend in callback.FriendList)
            {
                if (friend.Relationship == EFriendRelationship.RequestRecipient)
                {
                    // this user has added us, let's add him back
                    steamFriends.AddFriend(friend.SteamID);
                }
            }
        }

        void OnPersonaState(SteamFriends.PersonaStateCallback callback)
        {
            // this callback is received when the persona state (friend information) of a friend changes

            // for this sample we'll simply display the names of the friends
            TestContext.Progress.WriteLine("State change: {0} {1} {2} {3}", callback.Name, callback.State, callback.GameAppID, callback.GameName);
        }

        void OnFriendAdded(SteamFriends.FriendAddedCallback callback)
        {
            // someone accepted our friend request, or we accepted one
            TestContext.Progress.WriteLine("{0} is now a friend", callback.PersonaName);
        }

        void OnChatMsg(SteamFriends.ChatMsgCallback callback)
        {
            TestContext.Progress.WriteLine($"Receive chat msg: {callback.Message}, Type: {callback.ChatMsgType}, ChatRoomID: {callback.ChatRoomID}, ChatterID: {callback.ChatterID}");
        }

        // This is simply showing how to parse JWT, this is not required to log in to Steam
        void ParseJsonWebToken(string token, string name)
        {
            // You can use a JWT library to do the parsing for you
            var tokenComponents = token.Split('.');

            // Fix up base64url to normal base64
            var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

            if (base64.Length % 4 != 0)
            {
                base64 += new string('=', 4 - base64.Length % 4);
            }

            var payloadBytes = Convert.FromBase64String(base64);

            // Payload can be parsed as JSON, and then fields such expiration date, scope, etc. can be accessed
            var payload = JsonDocument.Parse(payloadBytes);

            // For brevity, we will simply output formatted JSON to console
            var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            TestContext.Progress.WriteLine($"{name}: {formatted}");
            TestContext.Progress.WriteLine();
        }

        void DrawQRCode(QrAuthSession authSession)
        {
            TestContext.Progress.WriteLine($"Challenge URL: {authSession.ChallengeURL}");
            TestContext.Progress.WriteLine();

            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetGraphic(1, drawQuietZones: false);

            TestContext.Progress.WriteLine("Use the Steam Mobile App to sign in via QR code:");
            TestContext.Progress.WriteLine(qrCodeAsAsciiArt);
        }
    }

    private class MyHandler : ClientMsgHandler
    {
        // define our custom callback class
        // this will pass data back to the user of the handler
        public class MyCallback : CallbackMsg
        {
            public EResult Result { get; private set; }

            // generally we don't want user code to instantiate callback objects,
            // but rather only let handlers create them
            internal MyCallback(EResult result)
            {
                Result = result;
            }
        }

        // handlers can also define functions which can send data to the steam servers
        private void LogOff(string user, string pass)
        {
            var logOffMsg = new ClientMsgProtobuf<CMsgClientLogOff>(EMsg.ClientLogOff);
            Client.Send(logOffMsg);
        }

        // some other useful function
        private void DoSomething()
        {
            // this function could send some other message or perform some other logic

            // ...
            // Client.Send( somethingElse ); // etc
            // ...
        }

        public override void HandleMsg(IPacketMsg packetMsg)
        {
            // this function is called when a message arrives from the Steam network
            // the SteamClient class will pass the message along to every registered ClientMsgHandler

            // the MsgType exposes the EMsg (type) of the message
            switch (packetMsg.MsgType)
            {
                // we want to custom handle this message, for the sake of an example
                case EMsg.ClientLogOnResponse:
                    HandleLogonResponse(packetMsg);
                    break;
            }
        }

        private void HandleLogonResponse(IPacketMsg packetMsg)
        {
            // in order to get at the message contents, we need to wrap the packet message
            // in an object that gives us access to the message body
            var logonResponse = new ClientMsgProtobuf<CMsgClientLogonResponse>(packetMsg);

            // the raw body of the message often doesn't make use of useful types, so we need to
            // cast them to types that are prettier for the user to handle
            var result = (EResult)logonResponse.Body.eresult;

            // our handler will simply display a message in the console, and then post our custom callback with the result of logon
            TestContext.Progress.WriteLine("HandleLogonResponse: {0}", result);

            // post the callback to be consumed by user code
            Client.PostCallback(new MyCallback(result));
        }
    }

    private class MyListener : IDebugListener
    {
        public void WriteLine(string category, string msg)
        {
            // this function will be called when internal steamkit components write to the debuglog

            // for this example, we'll print the output to the console
            TestContext.Progress.WriteLine("MyListener - {0}: {1}", category, msg);
        }
    }
}