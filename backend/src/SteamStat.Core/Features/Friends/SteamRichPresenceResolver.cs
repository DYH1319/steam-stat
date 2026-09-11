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
        var result = await localizationGateway.GetLocalizationAsync(
            accountName,
            appId,
            languageProvider.GetSteamLanguage(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null)
        {
            logger.LogDebug("Rich presence localization is unavailable: {DiagnosticCode}", result.DiagnosticCode);
            return status ?? string.Empty;
        }
        var resolved = ResolveTokens(displayToken, richPresence, result.Value);
        return string.IsNullOrEmpty(resolved) || resolved.StartsWith('#') ? status ?? string.Empty : resolved;
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
