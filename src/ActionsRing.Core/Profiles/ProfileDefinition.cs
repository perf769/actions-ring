using ActionsRing.Core.Domain;

namespace ActionsRing.Core.Profiles;

/// <summary>An independently selectable set of rings and application contexts.</summary>
public sealed class UserProfile
{
    public string Id { get; set; } = ConfigurationIds.New("user");

    public string Name { get; set; } = "Основной";

    public RingProfile GlobalProfile { get; set; } = new();

    public List<ApplicationProfile> ApplicationProfiles { get; set; } = [];
}

/// <summary>The fallback actions ring used when no application profile matches.</summary>
public sealed class RingProfile
{
    public string Id { get; set; } = "global";

    public string Name { get; set; } = "Все приложения";

    public bool IsEnabled { get; set; } = true;

    public RingStyleDefinition Style { get; set; } = new();

    public RingDefinition RootRing { get; set; } = new();
}

/// <summary>A profile selected from properties of the foreground application.</summary>
public sealed class ApplicationProfile
{
    public string Id { get; set; } = ConfigurationIds.New("profile");

    public string Name { get; set; } = "Приложение";

    public bool IsEnabled { get; set; } = true;

    /// <summary>Higher values win when several enabled profiles match.</summary>
    public int Priority { get; set; }

    /// <summary>If true every rule must match; otherwise any rule can match.</summary>
    public bool MatchAllRules { get; set; }

    public List<ApplicationMatchRule> MatchRules { get; set; } = [];

    /// <summary>Optional Windows AppsFolder identifier for packaged applications.</summary>
    public string? AppUserModelId { get; set; }

    /// <summary>Optional Shell launch target such as shell:AppsFolder\Package!App.</summary>
    public string? LaunchTarget { get; set; }

    /// <summary>Optional icon file supplied by the application installation.</summary>
    public string? IconPath { get; set; }

    public RingStyleDefinition Style { get; set; } = new();

    public RingDefinition RootRing { get; set; } = new();
}

public enum RingStylePreset
{
    Inherit,
    Light,
    Dark,
    Ocean,
    Purple,
    Custom,
}

/// <summary>Optional palette applied to every ring in a profile.</summary>
public sealed class RingStyleDefinition
{
    public RingStylePreset Preset { get; set; } = RingStylePreset.Inherit;

    public string? BubbleColor { get; set; }

    public string? IconColor { get; set; }

    public string? HoverColor { get; set; }
}

public enum ApplicationMatchKind
{
    ProcessName,
    ExecutablePath,
    WindowTitle,
}

public enum TextMatchMode
{
    Equals,
    StartsWith,
    EndsWith,
    Contains,
    Wildcard,
}

public sealed class ApplicationMatchRule
{
    public ApplicationMatchKind Kind { get; set; } = ApplicationMatchKind.ProcessName;

    public TextMatchMode Mode { get; set; } = TextMatchMode.Equals;

    public string Pattern { get; set; } = string.Empty;

    public bool IgnoreCase { get; set; } = true;
}

/// <summary>A platform-neutral snapshot of the current foreground process/window.</summary>
public sealed record ForegroundApplication(
    string? ProcessName,
    string? ExecutablePath,
    string? WindowTitle);

/// <summary>The profile selected for a foreground application.</summary>
public sealed record ProfileSelection(
    string Id,
    string Name,
    RingDefinition RootRing,
    bool IsApplicationSpecific,
    ApplicationProfile? ApplicationProfile);
