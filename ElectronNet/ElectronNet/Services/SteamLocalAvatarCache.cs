namespace ElectronNet.Services;

/// <summary>
/// Steam 客户端在本机缓存的账号头像（{Steam}/config/avatarcache/{SteamID}.png，184×184）。
/// CM 只能取到当前已登录账号的头像，其余本地账号靠它显示，不需要联网。
/// </summary>
internal static class SteamLocalAvatarCache
{
    /// <summary>
    /// 复制到应用目录（渲染层只能读应用目录下的文件）。
    /// 没有缓存，或现有头像不比缓存旧时返回 null，避免用旧缓存盖掉 CM 下载的新头像。
    /// </summary>
    public static string? Import(string? steamPath, string steamId, string tempDirectory, string? currentAvatar)
    {
        if (string.IsNullOrWhiteSpace(steamPath)) return null;
        var source = Path.Combine(steamPath, "config", "avatarcache", $"{steamId}.png");
        if (!File.Exists(source)) return null;

        var isRealAvatar = !string.IsNullOrEmpty(currentAvatar)
                           && Path.GetFileNameWithoutExtension(currentAvatar) != "default"
                           && File.Exists(currentAvatar);
        if (isRealAvatar && File.GetLastWriteTimeUtc(currentAvatar!) >= File.GetLastWriteTimeUtc(source)) return null;

        var directory = Path.Combine(tempDirectory, "AvatarFull");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"{steamId}.png");
        File.Copy(source, target, overwrite: true);
        return target;
    }
}
