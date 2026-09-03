namespace ActionsRing.Core.Domain;

/// <summary>The platform-neutral operation assigned to a ring slot.</summary>
public enum ActionKind
{
    None,
    KeyboardShortcut,
    LaunchApplication,
    OpenUri,
    TypeText,
    BuiltIn,
    MouseInput,
    Sequence,
    AdjustParameter,
}

/// <summary>Built-in commands implemented by the Windows integration layer.</summary>
public enum BuiltInCommand
{
    None,
    Back,
    Forward,
    Copy,
    Paste,
    Cut,
    Undo,
    Redo,
    SelectAll,
    Find,
    ShowDesktop,
    TaskView,
    SwitchWindow,
    CloseWindow,
    MinimizeWindow,
    MaximizeOrRestoreWindow,
    VolumeUp,
    VolumeDown,
    VolumeMute,
    MediaPlayPause,
    MediaNext,
    MediaPrevious,
    MediaStop,
    TakeScreenshot,
    LockWorkstation,
    OpenFileExplorer,
    OpenSettings,
    OpenSearch,
    InsertDate,
    InsertTime,
    InsertDateTime,
    InsertDayOfYear,
    InsertWeekNumber,
    InsertMoonPhase,
    ClearClipboard,
    PasteClipboardPlainText,
    ClipboardLowercase,
    ClipboardSentenceCase,
    ClipboardTitleCase,
    ClipboardUppercase,
    ClipboardToggleCase,
    ClipboardUrlEncode,
    ClipboardUrlDecode,
    ClipboardHtmlEncode,
    ClipboardHtmlDecode,
    ToggleCapsLock,
    ToggleNumLock,
    ToggleScrollLock,
}

/// <summary>
/// A tagged action document. Only the payload matching <see cref="Kind"/> is retained during
/// normalization, which makes malformed hand-edited JSON safe for consumers.
/// </summary>
public sealed class ActionDefinition
{
    public string Id { get; set; } = ConfigurationIds.New("action");

    public string Name { get; set; } = "Нет действия";

    public string? Description { get; set; }

    /// <summary>A built-in icon key or a path understood by the presentation layer.</summary>
    public string? Icon { get; set; }

    public ActionKind Kind { get; set; }

    public KeyboardShortcutAction? KeyboardShortcut { get; set; }

    public LaunchApplicationAction? LaunchApplication { get; set; }

    public OpenUriAction? OpenUri { get; set; }

    public TypeTextAction? TypeText { get; set; }

    public BuiltInAction? BuiltIn { get; set; }

    public MouseInputAction? MouseInput { get; set; }

    public SequenceAction? Sequence { get; set; }

    public AdjustParameterAction? AdjustParameter { get; set; }

    public static ActionDefinition None(string? name = null) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Нет действия" : name.Trim(),
        Kind = ActionKind.None,
    };

    public static ActionDefinition Shortcut(
        string name,
        string key,
        KeyboardModifiers modifiers = KeyboardModifiers.None,
        string? icon = null,
        string? description = null) => new()
        {
            Name = name,
            Description = description,
            Icon = icon,
            Kind = ActionKind.KeyboardShortcut,
            KeyboardShortcut = new KeyboardShortcutAction
            {
                Chords = [new KeyChord { Key = key, Modifiers = modifiers }],
            },
        };

    public static ActionDefinition BuiltInCommand(
        string name,
        global::ActionsRing.Core.Domain.BuiltInCommand command,
        string? icon = null,
        string? description = null) => new()
        {
            Name = name,
            Description = description,
            Icon = icon,
            Kind = ActionKind.BuiltIn,
            BuiltIn = new BuiltInAction { Command = command },
        };
}

public sealed class KeyboardShortcutAction
{
    /// <summary>One or more key chords sent in order.</summary>
    public List<KeyChord> Chords { get; set; } = [];
}

public sealed class LaunchApplicationAction
{
    public string ExecutablePath { get; set; } = string.Empty;

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    public bool RunAsAdministrator { get; set; }
}

public sealed class OpenUriAction
{
    public string Uri { get; set; } = string.Empty;
}

public sealed class TypeTextAction
{
    public string Text { get; set; } = string.Empty;

    /// <summary>Delay between characters; zero requests the fastest supported input.</summary>
    public int CharacterDelayMilliseconds { get; set; }
}

public sealed class BuiltInAction
{
    public BuiltInCommand Command { get; set; }
}

public sealed class MouseInputAction
{
    public MouseButton Button { get; set; } = MouseButton.Left;

    /// <summary>Number of clicks for button actions. Ignored for wheel actions.</summary>
    public int ClickCount { get; set; } = 1;

    /// <summary>Wheel delta magnitude. Direction is determined by <see cref="Button"/>.</summary>
    public int WheelDelta { get; set; } = 120;
}

/// <summary>An ordered macro of actions with optional pauses between them.</summary>
public sealed class SequenceAction
{
    public List<ActionSequenceStep> Steps { get; set; } = [];
}

public sealed class ActionSequenceStep
{
    public string Id { get; set; } = ConfigurationIds.New("step");

    public ActionDefinition Action { get; set; } = ActionDefinition.None();

    public int DelayAfterMilliseconds { get; set; }
}

public enum AdjustableParameter
{
    SystemVolume,
    ScreenBrightness,
    Zoom,
    VerticalScroll,
    HorizontalScroll,
    Custom,
}

public enum AdjustmentMode
{
    Relative,
    Absolute,
}

/// <summary>
/// A continuous or one-shot parameter change. Platform integrations may add custom parameter
/// handlers keyed by <see cref="CustomParameterId"/> without changing the persisted shape.
/// </summary>
public sealed class AdjustParameterAction
{
    public AdjustableParameter Parameter { get; set; } = AdjustableParameter.SystemVolume;

    public AdjustmentMode Mode { get; set; } = AdjustmentMode.Relative;

    /// <summary>A signed delta in relative mode or target value in absolute mode.</summary>
    public double Value { get; set; } = 5;

    public string? CustomParameterId { get; set; }
}

/// <summary>Creates stable, human-readable identifiers for configuration entities.</summary>
public static class ConfigurationIds
{
    public static string New(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return $"{prefix.Trim().ToLowerInvariant()}-{Guid.NewGuid():N}";
    }
}
