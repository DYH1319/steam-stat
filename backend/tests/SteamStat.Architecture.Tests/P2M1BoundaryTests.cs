using System.Reflection;
using FluentAssertions;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class P2M1BoundaryTests
{
    [Test]
    public void GatewayAndCacheContractsRemainInCoreWithoutHostPersistenceTypes()
    {
        var core = typeof(SteamGatewayResult<>).Assembly;
        new[]
        {
            typeof(ISteamAppCatalogGateway), typeof(ISteamResourceCacheStore), typeof(SteamCacheKey),
            typeof(SteamCachePolicy), typeof(SteamGatewayResult<>), typeof(SteamRequestCoalescer<>)
        }.Should().OnlyContain(type => type.Assembly == core);

        new[] { typeof(ISteamAppCatalogGateway), typeof(ISteamResourceCacheStore) }
            .SelectMany(PublicSurfaceTypes)
            .Select(type => type.Namespace ?? string.Empty)
            .Should().NotContain(ns =>
                ns.StartsWith("ElectronNet", StringComparison.Ordinal)
                || ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || ns.StartsWith("SteamKit", StringComparison.Ordinal)
                || ns.StartsWith("SteamStat.Contracts", StringComparison.Ordinal));
    }

    [Test]
    public void EfCacheAdapterStaysInHostAndGatewayResultHasNoSecretFields()
    {
        var host = typeof(ElectronNet.AppDbContext).Assembly;
        var adapter = host.GetType("ElectronNet.Features.SteamCache.Persistence.EfSteamResourceCacheStore");
        adapter.Should().NotBeNull();
        adapter!.Assembly.FullName.Should().NotBe(typeof(ISteamResourceCacheStore).Assembly.FullName);
        typeof(SteamGatewayResult<>).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(name =>
                name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Type> PublicSurfaceTypes(Type type)
    {
        yield return type;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            yield return Unwrap(property.PropertyType);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return Unwrap(method.ReturnType);
            foreach (var parameter in method.GetParameters()) yield return Unwrap(parameter.ParameterType);
        }
    }

    private static Type Unwrap(Type type)
    {
        while (type.IsArray || type.IsGenericType)
        {
            if (type.IsArray)
            {
                type = type.GetElementType()!;
                continue;
            }
            var arguments = type.GetGenericArguments();
            type = arguments.Length == 1 ? arguments[0] : type.GetGenericTypeDefinition();
            if (arguments.Length != 1) break;
        }
        return type;
    }
}
