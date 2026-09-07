namespace SteamStat.Core.Environment;

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);

        UserDataDirectory = Path.GetFullPath(userDataDirectory);
        DatabaseDirectory = Path.Combine(UserDataDirectory, "Database");
        DatabaseFile = Path.Combine(DatabaseDirectory, "steam-stat.db");
        DatabaseBackupFile = Path.Combine(DatabaseDirectory, "steam-stat.bak");
        SettingsDirectory = Path.Combine(UserDataDirectory, "Settings");
        SettingsFile = Path.Combine(SettingsDirectory, "app-settings.json");
        LogsDirectory = Path.Combine(UserDataDirectory, "Logs");
        LogFilePattern = Path.Combine(LogsDirectory, "steam-stat-.log");
        TempDirectory = Path.Combine(UserDataDirectory, "Temp");
    }

    public string UserDataDirectory { get; }
    public string DatabaseDirectory { get; }
    public string DatabaseFile { get; }
    public string DatabaseBackupFile { get; }
    public string SettingsDirectory { get; }
    public string SettingsFile { get; }
    public string LogsDirectory { get; }
    public string LogFilePattern { get; }
    public string TempDirectory { get; }
}
