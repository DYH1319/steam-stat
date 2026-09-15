using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class P3M1BoundaryTests
{
    private static readonly string[] BannedFeatureTokens =
    [
        "SteamClient",
        "SteamUnifiedMessages",
        "SteamKit2",
        "ClientMsgProtobuf<",
        "EMsg",
        "ProtoContract",
        "IHttpClientFactory",
        "steampowered.com",
        "steamstatic.com",
        "ISteamSessionAccessor",
        "ISteamResourceCacheStore",
        "SteamFeatureSnapshotStore",
        "SteamOwnedGame",
        "EntityFrameworkCore",
        "SteamStat.Contracts",
        "Electron"
    ];

    private static readonly string[] BannedSurfaceAssemblies =
    [
        "SteamKit2",
        "protobuf-net",
        "protobuf-net.Core",
        "Microsoft.EntityFrameworkCore",
        "SteamStat.Contracts"
    ];

    [Test]
    public void AchievementsFeature_DoesNotContainSteamTransportOrPersistenceReferences()
    {
        var files = Directory.GetFiles(
            RepoFile("backend", "src", "SteamStat.Core", "Features", "Achievements"),
            "*.cs",
            SearchOption.AllDirectories);
        files.Should().NotBeEmpty();

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var token in BannedFeatureTokens)
                source.Should().NotContain(token, $"{file} must not reference {token}");
            Regex.IsMatch(source, @"ConcurrentDictionary<[^,\r\n]+,\s*Task<")
                .Should().BeFalse($"{file} must not implement a local coalescer");
        }
    }

    [Test]
    public void LibraryFeature_DoesNotDependOnAchievements()
    {
        var files = Directory.GetFiles(
            RepoFile("backend", "src", "SteamStat.Core", "Features", "Library"),
            "*.cs",
            SearchOption.AllDirectories);
        files.Should().NotBeEmpty();

        foreach (var file in files)
            File.ReadAllText(file).Should()
                .NotContain("Features.Achievements", $"{file} must not depend on Achievements");
    }

    [Test]
    public void AchievementsPublicSurface_DoesNotExposeTransportOrPersistenceTypes()
    {
        var types = typeof(SteamAchievementGameResult).Assembly.GetTypes()
            .Where(type => type.IsPublic
                && type.Namespace != null
                && type.Namespace.StartsWith("SteamStat.Core.Features.Achievements", StringComparison.Ordinal))
            .ToArray();
        types.Should().NotBeEmpty();

        foreach (var type in types)
        foreach (var reachable in ReachableSurfaceTypes(type))
        {
            var assemblyName = reachable.Assembly.GetName().Name ?? string.Empty;
            foreach (var banned in BannedSurfaceAssemblies)
                assemblyName.Should().NotBe(banned,
                    $"{type.Name} must not expose {reachable.Name} from {assemblyName}");
        }
    }

    [Test]
    public void PublicSchemaPayload_IsAccountAndSessionFree()
    {
        var banned = new[] { "SteamId", "AccountName", "Token", "Session", "Generation" };
        var types = new[]
        {
            typeof(SteamAchievementSchemaSnapshot),
            typeof(SteamAchievementDefinition),
            typeof(SteamAchievementGroup)
        };
        foreach (var type in types)
            type.GetProperties().Select(property => property.Name).Should().NotContain(
                name => banned.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)),
                $"{type.Name} must not carry account or session data");
    }

    [Test]
    public void AchievementCacheKeys_SeparatePublicAndPersonalScopes()
    {
        typeof(SteamAchievementCacheKeys)
            .GetMethod(nameof(SteamAchievementCacheKeys.ProgressSummary))!
            .GetParameters().Should().Contain(parameter =>
                parameter.Name == "steamId" && parameter.ParameterType == typeof(ulong));
        typeof(SteamAchievementCacheKeys)
            .GetMethod(nameof(SteamAchievementCacheKeys.Unlocks))!
            .GetParameters().Should().Contain(parameter =>
                parameter.Name == "steamId" && parameter.ParameterType == typeof(ulong));

        SteamAchievementCacheKeys.ProgressSummary(100000001UL).ScopeId.Should().NotBe("public");
        SteamAchievementCacheKeys.Unlocks(100000001UL, 1).ScopeId.Should().NotBe("public");
        SteamAchievementCacheKeys.Schema(1, "english").ScopeId.Should().Be("public");
    }

    [Test]
    public void ProtoContractTypes_StayInsideGatewayInternal()
    {
        var offenders = typeof(SteamAchievementGameResult).Assembly.GetTypes()
            .Where(type => type.IsDefined(typeof(ProtoBuf.ProtoContractAttribute), false)
                && type.Namespace != "SteamStat.Core.Steam.Gateway.Internal")
            .Select(type => type.FullName)
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Test]
    public void OwnedGameCatalog_IsReadOnlyAndRegistered()
    {
        typeof(SteamOwnedGameCatalog)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single()
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().NotContain([
                typeof(ISteamLibraryGateway),
                typeof(ISteamCmOperationScheduler),
                typeof(ISteamSessionAccessor),
                typeof(IHttpClientFactory)
            ]);

        var services = new ServiceCollection().AddSteamStatCore();
        var descriptor = services.Single(
            service => service.ServiceType == typeof(IOwnedGameCatalog));
        descriptor.ImplementationType.Should().Be(typeof(SteamOwnedGameCatalog));
    }

    [Test]
    public void AchievementSchemaGateway_IsRegisteredAsSingleton()
    {
        var services = new ServiceCollection().AddSteamStatCore();
        var descriptor = services.Single(
            service => service.ServiceType == typeof(ISteamAchievementSchemaGateway));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType?.Name.Should().Be("SteamAchievementSchemaGateway");
    }

    [Test]
    public void AchievementCapabilityPorts_ReturnGatewayResultsAndAcceptRefreshMode()
    {
        var ports = new[]
        {
            typeof(ISteamAchievementSchemaGateway),
            typeof(ISteamAchievementProgressGateway)
        };
        foreach (var port in ports)
        {
            var methods = port.GetMethods();
            methods.Should().NotBeEmpty();
            foreach (var method in methods)
            {
                method.ReturnType.IsGenericType.Should().BeTrue();
                method.ReturnType.GetGenericTypeDefinition().Should().Be(typeof(Task<>));
                var payload = method.ReturnType.GetGenericArguments()[0];
                payload.IsGenericType.Should().BeTrue();
                payload.GetGenericTypeDefinition().Should().Be(typeof(SteamGatewayResult<>));
                method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Should().Contain(typeof(SteamRefreshMode));
            }
        }
    }

    private static IEnumerable<Type> ReachableSurfaceTypes(Type root)
    {
        var visited = new HashSet<Type>();
        var pending = new Stack<Type>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (type.IsGenericParameter || !visited.Add(type)) continue;
            yield return type;
            if (type.HasElementType) pending.Push(type.GetElementType()!);
            foreach (var argument in type.GetGenericArguments()) pending.Push(argument);
            if (type.Assembly != typeof(SteamAchievementGameResult).Assembly) continue;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                pending.Push(property.PropertyType);
            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                pending.Push(method.ReturnType);
                foreach (var parameter in method.GetParameters()) pending.Push(parameter.ParameterType);
            }
        }
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
