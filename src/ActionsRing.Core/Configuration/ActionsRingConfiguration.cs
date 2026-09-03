using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;
using System.Text.Json.Serialization;

namespace ActionsRing.Core.Configuration;

/// <summary>The complete durable configuration document for Actions Ring.</summary>
public sealed class ActionsRingConfiguration
{
    public int SchemaVersion { get; set; } = ConfigurationSchema.CurrentVersion;

    public TriggerBinding Trigger { get; set; } = new();

    /// <summary>Independent user workspaces with their own fallback ring and app contexts.</summary>
    public List<UserProfile> UserProfiles { get; set; } = [];

    /// <summary>The user profile used for manual and automatic application selection.</summary>
    public string ActiveUserProfileId { get; set; } = string.Empty;

    /// <summary>Compatibility accessor for the fallback ring in the selected user profile.</summary>
    [JsonIgnore]
    public RingProfile GlobalProfile
    {
        get => GetActiveUserProfile().GlobalProfile;
        set => GetActiveUserProfile().GlobalProfile = value;
    }

    /// <summary>Compatibility accessor for application contexts in the selected user profile.</summary>
    [JsonIgnore]
    public List<ApplicationProfile> ApplicationProfiles
    {
        get => GetActiveUserProfile().ApplicationProfiles;
        set => GetActiveUserProfile().ApplicationProfiles = value;
    }

    public UserPreferences Preferences { get; set; } = new();

    public OnboardingState Onboarding { get; set; } = new();

    public UserProfile GetActiveUserProfile()
    {
        var profiles = UserProfiles ??= [];
        var active = profiles.FirstOrDefault(profile =>
            profile is not null
            && string.Equals(profile.Id, ActiveUserProfileId, StringComparison.OrdinalIgnoreCase));
        if (active is not null)
        {
            return active;
        }

        active = profiles.FirstOrDefault(profile => profile is not null);
        if (active is null)
        {
            active = ConfigurationDefaults.CreateDefaultUserProfile();
            profiles.Add(active);
        }

        ActiveUserProfileId = active.Id;
        return active;
    }

    public static ActionsRingConfiguration CreateDefault() => ConfigurationDefaults.Create();
}

public static class ConfigurationSchema
{
    public const int OldestSupportedVersion = 1;
    public const int CurrentVersion = 4;
}

public sealed class UserPreferences
{
    public GeneralPreferences General { get; set; } = new();

    public AppearancePreferences Appearance { get; set; } = new();

    public UpdatePreferences Updates { get; set; } = new();
}

/// <summary>Controls background release checks and downloads. Installation always requires confirmation.</summary>
public sealed class UpdatePreferences
{
    public bool CheckAutomatically { get; set; } = true;

    public bool DownloadAutomatically { get; set; }

    /// <summary>A normalized semantic version that should not prompt during automatic checks.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>The completion time of the last successful or failed release check.</summary>
    public DateTimeOffset? LastCheckedAtUtc { get; set; }
}

public sealed class GeneralPreferences
{
    public bool RunAtStartup { get; set; }

    public bool StartMinimized { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public bool ShowKeyStateNotifications { get; set; } = true;
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed class AppearancePreferences
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public bool UseSystemAccentColor { get; set; }

    /// <summary>Optional #RRGGBB or #AARRGGBB override.</summary>
    public string AccentColor { get; set; } = "#824EF9";

    /// <summary>
    /// Scales the ring for the monitor under the pointer using its effective work area and DPI.
    /// RingDiameter remains the user's baseline size.
    /// </summary>
    public bool AutoScaleRing { get; set; } = true;

    public int RingDiameter { get; set; } = 212;

    public int CenterCloseDiameter { get; set; } = 32;

    public bool EnableAnimations { get; set; } = true;

    public bool ReduceMotion { get; set; }

    public int OpenAnimationMilliseconds { get; set; } = 240;

    public int SubmenuAnimationMilliseconds { get; set; } = 360;

    public bool ShowTooltips { get; set; } = true;

    public int TooltipDelayMilliseconds { get; set; } = 350;
}

/// <summary>Durable progress through the optional first-run setup wizard.</summary>
public sealed class OnboardingState
{
    public bool WelcomeCompleted { get; set; }

    public bool TriggerSetupCompleted { get; set; }

    public bool FirstRingCustomized { get; set; }

    public bool IsCompleted { get; set; }

    public int LastCompletedStep { get; set; }

    public string? LastSeenAppVersion { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }
}

internal static class UpdateVersionText
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80)
        {
            return false;
        }

        var buildSplit = value.Split('+');
        if (buildSplit.Length > 2
            || (buildSplit.Length == 2 && !AreIdentifiersValid(buildSplit[1], numericLeadingZeroAllowed: true)))
        {
            return false;
        }

        var versionAndPrerelease = buildSplit[0].Split('-', 2);
        if (versionAndPrerelease.Length == 2
            && !AreIdentifiersValid(versionAndPrerelease[1], numericLeadingZeroAllowed: false))
        {
            return false;
        }

        var core = versionAndPrerelease[0].Split('.');
        return core.Length == 3 && core.All(IsCoreNumber);
    }

    private static bool AreIdentifiersValid(string value, bool numericLeadingZeroAllowed)
    {
        var identifiers = value.Split('.');
        return identifiers.Length > 0 && identifiers.All(identifier =>
            identifier.Length > 0
            && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && (numericLeadingZeroAllowed
                || !identifier.All(char.IsAsciiDigit)
                || identifier.Length == 1
                || identifier[0] != '0'));
    }

    private static bool IsCoreNumber(string value) =>
        value.Length > 0
        && value.All(char.IsAsciiDigit)
        && (value.Length == 1 || value[0] != '0');
}
