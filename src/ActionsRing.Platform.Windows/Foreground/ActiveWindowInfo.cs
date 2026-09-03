namespace ActionsRing.Platform.Windows.Foreground;

public sealed record ActiveWindowInfo(
    nint WindowHandle,
    uint ProcessId,
    uint ThreadId,
    string WindowTitle,
    string WindowClass,
    string ProcessName,
    string? ExecutablePath,
    string? ProductName);
