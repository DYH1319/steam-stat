using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class AchievementCacheContractTests
{
    [Test]
    public void SchemaKey_UsesPublicScopeLanguageAndAppId()
    {
        var key = SteamAchievementCacheKeys.Schema(1, " SCHINESE ");

        key.ResourceKind.Should().Be("achievement-schema");
        key.ScopeId.Should().Be("public");
        key.ResourceId.Should().Be("1");
        key.Language.Should().Be("schinese");
        key.SchemaVersion.Should().Be(SteamAchievementCacheKeys.PayloadSchemaVersion);
        SteamAchievementCacheKeys.Schema(1, "english").Should().NotBe(key);
        SteamAchievementCacheKeys.Schema(1, "english").Language.Should().Be("english");
        ((Action)(() => SteamAchievementCacheKeys.Schema(0, "english")))
            .Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => SteamAchievementCacheKeys.Schema(1, "  ")))
            .Should().Throw<ArgumentException>();
    }

    [Test]
    public void PersonalKeys_UseSteamIdScope()
    {
        var summary = SteamAchievementCacheKeys.ProgressSummary(100000001UL);
        summary.ResourceKind.Should().Be("achievement-progress-summary");
        summary.ScopeId.Should().Be("100000001");
        summary.ScopeId.Should().NotBe(SteamAchievementCacheKeys.PublicScope);
        summary.ResourceId.Should().Be("summary");

        var unlocks = SteamAchievementCacheKeys.Unlocks(100000001UL, 1);
        unlocks.ResourceKind.Should().Be("achievement-unlocks");
        unlocks.ScopeId.Should().Be("100000001");
        unlocks.ResourceId.Should().Be("1");

        SteamAchievementCacheKeys.ProgressSummary(100000002UL).Should().NotBe(summary);
        SteamAchievementCacheKeys.Unlocks(100000002UL, 1).Should().NotBe(unlocks);
        ((Action)(() => SteamAchievementCacheKeys.ProgressSummary(0)))
            .Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => SteamAchievementCacheKeys.Unlocks(0, 1)))
            .Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => SteamAchievementCacheKeys.Unlocks(100000001UL, 0)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void AchievementPolicies_MatchDecidedIntervals()
    {
        var schema = SteamResourcePolicies.AchievementSchema;
        schema.RefreshInterval.Should().Be(TimeSpan.FromHours(24));
        schema.StaleInterval.Should().Be(TimeSpan.FromDays(30));
        schema.RetentionInterval.Should().Be(TimeSpan.FromDays(180));
        schema.MaximumPayloadBytes.Should().Be(1024 * 1024);
        schema.AllowExpiredOnFailure.Should().BeTrue();
        schema.AllowNegativeCache.Should().BeTrue();
        schema.NegativeCacheInterval.Should().Be(TimeSpan.FromHours(6));

        var summary = SteamResourcePolicies.AchievementProgressSummary;
        summary.RefreshInterval.Should().Be(TimeSpan.FromMinutes(15));
        summary.StaleInterval.Should().Be(TimeSpan.FromDays(30));
        summary.RetentionInterval.Should().Be(TimeSpan.FromDays(180));
        summary.AllowNegativeCache.Should().BeFalse();

        var unlocks = SteamResourcePolicies.AchievementUnlocks;
        unlocks.RefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        unlocks.StaleInterval.Should().Be(TimeSpan.FromDays(30));
        unlocks.RetentionInterval.Should().Be(TimeSpan.FromDays(365));
        unlocks.AllowNegativeCache.Should().BeFalse();

        foreach (var policy in new[] { schema, summary, unlocks })
        {
            policy.MaximumPayloadBytes.Should().Be(1024 * 1024);
            policy.AllowExpiredOnFailure.Should().BeTrue();
        }
    }

    [Test]
    public void NegativeCaching_OnlyAppliesToPublicSchemaNotFound()
    {
        SteamResourcePolicies.AchievementSchema.CanNegativeCache(SteamFailureKind.NotFound)
            .Should().BeTrue();
        foreach (var failure in new[]
                 {
                     SteamFailureKind.AuthenticationRequired,
                     SteamFailureKind.Timeout,
                     SteamFailureKind.RateLimited,
                     SteamFailureKind.Offline,
                     SteamFailureKind.Transient
                 })
            SteamResourcePolicies.AchievementSchema.CanNegativeCache(failure).Should().BeFalse();

        SteamResourcePolicies.AchievementProgressSummary.CanNegativeCache(SteamFailureKind.NotFound)
            .Should().BeFalse();
        SteamResourcePolicies.AchievementUnlocks.CanNegativeCache(SteamFailureKind.NotFound)
            .Should().BeFalse();
    }

    [Test]
    public void DiagnosticCodes_AreUniqueWellFormedAndComplete()
    {
        var codes = typeof(SteamAchievementDiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        codes.Should().OnlyHaveUniqueItems();
        codes.Should().OnlyContain(code => Regex.IsMatch(code, "^achievement_[a-z0-9_]+$"));
        codes.Should().Contain(["achievement_progress_partial", "achievement_user_stats_failed"]);
    }
}
