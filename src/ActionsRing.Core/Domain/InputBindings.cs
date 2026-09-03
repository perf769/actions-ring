namespace ActionsRing.Core.Domain;

/// <summary>Describes the physical input device used to open the ring.</summary>
public enum InputBindingKind
{
    Keyboard,
    MouseButton,
}

/// <summary>Controls how the ring lifetime relates to the trigger input.</summary>
public enum ActivationMode
{
    /// <summary>One press opens the ring and the next press closes it.</summary>
    Toggle,

    /// <summary>The ring remains open only while the trigger is held.</summary>
    Hold,
}

/// <summary>Mouse inputs that can be used by a trigger or mouse action.</summary>
public enum MouseButton
{
    Left,
    Right,
    Middle,
    XButton1,
    XButton2,
    WheelUp,
    WheelDown,
    WheelLeft,
    WheelRight,
}

[Flags]
public enum KeyboardModifiers
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    Windows = 1 << 3,
}

/// <summary>A platform-neutral keyboard key and its modifiers.</summary>
public sealed class KeyChord
{
    public string Key { get; set; } = "Space";

    public KeyboardModifiers Modifiers { get; set; }
}

/// <summary>The configurable global input that opens an actions ring.</summary>
public sealed class TriggerBinding
{
    public InputBindingKind Kind { get; set; } = InputBindingKind.MouseButton;

    /// <summary>Keyboard data when <see cref="Kind"/> is <see cref="InputBindingKind.Keyboard"/>.</summary>
    public KeyChord? Keyboard { get; set; }

    /// <summary>Mouse data when <see cref="Kind"/> is <see cref="InputBindingKind.MouseButton"/>.</summary>
    public MouseButton? Button { get; set; } = MouseButton.XButton2;

    /// <summary>Optional keyboard modifiers held together with a mouse button or wheel input.</summary>
    public KeyboardModifiers MouseModifiers { get; set; }

    public ActivationMode ActivationMode { get; set; } = ActivationMode.Hold;
}

/// <summary>Canonicalizes common key-name spellings while retaining unknown platform key names.</summary>
public static class KeyNames
{
    private static readonly HashSet<string> SupportedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Backspace", "Tab", "Enter", "Shift", "Control", "Alt", "Pause", "CapsLock",
        "Escape", "Space", "PageUp", "PageDown", "End", "Home", "Left", "Up", "Right",
        "Down", "PrintScreen", "Insert", "Delete", "LeftWindows", "RightWindows", "ContextMenu",
        "NumPad0", "NumPad1", "NumPad2", "NumPad3", "NumPad4", "NumPad5", "NumPad6", "NumPad7",
        "NumPad8", "NumPad9", "Multiply", "Add", "Separator", "Subtract", "Decimal", "Divide",
        "NumLock", "ScrollLock", "LeftShift", "RightShift", "LeftControl", "RightControl", "LeftAlt",
        "RightAlt", "BrowserBack", "BrowserForward", "BrowserRefresh", "BrowserStop", "BrowserSearch",
        "BrowserFavorites", "BrowserHome", "VolumeMute", "VolumeDown", "VolumeUp", "MediaNext",
        "MediaPrevious", "MediaStop", "MediaPlayPause", "LaunchMail", "LaunchMedia", "LaunchApp1",
        "LaunchApp2", "Semicolon", "Plus", "Comma", "Minus", "Period", "Slash", "Backtick",
        "LeftBracket", "Backslash", "RightBracket", "Quote", "Oem102",
    };

    private static readonly Dictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ESC"] = "Escape",
            ["RETURN"] = "Enter",
            ["SPACEBAR"] = "Space",
            ["PGUP"] = "PageUp",
            ["PGDN"] = "PageDown",
            ["DEL"] = "Delete",
            ["INS"] = "Insert",
            ["CTRL"] = "Control",
            ["WIN"] = "Windows",
            ["CMD"] = "Windows",
            ["OPTION"] = "Alt",
            ["BACK"] = "Backspace",
        };

    public static string Normalize(string? key)
    {
        var trimmed = key?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (Aliases.TryGetValue(trimmed, out var alias))
        {
            return alias;
        }

        if (trimmed.Length == 1)
        {
            return trimmed.ToUpperInvariant();
        }

        if (trimmed[0] is 'f' or 'F'
            && int.TryParse(trimmed.AsSpan(1), out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            return $"F{functionKey}";
        }

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    public static bool IsSupported(string? key)
    {
        var value = Normalize(key);
        if (value.Length == 1)
        {
            var character = value[0];
            return character is >= 'A' and <= 'Z' or >= '0' and <= '9';
        }
        if (value.Length is >= 2 and <= 3
            && value[0] == 'F'
            && int.TryParse(value.AsSpan(1), out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            return true;
        }
        if (value.StartsWith("VK_0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(
                value.AsSpan(5),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var raw))
        {
            return raw is > 0 and <= byte.MaxValue;
        }
        return SupportedNames.Contains(value);
    }
}
