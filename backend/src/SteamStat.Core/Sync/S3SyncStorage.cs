using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SteamStat.Core.Sync;

public sealed record S3SyncOptions
{
    /// <summary>服务地址，例如 https://s3.amazonaws.com、https://&lt;account&gt;.r2.cloudflarestorage.com、https://oss-cn-hangzhou.aliyuncs.com、MinIO 地址。</summary>
    public required string Endpoint { get; init; }
    public required string Region { get; init; }
    public required string Bucket { get; init; }
    public required string AccessKeyId { get; init; }
    public required string SecretAccessKey { get; init; }
    /// <summary>路径风格（endpoint/bucket/key）；关闭时使用虚拟主机风格（bucket.endpoint/key）。MinIO 等自建服务通常需要路径风格。</summary>
    public bool PathStyle { get; init; } = true;
    public string? RemoteRoot { get; init; }
}

/// <summary>
/// S3 兼容对象存储（AWS S3、Cloudflare R2、MinIO、阿里云 OSS、腾讯云 COS、Backblaze B2 等）。
/// 只用 ListObjectsV2 / GetObject / PutObject / DeleteObject，签名为 AWS Signature V4，不依赖 AWS SDK。
/// </summary>
public sealed class S3SyncStorage : ISyncStorage
{
    private readonly HttpClient _http;
    private readonly S3SyncOptions _options;
    private readonly Uri _endpoint;
    private readonly string _root;
    private readonly Func<DateTimeOffset> _clock;

    public S3SyncStorage(HttpClient http, S3SyncOptions options, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (!Uri.TryCreate(options.Endpoint?.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new SyncStorageException(SyncStorageErrorKind.Configuration, "The S3 endpoint must be an http(s) URL.");
        if (string.IsNullOrWhiteSpace(options.Bucket) || options.Bucket.Contains('/') || string.IsNullOrWhiteSpace(options.AccessKeyId)
            || string.IsNullOrEmpty(options.SecretAccessKey))
            throw new SyncStorageException(SyncStorageErrorKind.Configuration, "Bucket and access keys are required.");
        _http = http;
        _options = options with { Region = string.IsNullOrWhiteSpace(options.Region) ? "us-east-1" : options.Region.Trim(), Bucket = options.Bucket.Trim() };
        _endpoint = new UriBuilder(endpoint) { Path = "/", Query = string.Empty, Fragment = string.Empty }.Uri;
        var root = SyncKeys.NormalizeRoot(options.RemoteRoot);
        _root = root.Length == 0 ? string.Empty : root + "/";
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<SyncObjectInfo>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        SyncKeys.ValidatePrefix(prefix);
        var result = new List<SyncObjectInfo>();
        string? continuation = null;
        do
        {
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["list-type"] = "2",
                ["prefix"] = _root + prefix
            };
            if (continuation != null) query["continuation-token"] = continuation;
            using var response = await SendAsync(HttpMethod.Get, null, query, null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) await ThrowAsync(response, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            XDocument document;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            }
            catch (XmlException exception)
            {
                throw new SyncStorageException(SyncStorageErrorKind.Protocol, "The S3 service returned an invalid listing.", exception);
            }
            foreach (var contents in document.Descendants().Where(element => element.Name.LocalName == "Contents"))
            {
                var fullKey = contents.Elements().FirstOrDefault(element => element.Name.LocalName == "Key")?.Value;
                if (fullKey == null || !fullKey.StartsWith(_root, StringComparison.Ordinal)) continue;
                var key = fullKey[_root.Length..];
                if (!SyncKeys.IsValid(key)) continue;
                _ = long.TryParse(contents.Elements().FirstOrDefault(element => element.Name.LocalName == "Size")?.Value, out var size);
                result.Add(new SyncObjectInfo(key, size));
            }
            var truncated = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "IsTruncated")?.Value;
            continuation = string.Equals(truncated, "true", StringComparison.OrdinalIgnoreCase)
                ? document.Descendants().FirstOrDefault(element => element.Name.LocalName == "NextContinuationToken")?.Value
                : null;
        } while (continuation != null);

        result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return result;
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        SyncKeys.Validate(key);
        using var response = await SendAsync(HttpMethod.Get, _root + key, null, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        SyncKeys.Validate(key);
        using var response = await SendAsync(HttpMethod.Put, _root + key, null, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        SyncKeys.Validate(key);
        using var response = await SendAsync(HttpMethod.Delete, _root + key, null, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string? objectKey,
        SortedDictionary<string, string>? query,
        ReadOnlyMemory<byte>? body,
        CancellationToken cancellationToken)
    {
        var host = _options.PathStyle ? _endpoint.Authority : $"{_options.Bucket}.{_endpoint.Authority}";
        var path = _options.PathStyle ? "/" + S3Signer.EncodeSegment(_options.Bucket) : string.Empty;
        path += "/" + (objectKey == null ? string.Empty : S3Signer.EncodePath(objectKey));
        var queryString = query == null ? string.Empty : S3Signer.CanonicalQuery(query);
        var uri = new UriBuilder(_endpoint.Scheme, _options.PathStyle ? _endpoint.Host : $"{_options.Bucket}.{_endpoint.Host}", _endpoint.Port)
        {
            Path = path,
            Query = queryString
        }.Uri;

        var payload = body?.ToArray() ?? [];
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var now = _clock();
        var request = new HttpRequestMessage(method, uri);
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
        };
        var authorization = S3Signer.Authorization(
            method.Method, path, queryString, headers, payloadHash, now, _options.Region, _options.AccessKeyId, _options.SecretAccessKey);
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (body != null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new SyncStorageException(SyncStorageErrorKind.Network, "The S3 service could not be reached.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SyncStorageException(SyncStorageErrorKind.Network, "The S3 request timed out.", exception);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        await ThrowAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ThrowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length is > 0 and < 64 * 1024)
                code = XDocument.Parse(text).Descendants().FirstOrDefault(element => element.Name.LocalName == "Code")?.Value;
        }
        catch (Exception exception) when (exception is XmlException or HttpRequestException)
        {
        }
        var status = (int)response.StatusCode;
        var kind = code switch
        {
            "RequestTimeTooSkewed" => SyncStorageErrorKind.ClockSkew,
            "SignatureDoesNotMatch" or "InvalidAccessKeyId" or "AccessDenied" => SyncStorageErrorKind.Unauthorized,
            "NoSuchBucket" or "PermanentRedirect" or "AuthorizationHeaderMalformed" => SyncStorageErrorKind.Configuration,
            _ => status switch
            {
                401 or 403 => SyncStorageErrorKind.Unauthorized,
                400 or 404 or 301 => SyncStorageErrorKind.Configuration,
                >= 500 or 408 or 429 => SyncStorageErrorKind.Network,
                _ => SyncStorageErrorKind.Protocol
            }
        };
        throw new SyncStorageException(kind, $"The S3 service responded with HTTP {status}{(code == null ? string.Empty : $" ({code})")}.");
    }
}

/// <summary>AWS Signature Version 4（服务名固定为 s3）。</summary>
public static class S3Signer
{
    public static string Authorization(
        string method,
        string canonicalPath,
        string canonicalQuery,
        SortedDictionary<string, string> headers,
        string payloadHash,
        DateTimeOffset time,
        string region,
        string accessKeyId,
        string secretAccessKey)
    {
        var amzDate = time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        var canonicalHeaders = string.Concat(headers.Select(header => $"{header.Key}:{header.Value.Trim()}\n"));
        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalRequest = string.Join('\n', method, canonicalPath.Length == 0 ? "/" : canonicalPath, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);
        var scope = $"{date}/{region}/s3/aws4_request";
        var stringToSign = string.Join('\n', "AWS4-HMAC-SHA256", amzDate, scope,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), Encoding.UTF8.GetBytes(date));
        key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(region));
        key = HMACSHA256.HashData(key, "s3"u8);
        key = HMACSHA256.HashData(key, "aws4_request"u8);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
        return $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    public static string CanonicalQuery(SortedDictionary<string, string> query)
        => string.Join('&', query
            .Select(pair => (Key: EncodeSegment(pair.Key), Value: EncodeSegment(pair.Value)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));

    /// <summary>对象键按「/」分段编码，「/」本身保留。</summary>
    public static string EncodePath(string key) => string.Join('/', key.Split('/').Select(EncodeSegment));

    /// <summary>RFC 3986 非保留字符（A-Z a-z 0-9 - _ . ~）之外全部百分号编码（大写十六进制）。</summary>
    public static string EncodeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~') builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
