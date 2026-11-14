using System.Text.Json.Nodes;

namespace SteamStat.Core.Assistant;

/// <summary>模型被要求输出 JSON 时，常常包一层代码块或说两句客套话。这里只取第一个完整的 JSON 值。</summary>
public static class ModelOutput
{
    public static JsonNode? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOfAny(['{', '[']);
        if (start < 0) return null;
        var close = text[start] == '{' ? '}' : ']';
        for (var end = text.LastIndexOf(close); end > start; end = text.LastIndexOf(close, end - 1))
        {
            try
            {
                return JsonNode.Parse(text[start..(end + 1)]);
            }
            catch (System.Text.Json.JsonException)
            {
                // 往前找更短的一段
            }
        }
        return null;
    }

    /// <summary>去掉模型偶尔加的引号、代码块和首尾空白，并限制长度。</summary>
    public static string CleanLine(string? text, int maxLength)
    {
        var value = (text ?? string.Empty).Trim().Trim('`').Trim();
        if (value.Length >= 2 && (value[0] is '"' or '“' or '「') && (value[^1] is '"' or '”' or '」')) value = value[1..^1].Trim();
        return value.Length > maxLength ? value[..maxLength] : value;
    }
}
