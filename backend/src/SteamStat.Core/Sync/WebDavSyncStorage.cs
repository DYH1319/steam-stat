using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SteamStat.Core.Sync;

/// <summary>
/// WebDAV（坚果云、Nextcloud / ownCloud、群晖、Apache mod_dav 等）。
/// 只使用 PROPFIND（Depth: 1，逐层列出，兼容不支持 infinity 的服务）、GET、PUT、DELETE、MKCOL。
/// </summary>
public sealed class WebDavSyncStorage : ISyncStorage
{
    private const int MaxDepth = 4;
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Mkcol = new("MKCOL");
    private static readonly XNamespace Dav = "DAV:";
    private const string PropfindBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/></d:prop></d:propfind>""";

    private readonly HttpClient _http;
    private readonly Uri _root;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly HashSet<string> _knownCollections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _collectionGate = new(1, 1);

    public WebDavSyncStorage(HttpClient http, string url, string? userName, string? password, string? remoteRoot = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(baseUri.UserInfo))
            throw new SyncStorageException(SyncStorageErrorKind.Configuration, "The WebDAV address must be an http(s) URL without embedded credentials.");
        var root = SyncKeys.NormalizeRoot(remoteRoot);
        var basePath = baseUri.AbsolutePath.EndsWith('/') ? baseUri.AbsolutePath : baseUri.AbsolutePath + "/";
        var rootPath = root.Length == 0 ? basePath : basePath + string.Join('/', root.Split('/').Select(Uri.EscapeDataString)) + "/";
        _root = new UriBuilder(baseUri) { Path = rootPath, Query = string.Empty, Fragment = string.Empty }.Uri;
        _http = http;
        if (!string.IsNullOrEmpty(userName))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Concat(userName, ":", password)));
            _authorization = new AuthenticationHeaderValue("Basic", token);
        }
        // 根目录之上的部分由用户提供，视为已存在；根目录本身在首次写入时创建
        _rootSegments = root.Length == 0 ? [] : root.Split('/');
        _baseUri = new UriBuilder(baseUri) { Path = basePath, Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    private readonly string[] _rootSegments;
    private readonly Uri _baseUri;

    public async Task<IReadOnlyList<SyncObjectInfo>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        SyncKeys.ValidatePrefix(prefix);
        var result = new List<SyncObjectInfo>();
        await ListCollectionAsync(prefix, 0, result, cancellationToken).ConfigureAwait(false);
        result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return result;
    }

    private async Task ListCollectionAsync(string prefix, int depth, List<SyncObjectInfo> result, CancellationToken cancellationToken)
    {
        if (depth > MaxDepth) return;
        var collection = new Uri(_root, EscapePath(prefix));
        using var request = CreateRequest(Propfind, collection);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        EnsureSuccess(response);

        XDocument document;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (XmlException exception)
        {
            throw new SyncStorageException(SyncStorageErrorKind.Protocol, "The WebDAV server returned an invalid listing.", exception);
        }

        var selfPath = collection.AbsolutePath.TrimEnd('/');
        foreach (var item in document.Descendants(Dav + "response"))
        {
            var href = item.Element(Dav + "href")?.Value;
            if (string.IsNullOrEmpty(href)) continue;
            var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href;
            var decoded = Uri.UnescapeDataString(path).TrimEnd('/');
            if (string.Equals(decoded, Uri.UnescapeDataString(selfPath), StringComparison.Ordinal)) continue;
            var name = decoded[(decoded.LastIndexOf('/') + 1)..];
            if (name.Length == 0) continue;

            var prop = item.Descendants(Dav + "prop").FirstOrDefault();
            var isCollection = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") != null;
            var key = prefix + name;
            if (isCollection)
            {
                if (SyncKeys.IsValid(key)) await ListCollectionAsync(key + "/", depth + 1, result, cancellationToken).ConfigureAwait(false);
            }
            else if (SyncKeys.IsValid(key))
            {
                _ = long.TryParse(prop?.Element(Dav + "getcontentlength")?.Value, out var size);
                result.Add(new SyncObjectInfo(key, size));
            }
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        SyncKeys.Validate(key);
        using var request = CreateRequest(HttpMethod.Get, new Uri(_root, EscapePath(key)));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        SyncKeys.Validate(key);
        await EnsureCollectionsAsync(key, cancellationToken).ConfigureAwait(false);
        using var request = CreateRequest(HttpMethod.Put, new Uri(_root, EscapePath(key)));
        request.Content = new ReadOnlyMemoryContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        SyncKeys.Validate(key);
        using var request = CreateRequest(HttpMethod.Delete, new Uri(_root, EscapePath(key)));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        EnsureSuccess(response);
    }

    /// <summary>逐级 MKCOL 远端根目录与对象的上级目录（已存在时服务端返回 405，视为成功）。</summary>
    private async Task EnsureCollectionsAsync(string key, CancellationToken cancellationToken)
    {
        var segments = _rootSegments.Concat(key.Split('/')[..^1]).ToArray();
        await _collectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = string.Empty;
            foreach (var segment in segments)
            {
                path += Uri.EscapeDataString(segment) + "/";
                if (_knownCollections.Contains(path)) continue;
                using var request = CreateRequest(Mkcol, new Uri(_baseUri, path));
                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed
                    || (int)response.StatusCode == 301 || (int)response.StatusCode == 302)
                {
                    _knownCollections.Add(path);
                    continue;
                }
                EnsureSuccess(response);
            }
        }
        finally
        {
            _collectionGate.Release();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (_authorization != null) request.Headers.Authorization = _authorization;
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new SyncStorageException(SyncStorageErrorKind.Network, "The WebDAV server could not be reached.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SyncStorageException(SyncStorageErrorKind.Network, "The WebDAV request timed out.", exception);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        var kind = status switch
        {
            401 or 403 => SyncStorageErrorKind.Unauthorized,
            404 or 405 or 409 or 400 => SyncStorageErrorKind.Configuration,
            >= 500 or 408 or 429 => SyncStorageErrorKind.Network,
            _ => SyncStorageErrorKind.Protocol
        };
        throw new SyncStorageException(kind, $"The WebDAV server responded with HTTP {status}.");
    }

    private static string EscapePath(string key)
        => string.Join('/', key.Split('/').Select(segment => segment.Length == 0 ? segment : Uri.EscapeDataString(segment)));
}
