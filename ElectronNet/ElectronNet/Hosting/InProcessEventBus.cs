using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Events;

namespace ElectronNet.Infrastructure;

internal sealed class InProcessEventBus(
    IServiceProvider serviceProvider,
    ILogger<InProcessEventBus> logger) : IEventBus
{
    public async Task PublishAsync<TEvent>(
        TEvent message,
        CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(message);

        var handlers = serviceProvider.GetServices<IEventHandler<TEvent>>();
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Event handler {HandlerType} failed while handling {EventType}",
                    handler.GetType().FullName,
                    typeof(TEvent).FullName);
            }
        }
    }
}
