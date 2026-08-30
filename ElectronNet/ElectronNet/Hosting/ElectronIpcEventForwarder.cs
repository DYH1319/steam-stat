using ElectronNET.API;
using ElectronNet.Infrastructure;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Events;
using SteamStat.Core.Events;

namespace ElectronNet.Hosting;

internal sealed class ElectronIpcEventForwarder(
    IMainWindowAccessor mainWindowAccessor,
    ILogger<ElectronIpcEventForwarder> logger) :
    IEventHandler<LoginUsersChanged>,
    IEventHandler<SteamLoginProgressChanged>,
    IEventHandler<FriendsChanged>,
    IEventHandler<UpdaterStateChanged>
{
    public Task HandleAsync(LoginUsersChanged message, CancellationToken cancellationToken)
        => SendAsync("steam:loginUsers:updated", cancellationToken);

    public Task HandleAsync(SteamLoginProgressChanged message, CancellationToken cancellationToken)
        => SendAsync("steamLogin:event", cancellationToken, ToDto(message));

    public Task HandleAsync(FriendsChanged message, CancellationToken cancellationToken)
        => SendAsync("steamFriends:update", cancellationToken, ToDto(message));

    public Task HandleAsync(UpdaterStateChanged message, CancellationToken cancellationToken)
        => SendAsync("updater:event", cancellationToken, ToDto(message));

    internal static SteamLoginEventDto ToDto(SteamLoginProgressChanged message)
        => new(message.Type, message.Data);

    internal static SteamFriendsUpdatedEventDto ToDto(FriendsChanged message)
        => new(message.AccountName, ToDto(message.Data));

    internal static UpdaterEventDto ToDto(UpdaterStateChanged message)
        => new(message.UpdaterEvent, message.Data);

    private static SteamFriendsDataDto ToDto(SteamFriendsSnapshot data)
        => new(
            data.AccountName,
            ToDto(data.CurrentUser),
            data.Friends.Select(ToDto).ToArray(),
            data.LastUpdateTime);

    private static SteamFriendDto ToDto(SteamFriendSnapshot friend)
        => new(
            friend.SteamId,
            friend.PersonaName,
            friend.PersonaState,
            friend.PersonaStateFlags,
            friend.Relationship,
            friend.GameName,
            friend.GameId,
            friend.AvatarHash,
            friend.LastLogOff,
            friend.LastLogOn,
            friend.RichPresence,
            friend.Level);

    private async Task SendAsync(
        string channel,
        CancellationToken cancellationToken,
        params object[] data)
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null)
        {
            logger.LogDebug(
                "Skipped Electron IPC event {Channel} because the main window is {Availability}",
                channel,
                snapshot.Availability);
            return;
        }

        try
        {
            Electron.IpcMain.Send(snapshot.Window, channel, data);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to forward Electron IPC event {Channel}", channel);
        }
    }
}

internal sealed record UpdaterStateChanged(string UpdaterEvent, object? Data = null);
