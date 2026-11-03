using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Platform;

namespace SteamStat.Platform.Linux;

/// <summary>
/// Linux 桌面活动探针（X11 / XWayland）。
/// <list type="bullet">
/// <item>空闲时间：MIT-SCREEN-SAVER 扩展（XScreenSaverQueryInfo）。</item>
/// <item>前台进程：根窗口的 _NET_ACTIVE_WINDOW → 窗口的 _NET_WM_PID → /proc/&lt;pid&gt;/exe 与 cwd。</item>
/// </list>
/// Wayland 会话不允许读取全局输入与前台窗口：只有前台是 XWayland 窗口（大多数 Proton 游戏）时结果可信，
/// 否则两项都返回 null（未知），由会话跟踪降级为不区分前后台 / 不判定挂机。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class LinuxDesktopActivityProbe(
    LinuxSystemContext context,
    ILogger<LinuxDesktopActivityProbe> logger) : IDesktopActivityProbe, IDisposable
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXss = "libXss.so.1";

    private readonly object _sync = new();
    private readonly ProcScanner _proc = new(context.ProcRoot);
    private IntPtr _display;
    private bool _unavailable;
    private bool _screenSaverAvailable;
    private IntPtr _activeWindowAtom;
    private IntPtr _wmPidAtom;
    private (int Pid, string? Path, string? WorkingDirectory)? _foregroundCache;

    private bool IsWaylandSession
        => !string.IsNullOrEmpty(context.GetEnvironmentVariable("WAYLAND_DISPLAY"))
           || string.Equals(context.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    public DesktopActivitySample Sample()
    {
        lock (_sync)
        {
            if (!EnsureDisplay()) return new DesktopActivitySample(null, null);
            try
            {
                var foregroundPid = ReadForegroundPid(out var hasActiveWindow);
                // Wayland 下 XWayland 只能看到 X 客户端的输入：前台不是 X 窗口时空闲时间没有意义
                var idle = IsWaylandSession && !hasActiveWindow ? null : ReadIdleTime();
                var (path, workingDirectory) = ResolveProcess(foregroundPid);
                return new DesktopActivitySample(idle, path, workingDirectory);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "X11 desktop activity sampling failed");
                return new DesktopActivitySample(null, null);
            }
        }
    }

    private bool EnsureDisplay()
    {
        if (_unavailable) return false;
        if (_display != IntPtr.Zero) return true;
        if (string.IsNullOrEmpty(context.GetEnvironmentVariable("DISPLAY")))
        {
            MarkUnavailable("DISPLAY is not set (pure Wayland session without XWayland)");
            return false;
        }

        try
        {
            // 默认的 X 错误处理器会直接结束进程（窗口在查询途中被销毁时很常见），必须换成忽略错误的处理器
            XSetErrorHandler(&IgnoreXError);
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
            {
                MarkUnavailable("XOpenDisplay failed");
                return false;
            }
            _activeWindowAtom = XInternAtom(_display, "_NET_ACTIVE_WINDOW", 1);
            _wmPidAtom = XInternAtom(_display, "_NET_WM_PID", 1);
            try
            {
                _screenSaverAvailable = XScreenSaverQueryExtension(_display, out _, out _) != 0;
            }
            catch (DllNotFoundException)
            {
                _screenSaverAvailable = false;
                logger.LogInformation("libXss is not installed; AFK detection is unavailable");
            }
            logger.LogInformation(
                "X11 desktop activity probe ready (session: {SessionType}, idle: {IdleAvailable}, foreground: {ForegroundAvailable})",
                IsWaylandSession ? "wayland/xwayland" : "x11",
                _screenSaverAvailable,
                _activeWindowAtom != IntPtr.Zero);
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            MarkUnavailable("libX11 is not installed");
            return false;
        }
    }

    private void MarkUnavailable(string reason)
    {
        _unavailable = true;
        logger.LogInformation("Desktop activity detection is unavailable: {Reason}; AFK and foreground tracking are disabled", reason);
    }

    private TimeSpan? ReadIdleTime()
    {
        if (!_screenSaverAvailable) return null;
        var info = XScreenSaverAllocInfo();
        if (info == IntPtr.Zero) return null;
        try
        {
            if (XScreenSaverQueryInfo(_display, XDefaultRootWindow(_display), info) == 0) return null;
            // XScreenSaverInfo { Window window; int state; int kind; unsigned long til_or_since; unsigned long idle; unsigned long eventMask; }
            var idleMilliseconds = (ulong)Marshal.ReadInt64(info, 24);
            return TimeSpan.FromMilliseconds(idleMilliseconds);
        }
        finally
        {
            XFree(info);
        }
    }

    private int ReadForegroundPid(out bool hasActiveWindow)
    {
        hasActiveWindow = false;
        if (_activeWindowAtom == IntPtr.Zero || _wmPidAtom == IntPtr.Zero) return 0;
        var window = ReadCardinal(XDefaultRootWindow(_display), _activeWindowAtom);
        if (window is null or 0) return 0;
        hasActiveWindow = true;
        var pid = ReadCardinal((IntPtr)(long)window.Value, _wmPidAtom);
        return pid is > 0 and <= int.MaxValue ? (int)pid.Value : 0;
    }

    /// <summary>读取 32 位格式窗口属性的第一个元素（Xlib 以 long 存放）。</summary>
    private ulong? ReadCardinal(IntPtr window, IntPtr property)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            var status = XGetWindowProperty(_display, window, property, 0, 1, 0, IntPtr.Zero,
                out _, out var format, out var items, out _, out data);
            if (status != 0 || format != 32 || items == 0 || data == IntPtr.Zero) return null;
            return (ulong)Marshal.ReadInt64(data);
        }
        finally
        {
            if (data != IntPtr.Zero) XFree(data);
        }
    }

    private (string? Path, string? WorkingDirectory) ResolveProcess(int pid)
    {
        if (pid <= 0) return (null, null);
        // pid 复用后旧路径可能失效，只缓存上一次前台 pid 的结果
        if (_foregroundCache is { } cached && cached.Pid == pid && _proc.IsAlive(pid)) return (cached.Path, cached.WorkingDirectory);
        var path = _proc.ReadExecutablePath(pid);
        var workingDirectory = _proc.ReadWorkingDirectory(pid);
        _foregroundCache = (pid, path, workingDirectory);
        return (path, workingDirectory);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_display == IntPtr.Zero) return;
            try
            {
                XCloseDisplay(_display);
            }
            catch
            {
                // 进程退出阶段，忽略
            }
            _display = IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IgnoreXError(IntPtr display, IntPtr errorEvent) => 0;

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XSetErrorHandler(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> handler);

    [DllImport(LibX11)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPUTF8Str)] string atomName, int onlyIfExists);

    [DllImport(LibX11)]
    private static extern int XGetWindowProperty(
        IntPtr display, IntPtr window, IntPtr property, long longOffset, long longLength, int delete, IntPtr requestedType,
        out IntPtr actualType, out int actualFormat, out ulong itemCount, out ulong bytesAfter, out IntPtr data);

    [DllImport(LibX11)]
    private static extern int XFree(IntPtr data);

    [DllImport(LibXss)]
    private static extern int XScreenSaverQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [DllImport(LibXss)]
    private static extern IntPtr XScreenSaverAllocInfo();

    [DllImport(LibXss)]
    private static extern int XScreenSaverQueryInfo(IntPtr display, IntPtr drawable, IntPtr info);
}
