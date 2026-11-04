using FluentAssertions;
using NUnit.Framework;
using SteamStat.Core.Diagnostics;

namespace SteamStat.Core.Tests.Diagnostics;

public sealed class DiagnosticsRedactorTests
{
    private static readonly DiagnosticsRedactor Redactor = new(new DiagnosticsRedactionContext(
        "/home/alice", "alice", ["alice_steam", "bo"], ["Alice ✨", "Bobcat"]));

    [TestCase("Opened /home/alice/.local/share/Steam/config", "Opened ~/.local/share/Steam/config")]
    [TestCase(@"C:\Users\Carol\AppData\Roaming\steam-stat", @"C:\Users\[user]\AppData\Roaming\steam-stat")]
    [TestCase("/Users/dave/Library", "/Users/[user]/Library")]
    [TestCase("Login succeeded for alice_steam (76561198000000001)", "Login succeeded for [account] ([steamid])")]
    [TestCase("Persona Alice ✨ is online; bobcats are not Bobcat", "Persona [persona] is online; bobcats are not [persona]")]
    [TestCase("GET https://api.steampowered.com/x?key=ABCDEF&access_token=xyz123", "GET https://api.steampowered.com/x?key=[redacted]&access_token=[redacted]")]
    [TestCase("Authorization: Bearer abc.def-ghi", "Authorization: Bearer [redacted]")]
    [TestCase("token eyJhbGciOiJFZERTQSJ9.eyJzdWIiOiI3NjU2MTE5OCJ9.c2lnbmF0dXJl end", "token [jwt] end")]
    [TestCase("proxy socks5://user:pa55@10.0.0.2:1080", "proxy socks5://[redacted]@10.0.0.2:1080")]
    [TestCase("mail me@example.com", "mail [email]")]
    [TestCase(@"D:\Steam\userdata\39734273\760\remote", @"D:\Steam\userdata\[accountid]\760\remote")]
    public void Redact_ReplacesIdentityAndSecrets(string input, string expected)
        => Redactor.Redact(input).Should().Be(expected);

    [Test]
    public void Redact_IgnoresNamesShorterThanThreeCharacters()
        => Redactor.Redact("bo is not a secret").Should().Be("bo is not a secret");

    [Test]
    public void Redact_KeepsOrdinaryDiagnosticText()
    {
        const string line = "2026-11-03 12:00:00.000 +08:00 [ERR] [SteamStat.Core.Http] Request to store.steampowered.com failed (HTTP 503) after 1200ms";
        Redactor.Redact(line).Should().Be(line);
    }
}

public sealed class CrashReportFingerprintTests
{
    [Test]
    public void Compute_IgnoresFilePathsLineNumbersAndAddresses()
    {
        var first = CrashReportFingerprint.Compute("System.InvalidOperationException", """
            at ElectronNet.Services.Foo.Bar() in C:\build\a\Foo.cs:line 42
            at ElectronNet.Services.Foo.Baz() in C:\build\a\Foo.cs:line 10
            """);
        var second = CrashReportFingerprint.Compute("System.InvalidOperationException", """
            at ElectronNet.Services.Foo.Bar() in /src/Foo.cs:line 44
            at ElectronNet.Services.Foo.Baz() in /src/Foo.cs:line 11
            """);
        var renderer = CrashReportFingerprint.Compute("TypeError", "at render (http://localhost:9000/src/a.ts?t=1:10:5)");
        var rendererProduction = CrashReportFingerprint.Compute("TypeError", "at render (file:///opt/steam-stat/dist/assets/a-1b2c.js:1:2000)");

        first.Should().Be(second).And.HaveLength(16);
        renderer.Should().Be(rendererProduction);
        CrashReportFingerprint.Compute("System.ArgumentException", "at ElectronNet.Services.Foo.Bar()").Should().NotBe(first);
    }
}

public sealed class GitHubIssueLinkTests
{
    [Test]
    public void Build_EncodesTitleBodyAndLabels()
    {
        var url = GitHubIssueLink.Build("[Bug] 启动失败", "第一行\n第二行 & more", ["bug"]);

        url.Should().StartWith("https://github.com/DYH1319/steam-stat/issues/new?title=");
        new Uri(url).Query.Should().Contain("labels=bug").And.Contain("body=");
        Uri.UnescapeDataString(url).Should().Contain("第一行\n第二行 & more").And.Contain("[Bug] 启动失败");
    }

    [Test]
    public void Build_TruncatesLongBodiesWithinTheUrlLimit()
    {
        var url = GitHubIssueLink.Build("crash", string.Concat(Enumerable.Repeat("堆栈帧 at Foo.Bar() 🙂\n", 2000)));

        url.Length.Should().BeLessThanOrEqualTo(GitHubIssueLink.MaxUrlLength);
        Uri.UnescapeDataString(url).Should().EndWith("…(truncated)");
    }
}
