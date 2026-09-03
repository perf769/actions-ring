using ActionsRing.Platform.Windows.Actions;
using ActionsRing.Platform.Windows.Display;
using ActionsRing.Platform.Windows.Foreground;
using ActionsRing.Platform.Windows.Input;

namespace ActionsRing.Platform.Windows;

/// <summary>
/// UI-agnostic contract for the process-wide keyboard and mouse hook service.
/// </summary>
public interface IGlobalInputHook : IDisposable, IAsyncDisposable
{
    event EventHandler<GlobalInputEventArgs>? InputReceived;
    event EventHandler<ActivationInputEventArgs>? ActivationTriggered;
    event EventHandler<PlatformErrorEventArgs>? PlatformError;

    bool IsRunning { get; }
    InputGesture? ActivationGesture { get; set; }
    bool SuppressActivationInput { get; set; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    InputCaptureSession BeginCapture(
        InputCaptureOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface IForegroundWindowResolver
{
    ActiveWindowInfo? GetActiveWindow();
}

public interface IWindowActivationService
{
    /// <summary>
    /// Restores and requests foreground activation. Returns false when Windows
    /// foreground-stealing policy declines the request; the taskbar is flashed then.
    /// </summary>
    bool TryActivate(nint windowHandle);
}

public interface IDisplayInfoService
{
    ScreenPoint GetCursorPosition();
    MonitorSnapshot GetMonitorAtCursor();
    MonitorSnapshot GetMonitorFromPoint(ScreenPoint point);
    MonitorSnapshot GetMonitorForWindow(nint windowHandle);
    IReadOnlyList<MonitorSnapshot> GetMonitors();
}

public interface IWindowsAutostartService
{
    AutostartStatus GetStatus(string valueName, string? expectedExecutablePath = null);
    void Enable(string valueName, string executablePath, string? arguments = null);
    void Disable(string valueName);
}

public interface IWindowsActionExecutor
{
    Task ExecuteAsync(WindowsAction action, CancellationToken cancellationToken = default);
}

public sealed class PlatformErrorEventArgs : EventArgs
{
    public PlatformErrorEventArgs(string operation, Exception exception)
    {
        Operation = operation;
        Exception = exception;
    }

    public string Operation { get; }
    public Exception Exception { get; }
}
