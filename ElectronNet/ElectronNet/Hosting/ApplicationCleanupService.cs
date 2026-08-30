using Microsoft.Extensions.Hosting;

namespace ElectronNet.Hosting;

internal sealed class ApplicationCleanupService(Func<Task> cleanup) : IHostedService
{
    private int _cleanupStarted;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => CleanupAsync();

    internal async Task CleanupAsync()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;
        await cleanup();
    }
}
