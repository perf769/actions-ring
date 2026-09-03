using System.Diagnostics;
using ActionsRing.Platform.Windows.Interop;

namespace ActionsRing.Platform.Windows.Foreground;

public sealed class ForegroundWindowResolver : IForegroundWindowResolver
{
    public ActiveWindowInfo? GetActiveWindow()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == nint.Zero)
        {
            return null;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var processName = TryGetProcessName(processId);
        if (string.Equals(processName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            var childProcessId = TryResolveHostedApplicationProcess(window, processId);
            if (childProcessId is not null)
            {
                processId = childProcessId.Value;
                processName = TryGetProcessName(processId);
            }
        }

        var executablePath = TryGetExecutablePath(processId);
        var productName = TryGetProductName(executablePath);

        return new ActiveWindowInfo(
            window,
            processId,
            threadId,
            GetWindowTitle(window),
            GetWindowClass(window),
            processName,
            executablePath,
            productName);
    }

    private static uint? TryResolveHostedApplicationProcess(nint parent, uint hostProcessId)
    {
        uint? result = null;
        _ = NativeMethods.EnumChildWindows(
            parent,
            (child, unusedData) =>
            {
                if (!NativeMethods.IsWindowVisible(child))
                {
                    return true;
                }

                _ = NativeMethods.GetWindowThreadProcessId(child, out var childProcessId);
                if (childProcessId != 0 && childProcessId != hostProcessId)
                {
                    result = childProcessId;
                    return false;
                }

                return true;
            },
            nint.Zero);

        return result;
    }

    private static string GetWindowTitle(nint window)
    {
        var length = Math.Clamp(NativeMethods.GetWindowTextLength(window) + 1, 2, 32_768);
        var buffer = new char[length];
        var characters = NativeMethods.GetWindowText(window, buffer, buffer.Length);
        return characters > 0 ? new string(buffer, 0, characters) : string.Empty;
    }

    private static string GetWindowClass(nint window)
    {
        var buffer = new char[512];
        var characters = NativeMethods.GetClassName(window, buffer, buffer.Length);
        return characters > 0 ? new string(buffer, 0, characters) : string.Empty;
    }

    private static string TryGetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? TryGetExecutablePath(uint processId)
    {
        try
        {
            using var process = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation,
                inheritHandle: false,
                processId);

            if (process.IsInvalid)
            {
                return null;
            }

            var capacity = 1024u;
            while (capacity <= 32_768)
            {
                var buffer = new char[checked((int)capacity)];
                var size = capacity;
                if (NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size))
                {
                    return new string(buffer, 0, checked((int)size));
                }

                if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() !=
                    NativeMethods.ErrorInsufficientBuffer)
                {
                    return null;
                }

                capacity *= 2;
            }
        }
        catch
        {
            // Access to elevated/protected processes is expected to fail sometimes.
        }

        return null;
    }

    private static string? TryGetProductName(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).ProductName;
        }
        catch
        {
            return null;
        }
    }
}
