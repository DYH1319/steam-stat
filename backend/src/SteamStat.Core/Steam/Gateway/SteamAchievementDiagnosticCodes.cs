namespace SteamStat.Core.Steam.Gateway;

public static class SteamAchievementDiagnosticCodes
{
    public const string SchemaCacheMiss = "achievement_schema_cache_miss";
    public const string SchemaSessionUnavailable = "achievement_schema_session_unavailable";
    public const string SchemaHandlerUnavailable = "achievement_schema_handler_unavailable";
    public const string SchemaInvalidPayload = "achievement_schema_invalid_payload";
    public const string SchemaPayloadTooLarge = "achievement_schema_payload_too_large";
    public const string SchemaStaleSessionGeneration = "achievement_schema_stale_session_generation";
    public const string ProgressSessionUnavailable = "achievement_progress_session_unavailable";
    public const string ProgressCacheMiss = "achievement_progress_cache_miss";
    public const string ProgressPartial = "achievement_progress_partial";
    public const string ProgressInvalidPayload = "achievement_progress_invalid_payload";
    public const string ProgressStaleSessionGeneration = "achievement_progress_stale_session_generation";
    public const string UnlocksCacheMiss = "achievement_unlocks_cache_miss";
    public const string UnlocksUnmatched = "achievement_unlocks_unmatched";
    public const string UserStatsFailed = "achievement_user_stats_failed";
}
