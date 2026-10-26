using FluentAssertions;
using SteamKit2;
using SteamKit2.Internal;
using SteamStat.Core.Features;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamKitContractTests
{
    [Test]
    public void SteamConfiguration_HttpClientFactorySignatureCreatesConfiguration()
    {
        var configuration = SteamConfiguration.Create(builder =>
            builder.WithHttpClientFactory(_ => new HttpClient()));

        configuration.HttpClientFactory.Should().NotBeNull();
    }

    [Test]
    public void PublicApp_ControlledResponsesRequireCompleteProductBatch()
    {
        const uint appId = 730;
        PicsResponseSemantics.ClassifyAccessToken(
            appId, new Dictionary<uint, ulong> { [appId] = 0 }, new HashSet<uint>())
            .Should().Be(PicsAppOutcome.Success);

        var appData = new PicsProductInfoBatch(false, new Dictionary<uint, bool> { [appId] = false }, new HashSet<uint>());
        PicsResponseSemantics.ClassifyProductInfo(appId, new PicsProductInfoResultSet(false, false, [appData]))
            .Should().Be(PicsAppOutcome.Incomplete);
        PicsResponseSemantics.ClassifyProductInfo(appId, new PicsProductInfoResultSet(true, false, [appData]))
            .Should().Be(PicsAppOutcome.Success);
    }

    [Test]
    public void RestrictedApp_ControlledDeniedTokenAndMissingTokenAreAccessDenied()
    {
        const uint appId = 123456;
        PicsResponseSemantics.ClassifyAccessToken(
            appId, new Dictionary<uint, ulong>(), new HashSet<uint> { appId })
            .Should().Be(PicsAppOutcome.AccessDenied);

        PicsResponseSemantics.ClassifyProductInfo(appId, new PicsProductInfoResultSet(true, false,
        [
            new PicsProductInfoBatch(false, new Dictionary<uint, bool> { [appId] = true }, new HashSet<uint>())
        ])).Should().Be(PicsAppOutcome.AccessDenied);
    }

    [Test]
    public void NonexistentApp_ControlledCompleteUnknownResponseIsNotFound()
    {
        const uint appId = 999999999;
        PicsResponseSemantics.ClassifyAccessToken(
            appId, new Dictionary<uint, ulong>(), new HashSet<uint>())
            .Should().Be(PicsAppOutcome.Incomplete);

        PicsResponseSemantics.ClassifyProductInfo(appId, new PicsProductInfoResultSet(true, false,
        [
            new PicsProductInfoBatch(false, new Dictionary<uint, bool>(), new HashSet<uint> { appId })
        ])).Should().Be(PicsAppOutcome.NotFound);
    }

    [Test]
    public void FailedOrPendingResultSet_IsIncompleteEvenWhenItContainsAppData()
    {
        const uint appId = 730;
        var appData = new PicsProductInfoBatch(false, new Dictionary<uint, bool> { [appId] = false }, new HashSet<uint>());

        PicsResponseSemantics.ClassifyProductInfo(appId, new PicsProductInfoResultSet(true, true, [appData]))
            .Should().Be(PicsAppOutcome.Incomplete);
        PicsResponseSemantics.ClassifyProductInfo(appId, new PicsProductInfoResultSet(true, false,
        [
            appData with { ResponsePending = true }
        ])).Should().Be(PicsAppOutcome.Incomplete);
    }

    [Test]
    public void GameAchievementsSchema_ContractMatchesLockedSteamKitSurface()
    {
        var request = new CPlayer_GetGameAchievements_Request();
        request.ShouldSerializeappid().Should().BeFalse();
        request.ShouldSerializelanguage().Should().BeFalse();
        request.appid = 730;
        request.language = "english";
        request.ShouldSerializeappid().Should().BeTrue();
        request.ShouldSerializelanguage().Should().BeTrue();
        typeof(CPlayer_GetGameAchievements_Request).GetProperty("hash_only").Should().BeNull();

        var response = new CPlayer_GetGameAchievements_Response();
        typeof(CPlayer_GetGameAchievements_Response).GetProperty("schema_version").Should().BeNull();
        typeof(CPlayer_GetGameAchievements_Response).GetProperty("schema_hash").Should().BeNull();
        typeof(CPlayer_GetGameAchievements_Response).GetProperty("groups").Should().BeNull();
        typeof(CPlayer_GetGameAchievements_Response.Achievement).GetProperty("internal_key").Should().BeNull();
        typeof(CPlayer_GetGameAchievements_Response.Achievement).GetProperty("groupid").Should().BeNull();
        typeof(CPlayer_GetGameAchievements_Response.Achievement).GetProperty("progress_type").Should().BeNull();
        response.achievements.Should().BeEmpty();
        var achievement = new CPlayer_GetGameAchievements_Response.Achievement
        {
            internal_name = "ACH_SYNTH",
            localized_name = "Synthetic",
            localized_desc = "Synthetic description",
            icon = "synthetic_icon.jpg",
            icon_gray = "synthetic_icon_gray.jpg",
            hidden = true,
            player_percent_unlocked = "12.5"
        };
        achievement.ShouldSerializeinternal_name().Should().BeTrue();
        achievement.ShouldSerializehidden().Should().BeTrue();
        response.achievements.Add(achievement);
        response.achievements.Should().ContainSingle();
    }

    [Test]
    public void UserStatsMessages_ContractMatchesLockedSteamKitSurface()
    {
        ((int)EMsg.ClientGetUserStats).Should().Be(818);
        ((int)EMsg.ClientGetUserStatsResponse).Should().Be(819);

        new CMsgClientGetUserStats().ShouldSerializecrc_stats().Should().BeFalse();
        var request = new CMsgClientGetUserStats
        {
            game_id = 730,
            crc_stats = 0,
            steam_id_for_user = 1
        };
        request.ShouldSerializegame_id().Should().BeTrue();
        request.ShouldSerializecrc_stats().Should().BeTrue();
        request.ShouldSerializesteam_id_for_user().Should().BeTrue();

        var response = new CMsgClientGetUserStatsResponse
        {
            game_id = 730,
            eresult = (int)EResult.OK
        };
        response.ShouldSerializeeresult().Should().BeTrue();
        response.stats.Should().BeEmpty();
        response.achievement_blocks.Should().BeEmpty();
        var block = new CMsgClientGetUserStatsResponse.Achievement_Blocks { achievement_id = 4 };
        block.unlock_time.Add(1700000000u);
        response.achievement_blocks.Add(block);
        response.achievement_blocks.Should().ContainSingle()
            .Which.unlock_time.Should().Equal(1700000000u);
        response.stats.Add(new CMsgClientGetUserStatsResponse.Stats { stat_id = 1, stat_value = 2 });
        response.stats.Should().ContainSingle().Which.stat_value.Should().Be(2u);
    }

    private static async Task CompileAchievementSignatures(
        Player player, AchievementUserStatsProtocolHandler userStats)
    {
        SteamUnifiedMessages.ServiceMethodResponse<CPlayer_GetGameAchievements_Response> schema =
            await player.GetGameAchievements(new CPlayer_GetGameAchievements_Request());
        AsyncJob<AchievementUserStatsCallback> unlockJob = userStats.GetUserStats(1, 1);
        AchievementUserStatsCallback unlocks = await unlockJob;
        _ = schema.Result;
        _ = schema.Body.achievements;
        _ = unlocks.Result;
        _ = unlocks.Body.achievement_blocks;
    }

    private static async Task CompileSteamKitSignatures(
        SteamClient steamClient, CallbackManager callbackManager, SteamApps steamApps)
    {
        await callbackManager.RunWaitCallbackAsync(CancellationToken.None);
        CallbackMsg callback = await steamClient.WaitForCallbackAsync(CancellationToken.None);
        AsyncJob<SteamApps.PICSTokensCallback> tokenJob = steamApps.PICSGetAccessTokens([730u], []);
        SteamApps.PICSTokensCallback tokens = await tokenJob;
        AsyncJobMultiple<SteamApps.PICSProductInfoCallback> productJob = steamApps.PICSGetProductInfo(
            [new SteamApps.PICSRequest(730, tokens.AppTokens.GetValueOrDefault(730u))], [], false);
        AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet products = await productJob;
        _ = callback;
        _ = products.Complete;
        _ = products.Failed;
        _ = products.Results;
    }
}
