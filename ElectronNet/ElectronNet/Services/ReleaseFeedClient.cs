using Microsoft.Extensions.Logging;
using SteamStat.Core.Http;
using SteamStat.Core.Diagnostics;
using SteamStat.Core.Updates;

namespace ElectronNet.Services;

/// <summary>读取公开仓库的 GitHub Releases 订阅（releases.atom，不消耗 API 限额，草稿不可见）。</summary>
internal interface IReleaseFeedClient
{
    Task<IReadOnlyList<ReleaseFeedEntry>> GetAsync(CancellationToken cancellationToken);
}

internal sealed class GitHubReleaseFeedClient(
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubReleaseFeedClient> logger) : IReleaseFeedClient
{
    private const int MaxFeedBytes = 2 * 1024 * 1024;

    public async Task<IReadOnlyList<ReleaseFeedEntry>> GetAsync(CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(SteamStatHttpClients.Download);
        using var request = new HttpRequestMessage(HttpMethod.Get, ProjectLinks.ReleasesAtomUrl);
        request.Headers.Accept.ParseAdd("application/atom+xml");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxFeedBytes)
            throw new InvalidDataException("The release feed is unexpectedly large");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (limited.Length + read > MaxFeedBytes) throw new InvalidDataException("The release feed is unexpectedly large");
            limited.Write(buffer, 0, read);
        }

        var entries = ReleaseFeed.Parse(System.Text.Encoding.UTF8.GetString(limited.GetBuffer(), 0, (int)limited.Length));
        logger.LogDebug("Release feed returned {ReleaseCount} versions", entries.Count);
        return entries;
    }
}
