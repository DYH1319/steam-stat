using System.Security.Cryptography;
using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Infrastructure;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;

namespace ElectronNet.Services;

/// <summary>自定义背景图：复制到用户数据目录，原文件移动或删除后背景仍然可用。</summary>
internal sealed class AppearanceService(
    IMainWindowAccessor mainWindowAccessor,
    AppEnvironment environment,
    ILogger<AppearanceService> logger)
{
    internal const long MaxBackgroundBytes = 32L * 1024 * 1024;
    private static readonly string[] Extensions = ["png", "jpg", "jpeg", "webp", "avif", "gif", "bmp"];

    public async Task<AppearanceBackgroundPickResultDto> PickBackgroundAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return new() { Cancelled = true };
        try
        {
            var files = await Electron.Dialog.ShowOpenDialogAsync(snapshot.Window, new OpenDialogOptions
            {
                DefaultPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Properties = [OpenDialogProperty.openFile],
                Filters = [new FileFilter { Name = "Images", Extensions = Extensions }]
            }).ConfigureAwait(false);
            if (files is not { Length: > 0 }) return new() { Cancelled = true };
            return await ImportAsync(files[0], Path.Combine(environment.Paths.UserDataDirectory, "Backgrounds")).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Failed to import a background image");
            return new() { Cancelled = false, Error = "failed" };
        }
    }

    /// <summary>同名按内容哈希，导入成功后删掉旧背景，目录里始终只有当前这一张</summary>
    internal static async Task<AppearanceBackgroundPickResultDto> ImportAsync(string source, string directory)
    {
        var extension = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
        if (!Extensions.Contains(extension)) return new() { Cancelled = false, Error = "unsupported" };
        if (new FileInfo(source).Length > MaxBackgroundBytes) return new() { Cancelled = false, Error = "tooLarge" };

        Directory.CreateDirectory(directory);
        string hash;
        await using (var stream = File.OpenRead(source))
        {
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false))[..16];
        }

        var target = Path.Combine(directory, $"background-{hash}.{extension}");
        File.Copy(source, target, overwrite: true);
        foreach (var file in Directory.EnumerateFiles(directory).Where(file => file != target))
        {
            File.Delete(file);
        }
        return new() { Cancelled = false, Path = target };
    }
}
