namespace ElectronNet.Helpers;

public static class SteamIdHelper
{
    /// <summary>
    /// 将 AccountID 转换为 SteamID
    /// </summary>
    public static long? AccountIdToSteamId(int accountId)
    {
        if (accountId == 0) return null;

        // SteamID64 = 76561197960265728 + AccountID
        const long steamIdBase = 76561197960265728L;
        return steamIdBase + accountId;
    }
}