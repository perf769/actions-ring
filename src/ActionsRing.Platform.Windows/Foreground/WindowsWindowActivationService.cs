using System.Runtime.InteropServices;
using ActionsRing.Platform.Windows.Interop;

namespace ActionsRing.Platform.Windows.Foreground;

/// <summary>
/// Best-effort window activation suitable for second-instance IPC. Windows may
/// intentionally reject foreground stealing; in that case the taskbar is flashed.
/// </summary>
public sealed class WindowsWindowActivationService : IWindowActivationService
{
    public bool TryActivate(nint windowHandle)
    {
        if (windowHandle == nint.Zero || !NativeMethods.IsWindow(windowHandle))
        {
            return false;
        }

        if (NativeMethods.IsIconic(windowHandle))
        {
            _ = NativeMethods.ShowWindow(windowHandle, NativeMethods.SwRestore);
        }

        if (NativeMethods.SetForegroundWindow(windowHandle))
        {
            return true;
        }

        var flash = new NativeMethods.FlashWindowInfo
        {
            Size = checked((uint)Marshal.SizeOf<NativeMethods.FlashWindowInfo>()),
            Window = windowHandle,
            Flags = NativeMethods.FlashwTray | NativeMethods.FlashwTimerNoForeground,
            Count = 3,
            Timeout = 0
        };
        _ = NativeMethods.FlashWindowEx(ref flash);
        return false;
    }
}
