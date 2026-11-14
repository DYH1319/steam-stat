using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;

namespace ElectronNet.Features.Assistant;

internal static class AuditChannels
{
    public const string Ai = "ai";
    public const string Api = "api";
    public const string Mcp = "mcp";
}

/// <summary>
/// 审计日志：每次发给模型的完整请求体、每次本地接口返回给调用方的内容，原样保存在本机，界面上随时可翻。
/// 每月两个文件：索引（每行一条摘要）和正文（索引里记着偏移），列表只读索引。保留 12 个月。
/// 只在本机：不进备份、同步和诊断包。
/// </summary>
internal sealed class AuditLog(IAppPaths appPaths, TimeProvider timeProvider, ILogger<AuditLog> logger)
{
    internal const int MaxContentBytes = 256 * 1024;
    private const int RetainedMonths = 12;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Lock _lock = new();
    private long _lastId;

    internal string Directory => Path.Combine(appPaths.UserDataDirectory, "Audit");

    private sealed record IndexLine(
        long Id, long At, string Channel, string Kind, string Destination, string Summary,
        IReadOnlyList<string> Scopes, int Bytes, bool Truncated, long Offset, int Length);

    public long Append(string channel, string kind, string destination, string summary, IEnumerable<string> scopes, string content)
    {
        var now = timeProvider.GetUtcNow();
        var bytes = Encoding.UTF8.GetBytes(content);
        var truncated = bytes.Length > MaxContentBytes;
        var stored = truncated ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes, 0, MaxContentBytes) + "\n…[truncated]") : bytes;
        lock (_lock)
        {
            // 毫秒时间戳乘 1000 再加序号：同一毫秒里的多条也有序，列表按 id 倒序就是时间倒序
            var id = Math.Max(now.ToUnixTimeMilliseconds() * 1000, _lastId + 1);
            _lastId = id;
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                long offset;
                using (var stream = new FileStream(ContentFile(month), FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    offset = stream.Position;
                    stream.Write(stored);
                    stream.WriteByte((byte)'\n');
                }
                var line = new IndexLine(id, now.ToUnixTimeSeconds(), channel, kind, destination, Shorten(summary, 200),
                    scopes.ToList(), bytes.Length, truncated, offset, stored.Length);
                File.AppendAllText(IndexFile(month), JsonSerializer.Serialize(line, Options) + "\n", Encoding.UTF8);
                Prune(now);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to write the audit log");
            }
            return id;
        }
    }

    public IReadOnlyList<AuditEntrySummaryDto> List(long? beforeId, int limit, string? channel)
    {
        var result = new List<AuditEntrySummaryDto>(limit);
        lock (_lock)
        {
            foreach (var month in Months())
            {
                foreach (var line in ReadIndex(month).Reverse())
                {
                    if (beforeId != null && line.Id >= beforeId) continue;
                    if (channel != null && line.Channel != channel) continue;
                    result.Add(ToDto(line));
                    if (result.Count >= limit) return result;
                }
            }
        }
        return result;
    }

    public AuditEntryDto? Get(long id)
    {
        lock (_lock)
        {
            var month = DateTimeOffset.FromUnixTimeMilliseconds(id / 1000).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var line = ReadIndex(month).FirstOrDefault(item => item.Id == id);
            if (line == null) return null;
            try
            {
                using var stream = new FileStream(ContentFile(month), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(line.Offset, SeekOrigin.Begin);
                var buffer = new byte[line.Length];
                stream.ReadExactly(buffer);
                return new AuditEntryDto { Summary = ToDto(line), Content = Encoding.UTF8.GetString(buffer) };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                logger.LogWarning(exception, "Failed to read an audit log entry");
                return null;
            }
        }
    }

    public bool Clear()
    {
        lock (_lock)
        {
            try
            {
                if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to clear the audit log");
                return false;
            }
        }
    }

    private string IndexFile(string month) => Path.Combine(Directory, $"{month}.index.jsonl");

    private string ContentFile(string month) => Path.Combine(Directory, $"{month}.content.jsonl");

    /// <summary>有索引的月份，新的在前。</summary>
    private IEnumerable<string> Months()
        => System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.index.jsonl")
                .Select(file => Path.GetFileName(file)[..7])
                .OrderDescending(StringComparer.Ordinal)
                .ToList()
            : [];

    private IReadOnlyList<IndexLine> ReadIndex(string month)
    {
        var file = IndexFile(month);
        if (!File.Exists(file)) return [];
        var lines = new List<IndexLine>();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } text)
            {
                if (text.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<IndexLine>(text, Options) is { } line) lines.Add(line);
                }
                catch (JsonException)
                {
                    // 写到一半断电留下的半行：跳过，不影响其他记录
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to read the audit log index");
        }
        return lines;
    }

    private void Prune(DateTimeOffset now)
    {
        var oldest = now.AddMonths(-RetainedMonths).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        foreach (var month in Months().Where(month => string.CompareOrdinal(month, oldest) < 0))
        {
            File.Delete(IndexFile(month));
            if (File.Exists(ContentFile(month))) File.Delete(ContentFile(month));
        }
    }

    private static AuditEntrySummaryDto ToDto(IndexLine line) => new()
    {
        Id = line.Id,
        At = line.At,
        Channel = line.Channel,
        Kind = line.Kind,
        Destination = line.Destination,
        Summary = line.Summary,
        Scopes = line.Scopes,
        Bytes = line.Bytes,
        Truncated = line.Truncated
    };

    internal static string Shorten(string text, int maxLength)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= maxLength ? single : single[..maxLength] + "…";
    }
}
