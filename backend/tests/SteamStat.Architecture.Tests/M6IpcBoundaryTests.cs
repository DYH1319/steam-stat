using FluentAssertions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Settings;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class M6IpcBoundaryTests
{
    [Test]
    public void Contracts_DoNotReferenceCoreEntityFrameworkSteamKitOrElectron()
    {
        typeof(IpcCatalog).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).Should()
            .NotContain(name => name != null && (name.StartsWith("SteamStat.Core", StringComparison.Ordinal)
                                                  || name.StartsWith("Electron", StringComparison.OrdinalIgnoreCase)
                                                  || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                                                  || name.StartsWith("SteamKit", StringComparison.Ordinal)));
    }

    [Test]
    public void EveryEndpointTypeComesFromContractsOrTheBcl()
    {
        IpcCatalog.All.SelectMany(endpoint => new[] { endpoint.RequestType, endpoint.ResponseType })
            .Where(type => type != null)
            .SelectMany(Flatten)
            .Where(type => !IsBclWireType(type))
            .Should().OnlyContain(type => type.Assembly == typeof(IpcCatalog).Assembly);
    }

    [Test]
    public void CoreIpcFacingServicesExposeTypedPublicMethods()
    {
        typeof(SettingsCoordinator).GetMethod(nameof(SettingsCoordinator.UpdateSettingsAsync))!
            .GetParameters()[0].ParameterType.Should().Be<AppSettings>();
        typeof(SteamLoginService).GetMethod(nameof(SteamLoginService.LoginWithCredentials))!
            .ReturnType.Should().Be<Task<SteamLoginResult>>();
        typeof(SteamLoginService).GetMethod(nameof(SteamLoginService.LoginWithQR))!
            .ReturnType.Should().Be<Task<SteamLoginResult>>();
        typeof(SteamLoginService).GetMethod(nameof(SteamLoginService.LoginWithToken))!
            .ReturnType.Should().Be<Task<SteamLoginResult>>();
        typeof(SteamLoginService).GetMethod(nameof(SteamLoginService.GetSavedTokens))!
            .ReturnType.Should().Be<IReadOnlyList<SteamLoginTokenSummary>>();
    }

    [Test]
    public void RegistrarContainsNoLiteralIpcRegistrationChannels()
    {
        var source = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Services", "IpcMainService.cs"));
        source.Should().NotContain("ipcMain.Handle(\"").And.NotContain("ipcMain.On(\"");
    }

    [Test]
    public void GeneratorDependsOnlyOnContracts()
    {
        var project = File.ReadAllText(RepoFile("tools", "GenerateIpcContracts", "GenerateIpcContracts.csproj"));
        project.Should().Contain("SteamStat.Contracts.csproj")
            .And.NotContain("SteamStat.Core.csproj")
            .And.NotContain("ElectronNet.csproj");
    }

    private static IEnumerable<Type> Flatten(Type? type)
    {
        if (type == null) yield break;
        type = Nullable.GetUnderlyingType(type) ?? type;
        yield return type;
        if (type.IsArray)
        {
            foreach (var nested in Flatten(type.GetElementType())) yield return nested;
            yield break;
        }
        if (!type.IsGenericType) yield break;
        foreach (var argument in type.GetGenericArguments())
            foreach (var nested in Flatten(argument)) yield return nested;
    }

    private static bool IsBclWireType(Type type)
        => type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
           || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true;

    private static string RepoFile(params string[] segments) => Path.Combine([RepoRoot(), .. segments]);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
