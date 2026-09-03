using System.Globalization;
using System.Windows.Input;
using ActionsRing.Core.Domain;
using PlatformInput = ActionsRing.Platform.Windows.Input;
using MouseButton = ActionsRing.Core.Domain.MouseButton;

namespace ActionsRing.App.Services;

public static class InputBindingMapper
{
    public static PlatformInput.InputGesture ToPlatform(TriggerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Kind == InputBindingKind.Keyboard)
        {
            var chord = binding.Keyboard ?? throw new InvalidDataException("Keyboard trigger has no key chord.");
            return PlatformInput.InputGesture.Keyboard(
                KeyNameToVirtualKey(chord.Key),
                ToPlatform(chord.Modifiers),
                PlatformInput.InputTriggerEdge.Pressed);
        }

        var button = binding.Button ?? MouseButton.XButton2;
        var gesture = button switch
        {
            MouseButton.WheelUp => PlatformInput.InputGesture.Wheel(PlatformInput.MouseWheelDirection.Up, ToPlatform(binding.MouseModifiers)),
            MouseButton.WheelDown => PlatformInput.InputGesture.Wheel(PlatformInput.MouseWheelDirection.Down, ToPlatform(binding.MouseModifiers)),
            MouseButton.WheelLeft => PlatformInput.InputGesture.Wheel(PlatformInput.MouseWheelDirection.Left, ToPlatform(binding.MouseModifiers)),
            MouseButton.WheelRight => PlatformInput.InputGesture.Wheel(PlatformInput.MouseWheelDirection.Right, ToPlatform(binding.MouseModifiers)),
            _ => PlatformInput.InputGesture.Mouse(ToPlatform(button), ToPlatform(binding.MouseModifiers)),
        };
        return binding.MouseModifiers == KeyboardModifiers.None
            ? gesture with { AllowAdditionalModifiers = true }
            : gesture;
    }

    public static TriggerBinding ToCore(
        PlatformInput.InputGesture gesture,
        ActivationMode activationMode)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if ((gesture.Modifiers & (PlatformInput.InputModifiers.Alt | PlatformInput.InputModifiers.Windows)) != 0)
        {
            throw new InvalidDataException(
                "Alt и Win зарезервированы Windows. Используйте клавишу, Ctrl/Shift-сочетание или кнопку мыши.");
        }
        if (gesture.Device == PlatformInput.InputDeviceKind.Keyboard)
        {
            return new TriggerBinding
            {
                Kind = InputBindingKind.Keyboard,
                Keyboard = new KeyChord
                {
                    Key = VirtualKeyToName(gesture.VirtualKey),
                    Modifiers = ToCore(gesture.Modifiers),
                },
                Button = null,
                ActivationMode = activationMode,
            };
        }

        var button = gesture.WheelDirection switch
        {
            PlatformInput.MouseWheelDirection.Up => MouseButton.WheelUp,
            PlatformInput.MouseWheelDirection.Down => MouseButton.WheelDown,
            PlatformInput.MouseWheelDirection.Left => MouseButton.WheelLeft,
            PlatformInput.MouseWheelDirection.Right => MouseButton.WheelRight,
            _ => ToCore(gesture.MouseButton),
        };
        return new TriggerBinding
        {
            Kind = InputBindingKind.MouseButton,
            Button = button,
            MouseModifiers = ToCore(gesture.Modifiers),
            Keyboard = null,
            ActivationMode = IsWheel(button)
                ? ActivationMode.Toggle
                : activationMode,
        };
    }

    public static KeyChord ToCoreKeyChord(PlatformInput.InputGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if (gesture.Device != PlatformInput.InputDeviceKind.Keyboard)
        {
            throw new InvalidDataException("Ожидалось сочетание клавиш.");
        }
        return new KeyChord
        {
            Key = VirtualKeyToName(gesture.VirtualKey),
            Modifiers = ToCore(gesture.Modifiers),
        };
    }

    public static string Describe(TriggerBinding binding)
    {
        if (binding.Kind == InputBindingKind.MouseButton)
        {
            var input = binding.Button switch
            {
                MouseButton.XButton1 => "Боковая кнопка 1 (Назад)",
                MouseButton.XButton2 => "Боковая кнопка 2 (Вперёд)",
                MouseButton.Middle => "Средняя кнопка мыши",
                MouseButton.Left => "Левая кнопка мыши",
                MouseButton.Right => "Правая кнопка мыши",
                MouseButton.WheelUp => "Колесо вверх",
                MouseButton.WheelDown => "Колесо вниз",
                MouseButton.WheelLeft => "Горизонтальное колесо влево",
                MouseButton.WheelRight => "Горизонтальное колесо вправо",
                _ => "Кнопка мыши",
            };
            var modifiers = DescribeModifiers(binding.MouseModifiers);
            return modifiers.Count == 0 ? input : string.Join(" + ", modifiers.Append(input));
        }

        var chord = binding.Keyboard;
        if (chord is null)
        {
            return "Клавиша не назначена";
        }
        var parts = DescribeModifiers(chord.Modifiers);
        parts.Add(LocalizeKey(chord.Key));
        return string.Join(" + ", parts);
    }

    public static string? GetConflictWarning(TriggerBinding binding)
    {
        if (binding.Kind == InputBindingKind.MouseButton && binding.Button is MouseButton.Left or MouseButton.Right)
        {
            return "Эта кнопка используется почти в каждом приложении. Кольцо будет перехватывать обычные клики.";
        }
        if (binding.Kind == InputBindingKind.MouseButton && binding.Button is MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight)
        {
            return "Колесо будет открывать кольцо вместо прокрутки. Для него автоматически используется режим нажатия.";
        }
        if (binding.Kind == InputBindingKind.MouseButton)
        {
            return null;
        }
        var chord = binding.Keyboard;
        if (chord is null)
        {
            return "Нужно записать клавишу или кнопку мыши.";
        }
        if (chord.Modifiers == KeyboardModifiers.None && chord.Key.Length == 1)
        {
            return "Одиночная печатная клавиша не будет вводиться в текст, пока Actions Ring активен.";
        }
        if (chord.Modifiers == KeyboardModifiers.None && KeyNames.Normalize(chord.Key) is
                "Shift" or "LeftShift" or "RightShift"
                or "Control" or "LeftControl" or "RightControl"
                or "Alt" or "LeftAlt" or "RightAlt"
                or "Windows" or "LeftWindows" or "RightWindows")
        {
            return "Эта клавиша будет перехватываться целиком, пока Actions Ring активен.";
        }
        if (chord.Modifiers == KeyboardModifiers.Windows)
        {
            return "Это сочетание может пересекаться с системной командой Windows.";
        }
        return null;
    }

    public static int KeyNameToVirtualKey(string keyName)
    {
        var normalized = KeyNames.Normalize(keyName);
        if (PlatformInput.WindowsVirtualKey.TryParse(normalized, out var platformKey))
        {
            return platformKey;
        }
        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Plus"] = 0xBB,
            ["+"] = 0xBB,
            ["Minus"] = 0xBD,
            ["-"] = 0xBD,
            ["Comma"] = 0xBC,
            ["Period"] = 0xBE,
            ["Control"] = 0x11,
            ["Alt"] = 0x12,
            ["Shift"] = 0x10,
            ["Windows"] = 0x5B,
        };
        if (aliases.TryGetValue(normalized, out var virtualKey))
        {
            return virtualKey;
        }

        try
        {
            var converted = new KeyConverter().ConvertFromString(null, CultureInfo.InvariantCulture, normalized);
            if (converted is Key key && key != Key.None)
            {
                return KeyInterop.VirtualKeyFromKey(key);
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
        {
            // Fall through to the explicit single-character mapping.
        }

        if (normalized.Length == 1)
        {
            var character = char.ToUpperInvariant(normalized[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return character;
            }
        }
        throw new ArgumentException($"Unsupported keyboard key '{keyName}'.", nameof(keyName));
    }

    public static string VirtualKeyToName(int virtualKey)
    {
        return PlatformInput.WindowsVirtualKey.GetPersistenceName(virtualKey);
    }

    private static string LocalizeKey(string value) => value switch
    {
        "Escape" => "Esc",
        "Return" or "Enter" => "Enter",
        "Space" => "Пробел",
        "Back" or "Backspace" => "Backspace",
        "Delete" => "Delete",
        _ => value,
    };

    private static List<string> DescribeModifiers(KeyboardModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(KeyboardModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyboardModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyboardModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyboardModifiers.Windows)) parts.Add("Win");
        return parts;
    }

    private static bool IsWheel(MouseButton button) => button is
        MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight;

    private static PlatformInput.InputModifiers ToPlatform(KeyboardModifiers modifiers)
    {
        var result = PlatformInput.InputModifiers.None;
        if (modifiers.HasFlag(KeyboardModifiers.Control)) result |= PlatformInput.InputModifiers.Control;
        if (modifiers.HasFlag(KeyboardModifiers.Alt)) result |= PlatformInput.InputModifiers.Alt;
        if (modifiers.HasFlag(KeyboardModifiers.Shift)) result |= PlatformInput.InputModifiers.Shift;
        if (modifiers.HasFlag(KeyboardModifiers.Windows)) result |= PlatformInput.InputModifiers.Windows;
        return result;
    }

    private static KeyboardModifiers ToCore(PlatformInput.InputModifiers modifiers)
    {
        var result = KeyboardModifiers.None;
        if (modifiers.HasFlag(PlatformInput.InputModifiers.Control)) result |= KeyboardModifiers.Control;
        if (modifiers.HasFlag(PlatformInput.InputModifiers.Alt)) result |= KeyboardModifiers.Alt;
        if (modifiers.HasFlag(PlatformInput.InputModifiers.Shift)) result |= KeyboardModifiers.Shift;
        if (modifiers.HasFlag(PlatformInput.InputModifiers.Windows)) result |= KeyboardModifiers.Windows;
        return result;
    }

    private static PlatformInput.MouseButton ToPlatform(MouseButton button) => button switch
    {
        MouseButton.Left => PlatformInput.MouseButton.Left,
        MouseButton.Right => PlatformInput.MouseButton.Right,
        MouseButton.Middle => PlatformInput.MouseButton.Middle,
        MouseButton.XButton1 => PlatformInput.MouseButton.XButton1,
        MouseButton.XButton2 => PlatformInput.MouseButton.XButton2,
        _ => throw new ArgumentOutOfRangeException(nameof(button)),
    };

    private static MouseButton ToCore(PlatformInput.MouseButton button) => button switch
    {
        PlatformInput.MouseButton.Left => MouseButton.Left,
        PlatformInput.MouseButton.Right => MouseButton.Right,
        PlatformInput.MouseButton.Middle => MouseButton.Middle,
        PlatformInput.MouseButton.XButton1 => MouseButton.XButton1,
        PlatformInput.MouseButton.XButton2 => MouseButton.XButton2,
        _ => MouseButton.XButton2,
    };
}
