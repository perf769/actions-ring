using ActionsRing.Platform.Windows.Input;

namespace ActionsRing.Platform.Windows.Actions;

/// <summary>
/// Base platform action. The executor also accepts custom subclasses through
/// injected <see cref="IWindowsActionHandler"/> implementations.
/// </summary>
public abstract record WindowsAction;

public sealed record KeyboardShortcutAction(
    IReadOnlyList<int> VirtualKeys,
    nint TargetWindow = default) : WindowsAction;

public sealed record TextInputAction(
    string Text,
    int CharacterDelayMilliseconds = 0,
    nint TargetWindow = default) : WindowsAction;

public enum MouseButtonOperation
{
    Click,
    Press,
    Release
}

public sealed record MouseButtonAction(
    MouseButton Button,
    MouseButtonOperation Operation = MouseButtonOperation.Click,
    nint TargetWindow = default) : WindowsAction;

/// <summary>
/// Sends a wheel delta either through the normal input stream or directly to a
/// captured application window. A non-zero target avoids routing synthetic
/// wheel input back into a visible, no-activate overlay under the pointer.
/// </summary>
public sealed record MouseWheelAction(
    int Delta,
    bool Horizontal = false,
    nint TargetWindow = default) : WindowsAction;

public enum MediaCommand
{
    PlayPause,
    Stop,
    PreviousTrack,
    NextTrack,
    VolumeMute,
    VolumeDown,
    VolumeUp
}

public sealed record MediaAction(MediaCommand Command) : WindowsAction;

/// <summary>
/// Adjusts master volume in Windows media-key steps. Positive values raise volume,
/// negative values lower it; zero is a no-op.
/// </summary>
public sealed record VolumeAdjustmentAction(int Steps) : WindowsAction;

/// <summary>
/// Sets or adjusts the default multimedia output endpoint volume in percentage
/// points through Windows Core Audio.
/// </summary>
public sealed record SystemVolumeAction(
    double Value,
    ValueAdjustmentMode Mode = ValueAdjustmentMode.Delta) : WindowsAction;

public enum ValueAdjustmentMode
{
    Absolute,
    Delta
}

/// <summary>
/// Changes the integrated-display brightness through the active Windows power
/// scheme. Unsupported external monitors may reject this action.
/// </summary>
public sealed record BrightnessAction(
    int Value,
    ValueAdjustmentMode Mode = ValueAdjustmentMode.Delta) : WindowsAction;

/// <summary>
/// Opens a URL, document, executable, or shell-known target. ProcessStartInfo is
/// used directly; the value is never concatenated into a command shell.
/// </summary>
public sealed record LaunchTargetAction(
    string Target,
    string? Arguments = null,
    string? WorkingDirectory = null,
    bool UseShellExecute = true,
    bool RunAsAdministrator = false) : WindowsAction;

public enum WindowManagementCommand
{
    Minimize,
    Maximize,
    Restore,
    ToggleMaximizeRestore,
    Close,
    SnapLeft,
    SnapRight,
    Center,
    MoveToNextMonitor,
    ToggleAlwaysOnTop
}

/// <summary>
/// Targets the application window captured before the ring opens. A zero handle is
/// rejected so a delayed command can never affect an unrelated foreground window.
/// </summary>
public sealed record WindowManagementAction(
    WindowManagementCommand Command,
    nint TargetWindow = default) : WindowsAction;

public interface IWindowsActionHandler
{
    bool CanExecute(WindowsAction action);
    Task ExecuteAsync(WindowsAction action, CancellationToken cancellationToken);
}
