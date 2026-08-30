using ElectronNet.Services;
using SteamStat.Core.Events;

namespace ElectronNet.Hosting;

internal sealed class FriendsSessionEventHandler(IEventBus eventBus) :
    IEventHandler<SteamSessionDisconnected>,
    IEventHandler<SteamSessionReconnected>
{
    public Task HandleAsync(SteamSessionDisconnected message, CancellationToken cancellationToken)
    {
        SteamFriendsService.ClearUserFriendsData(message.AccountName);
        return Task.CompletedTask;
    }

    public Task HandleAsync(SteamSessionReconnected message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SteamFriendsService.GetFriendsForUser(eventBus, message.AccountName);
        return Task.CompletedTask;
    }
}
