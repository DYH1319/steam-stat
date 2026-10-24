using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SteamStat.Core.Platform;

namespace SteamStat.Platform.Windows;

/// <summary>
/// Windows 桌面活动探针：通过 GetLastInputInfo / XInput 估计用户空闲时间，
/// 通过 GetForegroundWindow 链获取前台进程路径。任何单点失败只影响对应字段。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDesktopActivityProbe : IDesktopActivityProbe
{
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int MaxControllers = 4;

    private (int pid, string? path)? _foregroundCache;
    private readonly uint[] _lastPacketNumbers = new uint[MaxControllers];
    private bool _xInputAvailable = true;
    private bool _xInputInitialized;

    public DesktopActivitySample Sample()
    {
        TimeSpan? idle = SampleIdleTime();
        if (_xInputAvailable && HasGamepadInput())
        {
            idle = TimeSpan.Zero;
        }
        return new DesktopActivitySample(idle, SampleForegroundProcessPath());
    }

    private static TimeSpan? SampleIdleTime()
    {
        try
        {
            var lastInput = new LASTINPUTINFO();
            lastInput.cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>();
            if (!GetLastInputInfo(ref lastInput)) return null;
            var idleMs = unchecked((uint)Environment.TickCount - lastInput.dwTime);
            return TimeSpan.FromMilliseconds(idleMs);
        }
        catch
        {
            return null;
        }
    }

    private bool HasGamepadInput()
    {
        try
        {
            var hasInput = false;
            for (var i = 0; i < MaxControllers; i++)
            {
                var result = XInputGetState(i, out var state);
                if (result != 0) continue;
                if (!_xInputInitialized && _lastPacketNumbers[i] == 0)
                {
                    _lastPacketNumbers[i] = state.dwPacketNumber;
                    continue;
                }
                if (state.dwPacketNumber != _lastPacketNumbers[i])
                {
                    _lastPacketNumbers[i] = state.dwPacketNumber;
                    hasInput = true;
                }
            }
            _xInputInitialized = true;
            return hasInput;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            _xInputAvailable = false;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private string? SampleForegroundProcessPath()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            var processId = unchecked((int)pid);
            // pid 复用后旧路径可能失效，只缓存上一次前台 pid 的结果
            if (_foregroundCache is { } cached && cached.pid == processId) return cached.path;

            var path = QueryProcessPath(processId);
            _foregroundCache = (processId, path);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string? QueryProcessPath(int processId)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return null;
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
