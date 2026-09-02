using ElectronNET.API;
using Microsoft.Extensions.Logging;

namespace ElectronNet.Infrastructure;

internal enum MainWindowAvailability
{
    Available,
    Missing,
    Destroyed
}

internal readonly record struct MainWindowSnapshot(
    BrowserWindow? Window,
    MainWindowAvailability Availability);

internal interface IMainWindowAccessor
{
    Task<MainWindowSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

internal sealed class MainWindowAccessor(ILogger<MainWindowAccessor> logger) : IMainWindowAccessor
{
    private readonly Lock _sync = new();
    private BrowserWindow? _window;

    internal BrowserWindow? Window
    {
        get
        {
            lock (_sync) return _window;
        }
    }

    internal void Set(BrowserWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_sync)
        {
            _window = window;
        }
    }

    internal void Clear(BrowserWindow? window = null)
    {
        lock (_sync)
        {
            if (window == null || ReferenceEquals(_window, window))
            {
                _window = null;
            }
        }
    }

    public async Task<MainWindowSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        BrowserWindow? window;
        lock (_sync)
        {
            window = _window;
        }

        if (window == null)
        {
            return new MainWindowSnapshot(null, MainWindowAvailability.Missing);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await window.IsDestroyedAsync().ConfigureAwait(false)
                ? new MainWindowSnapshot(null, MainWindowAvailability.Destroyed)
                : new MainWindowSnapshot(window, MainWindowAvailability.Available);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to inspect the main Electron window; treating it as destroyed");
            return new MainWindowSnapshot(null, MainWindowAvailability.Destroyed);
        }
    }
}
