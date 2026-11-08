using ElectronNET.API;
using ElectronNet.Infrastructure;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;

namespace ElectronNet.Hosting;

internal sealed class ElectronIpcEventForwarder(
    IMainWindowAccessor mainWindowAccessor,
    IPopoutWindows popouts,
    ILogger<ElectronIpcEventForwarder> logger) :
    IEventHandler<LoginUsersChanged>,
    IEventHandler<SteamLoginProgressChanged>,
    IEventHandler<FriendsChanged>,
    IEventHandler<UpdaterStateChanged>,
    IEventHandler<AchievementArchiveSyncChanged>,
    IEventHandler<TrayCommandIssued>,
    IEventHandler<Services.SaveBackupProgressChanged>,
    IEventHandler<Services.SyncProgressChanged>,
    IEventHandler<Services.TaskProgressChanged>,
    IEventHandler<Services.WeeklyIssuePublished>,
    IEventHandler<Services.PreviouslyOnReady>,
    IEventHandler<Services.TimeCapsuleReady>,
    IEventHandler<AppSettingsChanged>
{
    /// <summary>主窗口是设置的来源，只需要告诉独立窗口</summary>
    public Task HandleAsync(AppSettingsChanged message, CancellationToken cancellationToken)
    {
        SendToPopouts(SettingIpc.Changed, [message.Settings]);
        return Task.CompletedTask;
    }

    public Task HandleAsync(Services.WeeklyIssuePublished message, CancellationToken cancellationToken)
        => SendAsync(WeeklyIpc.Published, message.Issue, cancellationToken);

    public Task HandleAsync(Services.PreviouslyOnReady message, CancellationToken cancellationToken)
        => SendAsync(MemoirIpc.PreviouslyOn, message.Recap, cancellationToken);

    public Task HandleAsync(Services.TimeCapsuleReady message, CancellationToken cancellationToken)
        => SendAsync(MemoirIpc.CapsuleReady, message.Capsule, cancellationToken);

    public Task HandleAsync(Services.TaskProgressChanged message, CancellationToken cancellationToken)
        => SendAsync(TaskIpc.Progress, message.Status, cancellationToken);

    public Task HandleAsync(Services.SyncProgressChanged message, CancellationToken cancellationToken)
        => SendAsync(SyncIpc.Progress, message.Status, cancellationToken);

    public Task HandleAsync(Services.SaveBackupProgressChanged message, CancellationToken cancellationToken)
        => SendAsync(SaveBackupIpc.Progress, message.Status, cancellationToken);

    public Task HandleAsync(TrayCommandIssued message, CancellationToken cancellationToken)
        => SendAsync(TrayIpc.Command, message.Command, cancellationToken);

    public Task HandleAsync(AchievementArchiveSyncChanged message, CancellationToken cancellationToken)
        => SendAsync(AchievementArchiveIpc.SyncProgress, message.Status, cancellationToken);

    public Task HandleAsync(LoginUsersChanged message, CancellationToken cancellationToken)
        => SendAsync(SteamIpc.LoginUsersUpdated, cancellationToken);

    public Task HandleAsync(SteamLoginProgressChanged message, CancellationToken cancellationToken)
        => SendAsync(SteamLoginIpc.Event, ToDto(message), cancellationToken);

    public Task HandleAsync(FriendsChanged message, CancellationToken cancellationToken)
        => SendAsync(SteamFriendsIpc.Updated, ToDto(message), cancellationToken);

    public Task HandleAsync(UpdaterStateChanged message, CancellationToken cancellationToken)
        => SendAsync(UpdaterIpc.Event, message.Event, cancellationToken);

    internal static SteamLoginEventDto ToDto(SteamLoginProgressChanged message)
        => new()
        {
            Type = message.Type,
            Data = message.Data == null ? null : new SteamLoginEventDataDto
            {
                GuardType = message.Data.GuardType,
                Email = message.Data.Email,
                PreviousCodeWasIncorrect = message.Data.PreviousCodeWasIncorrect,
                QrImageBase64 = message.Data.QrImageBase64,
                AccountName = message.Data.AccountName,
                Message = message.Data.Message,
                ErrorCode = message.Data.ErrorCode
            }
        };

    internal static SteamFriendsUpdatedEventDto ToDto(FriendsChanged message)
        => new(message.AccountName, ToDto(message.Data));

    private static SteamFriendsDataDto ToDto(SteamFriendsSnapshot data)
        => new(
            data.AccountName,
            ToDto(data.CurrentUser),
            data.Friends.Select(ToDto).ToArray(),
            data.LastUpdateTime);

    private static SteamFriendDto ToDto(SteamFriendSnapshot friend)
        => new()
        {
            SteamId = friend.SteamId,
            PersonaName = friend.PersonaName,
            PersonaState = friend.PersonaState,
            PersonaStateFlags = friend.PersonaStateFlags,
            Relationship = friend.Relationship,
            GameName = friend.GameName,
            GameId = friend.GameId,
            AvatarHash = friend.AvatarHash,
            LastLogOff = friend.LastLogOff,
            LastLogOn = friend.LastLogOn,
            RichPresence = friend.RichPresence,
            Level = friend.Level,
            Nickname = friend.Nickname
        };

    private Task SendAsync(
        IpcHostEvent<IpcNoPayload> endpoint,
        CancellationToken cancellationToken)
        => SendAsync(endpoint, cancellationToken, []);

    private Task SendAsync<TPayload>(
        IpcHostEvent<TPayload> endpoint,
        TPayload payload,
        CancellationToken cancellationToken)
        => SendAsync(endpoint, cancellationToken, [payload!]);

    private async Task SendAsync(
        IIpcEndpointDescriptor endpoint,
        CancellationToken cancellationToken,
        object[] data)
    {
        // 托盘命令（含窗口关闭流程）只由主窗口处理
        if (endpoint.Channel != TrayIpc.Command.Channel) SendToPopouts(endpoint, data);

        var snapshot = await mainWindowAccessor.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null)
        {
            logger.LogDebug(
                "Skipped Electron IPC event {Channel} because the main window is {Availability}",
                endpoint.Channel,
                snapshot.Availability);
            return;
        }

        try
        {
            Electron.IpcMain.Send(snapshot.Window, endpoint.Channel, data);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to forward Electron IPC event {Channel}", endpoint.Channel);
        }
    }

    private void SendToPopouts(IIpcEndpointDescriptor endpoint, object[] data)
    {
        foreach (var window in popouts.Windows())
        {
            try
            {
                Electron.IpcMain.Send(window, endpoint.Channel, data);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Skipped a popout window that is closing");
            }
        }
    }
}

internal sealed record AppSettingsChanged(AppSettingsDto Settings);

internal sealed record UpdaterStateChanged(UpdaterEventDto Event);

internal sealed record AchievementArchiveSyncChanged(AchievementArchiveSyncStatusDto Status);

internal sealed record TrayCommandIssued(TrayCommandDto Command);
