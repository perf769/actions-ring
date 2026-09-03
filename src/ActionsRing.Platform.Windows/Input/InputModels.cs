namespace ActionsRing.Platform.Windows.Input;

public enum InputDeviceKind
{
    Keyboard,
    Mouse
}

public enum InputEventKind
{
    KeyDown,
    KeyUp,
    MouseButtonDown,
    MouseButtonUp,
    MouseWheel,
    MouseHorizontalWheel,
    MouseMove
}

[Flags]
public enum InputModifiers
{
    None = 0,
    Control = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
    Windows = 1 << 3
}

public enum MouseButton
{
    None,
    Left,
    Right,
    Middle,
    XButton1,
    XButton2
}

public enum InputTriggerEdge
{
    Pressed,
    Released
}

public enum MouseWheelDirection
{
    None,
    Up,
    Down,
    Left,
    Right
}

public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// A normalized immutable event produced by the low-level Windows hooks.
/// </summary>
public sealed record GlobalInputEvent(
    InputDeviceKind Device,
    InputEventKind Kind,
    DateTimeOffset Timestamp,
    ScreenPoint CursorPosition,
    InputModifiers Modifiers = InputModifiers.None,
    int VirtualKey = 0,
    uint ScanCode = 0,
    MouseButton MouseButton = MouseButton.None,
    int WheelDelta = 0,
    bool IsExtended = false,
    bool IsInjected = false,
    bool IsRepeat = false)
{
    public MouseWheelDirection WheelDirection => Kind switch
    {
        InputEventKind.MouseWheel when WheelDelta > 0 => MouseWheelDirection.Up,
        InputEventKind.MouseWheel when WheelDelta < 0 => MouseWheelDirection.Down,
        InputEventKind.MouseHorizontalWheel when WheelDelta > 0 => MouseWheelDirection.Right,
        InputEventKind.MouseHorizontalWheel when WheelDelta < 0 => MouseWheelDirection.Left,
        _ => MouseWheelDirection.None
    };
}

public sealed class GlobalInputEventArgs : EventArgs
{
    public GlobalInputEventArgs(GlobalInputEvent input) => Input = input;
    public GlobalInputEvent Input { get; }
}

public sealed class ActivationInputEventArgs : EventArgs
{
    public ActivationInputEventArgs(InputGesture gesture, GlobalInputEvent input)
    {
        Gesture = gesture;
        Input = input;
    }

    public InputGesture Gesture { get; }
    public GlobalInputEvent Input { get; }
}
