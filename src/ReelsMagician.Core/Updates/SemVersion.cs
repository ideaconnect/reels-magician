using System.Globalization;

namespace ReelsMagician.Core.Updates;

/// <summary>
/// A Semantic Versioning 2.0.0 version, such as <c>0.3.0-beta.1</c>, compared by its precedence
/// (semver.org, rule 11). Release tags carry such versions, and <see cref="System.Version"/> can't
/// read one with a pre-release part, so updates compare these instead (issue #57).
/// </summary>
/// <remarks>
/// A leading <c>v</c>, as tags have it (<c>v0.2.0</c>), is allowed, and build metadata after a
/// <c>+</c> is read past and ignored, as precedence ignores it. Anything else that isn't a valid
/// version, such as <c>0.3</c>, <c>01.2.3</c> or <c>latest</c>, is refused.
/// </remarks>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private SemVersion(long major, long minor, long patch, IReadOnlyList<string> prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public long Major { get; }
    public long Minor { get; }
    public long Patch { get; }

    /// <summary>The dot-separated pre-release identifiers, <c>["beta", "1"]</c> for <c>-beta.1</c>; empty for a release.</summary>
    public IReadOnlyList<string> Prerelease { get; }

    public bool IsPrerelease => Prerelease.Count > 0;

    public static SemVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a semantic version.");

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];

        // Build metadata: dot-separated, non-empty [0-9A-Za-z-] identifiers, ignored for precedence.
        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            if (!s[(plus + 1)..].Split('.').All(IsIdentifier)) return false;
            s = s[..plus];
        }

        string[] prerelease = [];
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..].Split('.');
            if (!prerelease.All(id => IsIdentifier(id) && !(IsNumeric(id) && id.Length > 1 && id[0] == '0')))
                return false;
            s = s[..dash];
        }

        var core = s.Split('.');
        if (core.Length != 3) return false;
        var numbers = new long[3];
        for (var i = 0; i < 3; i++)
        {
            var part = core[i];
            if (!IsNumeric(part) || (part.Length > 1 && part[0] == '0')) return false;
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }

        version = new SemVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    private static bool IsIdentifier(string id) => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static bool IsNumeric(string id) => id.Length > 0 && id.All(char.IsAsciiDigit);

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0) return core;

        // A release has higher precedence than any of its pre-releases.
        if (!IsPrerelease || !other.IsPrerelease) return other.IsPrerelease.CompareTo(IsPrerelease);

        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            var compared = CompareIdentifiers(Prerelease[i], other.Prerelease[i]);
            if (compared != 0) return compared;
        }
        // All shared identifiers equal: the longer set comes later.
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    /// <summary>Numbers compare as numbers, words in ASCII order, and a number comes before a word.</summary>
    private static int CompareIdentifiers(string a, string b)
    {
        var aNumeric = IsNumeric(a);
        var bNumeric = IsNumeric(b);
        if (aNumeric && bNumeric)
        {
            // Without leading zeros, a longer number is a larger one, however long it is.
            var length = a.Length.CompareTo(b.Length);
            return length != 0 ? length : string.CompareOrdinal(a, b);
        }
        if (aNumeric != bNumeric) return aNumeric ? -1 : 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Prerelease));

    public static bool operator ==(SemVersion? a, SemVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemVersion? a, SemVersion? b) => !(a == b);
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;

    /// <summary><c>0.3.0-beta.1</c>: without the <c>v</c> and the build metadata.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") +
        (IsPrerelease ? "-" + string.Join('.', Prerelease) : "");
}
