using System.Text.Json.Nodes;
using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Helpers;
using SteamStat.Core.Http;
using SteamStat.Core.Platform;

namespace ElectronNet.Services;

public sealed class SteamUserService(
    IEventBus eventBus,
    IDbContextFactory<AppDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    ISteamInstallLocator installLocator,
    IAppPaths appPaths,
    LocalFileService localFileService,
    FileHelper fileHelper,
    GlobalStatusService globalStatusService,
    ILogger<SteamUserService> logger)
{
    private readonly Lock _syncDb = new();

    /// <summary>
    /// 同步最新的数据到数据库
    /// </summary>
    public async Task SyncDb(CancellationToken cancellationToken = default)
    {
        try
        {
            // 获取默认头像
            var defaultBaseUrl = "https://avatars.akamai.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb";
            var tempFolderPath = appPaths.TempDirectory;
            var defaultDownloads = new List<Task<string?>>();
            if (!File.Exists(Path.Combine(tempFolderPath, "AvatarFull", "default.jpg")))
            {
                defaultDownloads.Add(fileHelper.DownloadFileAsync($"{defaultBaseUrl}_full.jpg", Path.Combine(tempFolderPath, "AvatarFull"), "default", cancellationToken));
            }
            if (!File.Exists(Path.Combine(tempFolderPath, "AvatarMedium", "default.jpg")))
            {
                defaultDownloads.Add(fileHelper.DownloadFileAsync($"{defaultBaseUrl}_medium.jpg", Path.Combine(tempFolderPath, "AvatarMedium"), "default", cancellationToken));
            }
            if (!File.Exists(Path.Combine(tempFolderPath, "AvatarSmall", "default.jpg")))
            {
                defaultDownloads.Add(fileHelper.DownloadFileAsync($"{defaultBaseUrl}.jpg", Path.Combine(tempFolderPath, "AvatarSmall"), "default", cancellationToken));
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
                await Task.WhenAll(loginUsers.Select(user => SyncUserAvatarAndLevelFromApi(user.SteamID, cancellationToken)));
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
    /// 异步从 Steam API 同步用户头像和等级信息
    /// </summary>
    private async Task SyncUserAvatarAndLevelFromApi(string steamId, CancellationToken cancellationToken)
    {
        try
        {
            var accountId = SteamIdHelper.SteamIdToAccountId(steamId);
            var url = $"https://steam-chat.com/miniprofile/{accountId}/json";

            using var response = await httpClientFactory.CreateClient(SteamStatHttpClients.SteamApi).GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var node = JsonNode.Parse(json);
            if (node == null) return;

            var level = node["level"]?.GetValue<int>();
            var levelClass = node["level_class"]?.GetValue<string>();
            var avatarUrl = node["avatar_url"]?.GetValue<string>();
            var personaName = node["persona_name"]?.GetValue<string>();
            var avatarFrame = node["avatar_frame"]?.GetValue<string>();
            var animatedAvatar = node["animated_avatar"]?.GetValue<string>();

            var avatarFullPath = await fileHelper.DownloadFileAsync(avatarUrl, Path.Combine(appPaths.TempDirectory, "AvatarFull"), steamId, cancellationToken);
            var avatarMediumPath = await fileHelper.DownloadFileAsync(avatarUrl?.Replace("_full", "_medium"), Path.Combine(appPaths.TempDirectory, "AvatarMedium"), steamId, cancellationToken);
            var avatarSmallPath = await fileHelper.DownloadFileAsync(avatarUrl?.Replace("_full", ""), Path.Combine(appPaths.TempDirectory, "AvatarSmall"), steamId, cancellationToken);
            var animatedAvatarPath = await fileHelper.DownloadFileAsync(animatedAvatar, Path.Combine(appPaths.TempDirectory, "AnimatedAvatar"), steamId, cancellationToken);
            var avatarFramePath = await fileHelper.DownloadFileAsync(avatarFrame, Path.Combine(appPaths.TempDirectory, "AvatarFrame"), steamId, cancellationToken);

            // 同步数据库（使用锁确保并行任务不会冲突）
            lock (_syncDb)
            {
                using var db = dbContextFactory.CreateDbContext();
                var steamUser = db.SteamUserTable.First(u => u.SteamId == steamId);

                steamUser.PersonaName = personaName;
                // 由于网络问题获取失败会返回 string.Empty，不更新此字段
                steamUser.AvatarFull = avatarFullPath == string.Empty ? steamUser.AvatarFull : avatarFullPath;
                steamUser.AvatarMedium = avatarMediumPath == string.Empty ? steamUser.AvatarMedium : avatarMediumPath;
                steamUser.AvatarSmall = avatarSmallPath == string.Empty ? steamUser.AvatarSmall : avatarSmallPath;
                steamUser.AnimatedAvatar = animatedAvatarPath == string.Empty ? steamUser.AnimatedAvatar : animatedAvatarPath;
                steamUser.AvatarFrame = avatarFramePath == string.Empty ? steamUser.AvatarFrame : avatarFramePath;
                steamUser.Level = level;
                steamUser.LevelClass = levelClass;

                db.SaveChanges();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Steam profile requests are being throttled");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to synchronize a Steam user profile");
        }
    }
}
