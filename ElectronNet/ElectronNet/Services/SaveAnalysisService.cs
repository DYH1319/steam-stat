using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Features.SaveAnalysis;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Http;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 存档分析插件：插件管理（用户目录 + 开发目录）、从独立公开仓库安装 / 更新、按清单定位并只读读取原始存档。
/// 插件代码不在 Host 执行，而是交给渲染层的沙箱 iframe；Host 只把声明过的文件以文本形式交出去。
/// </summary>
internal sealed class SaveAnalysisService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppPaths appPaths,
    ISteamInstallLocator installLocator,
    ISettingsStore settingsStore,
    IMainWindowAccessor mainWindowAccessor,
    IHttpClientFactory httpClientFactory,
    ILogger<SaveAnalysisService> logger)
{
    /// <summary>开发目录：指向插件仓库的 plugins 目录，直接加载（只读，不可卸载）。</summary>
    public const string DevDirectoryVariable = "STEAM_STAT_PLUGINS_DIR";

    /// <summary>覆盖插件仓库地址：可以是 URL，也可以是本地插件仓库根目录（测试安装流程）。</summary>
    public const string RepositoryVariable = "STEAM_STAT_PLUGIN_REPOSITORY";

    private static readonly string[] DefaultRepositories =
    [
        "https://raw.githubusercontent.com/DYH1319/steam-stat-plugins/main",
        "https://cdn.jsdelivr.net/gh/DYH1319/steam-stat-plugins@main"
    ];

    private const long MaxPluginBytes = 32 * 1024 * 1024;
    private const int MaxTokens = 50_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ConcurrentDictionary<string, (string Path, long MaxSize)> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokensByPath = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _installLock = new(1, 1);

    private string UserDirectory => Path.Combine(appPaths.UserDataDirectory, "Plugins");

    private string StateFile => Path.Combine(UserDirectory, ".state.json");

    private static string? DevDirectory
        => Environment.GetEnvironmentVariable(DevDirectoryVariable) is { Length: > 0 } dev && Directory.Exists(dev)
            ? Path.GetFullPath(dev)
            : null;

    #region 插件管理

    private sealed record LoadedPlugin(SaveAnalysisManifest Manifest, string Directory, string Source);

    private (List<LoadedPlugin> Plugins, List<SaveAnalysisPluginErrorDto> Errors) LoadAll()
    {
        var plugins = new Dictionary<string, LoadedPlugin>(StringComparer.Ordinal);
        var errors = new List<SaveAnalysisPluginErrorDto>();
        // 开发目录优先：同 id 时覆盖用户目录中安装的版本
        foreach (var (root, source) in new[] { (UserDirectory, "user"), (DevDirectory, "dev") })
        {
            if (root == null || !Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith('.')) continue;
                if (SaveAnalysisManifestLoader.TryLoad(directory, out var manifest, out var error))
                    plugins[manifest!.Id] = new LoadedPlugin(manifest, directory, source);
                else
                    errors.Add(new SaveAnalysisPluginErrorDto { Directory = directory, Error = error ?? "invalid" });
            }
        }
        return (plugins.Values.OrderBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal).ToList(), errors);
    }

    private LoadedPlugin? Find(string pluginId) => LoadAll().Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == pluginId);

    private HashSet<string> ReadDisabled()
    {
        try
        {
            if (!File.Exists(StateFile)) return [];
            var state = JsonSerializer.Deserialize<PluginState>(File.ReadAllText(StateFile), JsonOptions);
            return state?.Disabled?.ToHashSet(StringComparer.Ordinal) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return [];
        }
    }

    private sealed record PluginState(List<string>? Disabled);

    public SaveAnalysisPluginListDto GetPlugins()
    {
        var (plugins, errors) = LoadAll();
        var disabled = ReadDisabled();
        return new SaveAnalysisPluginListDto
        {
            Plugins = plugins.Select(plugin => ToDto(plugin, !disabled.Contains(plugin.Manifest.Id))).ToArray(),
            Errors = errors,
            UserDirectory = UserDirectory,
            DevDirectory = DevDirectory
        };
    }

    public bool SetEnabled(SaveAnalysisPluginEnabledRequest request)
    {
        try
        {
            var disabled = ReadDisabled();
            if (request.Enabled) disabled.Remove(request.PluginId);
            else disabled.Add(request.PluginId);
            Directory.CreateDirectory(UserDirectory);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new PluginState(disabled.Order(StringComparer.Ordinal).ToList())));
            return true;
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to save plugin state");
            return false;
        }
    }

    /// <summary>只能卸载用户目录中的插件（开发目录由开发者自己管理）。</summary>
    public async Task<bool> UninstallAsync(string pluginId)
    {
        await _installLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(UserDirectory, pluginId);
            if (!SaveAnalysisManifestLoader.IsSafeRelativePath(pluginId) || !Directory.Exists(directory)) return false;
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to uninstall plugin {PluginId}", pluginId);
            return false;
        }
        finally
        {
            _installLock.Release();
        }
    }

    public async Task<bool> OpenFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(UserDirectory);
            await Electron.Shell.OpenPathAsync(DevDirectory ?? UserDirectory).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to open the plugins folder");
            return false;
        }
    }

    /// <summary>交出插件声明过的入口脚本或数据文件（文本）。</summary>
    public SaveAnalysisPluginFileDto GetPluginFile(SaveAnalysisPluginFileRequest request)
    {
        var plugin = Find(request.PluginId);
        if (plugin == null) return new SaveAnalysisPluginFileDto { Success = false, Error = "notFound", Content = string.Empty };
        var declared = request.Path == plugin.Manifest.Entry || plugin.Manifest.Data.Contains(request.Path);
        if (!declared || !SaveAnalysisManifestLoader.IsSafeRelativePath(request.Path))
            return new SaveAnalysisPluginFileDto { Success = false, Error = "notDeclared", Content = string.Empty };
        var file = Path.GetFullPath(Path.Combine(plugin.Directory, request.Path));
        if (!file.StartsWith(Path.GetFullPath(plugin.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            return new SaveAnalysisPluginFileDto { Success = false, Error = "notFound", Content = string.Empty };
        if (new FileInfo(file).Length > SaveAnalysisManifest.MaxPluginFileBytes)
            return new SaveAnalysisPluginFileDto { Success = false, Error = "tooLarge", Content = string.Empty };
        return new SaveAnalysisPluginFileDto { Success = true, Content = File.ReadAllText(file, Encoding.UTF8) };
    }

    private static SaveAnalysisPluginDto ToDto(LoadedPlugin plugin, bool enabled) => new()
    {
        Id = plugin.Manifest.Id,
        Version = plugin.Manifest.Version,
        ApiVersion = plugin.Manifest.ApiVersion,
        Name = ToDto(plugin.Manifest.Name),
        Description = ToDto(plugin.Manifest.Description ?? LocalizedText.Empty),
        Author = plugin.Manifest.Author,
        Homepage = plugin.Manifest.Homepage,
        AppIds = plugin.Manifest.AppIds,
        Entry = plugin.Manifest.Entry,
        Data = plugin.Manifest.Data,
        UsesSteamAchievements = plugin.Manifest.SteamAchievements,
        ImageHosts = plugin.Manifest.ImageHosts,
        MaxFileSize = plugin.Manifest.EffectiveMaxFileSize,
        Source = plugin.Source,
        Enabled = enabled,
        Compatible = plugin.Manifest.ApiVersion == SaveAnalysisManifest.SupportedApiVersion,
        Directory = plugin.Directory
    };

    private static LocalizedTextDto ToDto(LocalizedText text) => new() { ZhCn = text.ZhCn, EnUs = text.EnUs };

    #endregion

    #region 插件仓库

    private sealed record RepositoryFile(string Path, long Size, string Sha256);

    private sealed record RepositoryPlugin(
        string Id,
        string Version,
        int ApiVersion,
        LocalizedText Name,
        LocalizedText? Description,
        string? Author,
        IReadOnlyList<int>? AppIds,
        string Path,
        IReadOnlyList<RepositoryFile> Files);

    private sealed record RepositoryIndex(int SchemaVersion, IReadOnlyList<RepositoryPlugin> Plugins);

    private IReadOnlyList<string> RepositoryBases()
    {
        if (Environment.GetEnvironmentVariable(RepositoryVariable) is { Length: > 0 } custom) return [custom.TrimEnd('/', '\\')];
        // 国内网络优化时优先 jsDelivr
        return settingsStore.GetSettings().Network?.CnOptimized == true ? DefaultRepositories.Reverse().ToArray() : DefaultRepositories;
    }

    private async Task<byte[]?> FetchAsync(string baseUrl, string relative, long maxBytes, CancellationToken cancellationToken)
    {
        if (Directory.Exists(baseUrl))
        {
            var file = Path.GetFullPath(Path.Combine(baseUrl, relative.Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(file) && new FileInfo(file).Length <= maxBytes ? await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false) : null;
        }
        using var client = httpClientFactory.CreateClient(SteamStatHttpClients.Download);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{Uri.EscapeDataString(relative).Replace("%2F", "/")}");
        SteamHttpRequestOptions.SetOperation(request, "plugin-repository");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return bytes.Length <= maxBytes ? bytes : null;
    }

    private async Task<(RepositoryIndex? Index, string? Source)> LoadIndexAsync(CancellationToken cancellationToken)
    {
        foreach (var baseUrl in RepositoryBases())
        {
            try
            {
                var bytes = await FetchAsync(baseUrl, "index.json", 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                if (bytes == null) continue;
                // 手工编辑的 index.json 可能带 UTF-8 BOM
                var json = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes.AsSpan(3) : bytes.AsSpan();
                var index = JsonSerializer.Deserialize<RepositoryIndex>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new LocalizedTextConverter() }
                });
                if (index?.Plugins != null) return (index, baseUrl);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or TaskCanceledException)
            {
                logger.LogInformation("Plugin repository {Repository} unavailable with {ExceptionType}", baseUrl, exception.GetType().Name);
            }
        }
        return (null, null);
    }

    public async Task<SaveAnalysisRepositoryDto> GetRepositoryAsync(CancellationToken cancellationToken = default)
    {
        var (index, source) = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index == null) return new SaveAnalysisRepositoryDto { Success = false, Error = "unreachable", Plugins = [] };
        var installed = LoadAll().Plugins.Where(plugin => plugin.Source == "user").ToDictionary(plugin => plugin.Manifest.Id, plugin => plugin.Manifest.Version);
        return new SaveAnalysisRepositoryDto
        {
            Success = true,
            Source = source,
            Plugins = index.Plugins
                .Where(plugin => !string.IsNullOrWhiteSpace(plugin.Id) && plugin.Files is { Count: > 0 })
                .Select(plugin =>
                {
                    installed.TryGetValue(plugin.Id, out var current);
                    return new SaveAnalysisRepositoryPluginDto
                    {
                        Id = plugin.Id,
                        Version = plugin.Version,
                        ApiVersion = plugin.ApiVersion,
                        Name = ToDto(plugin.Name),
                        Description = ToDto(plugin.Description ?? LocalizedText.Empty),
                        Author = plugin.Author,
                        AppIds = plugin.AppIds ?? [],
                        Size = plugin.Files.Sum(file => file.Size),
                        InstalledVersion = current,
                        UpdateAvailable = current != null && SaveAnalysisManifestLoader.CompareVersions(plugin.Version, current) > 0,
                        Compatible = plugin.ApiVersion == SaveAnalysisManifest.SupportedApiVersion
                    };
                })
                .ToArray()
        };
    }

    /// <summary>下载到暂存目录并逐个校验 SHA-256 与清单，全部通过后整体替换已安装版本。</summary>
    public async Task<SaveAnalysisInstallResultDto> InstallAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(UserDirectory, $".staging-{Guid.NewGuid():N}");
        try
        {
            var (index, source) = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
            if (index == null || source == null) return new SaveAnalysisInstallResultDto { Success = false, Error = "unreachable" };
            var entry = index.Plugins.FirstOrDefault(plugin => plugin.Id == pluginId);
            if (entry == null) return new SaveAnalysisInstallResultDto { Success = false, Error = "notFound" };
            if (entry.ApiVersion != SaveAnalysisManifest.SupportedApiVersion) return new SaveAnalysisInstallResultDto { Success = false, Error = "incompatible" };
            if (!SaveAnalysisManifestLoader.IsSafeRelativePath(entry.Path) || entry.Files.Sum(file => file.Size) > MaxPluginBytes
                || entry.Files.Any(file => !SaveAnalysisManifestLoader.IsSafeRelativePath(file.Path)))
                return new SaveAnalysisInstallResultDto { Success = false, Error = "integrity" };

            var target = Path.Combine(staging, pluginId);
            Directory.CreateDirectory(target);
            foreach (var file in entry.Files)
            {
                var bytes = await FetchAsync(source, $"{entry.Path}/{file.Path}", Math.Max(file.Size, 1), cancellationToken).ConfigureAwait(false);
                if (bytes == null) return new SaveAnalysisInstallResultDto { Success = false, Error = "unreachable" };
                if (bytes.Length != file.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new SaveAnalysisInstallResultDto { Success = false, Error = "integrity" };
                var destination = Path.GetFullPath(Path.Combine(target, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return new SaveAnalysisInstallResultDto { Success = false, Error = "integrity" };
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);
            }
            if (!SaveAnalysisManifestLoader.TryLoad(target, out var manifest, out _) || manifest!.Id != pluginId)
                return new SaveAnalysisInstallResultDto { Success = false, Error = "invalidManifest" };

            var installed = Path.Combine(UserDirectory, pluginId);
            if (Directory.Exists(installed)) Directory.Delete(installed, recursive: true);
            Directory.Move(target, installed);
            return new SaveAnalysisInstallResultDto { Success = true, Version = manifest.Version };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to install plugin {PluginId}", pluginId);
            return new SaveAnalysisInstallResultDto { Success = false, Error = "failed" };
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
                // 暂存目录清理失败不影响结果
            }
            _installLock.Release();
        }
    }

    #endregion

    #region 存档定位与只读读取

    public async Task<IReadOnlyList<SaveAnalysisFileDto>> LocateAsync(string pluginId)
    {
        var plugin = Find(pluginId);
        if (plugin == null) return [];
        Dictionary<int, string?> installDirs;
        await using (var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false))
        {
            installDirs = (await db.SteamAppTable.AsNoTracking()
                    .Where(app => plugin.Manifest.AppIds.Contains(app.AppId) && app.InstallDirPath != null)
                    .Select(app => new { app.AppId, app.InstallDirPath })
                    .ToListAsync().ConfigureAwait(false))
                .GroupBy(app => app.AppId)
                .ToDictionary(group => group.Key, group => group.First().InstallDirPath);
        }
        var environment = new SaveLocatorEnvironment(
            installLocator.ReadSteamRegistry().SteamPath,
            SaveAnalysisLocator.SystemFolders,
            appId => installDirs.GetValueOrDefault(appId));
        var located = await Task.Run(() => SaveAnalysisLocator.Locate(plugin.Manifest, environment)).ConfigureAwait(false);
        return located.Select(item => new SaveAnalysisFileDto
        {
            Token = Issue(item.Path, plugin.Manifest.EffectiveMaxFileSize),
            Name = item.Name,
            Path = item.Path,
            LocationId = item.Location.Id,
            LocationLabel = item.Location.Label == null ? null : ToDto(item.Location.Label),
            SteamId = item.SteamId,
            Size = item.Size,
            ModifiedAt = item.ModifiedAt
        }).ToArray();
    }

    /// <summary>用户手动选择存档文件（智能识别：按各插件的 detect 规则匹配）。</summary>
    public async Task<SaveAnalysisPickResultDto?> PickAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return null;
        string[] files;
        try
        {
            files = await Electron.Dialog.ShowOpenDialogAsync(snapshot.Window, new OpenDialogOptions
            {
                Properties = [OpenDialogProperty.openFile]
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show the save file picker");
            return null;
        }
        if (files is not { Length: > 0 } || !File.Exists(files[0])) return null;
        var info = new FileInfo(files[0]);
        var head = new byte[64];
        int read;
        await using (var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            read = await stream.ReadAsync(head).ConfigureAwait(false);
        var disabled = ReadDisabled();
        var matches = LoadAll().Plugins
            .Where(plugin => !disabled.Contains(plugin.Manifest.Id) && SaveAnalysisLocator.Detects(plugin.Manifest, info.Name, head.AsSpan(0, read)))
            .Select(plugin => plugin.Manifest.Id)
            .ToArray();
        return new SaveAnalysisPickResultDto
        {
            File = new SaveAnalysisFileDto
            {
                Token = Issue(info.FullName, SaveAnalysisManifest.MaxAllowedFileSize),
                Name = info.Name,
                Path = info.FullName,
                Size = info.Length,
                ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()
            },
            PluginIds = matches
        };
    }

    /// <summary>分块只读读取（单条 IPC 消息有 1MB 上限）；源文件以共享只读方式打开，绝不写入。</summary>
    public async Task<SaveAnalysisChunkDto> ReadAsync(SaveAnalysisReadRequest request)
    {
        if (!_tokens.TryGetValue(request.Token, out var entry))
            return new SaveAnalysisChunkDto { Success = false, Error = "expired", Base64 = string.Empty, Offset = request.Offset, TotalSize = 0 };
        try
        {
            if (!File.Exists(entry.Path))
                return new SaveAnalysisChunkDto { Success = false, Error = "notFound", Base64 = string.Empty, Offset = request.Offset, TotalSize = 0 };
            await using var stream = new FileStream(entry.Path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete
            });
            if (stream.Length > entry.MaxSize)
                return new SaveAnalysisChunkDto { Success = false, Error = "tooLarge", Base64 = string.Empty, Offset = request.Offset, TotalSize = stream.Length };
            stream.Seek(Math.Min(request.Offset, stream.Length), SeekOrigin.Begin);
            var buffer = new byte[Math.Min(request.Length, Math.Max(0, stream.Length - request.Offset))];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            return new SaveAnalysisChunkDto
            {
                Success = true,
                Base64 = Convert.ToBase64String(buffer, 0, total),
                Offset = request.Offset,
                TotalSize = stream.Length
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SaveAnalysisChunkDto { Success = false, Error = "unreadable", Base64 = string.Empty, Offset = request.Offset, TotalSize = 0 };
        }
    }

    /// <summary>
    /// 签发读取凭据。必须带非十六进制前缀：Electron.NET 桥（JsonToBoxedPrimitivesConverter）会把形似
    /// GUID / 日期 / 时间间隔的字符串参数转换成对应类型，纯 32 位十六进制会被当成 GUID 改写成带连字符的格式。
    /// 同一路径复用同一个凭据：反复定位不会让表无限增长，也不会让渲染层手里的旧凭据失效。
    /// </summary>
    private string Issue(string path, long maxSize)
    {
        var key = Path.GetFullPath(path);
        if (_tokensByPath.TryGetValue(key, out var existing) && _tokens.TryGetValue(existing, out var entry))
        {
            // 用户手动选择的文件（上限更宽）与插件定位到的同一文件：取较大的上限
            _tokens[existing] = (key, Math.Max(entry.MaxSize, maxSize));
            return existing;
        }
        if (_tokens.Count > MaxTokens)
        {
            _tokens.Clear();
            _tokensByPath.Clear();
        }
        var token = "save_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _tokens[token] = (key, maxSize);
        _tokensByPath[key] = token;
        return token;
    }

    #endregion
}
