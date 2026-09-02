using SteamStat.Core.Environment;
using SteamStat.Core.Features;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

public sealed class SteamLanguageProvider(
    ISettingsStore settingsStore,
    AppEnvironment environment) : ILanguageProvider
{
    public string GetSteamLanguage()
    {
        var language = settingsStore.GetSettings().Language;
        if (string.IsNullOrEmpty(language) || language == "system") language = environment.Locale;
        if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return language.Contains("TW", StringComparison.OrdinalIgnoreCase)
                   || language.Contains("HK", StringComparison.OrdinalIgnoreCase)
                ? "tchinese"
                : "schinese";
        }
        return "english";
    }
}
