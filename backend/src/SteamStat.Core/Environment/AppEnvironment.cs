namespace SteamStat.Core.Environment;

public sealed class AppEnvironment
{
    public AppEnvironment(bool isDevelopment, string locale, bool isSilentStart, IAppPaths paths, string? appVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentNullException.ThrowIfNull(paths);

        IsDevelopment = isDevelopment;
        Locale = locale;
        IsSilentStart = isSilentStart;
        Paths = paths;
        AppVersion = string.IsNullOrWhiteSpace(appVersion) ? null : appVersion;
    }

    public bool IsDevelopment { get; }
    public string Locale { get; }
    public bool IsSilentStart { get; }
    public IAppPaths Paths { get; }

    /// <summary>应用版本（语义化版本，如 4.1.0 或 4.1.0-beta.1）；测试构造时可为空。</summary>
    public string? AppVersion { get; }
}
