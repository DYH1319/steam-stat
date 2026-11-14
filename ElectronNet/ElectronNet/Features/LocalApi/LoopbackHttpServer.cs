using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ElectronNet.Features.LocalApi;

internal sealed record HttpRequestData(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);

    public string BodyText => Encoding.UTF8.GetString(Body);
}

internal sealed record HttpResponseData(int Status, string ContentType, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static HttpResponseData Text(int status, string text, string contentType = "text/plain; charset=utf-8")
        => new(status, contentType, Encoding.UTF8.GetBytes(text));

    public static HttpResponseData Json(int status, string json) => Text(status, json, "application/json; charset=utf-8");

    public static HttpResponseData Empty(int status, IReadOnlyDictionary<string, string>? headers = null) => new(status, "text/plain; charset=utf-8", [], headers);
}

internal sealed class HttpParseException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// 最小的 HTTP/1.1 服务器，只监听环回地址。不用 HttpListener（Windows 上要 URL ACL）也不引入 Kestrel：
/// 本地接口只有几条只读路由，请求头 16 KB、请求体 1 MB 封顶，每个连接只处理一个请求（Connection: close）。
/// </summary>
internal sealed class LoopbackHttpServer(
    int port,
    Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler,
    ILogger logger) : IAsyncDisposable
{
    internal const int MaxHeaderBytes = 16 * 1024;
    internal const int MaxBodyBytes = 1024 * 1024;
    private const int MaxConcurrent = 16;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _loops = [];

    public int Port { get; private set; } = port;

    /// <summary>端口被占用时抛 <see cref="SocketException"/>（AddressAlreadyInUse）。IPv6 环回只是尽量监听。</summary>
    public void Start()
    {
        var v4 = new TcpListener(IPAddress.Loopback, Port);
        v4.Start();
        Port = ((IPEndPoint)v4.LocalEndpoint).Port;
        _listeners.Add(v4);
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                var v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
                v6.Server.DualMode = false;
                v6.Start();
                _listeners.Add(v6);
            }
            catch (SocketException exception)
            {
                logger.LogDebug(exception, "IPv6 loopback is not available for the local API");
            }
        }
        foreach (var listener in _listeners) _loops.Add(AcceptLoopAsync(listener));
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_stopping.IsCancellationRequested) return;
                logger.LogDebug(exception, "Local API accept failed");
                continue;
            }
            // 绑在环回上本来就只有本机连得上；这里再确认一次，防止系统把别的地址转进来
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
            {
                client.Dispose();
                continue;
            }
            if (!await _slots.WaitAsync(0).ConfigureAwait(false))
            {
                _ = RejectBusyAsync(client);
                continue;
            }
            _ = ServeAsync(client);
        }
    }

    private static async Task RejectBusyAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await WriteAsync(client.GetStream(), HttpResponseData.Text(503, "Busy"), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                // 对方已经断开
            }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(RequestTimeout);
                var stream = client.GetStream();
                HttpResponseData response;
                try
                {
                    var request = await ReadAsync(stream, timeout.Token).ConfigureAwait(false);
                    using var handling = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    handling.CancelAfter(HandlerTimeout);
                    response = await handler(request, handling.Token).ConfigureAwait(false);
                }
                catch (HttpParseException exception)
                {
                    response = HttpResponseData.Text(exception.Status, exception.Message);
                }
                await WriteAsync(stream, response, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogDebug(exception, "Local API connection ended early");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Local API request failed");
        }
        finally
        {
            _slots.Release();
        }
    }

    internal static async Task<HttpRequestData> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var filled = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            if (filled == buffer.Length) throw new HttpParseException(431, "Request headers too large");
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new HttpParseException(400, "Incomplete request");
            filled += read;
            headerEnd = IndexOf(buffer.AsSpan(0, filled), "\r\n\r\n"u8);
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var requestLine = head[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new HttpParseException(400, "Bad request line");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new HttpParseException(400, "Bad header");
            var name = line[..colon].Trim();
            // 重复的 Host / Authorization 是走私请求的常见手法，直接拒绝
            if (!headers.TryAdd(name, line[(colon + 1)..].Trim()) && name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                throw new HttpParseException(400, "Duplicate Host header");
        }
        if (headers.ContainsKey("Transfer-Encoding")) throw new HttpParseException(411, "Chunked bodies are not supported");

        var length = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText)
            && (!int.TryParse(lengthText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length) || length < 0))
            throw new HttpParseException(400, "Bad Content-Length");
        if (length > MaxBodyBytes) throw new HttpParseException(413, "Request body too large");

        var body = new byte[length];
        var already = Math.Min(length, filled - headerEnd - 4);
        Array.Copy(buffer, headerEnd + 4, body, 0, already);
        if (already < length) await stream.ReadExactlyAsync(body.AsMemory(already), cancellationToken).ConfigureAwait(false);

        var target = requestLine[1];
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (question >= 0)
            foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
                var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
                query.TryAdd(key, value);
            }
        return new HttpRequestData(requestLine[0].ToUpperInvariant(), Uri.UnescapeDataString(path), query, headers, body);
    }

    internal static async Task WriteAsync(Stream stream, HttpResponseData response, CancellationToken cancellationToken)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n")
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(response.Body.Length).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Referrer-Policy: no-referrer\r\n");
        foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>())
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) => haystack.IndexOf(needle);

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        411 => "Length Required",
        413 => "Payload Too Large",
        421 => "Misdirected Request",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status"
    };

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var listener in _listeners) listener.Stop();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // 退出时不等卡住的连接
        }
        _stopping.Dispose();
    }
}
