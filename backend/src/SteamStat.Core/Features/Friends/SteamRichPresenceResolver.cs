using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamStat.Core.Features.Friends;

public sealed class SteamRichPresenceResolver(
    ILanguageProvider languageProvider,
    ILogger<SteamRichPresenceResolver> logger) : IRichPresenceResolver, IDisposable
{
    private readonly ConcurrentDictionary<(uint AppId, string Language), Task<IReadOnlyDictionary<string, string>>> _localizationCache = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public async Task<string> ResolveAsync(
        SteamClient client,
        uint appId,
        IReadOnlyDictionary<string, string> richPresence,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (appId == 0 || richPresence.Count == 0) return string.Empty;
        richPresence.TryGetValue("steam_display", out var displayToken);
        richPresence.TryGetValue("status", out var status);
        if (string.IsNullOrEmpty(displayToken)) return status ?? string.Empty;

        var tokens = await GetLocalizationAsync(client, appId, languageProvider.GetSteamLanguage(), cancellationToken)
            .ConfigureAwait(false);
        var resolved = ResolveTokens(displayToken, richPresence, tokens);
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
            result = Regex.Replace(result, "%([^%]+)%", match => richPresence.GetValueOrDefault(match.Groups[1].Value, match.Value));
            result = Regex.Replace(result, @"\{#([A-Za-z0-9_]+)\}|#([A-Za-z0-9_]+)", match =>
            {
                var name = $"#{(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)}";
                return tokens.GetValueOrDefault(name, match.Value);
            });
            if (result == before) break;
        }
        return result.Trim();
    }

    private async Task<IReadOnlyDictionary<string, string>> GetLocalizationAsync(
        SteamClient client, uint appId, string language, CancellationToken cancellationToken)
    {
        var key = (appId, language);
        var task = _localizationCache.GetOrAdd(key, _ => FetchLocalizationAsync(client, appId, language));
        var result = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result.Count == 0)
        {
            ((ICollection<KeyValuePair<(uint AppId, string Language), Task<IReadOnlyDictionary<string, string>>>>)_localizationCache)
                .Remove(new(key, task));
        }
        return result;
    }

    private async Task<IReadOnlyDictionary<string, string>> FetchLocalizationAsync(
        SteamClient client, uint appId, string language)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var unifiedMessages = client.GetHandler<SteamUnifiedMessages>();
            if (unifiedMessages == null) return tokens;
            var responseTask = unifiedMessages.SendMessage<CCommunityGetAppRichPresenceLocalizationRequest, CCommunityGetAppRichPresenceLocalizationResponse>(
                "Community.GetAppRichPresenceLocalization#1",
                new CCommunityGetAppRichPresenceLocalizationRequest { appid = appId, language = language });
            _lifetime.Token.ThrowIfCancellationRequested();
            var response = await responseTask;
            _lifetime.Token.ThrowIfCancellationRequested();
            if (response.Result != EResult.OK)
            {
                logger.LogWarning("Rich presence localization failed for {AppId}: {Result}", appId, response.Result);
                return tokens;
            }
            var tokenLists = response.Body?.token_lists ?? [];
            var tokenList = tokenLists.FirstOrDefault(list => list.language == language) ?? tokenLists.FirstOrDefault();
            if (tokenList != null)
                foreach (var token in tokenList.tokens)
                    if (!string.IsNullOrEmpty(token.name)) tokens[token.name] = token.value ?? string.Empty;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to fetch rich presence localization for {AppId}", appId);
        }
        return tokens;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _localizationCache.Clear();
        _lifetime.Dispose();
    }
}
