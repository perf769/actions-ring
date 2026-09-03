using System.ComponentModel;
using System.Runtime.InteropServices;
using ActionsRing.Platform.Windows.Input;
using ActionsRing.Platform.Windows.Interop;

namespace ActionsRing.Platform.Windows.Display;

public sealed class WindowsDisplayInfoService : IDisplayInfoService
{
    public ScreenPoint GetCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not get the cursor position.");
        }

        return new ScreenPoint(point.X, point.Y);
    }

    public MonitorSnapshot GetMonitorAtCursor() => GetMonitorFromPoint(GetCursorPosition());

    public MonitorSnapshot GetMonitorFromPoint(ScreenPoint point)
    {
        var handle = NativeMethods.MonitorFromPoint(
            new NativeMethods.Point(point.X, point.Y),
            NativeMethods.MonitorDefaultToNearest);
        return GetSnapshot(handle, windowForDpi: nint.Zero);
    }

    public MonitorSnapshot GetMonitorForWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        var handle = NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MonitorDefaultToNearest);
        return GetSnapshot(handle, windowHandle);
    }

    public IReadOnlyList<MonitorSnapshot> GetMonitors()
    {
        var monitors = new List<MonitorSnapshot>();
        Exception? callbackError = null;

        var success = NativeMethods.EnumDisplayMonitors(
            nint.Zero,
            nint.Zero,
            (monitor, _, _, _) =>
            {
                try
                {
                    monitors.Add(GetSnapshot(monitor, windowForDpi: nint.Zero));
                    return true;
                }
                catch (Exception exception)
                {
                    callbackError = exception;
                    return false;
                }
            },
            nint.Zero);

        if (callbackError is not null)
        {
            throw callbackError;
        }

        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate displays.");
        }

        return monitors
            .OrderBy(static monitor => monitor.Bounds.X)
            .ThenBy(static monitor => monitor.Bounds.Y)
            .ToArray();
    }

    private static MonitorSnapshot GetSnapshot(nint monitor, nint windowForDpi)
    {
        if (monitor == nint.Zero)
        {
            throw new Win32Exception("Windows did not return a monitor handle.");
        }

        var info = new NativeMethods.MonitorInfoEx
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            DeviceName = string.Empty
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read monitor information.");
        }

        var (dpiX, dpiY) = GetDpi(monitor, windowForDpi);
        return new MonitorSnapshot(
            monitor,
            info.DeviceName ?? string.Empty,
            ToScreenRect(info.Monitor),
            ToScreenRect(info.WorkArea),
            (info.Flags & NativeMethods.MonitorInfoPrimary) != 0,
            dpiX,
            dpiY);
    }

    private static (uint X, uint Y) GetDpi(nint monitor, nint window)
    {
        try
        {
            if (window != nint.Zero)
            {
                var windowDpi = NativeMethods.GetDpiForWindow(window);
                if (windowDpi != 0)
                {
                    return (windowDpi, windowDpi);
                }
            }
        }
        catch (EntryPointNotFoundException)
        {
            // GetDpiForWindow was introduced in Windows 10 1607.
        }

        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, 0, out var x, out var y) == 0 && x != 0 && y != 0)
            {
                return (x, y);
            }
        }
        catch (DllNotFoundException)
        {
            // Older Windows fallback below.
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows fallback below.
        }

        try
        {
            var systemDpi = NativeMethods.GetDpiForSystem();
            if (systemDpi != 0)
            {
                return (systemDpi, systemDpi);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // The platform target normally guarantees this API; 96 is safe fallback.
        }

        return (96, 96);
    }

    private static ScreenRect ToScreenRect(NativeMethods.Rect rect) =>
        new(rect.Left, rect.Top, checked(rect.Right - rect.Left), checked(rect.Bottom - rect.Top));
}
