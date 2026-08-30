using System.Reflection;
using FluentAssertions;
using SteamStat.Core.Helpers;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class CoreDependencyTests
{
    private static readonly Assembly CoreAssembly = typeof(SteamIdHelper).Assembly;

    [Test]
    public void Core_DoesNotReferenceElectron()
    {
        var references = CoreAssembly.GetReferencedAssemblies().Select(assembly => assembly.Name);

        references.Should().NotContain(name =>
            name != null && name.StartsWith("Electron", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void Core_DoesNotReferenceForbiddenLoggingApis()
    {
        var references = CoreAssembly.GetReferencedAssemblies().Select(assembly => assembly.Name).ToArray();

        references.Should().NotContain("System.Console");
        references.Should().NotContain(name =>
            name != null && name.StartsWith("Serilog", StringComparison.OrdinalIgnoreCase));
    }
}
