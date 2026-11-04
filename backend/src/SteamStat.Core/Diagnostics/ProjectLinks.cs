namespace SteamStat.Core.Diagnostics;

/// <summary>公开仓库（DYH1319/steam-stat）的固定链接：Releases、Issue 与更新订阅。</summary>
public static class ProjectLinks
{
    public const string Owner = "DYH1319";
    public const string Repository = "steam-stat";
    public const string RepositoryUrl = "https://github.com/" + Owner + "/" + Repository;
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string ReleasesAtomUrl = RepositoryUrl + "/releases.atom";
    public const string NewIssueUrl = RepositoryUrl + "/issues/new";

    public static string ReleaseTagUrl(string tag) => $"{ReleasesUrl}/tag/{Uri.EscapeDataString(tag)}";
}
