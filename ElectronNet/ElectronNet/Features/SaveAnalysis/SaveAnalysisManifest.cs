using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ElectronNet.Features.SaveAnalysis;

/// <summary>多语言文本：清单里可以写字符串，也可以写 {"zh-CN": "...", "en-US": "..."}。</summary>
[JsonConverter(typeof(LocalizedTextConverter))]
public sealed record LocalizedText(string ZhCn, string EnUs)
{
    public static readonly LocalizedText Empty = new(string.Empty, string.Empty);
}

internal sealed class LocalizedTextConverter : JsonConverter<LocalizedText>
{
    public override LocalizedText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString() ?? string.Empty;
            return new LocalizedText(text, text);
        }
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Localized text must be a string or an object.");
        string? zh = null, en = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString();
            reader.Read();
            var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (string.Equals(key, "zh-CN", StringComparison.OrdinalIgnoreCase)) zh = value;
            else if (string.Equals(key, "en-US", StringComparison.OrdinalIgnoreCase)) en = value;
        }
        return new LocalizedText(zh ?? en ?? string.Empty, en ?? zh ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("zh-CN", value.ZhCn);
        writer.WriteString("en-US", value.EnUs);
        writer.WriteEndObject();
    }
}

public sealed record SaveAnalysisLocation
{
    public string Id { get; init; } = string.Empty;
    public LocalizedText? Label { get; init; }
    /// <summary>steamUserdata / documents / appData / localAppData / localLow / savedGames / gameInstall / home / xdgData / xdgConfig</summary>
    public string Root { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public IReadOnlyList<string> Files { get; init; } = [];
}

public sealed record SaveAnalysisDetect
{
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>文件开头的 ASCII 字符串（可选）。</summary>
    public string? Magic { get; init; }
}

/// <summary>插件清单（manifest.json）。</summary>
public sealed record SaveAnalysisManifest
{
    public const int SupportedApiVersion = 1;
    public const long DefaultMaxFileSize = 4 * 1024 * 1024;
    public const long MaxAllowedFileSize = 64 * 1024 * 1024;
    public const int MaxPluginFileBytes = 900 * 1024;

    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public int ApiVersion { get; init; }
    public LocalizedText Name { get; init; } = LocalizedText.Empty;
    public LocalizedText? Description { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public IReadOnlyList<int> AppIds { get; init; } = [];
    public string Entry { get; init; } = "parser.js";
    public IReadOnlyList<string> Data { get; init; } = [];
    public bool SteamAchievements { get; init; }
    public long? MaxFileSize { get; init; }
    public IReadOnlyList<string> ImageHosts { get; init; } = [];
    public IReadOnlyList<SaveAnalysisLocation> Locations { get; init; } = [];
    public SaveAnalysisDetect? Detect { get; init; }

    public long EffectiveMaxFileSize => Math.Clamp(MaxFileSize ?? DefaultMaxFileSize, 1, MaxAllowedFileSize);
}

public static partial class SaveAnalysisManifestLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly HashSet<string> Roots = new(StringComparer.Ordinal)
    {
        "steamUserdata", "documents", "appData", "localAppData", "localLow", "savedGames", "gameInstall", "home", "xdgData", "xdgConfig"
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+(\.\d+){0,3}([-+][0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    public static SaveAnalysisManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<SaveAnalysisManifest>(json, Options)
                       ?? throw new InvalidDataException("empty manifest");
        Validate(manifest);
        return manifest;
    }

    public static bool TryLoad(string directory, out SaveAnalysisManifest? manifest, out string? error)
    {
        manifest = null;
        var file = System.IO.Path.Combine(directory, "manifest.json");
        if (!File.Exists(file))
        {
            error = "missingManifest";
            return false;
        }
        try
        {
            manifest = Parse(File.ReadAllText(file));
            if (!string.Equals(manifest.Id, System.IO.Path.GetFileName(directory), StringComparison.Ordinal))
                throw new InvalidDataException("folderMismatch");
            if (!File.Exists(System.IO.Path.Combine(directory, manifest.Entry))) throw new InvalidDataException("missingEntry");
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or NotSupportedException)
        {
            manifest = null;
            error = exception is InvalidDataException ? exception.Message : "invalidJson";
            return false;
        }
    }

    private static void Validate(SaveAnalysisManifest manifest)
    {
        if (!IdPattern().IsMatch(manifest.Id)) throw new InvalidDataException("invalidId");
        if (!VersionPattern().IsMatch(manifest.Version)) throw new InvalidDataException("invalidVersion");
        if (manifest.ApiVersion < 1) throw new InvalidDataException("invalidApiVersion");
        if (string.IsNullOrWhiteSpace(manifest.Name.ZhCn) && string.IsNullOrWhiteSpace(manifest.Name.EnUs)) throw new InvalidDataException("missingName");
        if (!IsSafeRelativePath(manifest.Entry) || !manifest.Entry.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("invalidEntry");
        if (manifest.Data.Any(path => !IsSafeRelativePath(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("invalidData");
        if (manifest.AppIds.Any(appId => appId <= 0)) throw new InvalidDataException("invalidAppId");
        if (manifest.ImageHosts.Any(host => Uri.CheckHostName(host) != UriHostNameType.Dns)) throw new InvalidDataException("invalidImageHost");
        foreach (var location in manifest.Locations)
        {
            if (string.IsNullOrWhiteSpace(location.Id) || !Roots.Contains(location.Root)) throw new InvalidDataException("invalidLocation");
            if (location.Path.Length > 0 && !IsSafeRelativePath(location.Path)) throw new InvalidDataException("invalidLocation");
            if (location.Files.Count == 0 || location.Files.Any(pattern => !IsFilePattern(pattern))) throw new InvalidDataException("invalidLocation");
        }
        if (manifest.Detect?.Files.Any(pattern => !IsFilePattern(pattern)) == true) throw new InvalidDataException("invalidDetect");
    }

    /// <summary>相对路径：/ 分隔，不能是绝对路径，不能含 . / .. 段。</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 256) return false;
        if (path.Contains('\\') || path.Contains(':') || path.StartsWith('/')) return false;
        return path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    /// <summary>文件名通配：只允许 * 和 ?，不能含目录分隔符。</summary>
    public static bool IsFilePattern(string? pattern)
        => !string.IsNullOrWhiteSpace(pattern) && pattern.Length <= 128
           && pattern.IndexOfAny(['/', '\\', ':']) < 0 && pattern != "." && pattern != "..";

    public static bool MatchesPattern(string fileName, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    /// <summary>语义化版本比较（只比较数字段，预发布标记视为更低）。</summary>
    public static int CompareVersions(string left, string right)
    {
        static (int[] Numbers, bool Prerelease) Split(string version)
        {
            var core = version.Split('-', '+')[0];
            var numbers = core.Split('.').Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
            return (numbers, version.Contains('-'));
        }
        var (a, aPre) = Split(left);
        var (b, bPre) = Split(right);
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var x = index < a.Length ? a[index] : 0;
            var y = index < b.Length ? b[index] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return aPre == bPre ? 0 : aPre ? -1 : 1;
    }
}
