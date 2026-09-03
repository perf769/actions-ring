namespace ActionsRing.Platform.Windows.Input;

/// <summary>
/// Serializable description of an activation input. Keyboard gestures store a
/// virtual-key code and modifiers; mouse gestures store a button or wheel direction.
/// </summary>
public sealed record InputGesture
{
    public InputDeviceKind Device { get; init; }
    public int VirtualKey { get; init; }
    public InputModifiers Modifiers { get; init; }
    public MouseButton MouseButton { get; init; }
    public MouseWheelDirection WheelDirection { get; init; }
    public InputTriggerEdge Edge { get; init; } = InputTriggerEdge.Pressed;
    public bool AllowAdditionalModifiers { get; init; }
    public bool AllowKeyRepeat { get; init; }

    public static InputGesture Keyboard(
        int virtualKey,
        InputModifiers modifiers = InputModifiers.None,
        InputTriggerEdge edge = InputTriggerEdge.Pressed) =>
        new()
        {
            Device = InputDeviceKind.Keyboard,
            VirtualKey = virtualKey,
            Modifiers = modifiers,
            Edge = edge
        };

    public static InputGesture Mouse(
        MouseButton button,
        InputTriggerEdge edge = InputTriggerEdge.Pressed) =>
        new()
        {
            Device = InputDeviceKind.Mouse,
            MouseButton = button,
            Edge = edge
        };

    public static InputGesture Mouse(
        MouseButton button,
        InputModifiers modifiers,
        InputTriggerEdge edge = InputTriggerEdge.Pressed) =>
        new()
        {
            Device = InputDeviceKind.Mouse,
            MouseButton = button,
            Modifiers = modifiers,
            Edge = edge
        };

    public static InputGesture Wheel(MouseWheelDirection direction) =>
        new()
        {
            Device = InputDeviceKind.Mouse,
            WheelDirection = direction,
            Edge = InputTriggerEdge.Pressed
        };

    public static InputGesture Wheel(MouseWheelDirection direction, InputModifiers modifiers) =>
        new()
        {
            Device = InputDeviceKind.Mouse,
            WheelDirection = direction,
            Modifiers = modifiers,
            Edge = InputTriggerEdge.Pressed
        };

    public bool Matches(GlobalInputEvent input)
    {
        if (input.Device != Device)
        {
            return false;
        }

        if (Device == InputDeviceKind.Keyboard)
        {
            var expectedKind = Edge == InputTriggerEdge.Pressed
                ? InputEventKind.KeyDown
                : InputEventKind.KeyUp;

            if (input.Kind != expectedKind || input.VirtualKey != VirtualKey)
            {
                return false;
            }

            if (!AllowKeyRepeat && input.IsRepeat)
            {
                return false;
            }

            return AllowAdditionalModifiers
                ? (input.Modifiers & Modifiers) == Modifiers
                : input.Modifiers == Modifiers;
        }

        var modifiersMatch = AllowAdditionalModifiers
            ? (input.Modifiers & Modifiers) == Modifiers
            : input.Modifiers == Modifiers;

        if (!modifiersMatch)
        {
            return false;
        }

        if (WheelDirection != MouseWheelDirection.None)
        {
            return input.WheelDirection == WheelDirection;
        }

        var expectedMouseKind = Edge == InputTriggerEdge.Pressed
            ? InputEventKind.MouseButtonDown
            : InputEventKind.MouseButtonUp;

        return input.Kind == expectedMouseKind && input.MouseButton == MouseButton;
    }
}
