using FluentAssertions;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class M7OperationalBoundaryTests
{
    [Test]
    public void ProductCode_DoesNotWriteToConsoleOrUseSerilogStaticLogger()
    {
        foreach (var file in ProductSourceFiles())
        {
            var source = File.ReadAllText(file);
            source.Should().NotContain("Console.WriteLine(", $"{Relative(file)} is product code")
                .And.NotContain("Console.Write(", $"{Relative(file)} is product code")
                .And.NotContain("Serilog.Log.", $"{Relative(file)} must use Microsoft ILogger");
        }
    }

    [Test]
    public void ElectronHostServices_AreInstanceOwned()
    {
        var servicesRoot = RepoFile("ElectronNet", "ElectronNet", "Services");
        foreach (var file in Directory.GetFiles(servicesRoot, "*Service.cs", SearchOption.TopDirectoryOnly))
        {
            File.ReadAllText(file).Should().NotContain("static class", $"{Relative(file)} must be owned by DI");
        }
    }

    [Test]
    public void Serilog_IsConfiguredOnlyByTheElectronCompositionRoot()
    {
        var serilogFiles = ProductSourceFiles()
            .Where(file => File.ReadAllText(file).Contains("using Serilog", StringComparison.Ordinal))
            .Select(Relative);

        serilogFiles.Should().Equal(Path.Combine("ElectronNet", "ElectronNet", "Program.cs"));
    }

    [Test]
    public void LogTemplates_DoNotContainSecretProperties()
    {
        var forbidden = new[] { "{Password}", "{Token}", "{AccessToken}", "{RefreshToken}", "{GuardData}", "{Authorization}", "{QrChallenge}" };
        foreach (var file in ProductSourceFiles())
        {
            var source = File.ReadAllText(file);
            foreach (var property in forbidden)
                source.Should().NotContainEquivalentOf(property, $"{Relative(file)} must not log secret values");
        }
    }

    [Test]
    public void LoggingAndScheduledJobs_HaveBoundedHostOwnedConfiguration()
    {
        var project = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "ElectronNet.csproj"));
        project.Should().Contain("Serilog.Extensions.Hosting").And.Contain("Serilog.Sinks.File");

        var program = File.ReadAllText(RepoFile("ElectronNet", "ElectronNet", "Program.cs"));
        program.Should().Contain("LogFilePattern")
            .And.Contain("RollingInterval.Day")
            .And.Contain("rollOnFileSizeLimit: true")
            .And.Contain("retainedFileCountLimit: 14");

        foreach (var file in new[]
                 {
                     RepoFile("ElectronNet", "ElectronNet", "Services", "UpdateService.cs"),
                     RepoFile("ElectronNet", "ElectronNet", "Jobs", "UpdateAppRunningStatusJob.cs")
                 })
        {
            var source = File.ReadAllText(file);
            source.Should().Contain("BackgroundService").And.Contain("PeriodicTimer");
        }
    }

    [Test]
    public void ElectronApi_IsReferencedOnlyByTheElectronHost()
    {
        var root = RepoRoot();
        var hostProject = Path.GetFullPath(RepoFile("ElectronNet", "ElectronNet", "ElectronNet.csproj"));
        foreach (var project in Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(file => !Relative(file).StartsWith("third_party", StringComparison.OrdinalIgnoreCase))
                     .Where(file => !Relative(file).Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                     .Where(file => Path.GetFullPath(file) != hostProject))
        {
            File.ReadAllText(project).Should().NotContain("ElectronNET.API", $"{Relative(project)} is not the Electron Host");
        }
    }

    [Test]
    public void CoreServices_DoNotOwnMutableStaticStateOrUseServiceLocation()
    {
        var coreAssembly = typeof(SteamStat.Core.Features.Login.SteamLoginService).Assembly;
        coreAssembly.GetTypes()
            .Where(type => type.IsClass && type.Name.EndsWith("Service", StringComparison.Ordinal))
            .SelectMany(type => type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            .Where(field => !field.IsLiteral)
            .Should().BeEmpty();

        foreach (var file in Directory.GetFiles(RepoFile("backend", "src", "SteamStat.Core"), "*.cs", SearchOption.AllDirectories))
            File.ReadAllText(file).Should().NotContain("IServiceProvider", $"{Relative(file)} must use constructor injection");
    }

    [Test]
    public void Features_DoNotReferenceOtherFeatureInternalsOrPersistence()
    {
        foreach (var file in Directory.GetFiles(RepoFile("backend", "src", "SteamStat.Core", "Features"), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            System.Text.RegularExpressions.Regex.IsMatch(
                source,
                @"SteamStat\.Core\.Features\.[^;\r\n]+\.(Internal|Persistence)")
                .Should().BeFalse($"{Relative(file)} must use public feature contracts");
        }
    }

    [Test]
    public void RootSolution_IsUniqueAndContainsEveryFirstPartyDotNetProject()
    {
        var root = RepoRoot();
        Directory.GetFiles(root, "*.slnx", SearchOption.AllDirectories)
            .Where(file => !Relative(file).StartsWith("third_party", StringComparison.OrdinalIgnoreCase))
            .Select(Relative)
            .Should().Equal("SteamStat.slnx");

        var solution = File.ReadAllText(RepoFile("SteamStat.slnx"));
        var projectFiles = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(file => !Relative(file).StartsWith("third_party", StringComparison.OrdinalIgnoreCase))
            .Where(file => !Relative(file).Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        foreach (var project in projectFiles)
            solution.Should().Contain(Relative(project).Replace('\\', '/'));
    }

    private static IEnumerable<string> ProductSourceFiles()
    {
        var roots = new[]
        {
            RepoFile("backend", "src"),
            RepoFile("ElectronNet", "ElectronNet")
        };
        return roots.SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file => !Relative(file).Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !Relative(file).Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !Relative(file).Contains($"{Path.DirectorySeparatorChar}Publish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string Relative(string file) => Path.GetRelativePath(RepoRoot(), file);

    private static string RepoFile(params string[] segments) => Path.Combine([RepoRoot(), .. segments]);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
