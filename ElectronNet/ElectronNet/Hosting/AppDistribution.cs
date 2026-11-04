using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElectronNet.Hosting;

/// <summary>应用如何获得更新。</summary>
public enum AppUpdateMode
{
    /// <summary>应用内下载并安装（NSIS 安装版、AppImage，经 electron-updater）。</summary>
    Auto,
    /// <summary>只提示新版本，下载跳转发布页（便携版、tar.gz、开发构建）。</summary>
    Notify,
    /// <summary>由包管理器更新（Scoop / AUR / Flatpak 等），应用只提示并给出命令。</summary>
    Managed
}

/// <summary>
/// 当前进程的发行形态。打包脚本 / 包管理器清单可以在程序目录写入 distribution.json 显式声明：
/// <code>{ "kind": "scoop", "portableData": true, "updateCommand": "scoop update steam-stat" }</code>
/// 没有该文件时按环境推断：开发运行 → dev；FLATPAK_ID → flatpak；APPIMAGE → appimage；
/// Windows 程序目录有卸载程序 → installer；其余 Linux → archive（tar.gz）。
/// 便携模式（数据放程序目录）只能由 distribution.json 显式开启，避免误判后已有用户「丢失」数据。
/// </summary>
public sealed record AppDistribution(string Kind, AppUpdateMode UpdateMode, bool PortableData, string? ManagedUpdateCommand)
{
    public const string DescriptorFileName = "distribution.json";
    public const string WindowsUninstallerName = "Uninstall Steam Stat.exe";

    public static AppDistribution Development { get; } = new("dev", AppUpdateMode.Notify, false, null);

    public string UpdateModeName => UpdateMode switch
    {
        AppUpdateMode.Auto => "auto",
        AppUpdateMode.Managed => "managed",
        _ => "notify"
    };

    public static AppDistribution Detect(string baseDirectory, bool isDevelopment, Func<string, string?> getEnvironmentVariable)
    {
        if (isDevelopment) return Development;

        var descriptor = ReadDescriptor(Path.Combine(baseDirectory, DescriptorFileName));
        if (descriptor?.Kind is { Length: > 0 } declaredKind)
            return FromKind(declaredKind.Trim().ToLowerInvariant(), descriptor.PortableData, descriptor.UpdateCommand);

        if (!string.IsNullOrEmpty(getEnvironmentVariable("FLATPAK_ID"))) return FromKind("flatpak", null, null);
        if (!string.IsNullOrEmpty(getEnvironmentVariable("APPIMAGE"))) return FromKind("appimage", null, null);
        if (OperatingSystem.IsWindows())
            return File.Exists(Path.Combine(baseDirectory, WindowsUninstallerName))
                ? FromKind("installer", null, null)
                : FromKind("unknown", false, null);
        return FromKind("archive", null, null);
    }

    internal static AppDistribution FromKind(string kind, bool? portableData, string? updateCommand) => kind switch
    {
        "installer" or "appimage" => new(kind, AppUpdateMode.Auto, portableData ?? false, null),
        "portable" => new(kind, AppUpdateMode.Notify, portableData ?? true, null),
        "archive" => new(kind, AppUpdateMode.Notify, portableData ?? false, null),
        "scoop" => new(kind, AppUpdateMode.Managed, portableData ?? true, updateCommand ?? "scoop update steam-stat"),
        "aur" => new(kind, AppUpdateMode.Managed, portableData ?? false, updateCommand ?? "yay -Syu steam-stat-bin"),
        "flatpak" => new(kind, AppUpdateMode.Managed, portableData ?? false, updateCommand ?? "flatpak update asia.dyh1319.steamstat"),
        "winget" => new(kind, AppUpdateMode.Managed, portableData ?? false, updateCommand ?? "winget upgrade DYH1319.SteamStat"),
        _ => new(kind, string.IsNullOrWhiteSpace(updateCommand) ? AppUpdateMode.Notify : AppUpdateMode.Managed, portableData ?? false, updateCommand)
    };

    /// <summary>便携模式的数据目录：程序目录下的 data。</summary>
    public string? PortableDataDirectory(string baseDirectory)
        => PortableData ? Path.Combine(baseDirectory, "data") : null;

    private static Descriptor? ReadDescriptor(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            return JsonSerializer.Deserialize<Descriptor>(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record Descriptor(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("portableData")] bool? PortableData,
        [property: JsonPropertyName("updateCommand")] string? UpdateCommand);
}
