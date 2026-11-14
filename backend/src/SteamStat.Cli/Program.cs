using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace SteamStat.Cli;

/// <summary>
/// steamstat：Steam Stat 的命令行工具。应用必须在运行（它平时常驻托盘），并在设置里打开了「本地只读接口」。
/// <code>
/// steamstat today            今天玩了多久
/// steamstat top 5 --json     JSON 输出，可以接管道
/// steamstat mcp              以 stdio 方式提供 MCP（给 Claude Desktop 等客户端）
/// </code>
/// </summary>
internal static class Program
{
    internal const int ExitUnavailable = 69;
    internal const int ExitUnauthorized = 77;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        var arguments = args.ToList();
        var dataDirectory = TakeOption(arguments, "--data-dir");

        var endpoint = Discovery.Find(dataDirectory, System.Environment.GetEnvironmentVariable, AppContext.BaseDirectory);
        if (arguments is ["mcp", ..])
            return await McpBridge.RunAsync(() => Discovery.Find(dataDirectory, System.Environment.GetEnvironmentVariable, AppContext.BaseDirectory), Console.In, Console.Out).ConfigureAwait(false);

        if (endpoint == null)
        {
            if (arguments.Count == 0 || arguments[0] is "help" or "--help" or "-h") Console.WriteLine(OfflineHelp);
            Console.Error.WriteLine(Discovery.NotFoundMessage);
            return ExitUnavailable;
        }

        using var client = Http.Create(endpoint);
        var body = new JsonObject { ["args"] = new JsonArray(arguments.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray()) };
        if (System.Environment.GetEnvironmentVariable("STEAMSTAT_LANG") is { Length: > 0 } lang) body["lang"] = lang;
        try
        {
            using var response = await client.PostAsync("/v1/cli", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Console.Error.WriteLine("The local API rejected the token. Regenerated it recently? Restart the command; if it persists, check the data directory (--data-dir).");
                return ExitUnauthorized;
            }
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Steam Stat returned HTTP {(int)response.StatusCode}.");
                return ExitUnavailable;
            }
            var result = JsonNode.Parse(text);
            var output = result?["output"]?.GetValue<string>() ?? string.Empty;
            var exitCode = result?["exitCode"]?.GetValue<int>() ?? 0;
            if (output.Length > 0) (exitCode == 0 ? Console.Out : Console.Error).WriteLine(output);
            return exitCode;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine(Discovery.NotRunningMessage);
            return ExitUnavailable;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("Steam Stat did not answer in time.");
            return ExitUnavailable;
        }
    }

    private static string? TakeOption(List<string> arguments, string name)
    {
        var index = arguments.IndexOf(name);
        if (index < 0 || index + 1 >= arguments.Count) return null;
        var value = arguments[index + 1];
        arguments.RemoveRange(index, 2);
        return value;
    }

    private const string OfflineHelp = """
        steamstat — command line for Steam Stat (https://github.com/DYH1319/steam-stat)

          steamstat today | now | week | month | days [N] | top [N] | game <name> | sessions | achievements | metrics
          steamstat sql "<SELECT ...>"     read-only SQL on the semantic views
          steamstat mcp                    serve MCP over stdio (proxied to the running app)
          add --json to any command for JSON output; --data-dir <dir> if the app keeps its data elsewhere

        Steam Stat must be running with "Local read-only API" enabled in Settings → AI & local API.
        """;
}

internal sealed record Endpoint(Uri BaseUri, string Token);

/// <summary>
/// 找到正在运行的 Steam Stat：Host 把端口和 token 写在数据目录的 local-api.json 里（只有当前用户能读）。
/// 数据目录和 Host 的规则一致：便携版在程序目录的 data 下，其余在 %APPDATA%/steam-stat 或 ~/.config/steam-stat。
/// STEAMSTAT_URL + STEAMSTAT_TOKEN 可以直接指定。
/// </summary>
internal static class Discovery
{
    internal const string NotFoundMessage = "Steam Stat's local API is not enabled (or Steam Stat has never run as this user). Enable it in Settings → AI & local API.";
    internal const string NotRunningMessage = "Steam Stat is not running, or the local API is turned off. Start the app (it can stay in the tray) and try again.";

    internal static Endpoint? Find(string? dataDirectory, Func<string, string?> environment, string programDirectory)
    {
        if (environment("STEAMSTAT_URL") is { Length: > 0 } url && environment("STEAMSTAT_TOKEN") is { Length: > 0 } token
            && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new Endpoint(uri, token);
        foreach (var directory in Candidates(dataDirectory, environment, programDirectory))
        {
            var state = Read(Path.Combine(directory, "local-api.json"));
            if (state != null) return state;
        }
        return null;
    }

    internal static IEnumerable<string> Candidates(string? dataDirectory, Func<string, string?> environment, string programDirectory)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            yield return dataDirectory;
            yield break;
        }
        // 便携版：程序目录下有 distribution.json，数据在 data 里
        yield return Path.Combine(programDirectory, "data");
        // 被复制到数据目录的 cli 子目录时，上一级就是数据目录
        if (Path.GetFileName(programDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) == "cli")
            yield return Path.GetDirectoryName(programDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        if (OperatingSystem.IsWindows())
        {
            var appData = environment("APPDATA") ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "steam-stat");
            yield return Path.Combine(appData, "steam-stat-dev");
        }
        else
        {
            var home = environment("HOME") ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            var config = environment("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".config");
            yield return Path.Combine(config, "steam-stat");
            yield return Path.Combine(home, ".var", "app", "asia.dyh1319.steamstat", "config", "steam-stat");
            yield return Path.Combine(config, "steam-stat-dev");
        }
    }

    internal static Endpoint? Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var state = JsonNode.Parse(File.ReadAllText(file));
            if (state?["running"]?.GetValue<bool>() != true) return null;
            var port = state["port"]?.GetValue<int>() ?? 0;
            var token = state["token"]?.GetValue<string>();
            return port is > 0 and <= 65535 && !string.IsNullOrEmpty(token)
                ? new Endpoint(new Uri($"http://127.0.0.1:{port}"), token)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

internal static class Http
{
    /// <summary>只连环回地址，不走代理。</summary>
    internal static HttpClient Create(Endpoint endpoint)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = endpoint.BaseUri, Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("steamstat-cli");
        return client;
    }
}

/// <summary>
/// MCP 的 stdio 传输：每行一条 JSON-RPC 消息，原样转发到应用的 /mcp，回复写回 stdout。
/// 应用没运行时，对请求回一个 JSON-RPC 错误，客户端能把原因显示出来。stdout 只写协议消息，说明一律写 stderr。
/// </summary>
internal static class McpBridge
{
    /// <summary>客户端常在应用启动之前就拉起 steamstat mcp，所以每条消息都重新找一次服务（端口和 token 可能变了）。</summary>
    internal static async Task<int> RunAsync(Func<Endpoint?> discover, TextReader input, TextWriter output)
    {
        Endpoint? endpoint = null;
        HttpClient? client = null;
        try
        {
            while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var current = discover();
                if (current != endpoint)
                {
                    client?.Dispose();
                    endpoint = current;
                    client = endpoint == null ? null : Http.Create(endpoint);
                }
                var reply = await ForwardAsync(client, line).ConfigureAwait(false);
                if (reply == null) continue;
                await output.WriteLineAsync(reply).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            client?.Dispose();
        }
        return 0;
    }

    internal static async Task<string?> ForwardAsync(HttpClient? client, string line)
    {
        string? failure = null;
        if (client == null) failure = Discovery.NotFoundMessage;
        else
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(line, Encoding.UTF8, "application/json") };
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Accepted) return null;
                if (response.IsSuccessStatusCode) return (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).ReplaceLineEndings(string.Empty);
                failure = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "The local API rejected the token.",
                    HttpStatusCode.NotFound => "MCP is turned off in Steam Stat (Settings → AI & local API).",
                    _ => $"Steam Stat returned HTTP {(int)response.StatusCode}."
                };
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                failure = Discovery.NotRunningMessage;
            }
        }
        await Console.Error.WriteLineAsync(failure).ConfigureAwait(false);
        return ErrorFor(line, failure!);
    }

    /// <summary>通知不需要回复；请求按原 id 回错误。</summary>
    internal static string? ErrorFor(string line, string message)
    {
        JsonNode? id = null;
        try
        {
            id = JsonNode.Parse(line)?["id"]?.DeepClone();
        }
        catch (System.Text.Json.JsonException)
        {
            // 解析不了就当成没有 id 的请求
        }
        if (id == null) return null;
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message }
        }.ToJsonString();
    }
}
