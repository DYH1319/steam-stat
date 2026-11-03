using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SteamStat.Core.Http;

public enum NetworkProbeStatus
{
    Ok,
    Failed,
    Skipped
}

public sealed record NetworkProbeResult(
    string Id,
    string Category,
    string Target,
    NetworkProbeStatus Status,
    int? LatencyMs,
    int? HttpStatus,
    string? ErrorCode,
    string? ErrorMessage,
    bool ViaProxy);

public sealed record NetworkDiagnosticsReport(
    DateTimeOffset RanAt,
    SteamNetworkProfile Profile,
    bool StartupCnOptimized,
    bool CmWebSocket,
    string? EffectiveProxy,
    IReadOnlyList<NetworkProbeResult> Probes,
    IReadOnlyList<SteamFailureObservation> RecentFailures);

/// <summary>
/// 网络诊断：逐个探测 Steam 各端点（Web API / CM 目录 / CM WebSocket / CM TCP / 商店 / 社区 / 各 CDN）与更新源，
/// 全部经与业务请求相同的动态代理；每个探测使用独立连接（不复用连接池），延迟包含 DNS + TCP + TLS。
/// </summary>
public sealed class SteamNetworkDiagnostics(
    SteamNetworkProxy proxy,
    ISteamNetworkController networkController,
    SteamConnectivityMonitor connectivityMonitor,
    TimeProvider timeProvider,
    ILogger<SteamNetworkDiagnostics> logger)
{
    private const string SampleAppAsset = "store_item_assets/steam/apps/570/capsule_184x69.jpg";
    private const string DefaultAvatar = "fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb.jpg";

    private static readonly (string Id, string Category, string Url)[] HttpTargets =
    [
        ("webApi", "steam", "https://api.steampowered.com/ISteamWebAPIUtil/GetServerInfo/v1/"),
        ("store", "steam", "https://store.steampowered.com/robots.txt"),
        ("community", "steam", "https://steamcommunity.com/robots.txt"),
        ("cdnAkamai", "cdn", $"https://shared.akamai.steamstatic.com/{SampleAppAsset}"),
        ("cdnFastly", "cdn", $"https://shared.fastly.steamstatic.com/{SampleAppAsset}"),
        ("cdnCloudflare", "cdn", $"https://shared.cloudflare.steamstatic.com/{SampleAppAsset}"),
        ("cdnLegacy", "cdn", "https://steamcdn-a.akamaihd.net/steam/apps/570/capsule_184x69.jpg"),
        ("avatarAkamai", "cdn", $"https://avatars.akamai.steamstatic.com/{DefaultAvatar}"),
        ("avatarFastly", "cdn", $"https://avatars.fastly.steamstatic.com/{DefaultAvatar}"),
        ("github", "app", "https://api.github.com/")
    ];

    private const string CmDirectoryUrl = "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/?cellid=0&maxcount=20";

    public async Task<NetworkDiagnosticsReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var profile = networkController.Current;
        var timeout = networkController.StartupCnOptimized || profile.CnOptimized
            ? TimeSpan.FromSeconds(15)
            : TimeSpan.FromSeconds(8);
        var cmWebSocket = SteamCmTransportSelector.PreferWebSocket(proxy);

        var httpProbes = HttpTargets
            .Select(target => ProbeHttpAsync(target.Id, target.Category, new Uri(target.Url), timeout, cancellationToken))
            .ToList();
        var cmProbes = ProbeCmAsync(timeout, cmWebSocket, cancellationToken);
        var results = new List<NetworkProbeResult>();
        results.AddRange(await cmProbes.ConfigureAwait(false));
        results.AddRange(await Task.WhenAll(httpProbes).ConfigureAwait(false));

        var storeUri = new Uri("https://store.steampowered.com/");
        var effective = proxy.IsBypassed(storeUri) ? null : proxy.GetProxy(storeUri);
        var report = new NetworkDiagnosticsReport(
            timeProvider.GetUtcNow(),
            profile,
            networkController.StartupCnOptimized,
            cmWebSocket,
            effective?.GetLeftPart(UriPartial.Authority),
            Order(results),
            connectivityMonitor.RecentFailures);
        logger.LogInformation(
            "Network diagnostics finished: {Ok}/{Total} probes reachable (proxy {ProxyMode}, CM {CmTransport})",
            results.Count(result => result.Status == NetworkProbeStatus.Ok),
            results.Count,
            SteamNetworkProfile.FormatMode(profile.ProxyMode),
            cmWebSocket ? "WebSocket" : "TCP");
        return report;
    }

    private static IReadOnlyList<NetworkProbeResult> Order(List<NetworkProbeResult> results)
    {
        var order = new[] { "cmDirectory", "cmWebSocket", "cmTcp" }
            .Concat(HttpTargets.Select(target => target.Id))
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index);
        return results.OrderBy(result => order.GetValueOrDefault(result.Id, int.MaxValue)).ToArray();
    }

    private async Task<IReadOnlyList<NetworkProbeResult>> ProbeCmAsync(
        TimeSpan timeout,
        bool cmWebSocket,
        CancellationToken cancellationToken)
    {
        var directoryUri = new Uri(CmDirectoryUrl);
        string? body = null;
        var directory = await ProbeHttpAsync(
            "cmDirectory", "cm", directoryUri, timeout, cancellationToken,
            async response => body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        var displayDirectory = directory with { Target = "api.steampowered.com/ISteamDirectory" };

        var (webSocketEndpoint, tcpEndpoint) = ParseCmList(body);
        var webSocket = webSocketEndpoint == null
            ? Skipped("cmWebSocket", "cm", "wss://<cm>/cmsocket/", "no_endpoint")
            : await ProbeWebSocketAsync(webSocketEndpoint, timeout, cancellationToken).ConfigureAwait(false);
        NetworkProbeResult tcp;
        if (tcpEndpoint == null)
            tcp = Skipped("cmTcp", "cm", "tcp://<cm>", "no_endpoint");
        else if (cmWebSocket)
            // 走代理或国内优化时 CM 只用 WebSocket；TCP 直连不经代理，测了也不代表实际链路
            tcp = Skipped("cmTcp", "cm", $"tcp://{tcpEndpoint}", "tcp_not_used");
        else
            tcp = await ProbeTcpAsync(tcpEndpoint, timeout, cancellationToken).ConfigureAwait(false);
        return [displayDirectory, webSocket, tcp];
    }

    internal static (string? WebSocket, string? Tcp) ParseCmList(string? body)
    {
        if (string.IsNullOrEmpty(body)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("response", out var response)
                || !response.TryGetProperty("serverlist", out var servers)
                || servers.ValueKind != JsonValueKind.Array)
                return (null, null);
            string? webSocket = null;
            string? tcp = null;
            foreach (var server in servers.EnumerateArray())
            {
                var endpoint = server.TryGetProperty("endpoint", out var value) ? value.GetString() : null;
                var type = server.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                if (string.IsNullOrEmpty(endpoint)) continue;
                if (type == "websockets") webSocket ??= endpoint;
                else if (type == "netfilter") tcp ??= endpoint;
            }
            return (webSocket, tcp);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private async Task<NetworkProbeResult> ProbeHttpAsync(
        string id,
        string category,
        Uri uri,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<HttpResponseMessage, Task>? onSuccess = null)
    {
        var viaProxy = !proxy.IsBypassed(uri) && proxy.GetProxy(uri) != null;
        var target = uri.Host + (uri.AbsolutePath.Length > 1 ? "/…" : "");
        using var handler = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            ConnectTimeout = timeout,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStat/1.4");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var latency = (int)stopwatch.ElapsedMilliseconds;
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return new NetworkProbeResult(id, category, target, NetworkProbeStatus.Failed, latency, status,
                    $"http_{status}", response.ReasonPhrase, viaProxy);
            if (onSuccess != null) await onSuccess(response).ConfigureAwait(false);
            return new NetworkProbeResult(id, category, target, NetworkProbeStatus.Ok, latency, status, null, null, viaProxy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(id, category, target, exception, viaProxy);
        }
    }

    private async Task<NetworkProbeResult> ProbeWebSocketAsync(
        string endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"wss://{endpoint}/cmsocket/");
        var httpsEquivalent = new Uri($"https://{endpoint}/");
        var viaProxy = !proxy.IsBypassed(httpsEquivalent) && proxy.GetProxy(httpsEquivalent) != null;
        var target = $"wss://{endpoint}";
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = proxy;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(uri, cts.Token).ConfigureAwait(false);
            var latency = (int)stopwatch.ElapsedMilliseconds;
            socket.Abort();
            return new NetworkProbeResult("cmWebSocket", "cm", target, NetworkProbeStatus.Ok, latency, 101, null, null, viaProxy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed("cmWebSocket", "cm", target, exception, viaProxy);
        }
    }

    private static async Task<NetworkProbeResult> ProbeTcpAsync(
        string endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var target = $"tcp://{endpoint}";
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(endpoint[(separator + 1)..], out var port))
            return Skipped("cmTcp", "cm", target, "no_endpoint");
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(endpoint[..separator], port, cts.Token).ConfigureAwait(false);
            return new NetworkProbeResult("cmTcp", "cm", target, NetworkProbeStatus.Ok,
                (int)stopwatch.ElapsedMilliseconds, null, null, null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed("cmTcp", "cm", target, exception, false);
        }
    }

    private static NetworkProbeResult Skipped(string id, string category, string target, string code)
        => new(id, category, target, NetworkProbeStatus.Skipped, null, null, code, null, false);

    private static NetworkProbeResult Failed(string id, string category, string target, Exception exception, bool viaProxy)
    {
        var (code, message) = Classify(exception);
        return new NetworkProbeResult(id, category, target, NetworkProbeStatus.Failed, null, null, code, message, viaProxy);
    }

    internal static (string Code, string Message) Classify(Exception exception)
    {
        var message = Truncate(exception.InnerException?.Message ?? exception.Message);
        if (exception is OperationCanceledException or TimeoutException) return ("timeout", message);
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException { HttpRequestError: var error } when error != HttpRequestError.Unknown:
                    return (error switch
                    {
                        HttpRequestError.NameResolutionError => "dns",
                        HttpRequestError.ConnectionError => "connection",
                        HttpRequestError.SecureConnectionError => "tls",
                        HttpRequestError.ProxyTunnelError => "proxy",
                        HttpRequestError.HttpProtocolError or HttpRequestError.InvalidResponse
                            or HttpRequestError.ResponseEnded => "protocol",
                        _ => "connection"
                    }, message);
                case SocketException socket:
                    return (socket.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "dns",
                        SocketError.ConnectionRefused => "refused",
                        SocketError.TimedOut => "timeout",
                        _ => "connection"
                    }, message);
                case AuthenticationException:
                    return ("tls", message);
                case WebSocketException { WebSocketErrorCode: WebSocketError.NotAWebSocket }:
                    return ("protocol", message);
            }
        }
        return ("unknown", message);
    }

    private static string Truncate(string value)
        => value.Length <= 200 ? value : value[..200] + "…";
}
