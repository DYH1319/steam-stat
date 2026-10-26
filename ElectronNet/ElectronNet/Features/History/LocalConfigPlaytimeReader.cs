using System.Globalization;
using ElectronNet.Helpers;
using SteamStat.Core.Helpers;
using ValveKeyValue;

namespace ElectronNet.Features.History;

/// <summary>某账号某游戏的一次官方累计时长观测</summary>
public sealed record PlaytimeObservation(
    string SteamId,
    int AppId,
    int PlaytimeMinutes,
    int? LastPlayedAt,
    string Source,
    int ObservedAt);

/// <summary>
/// 读取 {SteamPath}\userdata\{accountId}\config\localconfig.vdf 中的
/// UserLocalConfigStore/Software/Valve/Steam/apps/{appId}/Playtime（分钟）与 LastPlayed。
/// </summary>
public static class LocalConfigPlaytimeReader
{
    public static IReadOnlyList<PlaytimeObservation> ReadAll(string steamPath, int observedAt)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return [];

        var result = new List<PlaytimeObservation>();
        foreach (var userDir in Directory.GetDirectories(userdata))
        {
            if (!int.TryParse(Path.GetFileName(userDir), out var accountId) || accountId <= 0) continue;
            var steamId = SteamIdHelper.AccountIdToSteamId(accountId);
            var file = Path.Combine(userDir, "config", "localconfig.vdf");
            if (steamId == null || !File.Exists(file)) continue;
            result.AddRange(Parse(VdfHelper.Read(file), steamId, observedAt));
        }
        return result;
    }

    public static IEnumerable<PlaytimeObservation> Parse(KVObject root, string steamId, int observedAt)
    {
        var apps = Child(Child(Child(Child(root, "Software"), "Valve"), "Steam"), "apps");
        if (apps == null) yield break;

        foreach (var app in apps.Children)
        {
            if (!int.TryParse(app.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId <= 0) continue;
            var playtime = ReadInt(Child(app, "Playtime"));
            if (playtime is not > 0) continue;
            var lastPlayed = ReadInt(Child(app, "LastPlayed"));
            yield return new PlaytimeObservation(
                steamId, appId, playtime.Value, lastPlayed is > 0 ? lastPlayed : null, "localconfig", observedAt);
        }
    }

    private static KVObject? Child(KVObject? parent, string name)
        => parent?.Children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));

    private static int? ReadInt(KVObject? node)
    {
        if (node?.Value == null) return null;
        try
        {
            return Convert.ToInt32(node.Value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
