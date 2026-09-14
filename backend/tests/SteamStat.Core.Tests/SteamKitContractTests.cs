using FluentAssertions;
using SteamKit2;
using SteamStat.Core.Features;

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
