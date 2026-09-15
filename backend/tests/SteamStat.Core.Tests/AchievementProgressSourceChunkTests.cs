using FluentAssertions;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class AchievementProgressSourceChunkTests
{
    [Test]
    public void ProgressChunks_EmptyAndSingleAndBoundaries()
    {
        CmAchievementProgressSource.CreateProgressChunks([]).Should().BeEmpty();
        CmAchievementProgressSource.CreateProgressChunks([42u])
            .Should().ContainSingle().Which.Should().Equal(42u);

        var hundred = CmAchievementProgressSource.CreateProgressChunks(
            Enumerable.Range(1, 100).Select(index => (uint)index).ToArray());
        hundred.Should().ContainSingle().Which.Should().HaveCount(100);

        var hundredOne = CmAchievementProgressSource.CreateProgressChunks(
            Enumerable.Range(1, 101).Select(index => (uint)index).ToArray());
        hundredOne.Should().HaveCount(2);
        hundredOne[0].Should().HaveCount(100);
        hundredOne[1].Should().Equal(101u);
    }

    [Test]
    public void ProgressChunks_DeduplicatesPreservingFirstSeenOrder()
    {
        CmAchievementProgressSource.CreateProgressChunks([5u, 1u, 5u, 9u, 1u])
            .Should().ContainSingle().Which.Should().Equal(5u, 1u, 9u);
    }

    [Test]
    public void ProgressChunks_ZeroAppId_IsRejected()
    {
        ((Action)(() => CmAchievementProgressSource.CreateProgressChunks([1u, 0u])))
            .Should().Throw<InvalidDataException>();
    }
}
