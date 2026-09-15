using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Features.Library.Contracts;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class P3M4BoundaryTests
{
    [Test]
    public void AchievementsService_IsRegisteredAsSingletonAlongsideOverviewQuery()
    {
        var services = new ServiceCollection().AddSteamStatCore();

        var service = services.Single(
            descriptor => descriptor.ServiceType == typeof(SteamAchievementsService));
        service.Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Single(descriptor => descriptor.ServiceType == typeof(SteamAchievementOverviewQuery))
            .Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Test]
    public void AchievementsService_PublicConstructorDependsOnTheExactFeaturePorts()
    {
        typeof(SteamAchievementsService)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single()
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().Equal(
                typeof(SteamAchievementOverviewQuery),
                typeof(IOwnedGameCatalog),
                typeof(ISteamAchievementSchemaGateway),
                typeof(ISteamAchievementProgressGateway),
                typeof(ILanguageProvider));
    }

    [Test]
    public void AchievementsServiceSource_AvoidsTransportPersistenceAndHostReferences()
    {
        var source = File.ReadAllText(RepoFile(
            "backend", "src", "SteamStat.Core", "Features", "Achievements",
            "SteamAchievementsService.cs"));

        foreach (var token in new[]
                 {
                     "SteamKit",
                     "EntityFrameworkCore",
                     "Electron",
                     "SteamStat.Contracts",
                     "ISteamSessionAccessor",
                     "ISteamResourceCacheStore",
                     "SteamFeatureSnapshotStore",
                     "IHttpClientFactory",
                     "ConcurrentDictionary<"
                 })
            source.Should().NotContain(token);
    }

    [Test]
    public void AchievementsServiceSurface_ExposesNoTransportPersistenceOrHostTypes()
    {
        var bannedAssemblies = new[]
        {
            "SteamKit2",
            "protobuf-net",
            "Microsoft.EntityFrameworkCore",
            "SteamStat.Contracts"
        };
        var service = typeof(SteamAchievementsService);
        var signatureTypes = service
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single()
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Concat(service
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)))
            .SelectMany(Flatten)
            .ToArray();

        foreach (var type in signatureTypes)
        {
            var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
            assemblyName.Should().NotStartWith("Electron");
            foreach (var banned in bannedAssemblies)
                assemblyName.Should().NotBe(banned, $"{type.Name} must not come from {banned}");
        }
    }

    [Test]
    public void IpcMain_AchievementRegistrations_StayThinAndDescriptorDriven()
    {
        var source = File.ReadAllText(
            RepoFile("ElectronNet", "ElectronNet", "Services", "IpcMainService.cs"));

        source.Should().Contain("HandleAsync(ipcMain, AchievementIpc.GetOverview")
            .And.Contain("HandleAsync(ipcMain, AchievementIpc.GetGame")
            .And.Contain("HandleAsync(ipcMain, AchievementIpc.RefreshGame")
            .And.Contain("achievementsService.GetOverviewAsync(request.AccountName)")
            .And.Contain("achievementsService.GetGameAsync(request.AccountName, request.AppId)")
            .And.Contain("achievementsService.RefreshGameAsync(request.AccountName, request.AppId)")
            .And.Contain("IpcDtoMapper.ToDto(await achievementsService")
            .And.Contain("requestBinder.Bind<TRequest>(value, endpoint)");
        source.Should().NotContain("ISteamAchievementSchemaGateway")
            .And.NotContain("ISteamAchievementProgressGateway")
            .And.NotContain("IOwnedGameCatalog")
            .And.NotContain("ILanguageProvider")
            .And.NotContain("ISteamResourceCacheStore")
            .And.NotContain("SteamRefreshMode")
            .And.NotContain("GetSchemaAsync")
            .And.NotContain("GetUnlocksAsync")
            .And.NotContain("GetSummariesAsync");
    }

    [Test]
    public void AchievementDtos_AreContractsRecordsBehindInvokeDescriptors()
    {
        var dtoTypes = new[]
        {
            typeof(SteamAchievementOverviewRequest),
            typeof(SteamAchievementGameRequest),
            typeof(SteamAchievementOverviewResultDto),
            typeof(SteamAchievementOverviewItemDto),
            typeof(SteamAchievementProgressDto),
            typeof(SteamAchievementGameResultDto),
            typeof(SteamAchievementDto),
            typeof(SteamAchievementGroupDto),
            typeof(SteamAchievementSummaryDto),
            typeof(SteamAchievementResourceStateDto)
        };
        dtoTypes.Should().OnlyContain(type => type.Assembly == typeof(IpcCatalog).Assembly);

        var endpoints = new IIpcEndpointDescriptor[]
        {
            AchievementIpc.GetOverview,
            AchievementIpc.GetGame,
            AchievementIpc.RefreshGame
        };
        endpoints.Should().OnlyContain(endpoint => endpoint.Direction == IpcDirection.Invoke)
            .And.OnlyContain(endpoint =>
                endpoint.RequestType!.Assembly == typeof(IpcCatalog).Assembly
                && endpoint.ResponseType!.Assembly == typeof(IpcCatalog).Assembly);
    }

    [Test]
    public void GeneratedApi_ExposesThreeTypedAchievementMethodsAndNoEvent()
    {
        var api = File.ReadAllText(RepoFile("src", "types", "ipc.d.ts"));

        api.Should().Contain(
                "steamAchievementsOverviewGet: (param: SteamAchievementOverviewRequest) => Promise<SteamAchievementOverviewResult>")
            .And.Contain(
                "steamAchievementsGameGet: (param: SteamAchievementGameRequest) => Promise<SteamAchievementGameResult>")
            .And.Contain(
                "steamAchievementsGameRefresh: (param: SteamAchievementGameRequest) => Promise<SteamAchievementGameResult>");
        api.Split('\n')
            .Where(line => line.Contains("steamAchievements", StringComparison.Ordinal))
            .Should().HaveCount(3)
            .And.OnlyContain(line => line.Contains("=> Promise<", StringComparison.Ordinal));
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        yield return type;
        foreach (var argument in type.GetGenericArguments())
            foreach (var nested in Flatten(argument))
                yield return nested;
    }

    private static string RepoFile(params string[] segments) => Path.Combine([RepoRoot(), .. segments]);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
