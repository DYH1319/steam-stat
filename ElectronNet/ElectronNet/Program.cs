using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ElectronNET;
using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNET.Runtime;
using ElectronNET.Runtime.Data;
using ElectronNet.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using SteamStat.Core.Environment;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Settings;
using Process = System.Diagnostics.Process;

namespace ElectronNet;

public sealed class Program
{
    // 共享公共字段
    internal static bool IsDev { get; private set; }
    internal static bool IsSilentStart { get; private set; }
    internal static string? UserDataPath { get; private set; }
    internal static string? Locale { get; private set; }
    internal static BrowserWindow? ElectronMainWindow { get; private set; }

    // 开发环境相关配置
    private static string ViteDevServerUrl { get; set; } = "http://localhost:9000";
    private static bool IsViteDevServerStarted { get; set; }

    // 打包环境相关配置
    private static string? HtmlFilePath { get; set; }

    // 窗口逻辑尺寸（实际尺寸会根据 DPI 缩放调整）
    private const int LOGICAL_WIDTH = 1820;
    private const int LOGICAL_HEIGHT = 1080;
    private const int MIN_LOGICAL_WIDTH = 1400;
    private const int MIN_LOGICAL_HEIGHT = 780;

    // Electron 相关
    private static IElectronNetRuntimeController? ElectronRuntimeController { get; set; }
    private static Process? ViteProcess { get; set; }
    private static App? ElectronApp { get; set; }
    private static Screen? ElectronScreen { get; set; }
    private static Tray? ElectronTray { get; set; }
    private static GlobalShortcut? ElectronGlobalShortcut { get; set; }

    public static async Task Main(string[] args)
    {
        // 设置控制台输出和输入编码为 UTF-8
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        using var bootstrapSerilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        using var bootstrapLoggerFactory = LoggerFactory.Create(logging => logging.AddSerilog(bootstrapSerilog));
        Microsoft.Extensions.Logging.ILogger<Program> logger = bootstrapLoggerFactory.CreateLogger<Program>();

        // 获取 Electron 运行控制器
        ElectronRuntimeController = ElectronNetRuntime.RuntimeController;
        IHost? host = null;
        ApplicationCleanupService? cleanupService = null;

        try
        {
            // 启动 Electron 运行时
            await ElectronRuntimeController.Start();

            // 等待 Electron 进程启动且 Socket 连接成功
            await ElectronRuntimeController.WaitReadyTask;

            var appEnvironment = await CreateAppEnvironment(args);
            Directory.CreateDirectory(appEnvironment.Paths.LogsDirectory);
            var builder = Host.CreateApplicationBuilder(args);
            builder.Environment.EnvironmentName = appEnvironment.IsDevelopment
                ? Environments.Development
                : Environments.Production;
            builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            }));
            builder.Services.AddSerilog((_, configuration) =>
            {
                configuration
                    .MinimumLevel.Is(appEnvironment.IsDevelopment ? LogEventLevel.Debug : LogEventLevel.Information)
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .WriteTo.File(
                        appEnvironment.Paths.LogFilePattern,
                        rollingInterval: RollingInterval.Day,
                        rollOnFileSizeLimit: true,
                        fileSizeLimitBytes: 10 * 1024 * 1024,
                        retainedFileCountLimit: 14,
                        shared: false,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj} {Properties:j}{NewLine}{Exception}");
                if (appEnvironment.IsDevelopment)
                    configuration.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
            });
            builder.Services
                .AddSteamStatCore()
                .AddSteamStatWindows()
                .AddSteamStatElectron(appEnvironment);

            host = builder.Build();
            logger = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
            logger.LogInformation(
                "Starting Steam Stat in {Environment} with locale {Locale}; UserData is {UserDataPath}",
                appEnvironment.IsDevelopment ? "Development" : "Production",
                appEnvironment.Locale,
                appEnvironment.Paths.UserDataDirectory);
            cleanupService = host.Services.GetRequiredService<ApplicationCleanupService>();
            await host.StartAsync();
            await host.Services.GetRequiredService<ApplicationStartupCoordinator>().StartAsync();

            // 等待关闭
            await ElectronRuntimeController.WaitStoppedTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Steam Stat terminated unexpectedly");
        }
        finally
        {
            if (host == null)
            {
                await Cleanup(logger: logger);
            }
            else
            {
                try
                {
                    await host.StopAsync();
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Failed to stop the application Host");
                }
                finally
                {
                    try
                    {
                        if (cleanupService == null)
                        {
                            await Cleanup(logger: logger);
                        }
                        else
                        {
                            await cleanupService.CleanupAsync();
                        }
                    }
                    finally
                    {
                        if (host is IAsyncDisposable asyncDisposable)
                        {
                            await asyncDisposable.DisposeAsync();
                        }
                        else
                        {
                            host.Dispose();
                        }
                    }
                }
            }
        }
    }

    private static async Task<AppEnvironment> CreateAppEnvironment(string[] args)
    {
        ElectronApp = Electron.App;
        ElectronScreen = Electron.Screen;
        ElectronTray = Electron.Tray;
        ElectronGlobalShortcut = Electron.GlobalShortcut;

        IsDev = ElectronNetRuntime.StartupMethod.Equals(StartupMethod.UnpackedDotnetFirst)
                || ElectronNetRuntime.StartupMethod.Equals(StartupMethod.UnpackedElectronFirst);
        IsSilentStart = args.Contains("--silent-start", StringComparer.OrdinalIgnoreCase);

        var appDataPath = await ElectronApp.GetPathAsync(PathName.AppData);
        ElectronApp.SetPath(PathName.UserData, Path.Combine(appDataPath, IsDev ? "steam-stat-dev" : "steam-stat"));
        UserDataPath = await ElectronApp.GetPathAsync(PathName.UserData);

        Locale = await ElectronApp.GetLocaleAsync();
        if (string.IsNullOrWhiteSpace(Locale)) Locale = "en-US";

        return new AppEnvironment(IsDev, Locale, IsSilentStart, new AppPaths(UserDataPath));
    }

    /// <summary>
    /// 初始化设置和设置相关任务
    /// </summary>
    internal static Task InitializeSettingsAndJobs(SettingsCoordinator settingsCoordinator)
    {
        return settingsCoordinator.InitializeAsync();
    }

    /// <summary>
    /// 初始化界面内容
    /// </summary>
    internal static async Task InitializeContent(Microsoft.Extensions.Logging.ILogger<Program> logger)
    {
        if (IsDev)
        {
            await LoadDevelopmentContentUrl(logger);
        }
        else
        {
            LoadProductionContentUrl(logger);
        }
    }

    /// <summary>
    /// 初始化主窗口
    /// </summary>
    internal static async Task InitializeMainWindow(MainWindowAccessor mainWindowAccessor, SettingsCoordinator settingsCoordinator, Microsoft.Extensions.Logging.ILogger<Program> logger)
    {
        // 界面缩放采用浏览器式缩放，由用户自行控制，与系统 DPI 缩放解耦。
        // 窗口尺寸使用逻辑像素（DIP），由 Electron 自行处理 DPI 缩放。
        double zoomFactor = settingsCoordinator.GetSettings().ZoomFactor!.Value;
        logger.LogDebug("Applying window zoom factor {ZoomFactor}", zoomFactor);

        Display nearestDisplay = await ElectronScreen!.GetDisplayNearestPointAsync(await ElectronScreen.GetCursorScreenPointAsync());
        double scaleFactor = nearestDisplay.ScaleFactor;
        // double scaleFactor = 1.0;

        // 计算实际窗口尺寸（根据 DPI 缩放）
        var width = (int)Math.Round(LOGICAL_WIDTH / scaleFactor);
        var height = (int)Math.Round(LOGICAL_HEIGHT / scaleFactor);
        var minWidth = (int)Math.Round(MIN_LOGICAL_WIDTH / scaleFactor);
        var minHeight = (int)Math.Round(MIN_LOGICAL_HEIGHT / scaleFactor);

        // 获取窗口图标路径
        string? iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icons8-steam-256.ico");
        if (!File.Exists(iconPath))
        {
            iconPath = null;
            logger.LogWarning("Window icon was not found; using the default icon");
        }

        // 创建主窗口
        ElectronMainWindow = await Electron.WindowManager.CreateWindowAsync(
            new BrowserWindowOptions
            {
                Width = width,
                Height = height,
                MinWidth = minWidth,
                MinHeight = minHeight,
                RoundedCorners = true,
                Icon = iconPath,
                Show = false,
                Center = true,
                SkipTaskbar = false,
                AlwaysOnTop = false,
                AutoHideMenuBar = true,
                Frame = false,
                TitleBarStyle = TitleBarStyle.hidden,
                WebPreferences = new WebPreferences
                {
                    Preload = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "preload.mjs"),
                    DevTools = IsDev,
                    WebSecurity = true,
                    AllowRunningInsecureContent = false,
                    ContextIsolation = true,
                    NodeIntegration = false,
                    NodeIntegrationInWorker = false,
                    NodeIntegrationInSubFrames = false,
                    Sandbox = true,
                    ZoomFactor = zoomFactor
                }
            },
            IsDev ? ViteDevServerUrl : HtmlFilePath!
        );

        var mainWindow = ElectronMainWindow!;
        mainWindowAccessor.Set(mainWindow);
        mainWindow.OnClosed += () =>
        {
            mainWindowAccessor.Clear(mainWindow);
            if (ReferenceEquals(ElectronMainWindow, mainWindow))
            {
                ElectronMainWindow = null;
            }
        };
    }

    /// <summary>
    /// 加载开发环境内容 Url（Vite 开发服务器 Url）
    /// </summary>
    private static async Task LoadDevelopmentContentUrl(Microsoft.Extensions.Logging.ILogger<Program> logger)
    {
        logger.LogInformation("Loading development content from the Vite dev server");

        if (!IsViteDevServerStarted)
        {
            // 启动 Vite 开发服务器
            bool started = await StartViteDevServer(logger);

            if (!started)
            {
                logger.LogError("Failed to start the Vite dev server automatically");
            }
        }
    }

    /// <summary>
    /// 加载生产环境内容（dist 目录）
    /// </summary>
    private static void LoadProductionContentUrl(Microsoft.Extensions.Logging.ILogger<Program> logger)
    {
        logger.LogInformation("Loading production content from the dist folder");

        // 获取 dist/index.html 路径
        string distPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dist", "index.html");

        if (!File.Exists(distPath))
        {
            logger.LogError("Frontend entry point was not found at {DistPath}; run pnpm run build", distPath);
        }

        HtmlFilePath = distPath;
    }

    /// <summary>
    /// 启动 Vite 开发服务器
    /// </summary>
    private static async Task<bool> StartViteDevServer(Microsoft.Extensions.Logging.ILogger<Program> logger)
    {
        try
        {
            // 查找项目根目录（包含 package.json 的目录）
            string? projectRoot = FindProjectRoot();

            if (projectRoot == null)
            {
                logger.LogError("Could not find the project root containing package.json from {BaseDirectory}", AppDomain.CurrentDomain.BaseDirectory);
                return false;
            }

            logger.LogInformation("Starting Vite from {ProjectRoot}", projectRoot);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/C pnpm run dev",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            ViteProcess = Process.Start(startInfo);
            if (ViteProcess == null) return false;

            // Vite 就绪信号：输出中出现 Local 地址时视为可访问
            var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            ViteProcess.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    logger.LogDebug("Vite: {ViteOutput}", args.Data);
                    if (args.Data.Contains("Local") && args.Data.Contains("http://localhost:"))
                    {
                        var ansiEscapeRegex = new Regex(@"\x1B\[[0-?]*[ -/]*[@-~]");
                        var data = ansiEscapeRegex.Replace(args.Data, "");
                        IsViteDevServerStarted = true;
                        ViteDevServerUrl = Regex.Match(data, @"http[s]?://[^\s]+/").Value;
                        readyTcs.TrySetResult(true);
                    }
                }
            };
            ViteProcess.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                    logger.LogWarning("Vite stderr: {ViteError}", args.Data);
            };
            ViteProcess.EnableRaisingEvents = true;
            ViteProcess.Exited += (_, _) => readyTcs.TrySetResult(false);

            ViteProcess.BeginOutputReadLine();
            ViteProcess.BeginErrorReadLine();

            // 原实现是 `while (!IsViteDevServerStarted) { }` 空转，会跑满一个 CPU 核心，
            // 且 Vite 启动失败时永久挂死。改为等待就绪信号并设置超时上限。
            var ready = await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(120));
            if (!ready)
            {
                logger.LogError("Vite dev server exited before becoming ready");
                return false;
            }

            logger.LogInformation("Vite dev server process started");
            return true;
        }
        catch (TimeoutException)
        {
            logger.LogError("Timed out waiting 120 seconds for the Vite dev server");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start the Vite dev server");
            return false;
        }
    }

    /// <summary>
    /// 查找项目根目录（包含 package.json 的目录）
    /// </summary>
    private static string? FindProjectRoot()
    {
        // 从当前目录开始向上查找
        var currentDir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

        while (currentDir != null)
        {
            string packageJsonPath = Path.Combine(currentDir.FullName, "package.json");
            if (File.Exists(packageJsonPath))
            {
                return currentDir.FullName;
            }

            currentDir = currentDir.Parent;
        }

        return null;
    }

    /// <summary>
    /// 创建系统托盘
    /// </summary>
    internal static void CreateTray(Microsoft.Extensions.Logging.ILogger<Program> logger)
    {
        // 获取托盘图标路径
        string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icons8-steam-256.ico");
        if (!File.Exists(iconPath))
        {
            logger.LogError("Tray icon was not found; the system tray cannot be created");
            return;
        }

        // 托盘菜单
        MenuItem[] menuItems =
        [
            new()
            {
                Label = "退出 (Exit) Steam Stat",
                Click = () =>
                {
                    // TODO
                    ElectronMainWindow!.Close();
                }
            }
        ];

        // 创建托盘
        if (ElectronTray == null) return;
        ElectronTray.Show(iconPath);
        ElectronTray.SetToolTip("Steam Stat");
        ElectronTray.SetMenuItems(menuItems);
        ElectronTray.OnClick += (_, _) =>
        {
            if (ElectronMainWindow == null) return;
            ElectronMainWindow.SetSkipTaskbar(false);
            ElectronMainWindow.Show();
        };

        logger.LogInformation("System tray created");
    }

    /// <summary>
    /// 添加 App 监听器
    /// </summary>
    internal static void AddAppListeners()
    {
        if (ElectronApp == null) return;

        ElectronApp.WindowAllClosed += () => ElectronApp.Quit();

        // ElectronApp.BeforeQuit += (_) => UnregisterAllGlobalShortcut();
    }

    /// <summary>
    /// 添加 Screen 监听器
    /// </summary>
    internal static void AddScreenListeners()
    {
        if (ElectronScreen == null) return;

        ElectronScreen.OnDisplayMetricsChanged += (display, changedMetrics) =>
        {
            if (!changedMetrics.Contains("scaleFactor")) return;
            var scaleFactor = display.ScaleFactor;

            if (ElectronMainWindow == null) return;
            // ElectronMainWindow.SetSize((int)Math.Round(LOGICAL_WIDTH / scaleFactor), (int)Math.Round(LOGICAL_HEIGHT / scaleFactor));
            ElectronMainWindow.SetMinimumSize((int)Math.Round(MIN_LOGICAL_WIDTH / scaleFactor), (int)Math.Round(MIN_LOGICAL_HEIGHT / scaleFactor));
        };
    }

    /// <summary>
    /// 添加 BrowserWindow, WebContents 监听器
    /// </summary>
    internal static void AddWindowListeners(SettingsCoordinator settingsCoordinator, Microsoft.Extensions.Logging.ILogger<Program> logger)
    {
        if (ElectronMainWindow == null) return;

        ElectronMainWindow.OnClose += () => { };

        // 窗口准备好后显示（如果不是静默启动）
        ElectronMainWindow.OnReadyToShow += () =>
        {
            if (!IsSilentStart)
            {
                ElectronMainWindow.Show();
                logger.LogInformation("Main window is ready and visible");
            }
            else
            {
                logger.LogInformation("Silent start completed with the main window hidden");
            }
        };

        ElectronMainWindow.WebContents.OnDidFinishLoad += () =>
        {
            // 页面加载/导航完成后重新应用用户设置的缩放，确保刷新后缩放保持一致
            ElectronMainWindow.WebContents.SetZoomFactor(settingsCoordinator.GetSettings().ZoomFactor!.Value);
            if (!IsDev || IsSilentStart) return;
            ElectronMainWindow.WebContents.OpenDevTools(new OpenDevToolsOptions
            {
                Activate = true,
                Mode = DevToolsMode.detach,
                Title = "Steam Stat Dev Tools"
            });
        };
    }

    /// <summary>
    /// 清理资源
    /// </summary>
    internal static async Task Cleanup(SteamLoginService? loginService = null, Microsoft.Extensions.Logging.ILogger<Program>? logger = null)
    {
        // 注销所有的全局快捷键
        if (ElectronGlobalShortcut != null && ElectronRuntimeController?.State == LifetimeState.Ready)
        {
            try
            {
                ElectronGlobalShortcut.UnregisterAll();
                logger?.LogInformation("Unregistered all global shortcuts");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to unregister global shortcuts");
            }
        }

        // 退出所有 Steam 登录会话
        try
        {
            if (loginService != null) await loginService.LogoutAllUsers();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to log out Steam users during shutdown");
        }

        // 停止 Vite 进程
        if (ViteProcess is { HasExited: false })
        {
            logger?.LogInformation("Stopping the Vite dev server");
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var processTreeKiller = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = $"/PID {ViteProcess.Id} /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (processTreeKiller != null)
                    {
                        await processTreeKiller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
                else
                {
                    ViteProcess.Kill(entireProcessTree: true);
                }

                await ViteProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                logger?.LogInformation("Vite dev server stopped");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to stop the Vite dev server cleanly");
                if (!ViteProcess.HasExited) ViteProcess.Kill(entireProcessTree: true);
            }
            finally
            {
                ViteProcess.Dispose();
                ViteProcess = null;
            }
        }

        // 如果 Electron 进程和 dotnet socket 进程状态不是 Stopped，尝试停止
        if (ElectronRuntimeController != null && ElectronRuntimeController.State != LifetimeState.Stopped)
        {
            try
            {
                await ElectronRuntimeController.Stop();
                logger?.LogInformation("Electron runtime stopped");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to stop the Electron runtime");
            }
        }

        logger?.LogInformation("Cleanup completed");

    }
}
