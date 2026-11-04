using System.Globalization;

namespace SteamStat.Core.Updates;

/// <summary>
/// 语义化版本（SemVer 2.0）：MAJOR.MINOR.PATCH[-prerelease][+build]，允许前缀 v。
/// 比较规则与 electron-updater 使用的 semver 一致：正式版高于同号预发布版，预发布标识逐段比较。
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, IReadOnlyList<string> prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    /// <summary>预发布通道名（beta.2 → beta）；正式版为 null。</summary>
    public string? PrereleaseChannel => IsPrerelease ? Prerelease[0] : null;

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        var dash = text.IndexOf('-');
        var core = dash >= 0 ? text[..dash] : text;
        var parts = core.Split('.');
        if (parts.Length != 3) return false;
        if (!TryParseNumber(parts[0], out var major) || !TryParseNumber(parts[1], out var minor) || !TryParseNumber(parts[2], out var patch))
            return false;

        IReadOnlyList<string> prerelease = [];
        if (dash >= 0)
        {
            var identifiers = text[(dash + 1)..].Split('.');
            if (identifiers.Any(identifier => identifier.Length == 0 || !identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                return false;
            prerelease = identifiers;
        }

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public static SemanticVersion Parse(string value)
        => TryParse(value, out var version) ? version : throw new FormatException($"Invalid semantic version: {value}");

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;
        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            result = CompareIdentifier(Prerelease[i], other.Prerelease[i]);
            if (result != 0) return result;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Prerelease));
    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{string.Join('.', Prerelease)}" : $"{Major}.{Minor}.{Patch}";

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = TryParseNumber(left, out var leftNumber);
        var rightNumeric = TryParseNumber(right, out var rightNumber);
        if (leftNumeric && rightNumeric) return leftNumber.CompareTo(rightNumber);
        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    private static bool TryParseNumber(string value, out int number)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}
