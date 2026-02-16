namespace ElectronNet.Helpers;

public static class SteamIdHelper
{
    /// <summary>
    /// 将 AccountID 转换为 SteamID
    /// </summary>
    public static string? AccountIdToSteamId(int? accountId)
    {
        if (accountId == null || accountId == 0) return null;

        // SteamID64 = 76561197960265728 + AccountID
        const long steamIdBase = 76561197960265728L;
        return (steamIdBase + accountId).ToString();
    }

    /// <summary>
    /// 将 SteamID 转换为 AccountID
    /// </summary>
    public static int? SteamIdToAccountId(string? steamId)
    {
        if (string.IsNullOrEmpty(steamId)) return null;

        // AccountID = SteamID64 - 76561197960265728
        const long steamIdBase = 76561197960265728L;
        return (int)(Convert.ToInt64(steamId) - steamIdBase);
    }
}