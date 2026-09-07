namespace SteamStat.Core.Environment;

public sealed class AppEnvironment
{
    public AppEnvironment(bool isDevelopment, string locale, bool isSilentStart, IAppPaths paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentNullException.ThrowIfNull(paths);

        IsDevelopment = isDevelopment;
        Locale = locale;
        IsSilentStart = isSilentStart;
        Paths = paths;
    }

    public bool IsDevelopment { get; }
    public string Locale { get; }
    public bool IsSilentStart { get; }
    public IAppPaths Paths { get; }
}
