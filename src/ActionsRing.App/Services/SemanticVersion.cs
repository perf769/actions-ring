namespace ActionsRing.App.Services;

/// <summary>A SemVer 2.0.0 value with precedence rules that ignore build metadata.</summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly string[] _prereleaseIdentifiers;

    private SemanticVersion(
        string major,
        string minor,
        string patch,
        string[] prereleaseIdentifiers,
        string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prereleaseIdentifiers = prereleaseIdentifiers;
        BuildMetadata = buildMetadata;
    }

    public string Major { get; }

    public string Minor { get; }

    public string Patch { get; }

    public IReadOnlyList<string> PrereleaseIdentifiers => _prereleaseIdentifiers;

    public string? BuildMetadata { get; }

    public bool IsPrerelease => _prereleaseIdentifiers.Length > 0;

    public static SemanticVersion FromVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var patch = version.Build < 0 ? 0 : version.Build;
        return Parse($"{version.Major}.{version.Minor}.{patch}");
    }

    public static SemanticVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new FormatException($"'{value}' is not a valid semantic version.");
        }

        return version;
    }

    public static bool TryParse(string? value, out SemanticVersion version) =>
        TryParseCore(value, allowTagPrefix: false, out version);

    public static bool TryParseTag(string? value, out SemanticVersion version) =>
        TryParseCore(value, allowTagPrefix: true, out version);

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = CompareNumericIdentifier(Major, other.Major);
        if (result != 0) return result;
        result = CompareNumericIdentifier(Minor, other.Minor);
        if (result != 0) return result;
        result = CompareNumericIdentifier(Patch, other.Patch);
        if (result != 0) return result;

        if (_prereleaseIdentifiers.Length == 0)
        {
            return other._prereleaseIdentifiers.Length == 0 ? 0 : 1;
        }
        if (other._prereleaseIdentifiers.Length == 0)
        {
            return -1;
        }

        var commonLength = Math.Min(_prereleaseIdentifiers.Length, other._prereleaseIdentifiers.Length);
        for (var index = 0; index < commonLength; index++)
        {
            result = ComparePrereleaseIdentifier(
                _prereleaseIdentifiers[index],
                other._prereleaseIdentifiers[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return _prereleaseIdentifiers.Length.CompareTo(other._prereleaseIdentifiers.Length);
    }

    public bool Equals(SemanticVersion? other) =>
        other is not null
        && CompareTo(other) == 0
        && string.Equals(BuildMetadata, other.BuildMetadata, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major, StringComparer.Ordinal);
        hash.Add(Minor, StringComparer.Ordinal);
        hash.Add(Patch, StringComparer.Ordinal);
        foreach (var identifier in _prereleaseIdentifiers)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }
        hash.Add(BuildMetadata, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        if (_prereleaseIdentifiers.Length > 0)
        {
            value += "-" + string.Join('.', _prereleaseIdentifiers);
        }
        if (BuildMetadata is not null)
        {
            value += "+" + BuildMetadata;
        }
        return value;
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    private static bool TryParseCore(
        string? input,
        bool allowTagPrefix,
        out SemanticVersion version)
    {
        version = null!;
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 200)
        {
            return false;
        }

        if (allowTagPrefix && value.Length > 1 && value[0] is 'v' or 'V')
        {
            value = value[1..];
        }

        var plusIndex = value.IndexOf('+');
        if (plusIndex >= 0 && value.IndexOf('+', plusIndex + 1) >= 0)
        {
            return false;
        }

        var buildMetadata = plusIndex >= 0 ? value[(plusIndex + 1)..] : null;
        var withoutBuild = plusIndex >= 0 ? value[..plusIndex] : value;
        if (buildMetadata is not null && !AreIdentifiersValid(buildMetadata, allowLeadingZero: true))
        {
            return false;
        }

        var hyphenIndex = withoutBuild.IndexOf('-');
        var prerelease = hyphenIndex >= 0 ? withoutBuild[(hyphenIndex + 1)..] : null;
        var coreValue = hyphenIndex >= 0 ? withoutBuild[..hyphenIndex] : withoutBuild;
        if (prerelease is not null && !AreIdentifiersValid(prerelease, allowLeadingZero: false))
        {
            return false;
        }

        var core = coreValue.Split('.');
        if (core.Length != 3 || core.Any(identifier => !IsNumericIdentifier(identifier, allowLeadingZero: false)))
        {
            return false;
        }

        version = new SemanticVersion(
            core[0],
            core[1],
            core[2],
            prerelease?.Split('.') ?? [],
            buildMetadata);
        return true;
    }

    private static bool AreIdentifiersValid(string value, bool allowLeadingZero)
    {
        var identifiers = value.Split('.');
        return identifiers.Length > 0 && identifiers.All(identifier =>
            identifier.Length > 0
            && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && (!identifier.All(char.IsAsciiDigit)
                || IsNumericIdentifier(identifier, allowLeadingZero)));
    }

    private static bool IsNumericIdentifier(string value, bool allowLeadingZero) =>
        value.Length > 0
        && value.All(char.IsAsciiDigit)
        && (allowLeadingZero || value.Length == 1 || value[0] != '0');

    private static int CompareNumericIdentifier(string left, string right)
    {
        var byLength = left.Length.CompareTo(right.Length);
        return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            return CompareNumericIdentifier(left, right);
        }
        if (leftNumeric)
        {
            return -1;
        }
        if (rightNumeric)
        {
            return 1;
        }
        return string.CompareOrdinal(left, right);
    }
}
