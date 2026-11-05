using System.Text.Json;
using ElectronNET.API;
using ElectronNET.API.Entities;
using Microsoft.Extensions.Logging;

namespace ElectronNet.Hosting;

/// <summary>窗口还原后的位置和大小（DIP），以及是否处于最大化</summary>
internal sealed record SavedWindowState(int X, int Y, int Width, int Height, bool Maximized);

internal sealed class WindowStateFile
{
    public SavedWindowState? Main { get; set; }

    /// <summary>独立窗口按类型（game / friend / report）记一份，同类窗口打开时沿用</summary>
    public Dictionary<string, SavedWindowState> Popouts { get; set; } = [];
}

internal static class WindowBoundsRules
{
    /// <summary>
    /// 把保存的窗口放回重叠面积最大的显示器并保证完整可见；和所有显示器都不重叠
    /// （显示器已拔掉）时返回 null，由调用方使用默认位置。
    /// </summary>
    public static Rectangle? Fit(SavedWindowState saved, IReadOnlyCollection<Rectangle> workAreas, int minWidth, int minHeight)
    {
        if (saved.Width <= 0 || saved.Height <= 0) return null;

        var area = workAreas.MaxBy(item => OverlapArea(saved, item));
        if (area == null || OverlapArea(saved, area) <= 0) return null;

        var (width, height) = FitSize(saved.Width, saved.Height, area, minWidth, minHeight);
        return new Rectangle
        {
            X = Math.Max(area.X, Math.Min(saved.X, area.X + area.Width - width)),
            Y = Math.Max(area.Y, Math.Min(saved.Y, area.Y + area.Height - height)),
            Width = width,
            Height = height
        };
    }

    public static (int Width, int Height) FitSize(int width, int height, Rectangle workArea, int minWidth, int minHeight)
        => (Math.Max(minWidth, Math.Min(width, workArea.Width)), Math.Max(minHeight, Math.Min(height, workArea.Height)));

    private static long OverlapArea(SavedWindowState window, Rectangle area)
    {
        long width = Math.Min(window.X + window.Width, area.X + area.Width) - Math.Max(window.X, area.X);
        long height = Math.Min(window.Y + window.Height, area.Y + area.Height) - Math.Max(window.Y, area.Y);
        return width <= 0 || height <= 0 ? 0 : width * height;
    }
}

/// <summary>窗口状态只属于这台设备，单独存文件，不进设置和同步</summary>
internal sealed class WindowStateStore(string directory, ILogger<WindowStateStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Lock _sync = new();
    private readonly string _path = Path.Combine(directory, "window-state.json");
    private WindowStateFile? _state;

    public WindowStateFile Load()
    {
        lock (_sync)
        {
            return _state ??= Read();
        }
    }

    public void SaveMain(SavedWindowState state) => Update(file => file.Main = state);

    public void SavePopout(string kind, SavedWindowState state) => Update(file => file.Popouts[kind] = state);

    private void Update(Action<WindowStateFile> change)
    {
        lock (_sync)
        {
            _state ??= Read();
            change(_state);
            try
            {
                Directory.CreateDirectory(directory);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to save the window state");
            }
        }
    }

    private WindowStateFile Read()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<WindowStateFile>(File.ReadAllText(_path), JsonOptions) ?? new WindowStateFile();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Failed to read the window state; using defaults");
        }
        return new WindowStateFile();
    }
}

/// <summary>
/// 窗口移动或缩放停下来后保存状态。最大化期间不更新位置和大小，这样还原的始终是普通窗口的尺寸。
/// 关闭时窗口已经查询不到，所以不等到关闭才保存。
/// </summary>
internal sealed class WindowStateTracker : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly BrowserWindow _window;
    private readonly Action<SavedWindowState> _save;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private Rectangle? _normalBounds;
    private volatile bool _disposed;

    public WindowStateTracker(BrowserWindow window, Rectangle? normalBounds, Action<SavedWindowState> save, ILogger logger)
    {
        _window = window;
        _normalBounds = normalBounds;
        _save = save;
        _logger = logger;
        _timer = new Timer(_ => _ = CaptureAsync());
        window.OnBoundsChanged += _ => Schedule();
        window.OnMaximize += Schedule;
        window.OnUnmaximize += Schedule;
    }

    private void Schedule()
    {
        if (!_disposed) _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task CaptureAsync()
    {
        try
        {
            if (_disposed || await _window.IsMinimizedAsync().ConfigureAwait(false)) return;
            var maximized = await _window.IsMaximizedAsync().ConfigureAwait(false);
            // 平铺窗口管理器里窗口一直算最大化：还不知道普通尺寸时先记当前尺寸，否则永远存不下来
            if (!maximized || _normalBounds == null) _normalBounds = await _window.GetBoundsAsync().ConfigureAwait(false);
            if (_disposed) return;
            var bounds = _normalBounds;
            _save(new SavedWindowState(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized));
        }
        catch (Exception exception)
        {
            // 窗口正在关闭时查询会失败，上一次保存的状态仍然有效
            _logger.LogDebug(exception, "Skipped saving the window state");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
