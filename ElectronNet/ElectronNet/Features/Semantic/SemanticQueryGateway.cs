using System.Diagnostics;
using System.Text;
using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace ElectronNet.Features.Semantic;

internal enum SemanticQueryFailure
{
    None,
    /// <summary>语句提到了视图以外的对象（内部表、未授权的视图），或做了读以外的操作。</summary>
    Denied,
    /// <summary>语句有语法或列名错误。</summary>
    Invalid,
    /// <summary>超过时限。</summary>
    Timeout
}

internal sealed record SemanticQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    SemanticQueryFailure Failure,
    long ElapsedMilliseconds);

/// <summary>
/// 所有外部查询（SQL 控制台、MCP、命令行、OBS）的唯一入口。在只读连接上有两层防线：
/// <list type="number">
/// <item>
/// 词法扫描：表名没法动态拼出来，语句要读一张表就得写出它的名字。所以语句里出现任何
/// 「库里存在、但不是被授权视图」的名字都拒绝，这一层保证隐藏游戏过滤和隐私分级绕不过去。
/// </item>
/// <item>
/// authorizer：只放行 SELECT，读取只限视图实际依赖的底层表。凭证、缓存、同步状态、sqlite_master
/// 不在其中，即使词法扫描被绕过也读不到；PRAGMA、ATTACH 和所有写操作同样被拒绝。
/// </item>
/// </list>
/// authorizer 不能单独承担第一层：它报告的「最内层视图」其实是 FROM 项的名字，
/// 用户写 <c>WITH v_session AS (SELECT * FROM steam_login_token)</c> 就能伪造。
/// </summary>
internal static class SemanticQueryGateway
{
    internal const int DefaultMaxRows = 5000;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    // sqlite3_progress_handler 每执行这么多条虚拟机指令回调一次
    private const int ProgressInterval = 20_000;

    private static readonly HashSet<string> BlockedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_extension", "fts3_tokenizer", "readfile", "writefile", "edit"
    };

    private static readonly string[] SystemTables =
        ["sqlite_master", "sqlite_schema", "sqlite_temp_master", "sqlite_temp_schema", "sqlite_sequence", "sqlite_stat1", "sqlite_stat4", "dbstat", "sqlite_dbpage"];

    /// <summary>authorizer 只放行视图本身和它们依赖的底层表。分级由词法扫描负责。</summary>
    private static readonly IReadOnlySet<string> ReadableObjects
        = SemanticSchema.UnderlyingTables().Concat(SemanticSchema.ByName.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static async Task<SemanticQueryResult> ExecuteAsync(
        string databaseFile,
        string sql,
        IReadOnlySet<string> allowedTiers,
        IReadOnlyDictionary<string, object?>? parameters = null,
        int maxRows = DefaultMaxRows,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var deadline = stopwatch.Elapsed + (timeout ?? DefaultTimeout);
        var timedOut = false;

        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(databaseFile, pooling: false, readOnly: true));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA query_only = ON", cancellationToken).ConfigureAwait(false);

        var allowedViews = SemanticSchema.Views
            .Where(view => allowedTiers.Contains(view.Tier))
            .Select(view => view.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var databaseObjects = await DatabaseObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        var forbidden = databaseObjects.Except(allowedViews, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (MentionsForbiddenName(sql, forbidden))
            return new SemanticQueryResult([], [], false, SemanticQueryFailure.Denied, stopwatch.ElapsedMilliseconds);

        raw.sqlite3_set_authorizer(connection.Handle!, (_, action, first, second, _, _) => Authorize(action, first, second, databaseObjects), null);
        raw.sqlite3_progress_handler(connection.Handle!, ProgressInterval, _ =>
        {
            if (stopwatch.Elapsed <= deadline && !cancellationToken.IsCancellationRequested) return 0;
            timedOut = true;
            return 1;
        }, null);

        var rows = new List<object?[]>();
        var columns = new List<string>();
        var truncated = false;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (parameters != null)
                foreach (var (name, value) in parameters)
                    command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < reader.FieldCount; index++) columns.Add(reader.GetName(index));
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }
                var row = new object?[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                    row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                rows.Add(row);
            }
        }
        catch (SqliteException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = timedOut ? SemanticQueryFailure.Timeout
                : exception.SqliteErrorCode == raw.SQLITE_AUTH
                  || exception.Message.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
                  || exception.Message.Contains("prohibited", StringComparison.OrdinalIgnoreCase) ? SemanticQueryFailure.Denied
                : SemanticQueryFailure.Invalid;
            return new SemanticQueryResult([], [], false, failure, stopwatch.ElapsedMilliseconds);
        }
        return new SemanticQueryResult(columns, rows, truncated, SemanticQueryFailure.None, stopwatch.ElapsedMilliseconds);
    }

    internal static int Authorize(int action, string? first, string? second, IReadOnlySet<string> databaseObjects) => action switch
    {
        raw.SQLITE_SELECT or raw.SQLITE_RECURSIVE => raw.SQLITE_OK,
        raw.SQLITE_FUNCTION => second != null && BlockedFunctions.Contains(second) ? raw.SQLITE_DENY : raw.SQLITE_OK,
        raw.SQLITE_READ => IsReadable(first, second, databaseObjects) ? raw.SQLITE_OK : raw.SQLITE_DENY,
        _ => raw.SQLITE_DENY
    };

    private static bool IsReadable(string? table, string? column, IReadOnlySet<string> databaseObjects)
    {
        if (table == null) return false;
        if (ReadableObjects.Contains(table)) return true;
        // SELECT COUNT(*) FROM cte 这类语句只发一个空列名的事件，名字是 CTE，不是库里的表
        return column is { Length: 0 } && !databaseObjects.Contains(table);
    }

    /// <summary>库里的全部表和视图（新增的表自动受保护）以及系统表。</summary>
    private static async Task<HashSet<string>> DatabaseObjectsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(SystemTables, StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view')";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>
    /// 按 SQLite 的词法规则切分语句，判断是否写出了某个名字。标识符有四种写法（裸写、"x"、`x`、[x]），
    /// 单引号字符串在表名位置也会被当成标识符，所以都要比较。注释里的内容不算。
    /// </summary>
    internal static bool MentionsForbiddenName(string sql, IReadOnlySet<string> forbidden)
    {
        foreach (var token in Tokens(sql))
            if (forbidden.Contains(token) || token.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static IEnumerable<string> Tokens(string sql)
    {
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];
            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                while (index < sql.Length && sql[index] != '\n') index++;
            }
            else if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? sql.Length : end + 2;
            }
            else if (current is '"' or '`' or '\'')
            {
                yield return ReadQuoted(sql, ref index, current);
            }
            else if (current == '[')
            {
                var end = sql.IndexOf(']', index + 1);
                var stop = end < 0 ? sql.Length : end;
                yield return sql[(index + 1)..stop];
                index = stop + 1;
            }
            else if (IsIdentifierStart(current))
            {
                var start = index;
                while (index < sql.Length && IsIdentifierPart(sql[index])) index++;
                yield return sql[start..index];
            }
            else
            {
                index++;
            }
        }
    }

    private static string ReadQuoted(string sql, ref int index, char quote)
    {
        var builder = new StringBuilder();
        index++;
        while (index < sql.Length)
        {
            if (sql[index] != quote)
            {
                builder.Append(sql[index++]);
                continue;
            }
            if (index + 1 < sql.Length && sql[index + 1] == quote)
            {
                builder.Append(quote);
                index += 2;
                continue;
            }
            index++;
            break;
        }
        return builder.ToString();
    }

    private static bool IsIdentifierStart(char value) => char.IsAsciiLetter(value) || value == '_' || value >= 0x80;

    private static bool IsIdentifierPart(char value) => IsIdentifierStart(value) || char.IsAsciiDigit(value) || value == '$';
}
