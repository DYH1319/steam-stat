namespace SteamStat.Core.Http;

public static class SteamStatHttpClients
{
    public const string SteamStore = "SteamStore";
    public const string SteamWebApi = "SteamWebApi";
    public const string SteamCdn = "SteamCdn";
    public const string SteamCommunity = "SteamCommunity";
    public const string SteamChat = "SteamChat";
    /// <summary>带登录状态读网页专用：不开 CookieContainer、不自动跳转，只由 SteamWebFetcher 使用。</summary>
    public const string SteamWeb = "SteamWeb";
    public const string Download = "Download";
    /// <summary>任天堂账号与游玩记录。只走用户配置的代理，不经 Steam 的配额 / 连通性观测管道。</summary>
    public const string Nintendo = "Nintendo";
}
