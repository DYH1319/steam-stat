using ElectronNet.Constants;
using ValveKeyValue;

namespace ElectronNet.Helpers;

/// <summary>
/// Valve Data File 格式助手类
/// </summary>
public static class VdfHelper
{
    private static readonly KVSerializerOptions DefaultOptions = new()
    {
        HasEscapeSequences = true,
    };

    /// <summary>
    /// 根据路径读取 Valve Data File 内容
    /// </summary>
    /// <param name="filePath">vdf 文件路径</param>
    /// <param name="isBinary">是否是二进制格式的数据格式，默认为 false</param>
    /// <param name="options">KV 序列化器选项，默认为 null；为 null 时使用 DefaultOptions</param>
    /// <returns></returns>
    public static KVDocument Read(string filePath, bool isBinary = false, KVSerializerOptions? options = null)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var kv = KVSerializer.Create(isBinary ? KVSerializationFormat.KeyValues1Binary : KVSerializationFormat.KeyValues1Text);
        var data = kv.Deserialize(
            stream,
            options ?? DefaultOptions
        );
        return data;
    }

    /// <summary>
    /// 根据路径写入 Valve Data File 内容
    /// </summary>
    /// <param name="filePath">vdf 文件路径</param>
    /// <param name="content">写入的文件内容</param>
    /// <param name="isBinary">是否是二进制格式的数据格式，默认为 false</param>
    /// <param name="options">KV 序列化器选项，默认为 null；为 null 时使用 DefaultOptions</param>
    public static void Write(string filePath, KVDocument content, bool isBinary = false, KVSerializerOptions? options = null)
    {
        try
        {
            // 不要用 FileMode.OpenOrCreate, 文件内容长度不一致会导致结尾内容错误
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var kv = KVSerializer.Create(isBinary ? KVSerializationFormat.KeyValues1Binary : KVSerializationFormat.KeyValues1Text);
            kv.Serialize(
                stream, 
                content, 
                options ?? DefaultOptions
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} Write vdf file error, filePath: {filePath}, ex: {ex.Message}");
        }
    }
}