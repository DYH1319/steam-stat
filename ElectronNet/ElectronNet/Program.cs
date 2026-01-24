using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ElectronNET;
using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Constants;
using ElectronNET.Runtime;
using ElectronNET.Runtime.Data;
using ElectronNet.Services;
using Process = System.Diagnostics.Process;

namespace ElectronNet;

public static class Program
{
    // 是否处于开发环境
    private static bool IsDev { get; set; }

    // 共享公共字段
    internal static string? UserDataPath { get; private set; }
    internal static string? Locale { get; private set; }

    // 开发环境相关配置
    private static string ViteDevServerUrl { get; set; } = "http://localhost:9000";
    private static bool IsViteDevServerStarted { get; set; }

    // 打包环境相关配置
    private static string? HtmlFilePath { get; set; }

    // 窗口逻辑尺寸（实际尺寸会根据 DPI 缩放调整）
    private const int LOGICAL_WIDTH = 1920;
    private const int LOGICAL_HEIGHT = 1080;
    private const int MIN_LOGICAL_WIDTH = 1600;
    private const int MIN_LOGICAL_HEIGHT = 900;

    // Electron 相关
    private static IElectronNetRuntimeController? ElectronRuntimeController { get; set; }
    private static Process? ViteProcess { get; set; }
    private static App? ElectronApp { get; set; }
    private static Screen? ElectronScreen { get; set; }
    private static BrowserWindow? ElectronMainWindow { get; set; }
    private static Tray? ElectronTray { get; set; }

    public static async Task Main()
    {
        // 设置控制台输出和输入编码为 UTF-8
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        // 注册进程退出事件
        AppDomain.CurrentDomain.ProcessExit += async (_, _) =>
        {
            await Task.Delay(100);
            await Cleanup();
        };

        // 获取 Electron 运行控制器
        ElectronRuntimeController = ElectronNetRuntime.RuntimeController;

        try
        {
            // 启动 Electron 运行时
            await ElectronRuntimeController.Start();

            // 等待 Electron 进程启动且 Socket 连接成功
            await ElectronRuntimeController.WaitReadyTask;

            // 初始化 Electron App
            await InitializeApp();

            // 等待关闭
            await ElectronRuntimeController.WaitStoppedTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} Error: {ex.Message}");
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} StackTrace: {ex.StackTrace}");
        }
        finally
        {
            // 清理资源
            await Cleanup();
        }
    }

    /// <summary>
    /// 初始化 Electron App
    /// </summary>
    private static async Task InitializeApp()
    {
        // 设置 Electron 相关引用
        ElectronApp = Electron.App;
        ElectronScreen = Electron.Screen;
        ElectronTray = Electron.Tray;

        // 判断是否为开发环境
        IsDev = ElectronNetRuntime.StartupMethod.Equals(StartupMethod.UnpackedDotnetFirst)
                || ElectronNetRuntime.StartupMethod.Equals(StartupMethod.UnpackedElectronFirst);
        Console.WriteLine($"{ConsoleLogPrefix.INFO} Environment: {(IsDev ? "Development" : "Production")}");

        // 区分开发环境和生产环境的 UserData 路径
        ElectronApp.SetPath(PathName.UserData, IsDev ? Path.Combine(await ElectronApp.GetPathAsync(PathName.AppData), "steam-stat-dev") : Path.Combine(await ElectronApp.GetPathAsync(PathName.AppData), "steam-stat"));
        UserDataPath = await ElectronApp.GetPathAsync(PathName.UserData);
        Console.WriteLine($"{ConsoleLogPrefix.INFO} UserData Path: {UserDataPath}");

        // 获取 Locale
        Locale = await ElectronApp.GetLocaleAsync();
        Console.WriteLine($"{ConsoleLogPrefix.INFO} Locale: {Locale}");

        // 加载应用设置
        var appSettings = SettingService.GetSettings();

        // 设置开机自启
        ElectronApp.SetLoginItemSettings(
            new LoginSettings
            {
                OpenAtLogin = appSettings.AutoStart!.Value,
                Path = await ElectronApp.GetPathAsync(PathName.Exe),
                Args = appSettings.SilentStart!.Value ? ["--silent-start"] : []
            }
        );

        // 执行数据库迁移
        await AppDbContext.Instance.ApplyMigrationsAsync();

        // 同步 / 初始化数据
        await GlobalStatusService.SyncDb();
        await SteamUserService.SyncDb();
        await SteamAppService.SyncDb();
        await UseAppRecordService.InitDb();

        // 初始化自动更新
        // await UpdateService.InitAutoUpdater();

        // 初始化界面内容
        await InitializeContent();

        // 初始化主窗口
        await InitializeMainWindow();

        // 添加监听器
        AddAppListeners();
        AddScreenListeners();
        AddWindowListeners();

        // 创建系统托盘
        CreateTray();

        // 注册 IPC 处理器
        // RegisterIpcHandlers();
    }

    /// <summary>
    /// 初始化界面内容
    /// </summary>
    private static async Task InitializeContent()
    {
        if (IsDev)
        {
            await LoadDevelopmentContentUrl();
        }
        else
        {
            LoadProductionContentUrl();
        }
    }

    /// <summary>
    /// 初始化主窗口
    /// </summary>
    private static async Task InitializeMainWindow()
    {
        // 获取主显示器信息（用于 DPI 缩放）
        Display primaryDisplay = await ElectronScreen!.GetPrimaryDisplayAsync();
        double scaleFactor = primaryDisplay.ScaleFactor;
        Console.WriteLine($"{ConsoleLogPrefix.INFO} Scale Factor: {scaleFactor}");

        // 计算实际窗口尺寸（根据 DPI 缩放）
        int width = (int)Math.Round(LOGICAL_WIDTH / scaleFactor);
        int height = (int)Math.Round(LOGICAL_HEIGHT / scaleFactor);
        int minWidth = (int)Math.Round(MIN_LOGICAL_WIDTH / scaleFactor);
        int minHeight = (int)Math.Round(MIN_LOGICAL_HEIGHT / scaleFactor);

        // 获取窗口图标路径
        string? iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icons8-steam-256.ico");
        if (!File.Exists(iconPath))
        {
            iconPath = null;
            Console.WriteLine($"{ConsoleLogPrefix.WARN} Window Icon not found, using default.");
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
                SkipTaskbar = false,
                AlwaysOnTop = false,
                AutoHideMenuBar = true,
                TitleBarStyle = TitleBarStyle.defaultStyle,
                WebPreferences = new WebPreferences
                {
                    // TODO
                    WebSecurity = !IsDev,
                    AllowRunningInsecureContent = IsDev,
                    ContextIsolation = true,
                    NodeIntegration = false,
                    ZoomFactor = 1.0 / scaleFactor
                }
            },
            IsDev ? ViteDevServerUrl : HtmlFilePath!
        );
    }

    /// <summary>
    /// 加载开发环境内容 Url（Vite 开发服务器 Url）
    /// </summary>
    private static async Task LoadDevelopmentContentUrl()
    {
        Console.WriteLine($"{ConsoleLogPrefix.INFO} Loading development content from Vite dev server...");

        if (!IsViteDevServerStarted)
        {
            // 启动 Vite 开发服务器
            bool started = await StartViteDevServer();

            if (!started)
            {
                Console.WriteLine($"{ConsoleLogPrefix.ERROR} Failed to start Vite dev server automatically.");
            }
        }
    }

    /// <summary>
    /// 加载生产环境内容（dist 目录）
    /// </summary>
    private static void LoadProductionContentUrl()
    {
        Console.WriteLine($"{ConsoleLogPrefix.INFO} Loading production content from dist folder...");

        // 获取 dist/index.html 路径
        string distPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dist", "index.html");

        if (!File.Exists(distPath))
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} Error: dist/index.html not found at: {distPath}");
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} Please run 'pnpm run build' to build the frontend first.");
        }

        // 使用 file:// 协议加载本地文件
        HtmlFilePath = $"file:///{distPath.Replace("\\", "/")}";
    }

    /// <summary>
    /// 启动 Vite 开发服务器
    /// </summary>
    private static Task<bool> StartViteDevServer()
    {
        try
        {
            // 查找项目根目录（包含 package.json 的目录）
            string? projectRoot = FindProjectRoot();

            if (projectRoot == null)
            {
                Console.WriteLine($"{ConsoleLogPrefix.ERROR} Could not find project root (directory containing package.json)");
                Console.WriteLine($"{ConsoleLogPrefix.ERROR} Searched from: {AppDomain.CurrentDomain.BaseDirectory}");
                return Task.FromResult(false);
            }

            Console.WriteLine($"{ConsoleLogPrefix.INFO} Found package.json, starting Vite from: {projectRoot}");

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/C pnpm run dev:electronnet",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            ViteProcess = Process.Start(startInfo);
            if (ViteProcess == null) return Task.FromResult(false);

            ViteProcess.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Console.WriteLine($"[Vite] {args.Data}");
                    if (args.Data.Contains("Local") && args.Data.Contains("http://localhost:"))
                    {
                        var ansiEscapeRegex = new Regex(@"\x1B\[[0-?]*[ -/]*[@-~]");
                        var data = ansiEscapeRegex.Replace(args.Data, "");
                        IsViteDevServerStarted = true;
                        ViteDevServerUrl = Regex.Match(data, @"http[s]?://[^\s]+/").Value;
                    }
                }
            };
            ViteProcess.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                    Console.WriteLine($"[Vite Error] {args.Data}");
            };

            ViteProcess.BeginOutputReadLine();
            ViteProcess.BeginErrorReadLine();

            while (!IsViteDevServerStarted)
            {
            }

            Console.WriteLine($"{ConsoleLogPrefix.INFO} Vite dev server process started.");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} Failed to start Vite dev server: {ex.Message}");
            return Task.FromResult(false);
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
    private static void CreateTray()
    {
        // 获取托盘图标路径
        string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icons8-steam-256.ico");
        if (!File.Exists(iconPath))
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} Tray Icon not found, fail to create tray.");
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

        Console.WriteLine($"{ConsoleLogPrefix.INFO} System tray created.");
    }

    /// <summary>
    /// 添加 App 监听器
    /// </summary>
    private static void AddAppListeners()
    {
        if (ElectronApp == null) return;

        ElectronApp.WindowAllClosed += () => ElectronApp.Quit();
    }

    /// <summary>
    /// 添加 Screen 监听器
    /// </summary>
    private static void AddScreenListeners()
    {
        if (ElectronScreen == null) return;

        ElectronScreen.OnDisplayMetricsChanged += (display, changedMetrics) =>
        {
            if (!changedMetrics.Contains("scaleFactor")) return;
            double scaleFactor = display.ScaleFactor;

            if (ElectronMainWindow == null) return;
            ElectronMainWindow.SetSize((int)Math.Round(LOGICAL_WIDTH / scaleFactor), (int)Math.Round(LOGICAL_HEIGHT / scaleFactor));
            ElectronMainWindow.SetMinimumSize((int)Math.Round(MIN_LOGICAL_WIDTH / scaleFactor), (int)Math.Round(MIN_LOGICAL_HEIGHT / scaleFactor));

            ElectronMainWindow.WebContents.SetZoomFactor(1.0 / scaleFactor);
        };
    }

    /// <summary>
    /// 添加 BrowserWindow, WebContents 监听器
    /// </summary>
    private static void AddWindowListeners()
    {
        if (ElectronMainWindow == null) return;

        ElectronMainWindow.OnClose += () => { };

        // 窗口准备好后显示（如果不是静默启动）
        ElectronMainWindow.OnReadyToShow += () =>
        {
            bool isSilentStart = Environment.GetCommandLineArgs().Contains("--silent-start");
            if (!isSilentStart)
            {
                ElectronMainWindow.Show();
                Console.WriteLine($"{ConsoleLogPrefix.INFO} Window is ready and shown.");
            }
            else
            {
                Console.WriteLine($"{ConsoleLogPrefix.INFO} Silent start - window hidden.");
            }
        };

        ElectronMainWindow.WebContents.OnDidFinishLoad += async () =>
        {
            ElectronMainWindow.WebContents.SetZoomFactor(1.0 / (await ElectronScreen!.GetPrimaryDisplayAsync()).ScaleFactor);
            if (!IsDev) return;
            ElectronMainWindow.WebContents.OpenDevTools(new OpenDevToolsOptions
            {
                Activate = true,
                Mode = DevToolsMode.detach,
                Title = "Steam Stat Dev Tools"
            });
        };
    }

    // private static void RegisterIpcHandlers()
    // {
    //     // 应用窗口相关 API
    //     Electron.IpcMain.On("app:minimizeToTray", (_) =>
    //     {
    //         if (ElectronMainWindow != null)
    //         {
    //             ElectronMainWindow.Hide();
    //         }
    //     });
    //
    //     Electron.IpcMain.On("app:quit", (_) =>
    //     {
    //         if (ElectronApp != null)
    //         {
    //             ElectronApp.Exit();
    //         }
    //     });
    //
    //     // Shell 相关 API - 使用简化实现
    //     Electron.IpcMain.On("shell:openExternal", (args) =>
    //     {
    //         if (args != null)
    //         {
    //             var url = args.ToString();
    //             Electron.Shell.OpenExternalAsync(url);
    //         }
    //     });
    //
    //     Electron.IpcMain.On("shell:openPath", (args) =>
    //     {
    //         if (args != null)
    //         {
    //             var path = args.ToString();
    //             Electron.Shell.OpenPathAsync(path);
    //         }
    //     });
    //
    //     Console.WriteLine($"{ConsoleLogPrefix.INFO} IPC handlers registered.");
    // }

    /// <summary>
    /// 清理资源
    /// </summary>
    private static async Task Cleanup()
    {
        // 释放数据库上下文
        try
        {
            await AppDbContext.Instance.DisposeAsync();
            Console.WriteLine($"{ConsoleLogPrefix.INFO} DbContext disposed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} Error disposing DbContext: {ex.Message}");
        }

        // 停止 Vite 进程
        if (ViteProcess is { HasExited: false })
        {
            Console.WriteLine($"{ConsoleLogPrefix.INFO} Stopping Vite dev server...");
            try
            {
                ViteProcess.Kill(entireProcessTree: true);
                await ViteProcess.WaitForExitAsync();
                ViteProcess.Dispose();
                Console.WriteLine($"{ConsoleLogPrefix.INFO} Vite dev server stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ConsoleLogPrefix.ERROR} Error stopping Vite: {ex.Message}");
            }
        }

        // 如果 Electron 进程和 dotnet socket 进程状态不是 Stopped，尝试停止
        if (ElectronRuntimeController != null && ElectronRuntimeController.State != LifetimeState.Stopped)
        {
            try
            {
                await ElectronRuntimeController.Stop();
                Console.WriteLine($"{ConsoleLogPrefix.INFO} Electron stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ConsoleLogPrefix.ERROR} Error stopping Electron: {ex.Message}");
            }
        }
    }
}