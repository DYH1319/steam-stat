using ElectronNet.Infrastructure;
using FluentAssertions;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class ShellIpcPolicyTests
{
    [TestCase("https://steamcommunity.com/profiles/76561198000000001", true)]
    [TestCase("http://example.com/path", true)]
    [TestCase("javascript:alert(1)", false)]
    [TestCase("data:text/html,test", false)]
    [TestCase("file:///C:/Windows/System32", false)]
    [TestCase("https://user:password@example.com", false)]
    [TestCase("not-a-url", false)]
    public void ExternalUrls_AllowOnlySafeHttpProtocols(string value, bool expected)
        => ShellIpcPolicy.IsAllowedExternalUrl(value).Should().Be(expected);

    [Test]
    public void Paths_AllowOnlyExistingApplicationProducedDirectories()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"shell-policy-{Guid.NewGuid():N}");
        var allowed = Path.Combine(root, "allowed");
        var denied = Path.Combine(root, "denied");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(denied);
        try
        {
            ShellIpcPolicy.IsAllowedPath(allowed, [allowed]).Should().BeTrue();
            ShellIpcPolicy.IsAllowedPath(denied, [allowed]).Should().BeFalse();
            ShellIpcPolicy.IsAllowedPath("allowed", [allowed]).Should().BeFalse();
            ShellIpcPolicy.IsAllowedPath(Path.Combine(allowed, "missing"), [allowed]).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
