using System.Globalization;
using ActionsRing.Platform.Windows.Interop;

namespace ActionsRing.Platform.Windows.Input;

/// <summary>
/// Converts stable configuration key names to Win32 virtual-key codes and supplies
/// an OS-localized display name for settings UI. Unknown keys round-trip as VK_0xNN.
/// </summary>
public static class WindowsVirtualKey
{
    private static readonly Dictionary<string, int> NameToCode = CreateNameToCode();
    private static readonly Dictionary<int, string> CodeToName = CreateCodeToName();

    public static bool TryParse(string? name, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var value = name.Trim();
        if (NameToCode.TryGetValue(value, out virtualKey))
        {
            return true;
        }

        if (value.Length == 1)
        {
            var character = char.ToUpperInvariant(value[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = character;
                return true;
            }
        }

        if (value.Length is >= 2 and <= 3 &&
            (value[0] is 'F' or 'f') &&
            int.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function) &&
            function is >= 1 and <= 24)
        {
            virtualKey = 0x6F + function;
            return true;
        }

        if (value.StartsWith("VK_0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw) &&
            raw is > 0 and <= byte.MaxValue)
        {
            virtualKey = raw;
            return true;
        }

        return false;
    }

    public static string GetPersistenceName(int virtualKey)
    {
        Validate(virtualKey);
        if (virtualKey is >= '0' and <= '9' or >= 'A' and <= 'Z')
        {
            return ((char)virtualKey).ToString(CultureInfo.InvariantCulture);
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        return CodeToName.TryGetValue(virtualKey, out var name)
            ? name
            : $"VK_0x{virtualKey:X2}";
    }

    public static string GetDisplayName(int virtualKey, uint scanCode = 0, bool isExtended = false)
    {
        Validate(virtualKey);

        var mappedScanCode = scanCode;
        if (mappedScanCode == 0)
        {
            mappedScanCode = NativeMethods.MapVirtualKey(
                checked((uint)virtualKey),
                NativeMethods.MapVkVirtualKeyToScanCodeEx);
        }

        var extended = isExtended || (mappedScanCode & 0xFF00) == 0xE000;
        var keyNameParameter = checked((int)((mappedScanCode & 0xFF) << 16));
        if (extended)
        {
            keyNameParameter |= 1 << 24;
        }

        var buffer = new char[128];
        var characters = NativeMethods.GetKeyNameText(keyNameParameter, buffer, buffer.Length);
        return characters > 0
            ? new string(buffer, 0, characters)
            : GetPersistenceName(virtualKey);
    }

    private static Dictionary<string, int> CreateNameToCode()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in CreateCanonicalPairs())
        {
            result[pair.Name] = pair.Code;
        }

        result["Esc"] = 0x1B;
        result["Return"] = 0x0D;
        result["Spacebar"] = 0x20;
        result["Ctrl"] = 0x11;
        result["Win"] = 0x5B;
        result["Cmd"] = 0x5B;
        result["Option"] = 0x12;
        result["Del"] = 0x2E;
        result["Ins"] = 0x2D;
        result["PgUp"] = 0x21;
        result["PgDn"] = 0x22;
        result["Back"] = 0x08;
        return result;
    }

    private static Dictionary<int, string> CreateCodeToName()
    {
        var result = new Dictionary<int, string>();
        foreach (var pair in CreateCanonicalPairs())
        {
            result.TryAdd(pair.Code, pair.Name);
        }

        return result;
    }

    private static IEnumerable<(string Name, int Code)> CreateCanonicalPairs()
    {
        yield return ("Backspace", 0x08);
        yield return ("Tab", 0x09);
        yield return ("Enter", 0x0D);
        yield return ("Shift", 0x10);
        yield return ("Control", 0x11);
        yield return ("Alt", 0x12);
        yield return ("Pause", 0x13);
        yield return ("CapsLock", 0x14);
        yield return ("Escape", 0x1B);
        yield return ("Space", 0x20);
        yield return ("PageUp", 0x21);
        yield return ("PageDown", 0x22);
        yield return ("End", 0x23);
        yield return ("Home", 0x24);
        yield return ("Left", 0x25);
        yield return ("Up", 0x26);
        yield return ("Right", 0x27);
        yield return ("Down", 0x28);
        yield return ("PrintScreen", 0x2C);
        yield return ("Insert", 0x2D);
        yield return ("Delete", 0x2E);
        yield return ("LeftWindows", 0x5B);
        yield return ("RightWindows", 0x5C);
        yield return ("ContextMenu", 0x5D);
        yield return ("NumPad0", 0x60);
        yield return ("NumPad1", 0x61);
        yield return ("NumPad2", 0x62);
        yield return ("NumPad3", 0x63);
        yield return ("NumPad4", 0x64);
        yield return ("NumPad5", 0x65);
        yield return ("NumPad6", 0x66);
        yield return ("NumPad7", 0x67);
        yield return ("NumPad8", 0x68);
        yield return ("NumPad9", 0x69);
        yield return ("Multiply", 0x6A);
        yield return ("Add", 0x6B);
        yield return ("Separator", 0x6C);
        yield return ("Subtract", 0x6D);
        yield return ("Decimal", 0x6E);
        yield return ("Divide", 0x6F);
        yield return ("NumLock", 0x90);
        yield return ("ScrollLock", 0x91);
        yield return ("LeftShift", 0xA0);
        yield return ("RightShift", 0xA1);
        yield return ("LeftControl", 0xA2);
        yield return ("RightControl", 0xA3);
        yield return ("LeftAlt", 0xA4);
        yield return ("RightAlt", 0xA5);
        yield return ("BrowserBack", 0xA6);
        yield return ("BrowserForward", 0xA7);
        yield return ("BrowserRefresh", 0xA8);
        yield return ("BrowserStop", 0xA9);
        yield return ("BrowserSearch", 0xAA);
        yield return ("BrowserFavorites", 0xAB);
        yield return ("BrowserHome", 0xAC);
        yield return ("VolumeMute", 0xAD);
        yield return ("VolumeDown", 0xAE);
        yield return ("VolumeUp", 0xAF);
        yield return ("MediaNext", 0xB0);
        yield return ("MediaPrevious", 0xB1);
        yield return ("MediaStop", 0xB2);
        yield return ("MediaPlayPause", 0xB3);
        yield return ("LaunchMail", 0xB4);
        yield return ("LaunchMedia", 0xB5);
        yield return ("LaunchApp1", 0xB6);
        yield return ("LaunchApp2", 0xB7);
        yield return ("Semicolon", 0xBA);
        yield return ("Plus", 0xBB);
        yield return ("Comma", 0xBC);
        yield return ("Minus", 0xBD);
        yield return ("Period", 0xBE);
        yield return ("Slash", 0xBF);
        yield return ("Backtick", 0xC0);
        yield return ("LeftBracket", 0xDB);
        yield return ("Backslash", 0xDC);
        yield return ("RightBracket", 0xDD);
        yield return ("Quote", 0xDE);
        yield return ("Oem102", 0xE2);
    }

    private static void Validate(int virtualKey)
    {
        if (virtualKey is <= 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(virtualKey));
        }
    }
}
