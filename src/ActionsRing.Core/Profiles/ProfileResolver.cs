using ActionsRing.Core.Configuration;

namespace ActionsRing.Core.Profiles;

/// <summary>Deterministically selects the highest-priority matching application profile.</summary>
public static class ProfileResolver
{
    public static ProfileSelection Resolve(
        ActionsRingConfiguration configuration,
        ForegroundApplication? foregroundApplication)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Resolve(configuration.GetActiveUserProfile(), foregroundApplication);
    }

    public static ProfileSelection Resolve(
        UserProfile userProfile,
        ForegroundApplication? foregroundApplication)
    {
        ArgumentNullException.ThrowIfNull(userProfile);

        if (foregroundApplication is not null)
        {
            var match = (userProfile.ApplicationProfiles ?? [])
                .Where(profile => profile.IsEnabled && Matches(profile, foregroundApplication))
                .OrderByDescending(profile => profile.Priority)
                .FirstOrDefault();

            if (match is not null)
            {
                return new ProfileSelection(
                    match.Id,
                    match.Name,
                    match.RootRing,
                    IsApplicationSpecific: true,
                    match);
            }
        }

        var global = userProfile.GlobalProfile ?? ConfigurationDefaults.CreateDefaultUserProfile().GlobalProfile;
        return new ProfileSelection(
            global.Id,
            global.Name,
            global.RootRing,
            IsApplicationSpecific: false,
            ApplicationProfile: null);
    }

    public static bool Matches(ApplicationProfile profile, ForegroundApplication application)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(application);

        if (profile.MatchRules is null || profile.MatchRules.Count == 0)
        {
            return false;
        }

        return profile.MatchAllRules
            ? profile.MatchRules.All(rule => rule is not null && Matches(rule, application))
            : profile.MatchRules.Any(rule => rule is not null && Matches(rule, application));
    }

    public static bool Matches(ApplicationMatchRule rule, ForegroundApplication application)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(application);

        var candidate = rule.Kind switch
        {
            ApplicationMatchKind.ProcessName => NormalizeProcessName(application.ProcessName),
            ApplicationMatchKind.ExecutablePath => NormalizePath(application.ExecutablePath),
            ApplicationMatchKind.WindowTitle => application.WindowTitle ?? string.Empty,
            _ => string.Empty,
        };

        var pattern = rule.Kind switch
        {
            ApplicationMatchKind.ProcessName => NormalizeProcessName(rule.Pattern),
            ApplicationMatchKind.ExecutablePath => NormalizePath(rule.Pattern),
            _ => rule.Pattern,
        };

        if (candidate.Length == 0 || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var comparison = rule.IgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return rule.Mode switch
        {
            TextMatchMode.Equals => candidate.Equals(pattern, comparison),
            TextMatchMode.StartsWith => candidate.StartsWith(pattern, comparison),
            TextMatchMode.EndsWith => candidate.EndsWith(pattern, comparison),
            TextMatchMode.Contains => candidate.Contains(pattern, comparison),
            TextMatchMode.Wildcard => WildcardMatch(candidate, pattern, rule.IgnoreCase),
            _ => false,
        };
    }

    private static string NormalizeProcessName(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }

    private static string NormalizePath(string? value) =>
        (value?.Trim() ?? string.Empty).Replace('/', '\\');

    private static bool WildcardMatch(string value, string pattern, bool ignoreCase)
    {
        var valueIndex = 0;
        var patternIndex = 0;
        var lastStarIndex = -1;
        var valueIndexAfterStar = -1;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?'
                    || CharactersEqual(pattern[patternIndex], value[valueIndex], ignoreCase)))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                lastStarIndex = patternIndex++;
                valueIndexAfterStar = valueIndex;
                continue;
            }

            if (lastStarIndex < 0)
            {
                return false;
            }

            patternIndex = lastStarIndex + 1;
            valueIndex = ++valueIndexAfterStar;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static bool CharactersEqual(char left, char right, bool ignoreCase) =>
        left == right
        || (ignoreCase && char.ToUpperInvariant(left) == char.ToUpperInvariant(right));
}
