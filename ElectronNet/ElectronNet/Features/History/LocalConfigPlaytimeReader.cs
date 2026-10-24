using System.Globalization;
using ElectronNet.Helpers;
using ValveKeyValue;

namespace ElectronNet.Features.History;

/// <summary>
/// 从 localconfig.vdf / CM 等来源观察到的一次官方累计时长
/// </summary>
public sealed record PlaytimeObservation(
    string SteamId,
    int AppId,
    int PlaytimeMinutes,
    int? LastPlayed,
    int ObservedAt);

/// <summary>
/// 读取 userdata/{accountId}/config/localconfig.vdf 中的官方累计时长
/// </summary>
public static class LocalConfigPlaytimeReader
{
    private const ulong SteamId64Base = 76561197960265728UL;

    /// <summary>
    /// 解析单个 localconfig.vdf，返回其中的累计时长条目
    /// </summary>
    public static IReadOnlyList<PlaytimeObservation> Read(string localConfigPath, string steamId, int observedAt)
    {
        var document = VdfHelper.Read(localConfigPath);
        var root = FindChild(document, "UserLocalConfigStore") ?? document;
        var apps = FindChild(
            FindChild(
                FindChild(
                    FindChild(root, "Software"),
                    "Valve"),
                "Steam"),
            "apps");
        if (apps == null) return [];

        var observations = new List<PlaytimeObservation>();
        foreach (var appEntry in apps.Children)
        {
            if (!int.TryParse(appEntry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId)
                || appId <= 0)
            {
                continue;
            }
            var playtime = ToInt64(FindChild(appEntry, "Playtime"));
            if (playtime is not > 0 || playtime > int.MaxValue) continue;
            var lastPlayed = ToInt64(FindChild(appEntry, "LastPlayed"));
            observations.Add(new PlaytimeObservation(
                steamId,
                appId,
                (int)playtime.Value,
                lastPlayed is > 0 and <= int.MaxValue ? (int)lastPlayed.Value : null,
                observedAt));
        }
        return observations;
    }

    /// <summary>
    /// 枚举 {steamPath}/userdata 下所有带 config/localconfig.vdf 的用户目录
    /// </summary>
    public static IEnumerable<(string SteamId, string Path)> EnumerateUserConfigs(string steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath)) yield break;
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) yield break;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(userdata);
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (!uint.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var accountId)
                || accountId == 0)
            {
                continue;
            }
            var configPath = Path.Combine(directory, "config", "localconfig.vdf");
            if (!File.Exists(configPath)) continue;
            yield return ((SteamId64Base + accountId).ToString(CultureInfo.InvariantCulture), configPath);
        }
    }

    private static KVObject? FindChild(KVObject? node, string name)
        => node?.Children.FirstOrDefault(child =>
            string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));

    private static long? ToInt64(KVObject? node)
    {
        if (node?.Value == null) return null;
        try
        {
            return Convert.ToInt64(node.Value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }
}
