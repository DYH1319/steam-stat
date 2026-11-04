using FluentAssertions;
using NUnit.Framework;
using SteamStat.Core.Settings;
using SteamStat.Core.Updates;

namespace SteamStat.Core.Tests.Updates;

public sealed class ReleaseFeedTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom" xml:lang="en-US">
          <id>tag:github.com,2008:https://github.com/DYH1319/steam-stat/releases</id>
          <title>Release notes from steam-stat</title>
          <entry>
            <id>tag:github.com,2008:Repository/1/v4.2.0-alpha.1</id>
            <updated>2026-11-05T10:00:00Z</updated>
            <link rel="alternate" type="text/html" href="https://github.com/DYH1319/steam-stat/releases/tag/v4.2.0-alpha.1"/>
            <title>v4.2.0-alpha.1</title>
            <content type="html">&lt;p&gt;alpha&lt;/p&gt;</content>
          </entry>
          <entry>
            <id>tag:github.com,2008:Repository/1/v4.1.0-beta.2</id>
            <updated>2026-11-04T10:00:00Z</updated>
            <link rel="alternate" type="text/html" href="https://github.com/DYH1319/steam-stat/releases/tag/v4.1.0-beta.2"/>
            <title>v4.1.0 Beta 2</title>
            <content type="html">&lt;p&gt;beta notes&lt;/p&gt;</content>
          </entry>
          <entry>
            <id>tag:github.com,2008:Repository/1/v4.0.1</id>
            <updated>2026-11-03T10:00:00Z</updated>
            <link rel="alternate" type="text/html" href="https://github.com/DYH1319/steam-stat/releases/tag/v4.0.1"/>
            <title>v4.0.1</title>
          </entry>
          <entry>
            <id>tag:github.com,2008:Repository/1/nightly</id>
            <link rel="alternate" type="text/html" href="https://github.com/DYH1319/steam-stat/releases/tag/nightly"/>
            <title>nightly</title>
          </entry>
        </feed>
        """;

    [Test]
    public void Parse_ReadsTaggedReleasesAndSkipsNonSemverTags()
    {
        var entries = ReleaseFeed.Parse(Feed);

        entries.Select(entry => entry.Tag).Should().Equal("v4.2.0-alpha.1", "v4.1.0-beta.2", "v4.0.1");
        entries[1].Title.Should().Be("v4.1.0 Beta 2");
        entries[1].NotesHtml.Should().Be("<p>beta notes</p>");
        entries[1].PageUrl.Should().Be("https://github.com/DYH1319/steam-stat/releases/tag/v4.1.0-beta.2");
        entries[1].UpdatedAt.Should().Be(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public void Parse_InvalidXml_ReturnsEmpty()
        => ReleaseFeed.Parse("<feed").Should().BeEmpty();

    [TestCase(UpdateChannels.Stable, "4.0.0", "v4.0.1")]
    [TestCase(UpdateChannels.Beta, "4.0.0", "v4.1.0-beta.2")]
    [TestCase(UpdateChannels.Stable, "4.0.1", null)]
    [TestCase(UpdateChannels.Beta, "4.1.0-beta.2", null)]
    [TestCase(UpdateChannels.Stable, "4.1.0-beta.1", null)]
    public void SelectUpdate_HonoursChannel(string channel, string current, string? expectedTag)
        => ReleaseFeed.SelectUpdate(ReleaseFeed.Parse(Feed), channel, SemanticVersion.Parse(current))?.Tag
            .Should().Be(expectedTag);
}

public sealed class SemanticVersionTests
{
    [TestCase("1.0.0-alpha", "1.0.0-alpha.1")]
    [TestCase("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [TestCase("1.0.0-alpha.beta", "1.0.0-beta")]
    [TestCase("1.0.0-beta.2", "1.0.0-beta.11")]
    [TestCase("1.0.0-rc.1", "1.0.0")]
    [TestCase("1.9.9", "1.10.0")]
    [TestCase("v4.0.0", "4.0.1+build.5")]
    public void CompareTo_FollowsSemverPrecedence(string lower, string higher)
    {
        (SemanticVersion.Parse(lower) < SemanticVersion.Parse(higher)).Should().BeTrue();
        (SemanticVersion.Parse(higher) > SemanticVersion.Parse(lower)).Should().BeTrue();
    }

    [TestCase("1.0")]
    [TestCase("1.0.0-")]
    [TestCase("1.0.0-be ta")]
    [TestCase("latest")]
    public void TryParse_RejectsInvalidVersions(string value)
        => SemanticVersion.TryParse(value, out _).Should().BeFalse();

    [Test]
    public void PrereleaseChannel_IsFirstIdentifier()
    {
        SemanticVersion.Parse("4.1.0-beta.3").PrereleaseChannel.Should().Be("beta");
        SemanticVersion.Parse("4.1.0").PrereleaseChannel.Should().BeNull();
    }
}
