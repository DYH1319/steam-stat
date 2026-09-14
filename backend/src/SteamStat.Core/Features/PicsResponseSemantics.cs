namespace SteamStat.Core.Features;

internal enum PicsAppOutcome
{
    Success,
    AccessDenied,
    NotFound,
    Incomplete
}

internal sealed record PicsProductInfoBatch(
    bool ResponsePending,
    IReadOnlyDictionary<uint, bool> AppsMissingToken,
    IReadOnlySet<uint> UnknownApps);

internal sealed record PicsProductInfoResultSet(
    bool Complete,
    bool Failed,
    IReadOnlyList<PicsProductInfoBatch> Results);

internal static class PicsResponseSemantics
{
    internal static PicsAppOutcome ClassifyAccessToken(
        uint appId,
        IReadOnlyDictionary<uint, ulong> appTokens,
        IReadOnlySet<uint> appTokensDenied)
    {
        if (appTokens.ContainsKey(appId)) return PicsAppOutcome.Success;
        return appTokensDenied.Contains(appId) ? PicsAppOutcome.AccessDenied : PicsAppOutcome.Incomplete;
    }

    internal static PicsAppOutcome ClassifyProductInfo(uint appId, PicsProductInfoResultSet resultSet)
    {
        if (!resultSet.Complete || resultSet.Failed || resultSet.Results.Any(batch => batch.ResponsePending))
            return PicsAppOutcome.Incomplete;
        var unknown = false;
        bool? missingToken = null;
        foreach (var batch in resultSet.Results)
        {
            unknown |= batch.UnknownApps.Contains(appId);
            if (batch.AppsMissingToken.TryGetValue(appId, out var missing)) missingToken = missing;
        }
        if (missingToken.HasValue) return missingToken.Value ? PicsAppOutcome.AccessDenied : PicsAppOutcome.Success;
        return unknown ? PicsAppOutcome.NotFound : PicsAppOutcome.Incomplete;
    }
}
