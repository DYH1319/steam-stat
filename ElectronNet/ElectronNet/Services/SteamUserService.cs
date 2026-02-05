using System.Text.Json.Nodes;
using ElectronNET.API;
using ElectronNet.Constants;
using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;

namespace ElectronNet.Services;

public static class SteamUserService
{
    // ReSharper disable once InconsistentNaming
    private static readonly Lock _syncDb = new();

    /// <summary>
    /// 同步最新的数据到数据库
    /// </summary>
    public static async Task SyncDb()
    {
        try
        {
            await using var db = AppDbContext.Create();

            var steamPath = LocalRegService.ReadSteamReg().SteamPath;
            var loginUsers = LocalFileService.ReadLoginUsersVdf(steamPath);

            if (loginUsers.Count == 0)
            {
                Console.WriteLine($"{ConsoleLogPrefix.DB} 没有找到用户数据");
                return;
            }

            // 查询数据库中已存在的 SteamId
            var steamIds = loginUsers.Select(u => u.SteamID).ToHashSet();
            var existingUsers = db.SteamUserTable.ToList();
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
                    Timestamp = userVdf.Timestamp
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

                updateCount++;
            }

            // 删除不存在的用户
            foreach (var steamUser in usersToDelete)
            {
                db.SteamUserTable.Remove(steamUser);
                deleteCount++;
            }

            await db.SaveChangesAsync();
            Console.WriteLine($"{ConsoleLogPrefix.DB} 成功同步 {insertCount + updateCount + deleteCount} 个用户（新增：{insertCount}，更新：{updateCount}，删除：{deleteCount}）");

            // 异步获取头像和等级信息
            _ = Task.Run(async () =>
            {
                try
                {
                    // 获取默认头像
                    var defaultBaseUrl = "https://avatars.akamai.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb";
                    var tempFolderPath = $"{Program.UserDataPath}/Temp";
                    await FileHelper.DownloadFileAsync($"{defaultBaseUrl}_full.jpg", tempFolderPath + "/AvatarFull", "default");
                    await FileHelper.DownloadFileAsync($"{defaultBaseUrl}_medium.jpg", tempFolderPath + "/AvatarMedium", "default");
                    await FileHelper.DownloadFileAsync($"{defaultBaseUrl}.jpg", tempFolderPath + "/AvatarSmall", "default");
                    
                    // 并行获取所有用户的头像和等级信息
                    var tasks = loginUsers.Select(user =>
                        SyncUserAvatarAndLevelFromApi(user.SteamID)
                    ).ToList();

                    await Task.WhenAll(tasks);
                }
                finally
                {
                    // 无论成功失败，更新刷新时间
                    await GlobalStatusService.UpdateSteamUserRefreshTime();

                    // 通知前端刷新
                    if (Program.ElectronMainWindow != null && !await Program.ElectronMainWindow.IsDestroyedAsync())
                    {
                        Electron.IpcMain.Send(Program.ElectronMainWindow, "steam:loginUsers:updated");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(SyncDb)} SteamUser 表失败: {ex}");
        }
    }

    /// <summary>
    /// 获取所有数据
    /// </summary>
    public static List<SteamUser> GetAll()
    {
        try
        {
            using var db = AppDbContext.Create();
            var result = db.SteamUserTable.AsNoTracking().ToList();
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetAll)} SteamUser 表失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 同步全局状态并返回全部数据
    /// </summary>
    public static async Task<List<SteamUser>> SyncAndGetAll()
    {
        await SyncDb();
        return GetAll();
    }

    /// <summary>
    /// 获取有记录的用户
    /// </summary>
    public static List<SteamUser> GetUsersInRecords()
    {
        try
        {
            using var db = AppDbContext.Create();
            var steamIds = db.UseAppRecordTable.AsNoTracking().Select(record => record.SteamId).ToHashSet();

            var result = db.SteamUserTable
                .AsNoTracking()
                .Where(user => steamIds.Contains(user.SteamId))
                .ToList();
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetUsersInRecords)} 失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 异步从 Steam API 同步用户头像和等级信息
    /// </summary>
    private static async Task SyncUserAvatarAndLevelFromApi(long steamId)
    {
        try
        {
            var accountId = SteamIdHelper.SteamIdToAccountId(steamId);
            var url = $"https://steam-chat.com/miniprofile/{accountId}/json";

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var node = JsonNode.Parse(json);
            if (node == null) return;

            var level = node["level"]?.GetValue<int>();
            var levelClass = node["level_class"]?.GetValue<string>();
            var avatarUrl = node["avatar_url"]?.GetValue<string>();
            var personaName = node["persona_name"]?.GetValue<string>();
            var avatarFrame = node["avatar_frame"]?.GetValue<string>();
            var animatedAvatar = node["animated_avatar"]?.GetValue<string>();

            var tempFolderPath = $"{Program.UserDataPath}/Temp";

            var avatarFullPath = await FileHelper.DownloadFileAsync(avatarUrl, tempFolderPath + "/AvatarFull", $"{steamId}");
            var avatarMediumPath = await FileHelper.DownloadFileAsync(avatarUrl?.Replace("_full", "_medium"), tempFolderPath + "/AvatarMedium", $"{steamId}");
            var avatarSmallPath = await FileHelper.DownloadFileAsync(avatarUrl?.Replace("_full", ""), tempFolderPath + "/AvatarSmall", $"{steamId}");
            var animatedAvatarPath = await FileHelper.DownloadFileAsync(animatedAvatar, tempFolderPath + "/AnimatedAvatar", $"{steamId}");
            var avatarFramePath = await FileHelper.DownloadFileAsync(avatarFrame, tempFolderPath + "/AvatarFrame", $"{steamId}");

            // TODO 修改 loginusers.vdf 中过时的 PersonaName

            // 同步数据库（使用锁确保并行任务不会冲突）
            lock (_syncDb)
            {
                using var db = AppDbContext.Create();
                var steamUser = db.SteamUserTable.First(u => u.SteamId == steamId);

                steamUser.PersonaName = personaName;
                steamUser.AvatarFull = avatarFullPath;
                steamUser.AvatarMedium = avatarMediumPath;
                steamUser.AvatarSmall = avatarSmallPath;
                steamUser.AnimatedAvatar = animatedAvatarPath;
                steamUser.AvatarFrame = avatarFramePath;
                steamUser.Level = level;
                steamUser.LevelClass = levelClass;

                db.SaveChanges();
            }
        }
        catch (HttpRequestException)
        {
            Console.WriteLine($"{ConsoleLogPrefix.WARN} {nameof(SyncUserAvatarAndLevelFromApi)}: Requests to Steam are too frequent.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.WARN} {nameof(SyncUserAvatarAndLevelFromApi)}: {ex}");
        }
    }
}