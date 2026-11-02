using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Friends.Contracts;

namespace SteamStat.Core.Features.Friends;

public sealed class SteamRichPresenceResolver(
    ILanguageProvider languageProvider,
    ISteamPresenceLocalizationGateway localizationGateway,
    ILogger<SteamRichPresenceResolver> logger) : IRichPresenceResolver, IDisposable
{
    private int _disposed;

    public async Task<string> ResolveAsync(
        string accountName,
        uint appId,
        IReadOnlyDictionary<string, string> richPresence,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (appId == 0 || richPresence.Count == 0) return string.Empty;
        richPresence.TryGetValue("steam_display", out var displayToken);
        richPresence.TryGetValue("status", out var status);
        if (string.IsNullOrEmpty(displayToken)) return status ?? string.Empty;
        var language = languageProvider.GetSteamLanguage();
        var resolved = await ResolveWithLanguageAsync(
            accountName, appId, language, displayToken, richPresence, cancellationToken).ConfigureAwait(false);
        // 目标语言没有该词条时退回英文
        if (!IsResolved(resolved) && !string.Equals(language, FallbackLanguage, StringComparison.OrdinalIgnoreCase))
            resolved = await ResolveWithLanguageAsync(
                accountName, appId, FallbackLanguage, displayToken, richPresence, cancellationToken).ConfigureAwait(false);
        return IsResolved(resolved) ? resolved! : status ?? string.Empty;
    }

    private const string FallbackLanguage = "english";

    private static bool IsResolved(string? value) => !string.IsNullOrEmpty(value) && !value.StartsWith('#');

    private async Task<string?> ResolveWithLanguageAsync(
        string accountName,
        uint appId,
        string language,
        string displayToken,
        IReadOnlyDictionary<string, string> richPresence,
        CancellationToken cancellationToken)
    {
        var result = await localizationGateway.GetLocalizationAsync(
            accountName,
            appId,
            language,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null)
        {
            logger.LogDebug("Rich presence localization is unavailable: {DiagnosticCode}", result.DiagnosticCode);
            return null;
        }
        return ResolveTokens(displayToken, richPresence, result.Value);
    }

    private static string ResolveTokens(
        string displayToken,
        IReadOnlyDictionary<string, string> richPresence,
        IReadOnlyDictionary<string, string> tokens)
    {
        var result = tokens.GetValueOrDefault(displayToken, displayToken);
        for (var i = 0; i < 10; i++)
        {
            var before = result;
            result = Regex.Replace(
                result,
                "%([^%]+)%",
                match => richPresence.GetValueOrDefault(match.Groups[1].Value, match.Value));
            result = Regex.Replace(result, @"\{#([A-Za-z0-9_]+)\}|#([A-Za-z0-9_]+)", match =>
            {
                var name = $"#{(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)}";
                return tokens.GetValueOrDefault(name, match.Value);
            });
            if (result == before) break;
        }
        return result.Trim();
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
