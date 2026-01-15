using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ElectronNET;
using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNET.Runtime.Data;
using Process = System.Diagnostics.Process;

namespace ElectronNet;

public static class Program
{
    // 是否处于开发环境
    private static bool isDev;

    // 开发环境相关配置
    private static string ViteDevServerUrl = "http://localhost:9000";
    private static bool isViteDevServerStarted;

    // 打包环境相关配置
    private static string? htmlFilePath;

    // 窗口逻辑尺寸（实际尺寸会根据 DPI 缩放调整）
    private const int LOGICAL_WIDTH = 1920;
    private const int LOGICAL_HEIGHT = 1080;
    private const int MIN_LOGICAL_WIDTH = 1600;
    private const int MIN_LOGICAL_HEIGHT = 900;

    private static Process? viteProcess;
    private static App? app;
    private static Screen? screen;
    private static BrowserWindow? mainWindow;
    private static Tray? tray;

    private static bool isQuitting;
    private static string closeConfirmAction = "ignore";

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
        var runtimeController = ElectronNetRuntime.RuntimeController;

        try
        {
            // 启动 Electron 运行时
            await runtimeController.Start();

            // 等待 Electron 进程启动且 Socket 连接成功
            await runtimeController.WaitReadyTask;

            // 初始化 Electron App
            await InitializeApp();

            // 等待关闭
            await runtimeController.WaitStoppedTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Steam Stat] Error: {ex.Message}");
            Console.WriteLine($"[Steam Stat] StackTrace: {ex.StackTrace}");
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
        app = Electron.App;
        screen = Electron.Screen;
        tray = Electron.Tray;

        // 判断是否为开发环境
        isDev = ElectronNetRuntime.StartupMethod.Equals(StartupMethod.UnpackedDotnetFirst)
                || ElectronNetRuntime.StartupMethod.Equals(StartupMethod.UnpackedElectronFirst);
        Console.WriteLine($"[Steam Stat] Environment: {(isDev ? "Development" : "Production")}");

        // 区分开发环境和生产环境的 userData 路径
        app.SetPath(PathName.UserData, isDev ? Path.Combine(await app.GetPathAsync(PathName.AppData), "steam-stat-net-dev") : Path.Combine(await app.GetPathAsync(PathName.AppData), "steam-stat-net"));
        Console.WriteLine($"[Steam Stat] UserData Path: {await app.GetPathAsync(PathName.UserData)}");

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
        if (isDev)
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
        Display primaryDisplay = await screen!.GetPrimaryDisplayAsync();
        double scaleFactor = primaryDisplay.ScaleFactor;
        Console.WriteLine($"[Steam Stat] Scale Factor: {scaleFactor}");

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
            Console.WriteLine("[Steam Stat] Window Icon not found, using default.");
        }

        // 创建主窗口
        mainWindow = await Electron.WindowManager.CreateWindowAsync(
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
                    WebSecurity = !isDev,
                    AllowRunningInsecureContent = isDev,
                    ContextIsolation = true,
                    NodeIntegration = false,
                    ZoomFactor = 1.0 / scaleFactor
                }
            },
            isDev ? ViteDevServerUrl : htmlFilePath!
        );
    }

    /// <summary>
    /// 加载开发环境内容 Url（Vite 开发服务器 Url）
    /// </summary>
    private static async Task LoadDevelopmentContentUrl()
    {
        Console.WriteLine("[Steam Stat] Loading development content from Vite dev server...");

        if (!isViteDevServerStarted)
        {
            // 启动 Vite 开发服务器
            bool started = await StartViteDevServer();

            if (!started)
            {
                Console.WriteLine("[Steam Stat] Failed to start Vite dev server automatically.");
            }
        }
    }

    /// <summary>
    /// 加载生产环境内容（dist 目录）
    /// </summary>
    private static void LoadProductionContentUrl()
    {
        Console.WriteLine("[Steam Stat] Loading production content from dist folder...");

        // 获取 dist/index.html 路径
        string distPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dist", "index.html");

        if (!File.Exists(distPath))
        {
            Console.WriteLine($"[Steam Stat] Error: dist/index.html not found at: {distPath}");
            Console.WriteLine("[Steam Stat] Please run 'pnpm run build' to build the frontend first.");
        }

        // 使用 file:// 协议加载本地文件
        htmlFilePath = $"file:///{distPath.Replace("\\", "/")}";
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
                Console.WriteLine("[Steam Stat] Could not find project root (directory containing package.json)");
                Console.WriteLine($"[Steam Stat] Searched from: {AppDomain.CurrentDomain.BaseDirectory}");
                return Task.FromResult(false);
            }

            Console.WriteLine($"[Steam Stat] Found package.json, starting Vite from: {projectRoot}");

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

            viteProcess = Process.Start(startInfo);
            if (viteProcess == null) return Task.FromResult(false);

            viteProcess.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Console.WriteLine($"[Vite] {args.Data}");
                    if (args.Data.Contains("Local") && args.Data.Contains("http://localhost:"))
                    {
                        var ansiEscapeRegex = new Regex(@"\x1B\[[0-?]*[ -/]*[@-~]");
                        var data = ansiEscapeRegex.Replace(args.Data, "");
                        isViteDevServerStarted = true;
                        ViteDevServerUrl = Regex.Match(data, @"http[s]?://[^\s]+/").Value;
                    }
                }
            };
            viteProcess.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                    Console.WriteLine($"[Vite Error] {args.Data}");
            };

            viteProcess.BeginOutputReadLine();
            viteProcess.BeginErrorReadLine();

            while (!isViteDevServerStarted)
            {
            }

            Console.WriteLine("[Steam Stat] Vite dev server process started.");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Steam Stat] Failed to start Vite dev server: {ex.Message}");
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
            Console.WriteLine("[Steam Stat] Tray Icon not found, fail to create tray.");
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
                    closeConfirmAction = "exit";
                    mainWindow!.Close();
                }
            }
        ];

        // 创建托盘
        if (tray == null) return;
        tray.Show(iconPath);
        tray.SetToolTip("Steam Stat");
        tray.SetMenuItems(menuItems);
        tray.OnClick += (_, _) =>
        {
            if (mainWindow == null) return;
            mainWindow.SetSkipTaskbar(false);
            mainWindow.Show();
        };

        Console.WriteLine("[Steam Stat] System tray created.");
    }
    
    /// <summary>
    /// 添加 App 监听器
    /// </summary>
    private static void AddAppListeners()
    {
        if (app == null) return;

        app.WindowAllClosed += () =>
        {
            app.Quit();
        };
    }

    /// <summary>
    /// 添加 Screen 监听器
    /// </summary>
    private static void AddScreenListeners()
    {
        if (screen == null) return;

        screen.OnDisplayMetricsChanged += (display, changedMetrics) =>
        {
            if (!changedMetrics.Contains("scaleFactor")) return;
            double scaleFactor = display.ScaleFactor;

            if (mainWindow == null) return;
            mainWindow.SetSize((int)Math.Round(LOGICAL_WIDTH / scaleFactor), (int)Math.Round(LOGICAL_HEIGHT / scaleFactor));
            mainWindow.SetMinimumSize((int)Math.Round(MIN_LOGICAL_WIDTH / scaleFactor), (int)Math.Round(MIN_LOGICAL_HEIGHT / scaleFactor));
            
            mainWindow.WebContents.SetZoomFactor(1.0 / scaleFactor);
        };
    }

    /// <summary>
    /// 添加 BrowserWindow, WebContents 监听器
    /// </summary>
    private static void AddWindowListeners()
    {
        if (mainWindow == null) return;

        mainWindow.OnClose += () => { };
        
        // 检查是否为静默启动（从命令行参数）
        bool isSilentStart = Environment.GetCommandLineArgs().Contains("--silent-start");

        // 窗口准备好后显示（如果不是静默启动）
        mainWindow.OnReadyToShow += () =>
        {
            if (!isSilentStart)
            {
                mainWindow.Show();
                Console.WriteLine("[Steam Stat] Window is ready and shown.");
            }
            else
            {
                Console.WriteLine("[Steam Stat] Silent start - window hidden.");
            }
        };

        mainWindow.WebContents.InputEvent += (InputEvent) => { };

        mainWindow.WebContents.OnDidFinishLoad += async () =>
        {
            mainWindow.WebContents.SetZoomFactor(1.0 / (await screen!.GetPrimaryDisplayAsync()).ScaleFactor);
            if (!isDev) return;
            mainWindow.WebContents.OpenDevTools(new OpenDevToolsOptions
            {
                Activate = true,
                Mode = DevToolsMode.detach,
                Title = "Steam Stat Dev Tools"
            });
        };
    }

    /// <summary>
    /// 注册 IPC 处理器
    /// </summary>
    private static void RegisterIpcHandlers()
    {
        // 应用窗口相关 API
        Electron.IpcMain.On("app:minimizeToTray", (args) =>
        {
            if (mainWindow != null)
            {
                mainWindow.Hide();
            }
        });

        Electron.IpcMain.On("app:quit", (args) =>
        {
            isQuitting = true;
            if (app != null)
            {
                app.Exit();
            }
        });

        // Shell 相关 API - 使用简化实现
        Electron.IpcMain.On("shell:openExternal", (args) =>
        {
            if (args != null)
            {
                var url = args.ToString();
                Electron.Shell.OpenExternalAsync(url);
            }
        });

        Electron.IpcMain.On("shell:openPath", (args) =>
        {
            if (args != null)
            {
                var path = args.ToString();
                Electron.Shell.OpenPathAsync(path);
            }
        });
        
        Console.WriteLine("[Steam Stat] IPC handlers registered.");
    }

    /// <summary>
    /// 清理资源
    /// </summary>
    private static async Task Cleanup()
    {
        // 停止 Vite 进程
        if (viteProcess is { HasExited: false })
        {
            Console.WriteLine("[Steam Stat] Stopping Vite dev server...");
            try
            {
                viteProcess.Kill(entireProcessTree: true);
                await viteProcess.WaitForExitAsync();
                viteProcess.Dispose();
                Console.WriteLine("[Steam Stat] Vite dev server stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Steam Stat] Error stopping Vite: {ex.Message}");
            }
        }
    }
}