using System.Net;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Settings;

namespace SteamStat.Core.Http;

public enum SteamProxyMode
{
    /// <summary>跟随系统代理（Windows Internet 设置 / 环境变量），默认。</summary>
    System,
    /// <summary>不使用任何代理，直连。</summary>
    None,
    /// <summary>使用自定义 HTTP / SOCKS 代理。</summary>
    Custom
}

/// <summary>
/// 当前生效的网络配置。代理与 CM 传输协议即时生效；超时在启动时按 <see cref="StartupCnOptimized"/> 固化。
/// </summary>
public sealed record SteamNetworkProfile(SteamProxyMode ProxyMode, Uri? ProxyUri, bool CnOptimized)
{
    public static SteamNetworkProfile Default { get; } = new(SteamProxyMode.System, null, false);

    public static readonly string[] SupportedProxySchemes = ["http", "socks5", "socks4"];

    /// <summary>
    /// 解析设置中的代理配置；自定义代理地址非法时退回系统代理（并给出原因）。
    /// </summary>
    public static SteamNetworkProfile FromSettings(NetworkSettings? settings, out string? error)
    {
        error = null;
        var cnOptimized = settings?.CnOptimized ?? false;
        var mode = ParseMode(settings?.ProxyMode);
        if (mode != SteamProxyMode.Custom) return new SteamNetworkProfile(mode, null, cnOptimized);
        if (TryParseProxyUri(settings?.ProxyUrl, out var uri, out error))
            return new SteamNetworkProfile(SteamProxyMode.Custom, uri, cnOptimized);
        return new SteamNetworkProfile(SteamProxyMode.System, null, cnOptimized);
    }

    public static SteamProxyMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => SteamProxyMode.None,
        "custom" => SteamProxyMode.Custom,
        _ => SteamProxyMode.System
    };

    public static string FormatMode(SteamProxyMode mode) => mode switch
    {
        SteamProxyMode.None => "none",
        SteamProxyMode.Custom => "custom",
        _ => "system"
    };

    /// <summary>
    /// 只接受 <c>scheme://host:port</c>（http / socks5 / socks4），不接受账号密码、路径和查询串：
    /// 代理凭证不落盘，渲染层（Chromium）与 .NET 两侧也能保持同一份规则。
    /// </summary>
    public static bool TryParseProxyUri(string? value, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "empty";
            return false;
        }
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            error = "invalid";
            return false;
        }
        if (!SupportedProxySchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            error = "scheme";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "credentials";
            return false;
        }
        if (parsed.AbsolutePath is not ("/" or "") || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "path";
            return false;
        }
        if (string.IsNullOrEmpty(parsed.Host) || parsed.IsDefaultPort && !text.Contains($":{parsed.Port}", StringComparison.Ordinal))
        {
            // Uri 会给 http 补默认端口 80；代理几乎从不监听 80，要求显式写端口以免误连
            error = "port";
            return false;
        }
        uri = new Uri($"{parsed.Scheme.ToLowerInvariant()}://{parsed.Authority}");
        return true;
    }
}

/// <summary>
/// 按国内网络优化模式调整超时（启动时应用一次；HttpClient 弹性管道在注册时固化）。
/// </summary>
public static class SteamNetworkTuning
{
    public static readonly TimeSpan DefaultCmConnectionTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CnCmConnectionTimeout = TimeSpan.FromSeconds(15);

    public static void Apply(SteamAccessOptions options, bool cnOptimized)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!cnOptimized) return;
        options.JsonTotalTimeout = TimeSpan.FromSeconds(40);
        options.JsonAttemptTimeout = TimeSpan.FromSeconds(12);
        options.DownloadTotalTimeout = TimeSpan.FromSeconds(60);
        options.DownloadAttemptTimeout = TimeSpan.FromSeconds(30);
        options.CmOperationTimeout = TimeSpan.FromSeconds(30);
        options.CircuitBreakDuration = TimeSpan.FromSeconds(10);
        // Polly 要求熔断采样窗口至少为单次尝试超时的两倍（下载单次 30s → 至少 60s），否则启动时选项校验失败
        options.CircuitSamplingDuration = TimeSpan.FromSeconds(60);
        options.CnOptimizedTimeouts = true;
    }
}

/// <summary>
/// 进程内唯一的动态代理：命名 HttpClient、SteamKit 的 WebAPI / WebSocket（经 <see cref="HttpClient.DefaultProxy"/>）
/// 都经它取代理，修改设置后对新请求立即生效。环回地址始终直连（Electron.NET 本地 socket、Vite 开发服务器）。
/// </summary>
public sealed class SteamNetworkProxy : IWebProxy
{
    private readonly IWebProxy? _systemProxy;
    private volatile SteamNetworkProfile _profile = SteamNetworkProfile.Default;

    public SteamNetworkProxy()
    {
        var current = HttpClient.DefaultProxy;
        _systemProxy = current is SteamNetworkProxy other ? other._systemProxy : current;
    }

    public ICredentials? Credentials { get; set; }

    public SteamNetworkProfile Profile => _profile;

    internal void Update(SteamNetworkProfile profile) => _profile = profile;

    public Uri? GetProxy(Uri destination)
    {
        if (IsLoopback(destination)) return null;
        var profile = _profile;
        return profile.ProxyMode switch
        {
            SteamProxyMode.Custom => profile.ProxyUri,
            SteamProxyMode.None => null,
            _ => SystemProxyFor(destination)
        };
    }

    public bool IsBypassed(Uri host)
    {
        if (IsLoopback(host)) return true;
        var profile = _profile;
        return profile.ProxyMode switch
        {
            SteamProxyMode.Custom => false,
            SteamProxyMode.None => true,
            _ => SystemProxyFor(host) == null
        };
    }

    /// <summary>系统代理对该地址给出的代理；直连返回 null。</summary>
    public Uri? SystemProxyFor(Uri destination)
    {
        if (_systemProxy == null) return null;
        try
        {
            if (_systemProxy.IsBypassed(destination)) return null;
            var proxy = _systemProxy.GetProxy(destination);
            return proxy == null || proxy == destination ? null : proxy;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLoopback(Uri uri)
        => uri.IsLoopback
           || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}

public interface ISteamNetworkController
{
    SteamNetworkProfile Current { get; }

    /// <summary>启动时生效的国内网络优化超时（修改模式后需重启才会变化）。</summary>
    bool StartupCnOptimized { get; }

    /// <summary>配置变化（代理 / 优化模式）。宿主据此同步渲染层（Chromium 会话）代理。</summary>
    event Action<SteamNetworkProfile>? Changed;

    void Apply(NetworkSettings? settings);
}

public sealed class SteamNetworkController(
    SteamNetworkProxy proxy,
    SteamAccessOptions accessOptions,
    ILogger<SteamNetworkController> logger) : ISteamNetworkController
{
    private int _installed;

    public SteamNetworkProfile Current => proxy.Profile;

    public bool StartupCnOptimized => accessOptions.CnOptimizedTimeouts;

    public event Action<SteamNetworkProfile>? Changed;

    public void Apply(NetworkSettings? settings)
    {
        if (Interlocked.Exchange(ref _installed, 1) == 0)
        {
            // SteamKit 的 WebAPI HttpClient 与 ClientWebSocket 都不接受注入代理，只读进程默认代理
            HttpClient.DefaultProxy = proxy;
        }

        var profile = SteamNetworkProfile.FromSettings(settings, out var error);
        if (error != null && SteamNetworkProfile.ParseMode(settings?.ProxyMode) == SteamProxyMode.Custom)
            logger.LogWarning("Ignoring invalid custom proxy setting ({Reason}); falling back to system proxy", error);

        var previous = proxy.Profile;
        proxy.Update(profile);
        if (previous == profile) return;
        logger.LogInformation(
            "Network profile changed: proxy {ProxyMode} {ProxyScheme}, China network optimization {CnOptimized}",
            SteamNetworkProfile.FormatMode(profile.ProxyMode),
            profile.ProxyUri?.Scheme ?? "-",
            profile.CnOptimized);
        Changed?.Invoke(profile);
    }
}

/// <summary>
/// CM 连接使用的传输协议：国内网络优化或请求会经过代理时只用 WebSocket（443 端口、可走 HTTP/SOCKS 代理），
/// 否则保持 SteamKit 默认的 TCP。
/// </summary>
public static class SteamCmTransportSelector
{
    private static readonly Uri ProbeUri = new("https://api.steampowered.com/");

    public static bool PreferWebSocket(SteamNetworkProxy proxy)
    {
        var profile = proxy.Profile;
        if (profile.CnOptimized) return true;
        return profile.ProxyMode switch
        {
            SteamProxyMode.Custom => true,
            SteamProxyMode.None => false,
            _ => proxy.SystemProxyFor(ProbeUri) != null
        };
    }
}
