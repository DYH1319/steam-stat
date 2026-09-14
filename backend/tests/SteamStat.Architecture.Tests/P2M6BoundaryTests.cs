using System.Reflection;
using FluentAssertions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class P2M6BoundaryTests
{
    [Test]
    public void Features_CannotBypassGatewaySessionOrHttpBoundaries()
    {
        var files = Directory.GetFiles(
            RepoFile("backend", "src", "SteamStat.Core", "Features"),
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            source.Should().NotContain("SteamClient", $"{file} must use a narrow gateway")
                .And.NotContain("CallbackManager", $"{file} must use a narrow gateway")
                .And.NotContain("SteamUnifiedMessages", $"{file} must use a source adapter")
                .And.NotContain("IHttpClientFactory", $"{file} must use a source adapter")
                .And.NotContain("ISteamSessionAccessor", $"{file} must not access raw sessions")
                .And.NotContain("steampowered.com", $"{file} must not contain Steam HTTP endpoints")
                .And.NotContain("steamstatic.com", $"{file} must not contain Steam HTTP endpoints");
        }
    }

    [Test]
    public void LegacyTaskCachePollingAndSessionControlBypasses_AreRetired()
    {
        var coreRoot = RepoFile("backend", "src", "SteamStat.Core");
        var product = Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => (File: file, Source: File.ReadAllText(file)))
            .ToArray();

        product.Should().NotContain(item => item.Source.Contains("RunWaitCallbacks(TimeSpan.FromMilliseconds(100)", StringComparison.Ordinal));
        product.Where(item => item.File.Contains($"{Path.DirectorySeparatorChar}Features{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Should().NotContain(item => System.Text.RegularExpressions.Regex.IsMatch(
                item.Source,
                @"ConcurrentDictionary<[^,\r\n]+,\s*Task<"));
        typeof(ISteamSessionManager).GetInterfaces().Should().NotContain(typeof(ISteamSessionAccessor));
        typeof(SteamLibraryService).GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single()
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().NotContain(typeof(ISteamSessionAccessor));
    }

    [Test]
    public void WindowsPackagingScript_ReadsUtf8ProjectAsSingleDocument()
    {
        var script = File.ReadAllText(RepoFile("scripts", "build-win.ps1"));

        script.Should().Contain("Get-Content $CsprojPath -Raw -Encoding UTF8");
    }

    [Test]
    public void OperationalStatusAndSnapshotContracts_AreTypedAndSecretFree()
    {
        IpcCatalog.All.Should().Contain(SteamIpc.GetOperationalStatus);
        typeof(ISteamResourceCacheStore).GetMethod(nameof(ISteamResourceCacheStore.GetByResourceKindAsync))
            .Should().NotBeNull();
        var statusTypes = new[]
        {
            typeof(SteamOperationalStatusDto),
            typeof(SteamSessionStatusDto),
            typeof(SteamResourceStatusDto),
            typeof(SteamDependencyHealthDto)
        };
        statusTypes.SelectMany(type => type.GetProperties()).Select(property => property.Name)
            .Should().NotContain(name => name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
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
