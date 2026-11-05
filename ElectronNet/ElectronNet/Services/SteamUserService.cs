using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Helpers;
using SteamStat.Core.Platform;

namespace ElectronNet.Services;

public sealed class SteamUserService(
    IEventBus eventBus,
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamProfileGateway profileGateway,
    ISteamAvatarUriProvider avatarUriProvider,
    ISteamInstallLocator installLocator,
    IAppPaths appPaths,
    LocalFileService localFileService,
    FileHelper fileHelper,
    GlobalStatusService globalStatusService,
    ILogger<SteamUserService> logger)
{
    private static readonly TimeSpan DecorationDownloadTimeout = TimeSpan.FromMinutes(3);

    private readonly Lock _syncDb = new();
    private int _syncingDecorations;

    /// <summary>
    /// 同步最新的数据到数据库
    /// </summary>
    public async Task SyncDb(CancellationToken cancellationToken = default)
    {
        try
        {
            // 获取默认头像
            var tempFolderPath = appPaths.TempDirectory;
            var defaultDownloads = new List<Task<string?>>();
            if (!File.Exists(Path.Combine(tempFolderPath, "AvatarFull", "default.jpg")))
            {
                defaultDownloads.Add(fileHelper.DownloadFileAsync(avatarUriProvider.GetDefaultAvatarUri(SteamAvatarSize.Full).ToString(), Path.Combine(tempFolderPath, "AvatarFull"), "default", cancellationToken));
            }
            if (!File.Exists(Path.Combine(tempFolderPath, "AvatarMedium", "default.jpg")))
            {
                defaultDownloads.Add(fileHelper.DownloadFileAsync(avatarUriProvider.GetDefaultAvatarUri(SteamAvatarSize.Medium).ToString(), Path.Combine(tempFolderPath, "AvatarMedium"), "default", cancellationToken));
            }
            if (!File.Exists(Path.Combine(tempFolderPath, "AvatarSmall", "default.jpg")))
            {
                defaultDownloads.Add(fileHelper.DownloadFileAsync(avatarUriProvider.GetDefaultAvatarUri(SteamAvatarSize.Small).ToString(), Path.Combine(tempFolderPath, "AvatarSmall"), "default", cancellationToken));
            }
            await Task.WhenAll(defaultDownloads);

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
            var loginUsers = localFileService.ReadLoginUsersVdf(steamPath);

            if (loginUsers.Count == 0)
            {
                logger.LogInformation("No Steam login users were found");
                return;
            }

            // 查询数据库中已存在的 SteamId
            var steamIds = loginUsers.Select(u => u.SteamID).ToHashSet();
            var existingUsers = await db.SteamUserTable.ToListAsync(cancellationToken);
            var existingSteamIds = existingUsers.Select(u => u.SteamId).ToHashSet();

            // 分离新增/更新/删除的用户
            var usersToInsert = loginUsers.Where(u => !existingSteamIds.Contains(u.SteamID)).ToList(); // 文件中存在，数据库中不存在
            var usersToUpdate = loginUsers.Where(u => existingSteamIds.Contains(u.SteamID)).ToList(); // 文件中存在，数据中存在
            var usersToDelete = existingUsers.Where(u => !steamIds.Contains(u.SteamId)).ToList(); // 数据库中存在，文件中不存在

            var insertCount = 0;
            var updateCount = 0;
            var deleteCount = 0;

            // 插入新用户
            foreach (var userVdf in usersToInsert)
            {
                var newUser = new SteamUser
                {
                    SteamId = userVdf.SteamID,
                    AccountId = SteamIdHelper.SteamIdToAccountId(userVdf.SteamID)!.Value,
                    AccountName = userVdf.AccountName,
                    PersonaName = userVdf.PersonaName,
                    RememberPassword = userVdf.RememberPassword,
                    WantsOfflineMode = userVdf.WantsOfflineMode,
                    SkipOfflineModeWarning = userVdf.SkipOfflineModeWarning,
                    AllowAutoLogin = userVdf.AllowAutoLogin,
                    MostRecent = userVdf.MostRecent,
                    Timestamp = userVdf.Timestamp,
                    AvatarFull = Path.Combine(tempFolderPath, "AvatarFull", "default.jpg"),
                    AvatarMedium = Path.Combine(tempFolderPath, "AvatarMedium", "default.jpg"),
                    AvatarSmall = Path.Combine(tempFolderPath, "AvatarSmall", "default.jpg")
                };
                db.SteamUserTable.Add(newUser);
                insertCount++;
            }

            // 更新已存在的用户
            foreach (var userVdf in usersToUpdate)
            {
                var existingUser = existingUsers.First(u => u.SteamId == userVdf.SteamID);

                existingUser.SteamId = userVdf.SteamID;
                existingUser.AccountId = SteamIdHelper.SteamIdToAccountId(userVdf.SteamID)!.Value;
                existingUser.AccountName = userVdf.AccountName;
                existingUser.PersonaName = userVdf.PersonaName;
                existingUser.RememberPassword = userVdf.RememberPassword;
                existingUser.WantsOfflineMode = userVdf.WantsOfflineMode;
                existingUser.SkipOfflineModeWarning = userVdf.SkipOfflineModeWarning;
                existingUser.AllowAutoLogin = userVdf.AllowAutoLogin;
                existingUser.MostRecent = userVdf.MostRecent;
                existingUser.Timestamp = userVdf.Timestamp;
                existingUser.AvatarFull ??= Path.Combine(tempFolderPath, "AvatarFull", "default.jpg");
                existingUser.AvatarMedium ??= Path.Combine(tempFolderPath, "AvatarMedium", "default.jpg");
                existingUser.AvatarSmall ??= Path.Combine(tempFolderPath, "AvatarSmall", "default.jpg");

                updateCount++;
            }

            // 删除不存在的用户
            foreach (var steamUser in usersToDelete)
            {
                db.SteamUserTable.Remove(steamUser);
                deleteCount++;
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Synchronized {UserCount} Steam users: {InsertedCount} inserted, {UpdatedCount} updated, {DeletedCount} deleted", insertCount + updateCount + deleteCount, insertCount, updateCount, deleteCount);

            try
            {
                // 并行获取所有用户的头像和等级信息
                await Task.WhenAll(loginUsers.Select(user => SyncUserProfileAsync(
                    user.AccountName, user.SteamID, steamPath, cancellationToken)));
            }
            finally
            {
                // 无论成功失败，更新刷新时间
                await globalStatusService.UpdateSteamUserRefreshTime(cancellationToken);

                // 通知前端刷新
                await eventBus.PublishAsync(new LoginUsersChanged(), cancellationToken);

                // 修改 loginusers.vdf 中过时的 PersonaName
                await using var taskDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var users = await taskDb.SteamUserTable.AsNoTracking().ToListAsync(cancellationToken);
                var writeSuccess = localFileService.WriteLoginUsersVdf(steamPath, users);
                if (writeSuccess) logger.LogInformation("Updated Steam login users VDF");
                else logger.LogWarning("Failed to update Steam login users VDF");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to synchronize Steam users");
        }
    }

    /// <summary>
    /// 获取所有数据
    /// </summary>
    public List<SteamUser> GetAll()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            return db.SteamUserTable.AsNoTracking().ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query Steam users");
            return [];
        }
    }

    /// <summary>
    /// 同步全局状态并返回全部数据
    /// </summary>
    public async Task<List<SteamUser>> SyncAndGetAll(CancellationToken cancellationToken = default)
    {
        await SyncDb(cancellationToken);
        _ = SyncDecorationsAsync(CancellationToken.None);
        return GetAll();
    }

    /// <summary>
    /// 获取有记录的用户
    /// </summary>
    public List<SteamUser> GetUsersInRecords()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            var steamIds = db.UseAppRecordTable.AsNoTracking().Select(record => record.SteamId).ToHashSet();

            return db.SteamUserTable
                .AsNoTracking()
                .Where(user => steamIds.Contains(user.SteamId))
                .ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query Steam users with usage records");
            return [];
        }
    }

    /// <summary>
    /// 同步用户头像和等级信息。CM 只有已登录的账号才有，其余账号的头像取 Steam 本机缓存
    /// </summary>
    private async Task SyncUserProfileAsync(
        string accountName,
        string steamId,
        string? steamPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ulong.TryParse(steamId, out var steamIdValue)) return;
            var result = await profileGateway.GetProfileAsync(
                accountName, steamIdValue, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value == null)
            {
                ImportLocalAvatar(steamPath, steamId);
                return;
            }
            var profile = result.Value;
            var avatarFullPath = await DownloadAvatarAsync(
                profile.AvatarHash, SteamAvatarSize.Full, "AvatarFull", steamId, cancellationToken);
            var avatarMediumPath = await DownloadAvatarAsync(
                profile.AvatarHash, SteamAvatarSize.Medium, "AvatarMedium", steamId, cancellationToken);
            var avatarSmallPath = await DownloadAvatarAsync(
                profile.AvatarHash, SteamAvatarSize.Small, "AvatarSmall", steamId, cancellationToken);

            // 同步数据库（使用锁确保并行任务不会冲突）
            lock (_syncDb)
            {
                using var db = dbContextFactory.CreateDbContext();
                var steamUser = db.SteamUserTable.First(u => u.SteamId == steamId);

                if (!string.IsNullOrWhiteSpace(profile.PersonaName)) steamUser.PersonaName = profile.PersonaName;
                // 由于网络问题获取失败会返回 string.Empty，不更新此字段
                if (!string.IsNullOrEmpty(avatarFullPath)) steamUser.AvatarFull = avatarFullPath;
                if (!string.IsNullOrEmpty(avatarMediumPath)) steamUser.AvatarMedium = avatarMediumPath;
                if (!string.IsNullOrEmpty(avatarSmallPath)) steamUser.AvatarSmall = avatarSmallPath;
                if (profile.Level.HasValue) steamUser.Level = profile.Level;

                db.SaveChanges();
            }
            if (string.IsNullOrEmpty(avatarFullPath)) ImportLocalAvatar(steamPath, steamId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to synchronize a Steam user profile with {ExceptionType}",
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// 后台从公开资料同步等级、头像框和动态头像，账号不需要登录，每有更新就通知渲染层刷新。
    /// 这些只是装饰信息，失败时保留现有数据，也不能让调用方等待：部分网络下这个接口不通。
    /// 先写完全部账号的等级，再下载图片：图片可能是几 MB 的动画，不能挡住其他账号。
    /// </summary>
    public async Task SyncDecorationsAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _syncingDecorations, 1) == 1) return;
        try
        {
            var downloads = await SyncLevelsAsync(cancellationToken);
            await eventBus.PublishAsync(new LoginUsersChanged(), cancellationToken);
            foreach (var download in downloads)
            {
                var path = await DownloadDecorationAsync(download.Url, download.Directory, cancellationToken);
                if (string.IsNullOrEmpty(path)) continue;
                UpdateUser(download.SteamId, steamUser => download.Apply(steamUser, path));
                await eventBus.PublishAsync(new LoginUsersChanged(), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 退出中
        }
        catch (Exception exception)
        {
            logger.LogWarning("Failed to synchronize Steam user decorations with {ExceptionType}", exception.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _syncingDecorations, 0);
        }
    }

    /// <summary>逐个请求，遇到第一次失败就停止，不用每个账号都等一次超时。返回需要下载的图片</summary>
    private async Task<List<DecorationDownload>> SyncLevelsAsync(CancellationToken cancellationToken)
    {
        var downloads = new List<DecorationDownload>();
        foreach (var user in GetAll())
        {
            if (!ulong.TryParse(user.SteamId, out var steamId)) continue;
            var result = await profileGateway.GetPublicProfileAsync(steamId, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value == null) break;
            var profile = result.Value;
            UpdateUser(user.SteamId, steamUser =>
            {
                if (profile.Level.HasValue) steamUser.Level = profile.Level;
                if (!string.IsNullOrEmpty(profile.LevelClass)) steamUser.LevelClass = profile.LevelClass;
                // 响应里没有才清除；下载失败时保留原来的图片
                if (profile.AvatarFrameUrl == null) steamUser.AvatarFrame = null;
                if (profile.AnimatedAvatarUrl == null) steamUser.AnimatedAvatar = null;
            });
            if (profile.AvatarFrameUrl != null)
                downloads.Add(new(user.SteamId, profile.AvatarFrameUrl, "AvatarFrame", (target, path) => target.AvatarFrame = path));
            if (profile.AnimatedAvatarUrl != null)
                downloads.Add(new(user.SteamId, profile.AnimatedAvatarUrl, "AnimatedAvatar", (target, path) => target.AnimatedAvatar = path));
        }
        return downloads;
    }

    /// <summary>文件按地址里的哈希命名，已下载过的图片不再下载，账号之间也能共用</summary>
    private async Task<string?> DownloadDecorationAsync(string url, string directoryName, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(appPaths.TempDirectory, directoryName);
        var name = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
        if (Directory.Exists(directory)
            && Directory.EnumerateFiles(directory, $"{name}.*").FirstOrDefault() is { } existing)
            return existing;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(DecorationDownloadTimeout);
        try
        {
            return await fileHelper.DownloadFileAsync(url, directory, name, attempt.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed record DecorationDownload(string SteamId, string Url, string Directory, Action<SteamUser, string> Apply);

    private void UpdateUser(string steamId, Action<SteamUser> update)
    {
        lock (_syncDb)
        {
            using var db = dbContextFactory.CreateDbContext();
            update(db.SteamUserTable.First(u => u.SteamId == steamId));
            db.SaveChanges();
        }
    }

    private void ImportLocalAvatar(string? steamPath, string steamId)
    {
        lock (_syncDb)
        {
            using var db = dbContextFactory.CreateDbContext();
            var steamUser = db.SteamUserTable.First(u => u.SteamId == steamId);
            var imported = SteamLocalAvatarCache.Import(steamPath, steamId, appPaths.TempDirectory, steamUser.AvatarFull);
            if (imported == null) return;
            steamUser.AvatarFull = imported;
            steamUser.AvatarMedium = imported;
            steamUser.AvatarSmall = imported;
            db.SaveChanges();
        }
    }

    private Task<string?> DownloadAvatarAsync(
        string? avatarHash,
        SteamAvatarSize size,
        string directory,
        string steamId,
        CancellationToken cancellationToken)
        => fileHelper.DownloadFileAsync(
            avatarUriProvider.GetAvatarUri(avatarHash, size)?.ToString(),
            Path.Combine(appPaths.TempDirectory, directory),
            steamId,
            cancellationToken);
}
