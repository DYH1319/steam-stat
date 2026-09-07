namespace SteamStat.Core.Environment;

public interface IAppPaths
{
    string UserDataDirectory { get; }
    string DatabaseDirectory { get; }
    string DatabaseFile { get; }
    string DatabaseBackupFile { get; }
    string SettingsDirectory { get; }
    string SettingsFile { get; }
    string LogsDirectory { get; }
    string LogFilePattern { get; }
    string TempDirectory { get; }
}
