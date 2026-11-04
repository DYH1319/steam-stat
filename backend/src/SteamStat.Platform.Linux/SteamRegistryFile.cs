using ValveKeyValue;

namespace SteamStat.Platform.Linux;

/// <summary>
/// Linux 版 Steam 用 ~/.steam/registry.vdf 模拟 Windows 注册表（根节点 Registry，下分 HKCU / HKLM）。
/// 键名大小写在不同 Steam 版本间并不统一（如 language / Language），这里一律不区分大小写读取。
/// </summary>
internal static class SteamRegistryFile
{
    internal static readonly string[] SteamKeyPath = ["HKCU", "Software", "Valve", "Steam"];
    internal static readonly string[] MachineSteamKeyPath = ["HKLM", "Software", "Valve", "Steam"];

    private static readonly KVSerializerOptions Options = new() { HasEscapeSequences = true };

    internal static KVDocument? Load(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream, Options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KeyValueException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>原子写回：先写临时文件再替换，避免与 Steam 并发写入时留下半截文件。</summary>
    internal static void Save(string path, KVDocument document)
    {
        var temporary = path + ".steam-stat.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Serialize(stream, document, Options);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static KVObject? Child(KVObject? parent, string name)
    {
        if (parent?.Value.ValueType != KVValueType.Collection) return null;
        return parent.Children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    internal static KVObject? Navigate(KVObject? root, IEnumerable<string> path)
    {
        var current = root;
        foreach (var segment in path)
        {
            current = Child(current, segment);
            if (current == null) return null;
        }
        return current;
    }

    /// <summary>沿路径定位节点，缺失的中间节点按需创建。</summary>
    internal static KVObject EnsurePath(KVObject root, IEnumerable<string> path)
    {
        var current = root;
        foreach (var segment in path)
        {
            var next = Child(current, segment);
            if (next == null)
            {
                next = new KVObject(segment, Array.Empty<KVObject>());
                current.Add(next);
            }
            current = next;
        }
        return current;
    }

    internal static string? GetString(KVObject? node, string name)
    {
        var child = Child(node, name);
        if (child == null || child.Value.ValueType == KVValueType.Collection) return null;
        return child.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static int GetInt(KVObject? node, string name)
        => int.TryParse(GetString(node, name), out var value) ? value : 0;

    internal static int? GetNullableInt(KVObject? node, string name)
        => int.TryParse(GetString(node, name), out var value) ? value : null;

    /// <summary>写入字符串值；已存在的键沿用原有大小写，避免产生重复键。</summary>
    internal static void SetString(KVObject node, string name, string value)
    {
        var existing = Child(node, name);
        node[existing?.Name ?? name] = value;
    }
}
