using System.Security.Cryptography;

namespace ElectronNet.Infrastructure;

/// <summary>
/// Host 写出的文件（导出、备份）登记在这里，渲染层只拿到不透明的 id，用来「在文件夹中显示」，
/// 不能借此打开任意路径。id 带非十六进制前缀，避免被 Electron.NET 桥当成 GUID 改写格式。
/// </summary>
internal sealed class RevealRegistry
{
    private const int MaxEntries = 32;
    private readonly Lock _lock = new();
    private readonly List<(string Id, string Path)> _entries = [];

    internal string Register(string path)
    {
        var id = "loc_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        lock (_lock)
        {
            _entries.Add((id, path));
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        return id;
    }

    internal string? Resolve(string id)
    {
        lock (_lock) return _entries.FirstOrDefault(entry => entry.Id == id).Path;
    }
}

/// <summary>
/// 渲染层传给 Host 的口令 / 密钥统一编码为「s1:」+ Base64(UTF-8)。
/// Electron.NET 桥会把形如日期、时间、GUID 的字符串参数改写格式，原样传递会破坏口令（例如「12:30」）。
/// </summary>
internal static class IpcSecret
{
    internal const string Prefix = "s1:";

    internal static string? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[Prefix.Length..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string Encode(string value) => Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
}
