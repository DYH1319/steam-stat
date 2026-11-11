namespace ElectronNet.Features.Friends.Persistence;

/// <summary>
/// 一次性清理：删掉值和同一位好友、同一类型的上一条相同的状态记录。
/// CM 会话反复重连时，每次重连都把追踪的好友重新记一遍「上线」「开始游玩」，
/// 在线区间、作息热力图和时间线都被这些记录污染。记录器现在写入前会比较（<c>FriendStatusRecordService.SameValue</c>），
/// 这里处理已经写进去的；比较的字段要和它保持一致。
/// </summary>
internal static class FriendStatusRecordCleanup
{
    private const string KeyValue = """
        CASE change_type
            WHEN 'state' THEN CAST(json_extract(current_value, '$.personaState') AS TEXT)
            WHEN 'game' THEN COALESCE(NULLIF(CAST(json_extract(current_value, '$.gameId') AS TEXT), ''), '0')
            WHEN 'personaName' THEN CAST(json_extract(current_value, '$.personaName') AS TEXT)
            WHEN 'relationship' THEN CAST(json_extract(current_value, '$.relationship') AS TEXT)
        END
        """;

    /// <summary>
    /// 一段连续相同的记录只留第一条：每条都和紧挨着的上一条比，相同就删。
    /// 格式异常的历史记录（不是合法 JSON）不参与。
    /// </summary>
    internal const string Sql = $"""
        DELETE FROM friend_status_record
        WHERE id IN (
            SELECT id FROM (
                SELECT
                    id,
                    {KeyValue} AS value,
                    LAG({KeyValue}) OVER (
                        PARTITION BY account_name, friend_steam_id, change_type
                        ORDER BY id
                    ) AS previous
                FROM friend_status_record
                WHERE change_type IN ('state', 'game', 'personaName', 'relationship')
                    AND current_value IS NOT NULL
                    AND json_valid(current_value)
            )
            WHERE value IS NOT NULL AND value = previous
        );
        """;
}
