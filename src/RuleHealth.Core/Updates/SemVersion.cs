using System.Text.RegularExpressions;

namespace RuleHealth.Core.Updates;

/// <summary>
/// A SemVer 2.0 version (https://semver.org) for the update check: precedence per §11, so
/// 0.1.0-beta.2 &lt; 0.1.0-beta.10 &lt; 0.1.0. Build metadata (+…) is ignored, as the spec says.
/// </summary>
public sealed partial record SemVersion(int Major, int Minor, int Patch, IReadOnlyList<string> Prerelease) : IComparable<SemVersion>
{
    public bool IsPrerelease => Prerelease.Count > 0;

    /// <summary>Parses "1.2.3", "v1.2.3-beta.4" or "1.2.3+abc"; false for anything else (tags like "nightly").</summary>
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = null!;
        var match = Pattern().Match(text?.Trim() ?? "");
        if (!match.Success)
            return false;
        if (!int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch))
            return false;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
        version = new SemVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
            return 1;
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
            return core;
        // A release ranks above its prereleases (1.0.0-beta < 1.0.0).
        if (IsPrerelease != other.IsPrerelease)
            return IsPrerelease ? -1 : 1;
        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            var result = CompareIdentifier(Prerelease[i], other.Prerelease[i]);
            if (result != 0)
                return result;
        }
        // All shared identifiers equal: more identifiers rank higher (beta < beta.1).
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    // Numeric identifiers compare as numbers and rank below alphanumeric ones; alphanumeric ones
    // compare in ASCII order (so "beta.2" < "beta.10", but "beta" < "rc").
    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        if (aNumeric && bNumeric)
        {
            // Compare by length first: no overflow for long digit strings, and no leading zeros (spec).
            var length = a.TrimStart('0').Length.CompareTo(b.TrimStart('0').Length);
            return length != 0 ? length : string.CompareOrdinal(a.TrimStart('0'), b.TrimStart('0'));
        }
        if (aNumeric != bNumeric)
            return aNumeric ? -1 : 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? "-" + string.Join('.', Prerelease) : "");

    // Records compare lists by reference; versions are equal when their precedence is.
    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Prerelease));

    [GeneratedRegex(@"^[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();
}
