using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ActionsRing.Platform.Windows.Display;
using ActionsRing.Platform.Windows.Input;
using ActionsRing.Platform.Windows.Interop;

namespace ActionsRing.Platform.Windows.Actions;

/// <summary>
/// Executes built-in Windows actions and delegates unknown action subclasses to
/// optional extension handlers. Instances are thread-safe.
/// </summary>
public sealed class WindowsActionExecutor : IWindowsActionExecutor
{
    private const int VirtualKeyMediaNextTrack = 0xB0;
    private const int VirtualKeyMediaPreviousTrack = 0xB1;
    private const int VirtualKeyMediaStop = 0xB2;
    private const int VirtualKeyMediaPlayPause = 0xB3;
    private const int VirtualKeyVolumeMute = 0xAD;
    private const int VirtualKeyVolumeDown = 0xAE;
    private const int VirtualKeyVolumeUp = 0xAF;

    private readonly IDisplayInfoService _displayInfo;
    private readonly IReadOnlyList<IWindowsActionHandler> _extensionHandlers;

    public WindowsActionExecutor(
        IDisplayInfoService? displayInfo = null,
        IEnumerable<IWindowsActionHandler>? extensionHandlers = null)
    {
        _displayInfo = displayInfo ?? new WindowsDisplayInfoService();
        _extensionHandlers = extensionHandlers?.ToArray() ?? [];
    }

    public async Task ExecuteAsync(WindowsAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var handler in _extensionHandlers)
        {
            if (handler.CanExecute(action))
            {
                await handler.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        switch (action)
        {
            case KeyboardShortcutAction shortcut:
                SendShortcut(shortcut);
                break;
            case TextInputAction text:
                await SendTextAsync(text, cancellationToken).ConfigureAwait(false);
                break;
            case MouseButtonAction mouseButton:
                SendMouseButton(mouseButton);
                break;
            case MouseWheelAction wheel:
                SendMouseWheel(wheel);
                break;
            case MediaAction media:
                SendMediaCommand(media.Command);
                break;
            case VolumeAdjustmentAction volume:
                AdjustVolume(volume.Steps);
                break;
            case SystemVolumeAction systemVolume:
                SetSystemVolume(systemVolume);
                break;
            case BrightnessAction brightness:
                SetBrightness(brightness);
                break;
            case LaunchTargetAction launch:
                Launch(launch);
                break;
            case WindowManagementAction window:
                ManageWindow(window);
                break;
            default:
                throw new NotSupportedException($"No Windows action handler accepts {action.GetType().FullName}.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void SendShortcut(KeyboardShortcutAction action)
    {
        ArgumentNullException.ThrowIfNull(action.VirtualKeys);
        if (action.VirtualKeys.Count == 0)
        {
            throw new ArgumentException("A keyboard shortcut must contain at least one key.", nameof(action));
        }

        EnsureTargetIsForeground(action.TargetWindow);

        var inputs = new List<NativeMethods.Input>(action.VirtualKeys.Count * 2);
        foreach (var key in action.VirtualKeys)
        {
            ValidateVirtualKey(key);
            inputs.Add(CreateKeyboardInput(key, keyUp: false));
        }

        for (var index = action.VirtualKeys.Count - 1; index >= 0; index--)
        {
            inputs.Add(CreateKeyboardInput(action.VirtualKeys[index], keyUp: true));
        }

        try
        {
            SendInputs(inputs.ToArray(), "keyboard shortcut");
        }
        catch
        {
            // A rare partial SendInput must not leave a modifier physically held.
            var releases = action.VirtualKeys
                .Reverse()
                .Select(static key => CreateKeyboardInput(key, keyUp: true))
                .ToArray();
            TrySendInputs(releases);
            throw;
        }
    }

    private static async Task SendTextAsync(TextInputAction action, CancellationToken cancellationToken)
    {
        var text = action.Text;
        ArgumentNullException.ThrowIfNull(text);
        if (action.CharacterDelayMilliseconds is < 0 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(action),
                "The per-character delay must be between 0 and 60000 milliseconds.");
        }

        if (text.Length == 0)
        {
            return;
        }

        EnsureTargetIsForeground(action.TargetWindow);

        if (action.CharacterDelayMilliseconds > 0)
        {
            for (var index = 0; index < text.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureTargetRemainsForeground(action.TargetWindow);
                var codeUnitCount = char.IsHighSurrogate(text[index]) &&
                                    index + 1 < text.Length &&
                                    char.IsLowSurrogate(text[index + 1])
                    ? 2
                    : 1;
                var inputs = new NativeMethods.Input[codeUnitCount * 2];
                for (var unit = 0; unit < codeUnitCount; unit++)
                {
                    var codeUnit = text[index + unit];
                    inputs[unit * 2] = CreateUnicodeInput(codeUnit, keyUp: false);
                    inputs[(unit * 2) + 1] = CreateUnicodeInput(codeUnit, keyUp: true);
                }

                SendInputs(inputs, "text input");
                index += codeUnitCount;
                if (index < text.Length)
                {
                    await Task.Delay(action.CharacterDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        const int codeUnitsPerBatch = 512;
        for (var offset = 0; offset < text.Length; offset += codeUnitsPerBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureTargetRemainsForeground(action.TargetWindow);
            var length = Math.Min(codeUnitsPerBatch, text.Length - offset);
            var inputs = new NativeMethods.Input[length * 2];
            for (var index = 0; index < length; index++)
            {
                var codeUnit = text[offset + index];
                inputs[index * 2] = CreateUnicodeInput(codeUnit, keyUp: false);
                inputs[(index * 2) + 1] = CreateUnicodeInput(codeUnit, keyUp: true);
            }

            SendInputs(inputs, "text input");
        }
    }

    private static void SendMouseButton(MouseButtonAction action)
    {
        if (action.Button == MouseButton.None)
        {
            throw new ArgumentException("A mouse button is required.", nameof(action));
        }

        EnsureTargetIsForeground(action.TargetWindow);

        var (downFlag, upFlag, mouseData) = action.Button switch
        {
            MouseButton.Left => (NativeMethods.MouseeventfLeftdown, NativeMethods.MouseeventfLeftup, 0u),
            MouseButton.Right => (NativeMethods.MouseeventfRightdown, NativeMethods.MouseeventfRightup, 0u),
            MouseButton.Middle => (NativeMethods.MouseeventfMiddledown, NativeMethods.MouseeventfMiddleup, 0u),
            MouseButton.XButton1 => (NativeMethods.MouseeventfXdown, NativeMethods.MouseeventfXup, NativeMethods.XButton1),
            MouseButton.XButton2 => (NativeMethods.MouseeventfXdown, NativeMethods.MouseeventfXup, NativeMethods.XButton2),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        var inputs = action.Operation switch
        {
            MouseButtonOperation.Press => new[] { CreateMouseInput(downFlag, mouseData) },
            MouseButtonOperation.Release => new[] { CreateMouseInput(upFlag, mouseData) },
            MouseButtonOperation.Click => new[]
            {
                CreateMouseInput(downFlag, mouseData),
                CreateMouseInput(upFlag, mouseData)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        try
        {
            SendInputs(inputs, "mouse button input");
        }
        catch when (action.Operation == MouseButtonOperation.Click)
        {
            // Ensure a partial click cannot leave the mouse button held down.
            TrySendInputs([CreateMouseInput(upFlag, mouseData)]);
            throw;
        }
    }

    private static void SendMouseWheel(MouseWheelAction action)
    {
        if (action.Delta == 0)
        {
            return;
        }

        if (action.TargetWindow != nint.Zero)
        {
            SendMouseWheelToWindow(action);
            return;
        }

        var flag = action.Horizontal
            ? NativeMethods.MouseeventfHWheel
            : NativeMethods.MouseeventfWheel;
        SendInputs([CreateMouseInput(flag, unchecked((uint)action.Delta))], "mouse wheel input");
    }

    private static void SendMouseWheelToWindow(MouseWheelAction action)
    {
        if (!NativeMethods.IsWindow(action.TargetWindow))
        {
            throw new InvalidOperationException("The captured wheel target window is no longer available.");
        }

        var messageTarget = ResolveWheelMessageTarget(action.TargetWindow);

        if (!NativeMethods.GetCursorPos(out var point))
        {
            ThrowLastWin32("Could not read the pointer position for targeted wheel input.");
        }
        var position = PackSignedWords(point.X, point.Y);
        var message = action.Horizontal
            ? NativeMethods.WmMouseHWheel
            : NativeMethods.WmMouseWheel;

        // WM_MOUSE(W)HEEL stores its delta in a signed 16-bit high word. Split
        // unusually large configured adjustments instead of truncating them.
        const int maximumChunk = 32_760;
        var remaining = (long)action.Delta;
        while (remaining != 0)
        {
            var chunk = checked((int)Math.Clamp(remaining, -maximumChunk, maximumChunk));
            var wheelData = PackSignedWords(0, chunk);
            if (!NativeMethods.PostMessage(messageTarget, message, wheelData, position))
            {
                ThrowLastWin32("Could not send wheel input to the captured target window.");
            }

            remaining -= chunk;
        }
    }

    private static nint ResolveWheelMessageTarget(nint capturedWindow)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(capturedWindow, out _);
        if (threadId == 0)
        {
            return capturedWindow;
        }

        var info = new NativeMethods.GuiThreadInfo
        {
            Size = checked((uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>())
        };
        if (!NativeMethods.GetGUIThreadInfo(threadId, ref info) || info.FocusWindow == nint.Zero)
        {
            return capturedWindow;
        }

        return info.FocusWindow == capturedWindow || NativeMethods.IsChild(capturedWindow, info.FocusWindow)
            ? info.FocusWindow
            : capturedWindow;
    }

    private static nint PackSignedWords(int low, int high)
    {
        var packed = unchecked((uint)(ushort)low | ((uint)(ushort)high << 16));
        return unchecked((nint)(int)packed);
    }

    private static void SendMediaCommand(MediaCommand command)
    {
        var virtualKey = command switch
        {
            MediaCommand.PlayPause => VirtualKeyMediaPlayPause,
            MediaCommand.Stop => VirtualKeyMediaStop,
            MediaCommand.PreviousTrack => VirtualKeyMediaPreviousTrack,
            MediaCommand.NextTrack => VirtualKeyMediaNextTrack,
            MediaCommand.VolumeMute => VirtualKeyVolumeMute,
            MediaCommand.VolumeDown => VirtualKeyVolumeDown,
            MediaCommand.VolumeUp => VirtualKeyVolumeUp,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

        try
        {
            SendInputs(
                [CreateKeyboardInput(virtualKey, keyUp: false), CreateKeyboardInput(virtualKey, keyUp: true)],
                "media key input");
        }
        catch
        {
            TrySendInputs([CreateKeyboardInput(virtualKey, keyUp: true)]);
            throw;
        }
    }

    private static void AdjustVolume(int steps)
    {
        if (steps == 0)
        {
            return;
        }

        if (steps is < -100 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "A single volume adjustment is limited to 100 steps.");
        }

        var virtualKey = steps > 0 ? VirtualKeyVolumeUp : VirtualKeyVolumeDown;
        var count = Math.Abs(steps);
        var inputs = new NativeMethods.Input[count * 2];
        for (var index = 0; index < count; index++)
        {
            inputs[index * 2] = CreateKeyboardInput(virtualKey, keyUp: false);
            inputs[(index * 2) + 1] = CreateKeyboardInput(virtualKey, keyUp: true);
        }

        SendInputs(inputs, "volume adjustment");
    }

    private static void SetSystemVolume(SystemVolumeAction action)
    {
        object? enumeratorObject = null;
        CoreAudioInterop.IDevice? device = null;
        object? endpointObject = null;

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(CoreAudioInterop.DeviceEnumeratorClassId, throwOnError: true)
                ?? throw new InvalidOperationException("Windows Core Audio is unavailable.");
            enumeratorObject = Activator.CreateInstance(enumeratorType)
                ?? throw new InvalidOperationException("Windows Core Audio could not be initialized.");
            var enumerator = (CoreAudioInterop.IDeviceEnumerator)enumeratorObject;

            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(
                CoreAudioInterop.DataFlow.Render,
                CoreAudioInterop.Role.Multimedia,
                out device));

            var interfaceId = typeof(CoreAudioInterop.IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(
                in interfaceId,
                CoreAudioInterop.ClassContext.All,
                nint.Zero,
                out endpointObject));

            var endpoint = (CoreAudioInterop.IAudioEndpointVolume)endpointObject;
            double targetPercent;
            if (action.Mode == ValueAdjustmentMode.Absolute)
            {
                targetPercent = action.Value;
            }
            else
            {
                Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var currentScalar));
                targetPercent = (currentScalar * 100d) + action.Value;
            }

            var targetScalar = checked((float)(Math.Clamp(targetPercent, 0d, 100d) / 100d));
            var eventContext = Guid.Empty;
            Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(targetScalar, in eventContext));
        }
        finally
        {
            ReleaseComObject(endpointObject);
            ReleaseComObject(device);
            ReleaseComObject(enumeratorObject);
        }
    }

    private static void SetBrightness(BrightnessAction action)
    {
        var result = NativeMethods.PowerGetActiveScheme(nint.Zero, out var schemePointer);
        if (result != 0 || schemePointer == nint.Zero)
        {
            throw new Win32Exception(unchecked((int)result), "Could not read the active Windows power scheme.");
        }

        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            var videoSubgroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
            var brightnessSetting = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb");
            var onAcPower = !NativeMethods.GetSystemPowerStatus(out var powerStatus) || powerStatus.AcLineStatus != 0;

            uint current;
            result = onAcPower
                ? NativeMethods.PowerReadACValueIndex(
                    nint.Zero, in scheme, in videoSubgroup, in brightnessSetting, out current)
                : NativeMethods.PowerReadDCValueIndex(
                    nint.Zero, in scheme, in videoSubgroup, in brightnessSetting, out current);

            if (result != 0)
            {
                throw new Win32Exception(unchecked((int)result), "This display does not expose power-scheme brightness.");
            }

            var desired = action.Mode == ValueAdjustmentMode.Absolute
                ? Math.Clamp(action.Value, 0, 100)
                : Math.Clamp(checked((int)current + action.Value), 0, 100);

            result = onAcPower
                ? NativeMethods.PowerWriteACValueIndex(
                    nint.Zero, in scheme, in videoSubgroup, in brightnessSetting, checked((uint)desired))
                : NativeMethods.PowerWriteDCValueIndex(
                    nint.Zero, in scheme, in videoSubgroup, in brightnessSetting, checked((uint)desired));

            if (result != 0)
            {
                throw new Win32Exception(unchecked((int)result), "Could not update display brightness.");
            }

            result = NativeMethods.PowerSetActiveScheme(nint.Zero, in scheme);
            if (result != 0)
            {
                throw new Win32Exception(unchecked((int)result), "Could not apply display brightness.");
            }
        }
        finally
        {
            _ = NativeMethods.LocalFree(schemePointer);
        }
    }

    private static void Launch(LaunchTargetAction action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action.Target);
        var startInfo = new ProcessStartInfo
        {
            FileName = action.Target,
            UseShellExecute = action.UseShellExecute || action.RunAsAdministrator
        };

        if (action.RunAsAdministrator)
        {
            startInfo.Verb = "runas";
        }

        if (!string.IsNullOrWhiteSpace(action.Arguments))
        {
            startInfo.Arguments = action.Arguments;
        }

        if (!string.IsNullOrWhiteSpace(action.WorkingDirectory))
        {
            startInfo.WorkingDirectory = action.WorkingDirectory;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Windows did not start '{action.Target}'.");
    }

    private void ManageWindow(WindowManagementAction action)
    {
        var window = action.TargetWindow;

        if (window == nint.Zero || !NativeMethods.IsWindow(window))
        {
            throw new InvalidOperationException("No valid target window is available.");
        }

        switch (action.Command)
        {
            case WindowManagementCommand.Minimize:
                _ = NativeMethods.ShowWindow(window, NativeMethods.SwMinimize);
                break;
            case WindowManagementCommand.Maximize:
                _ = NativeMethods.ShowWindow(window, NativeMethods.SwMaximize);
                break;
            case WindowManagementCommand.Restore:
                _ = NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
                break;
            case WindowManagementCommand.ToggleMaximizeRestore:
                _ = NativeMethods.ShowWindow(
                    window,
                    NativeMethods.IsZoomed(window) ? NativeMethods.SwRestore : NativeMethods.SwMaximize);
                break;
            case WindowManagementCommand.Close:
                if (!NativeMethods.PostMessage(window, NativeMethods.WmClose, nint.Zero, nint.Zero))
                {
                    ThrowLastWin32("Could not request that the target window close.");
                }

                break;
            case WindowManagementCommand.SnapLeft:
                Snap(window, left: true);
                break;
            case WindowManagementCommand.SnapRight:
                Snap(window, left: false);
                break;
            case WindowManagementCommand.Center:
                Center(window);
                break;
            case WindowManagementCommand.MoveToNextMonitor:
                MoveToNextMonitor(window);
                break;
            case WindowManagementCommand.ToggleAlwaysOnTop:
                ToggleAlwaysOnTop(window);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private void Snap(nint window, bool left)
    {
        var workArea = _displayInfo.GetMonitorForWindow(window).WorkArea;
        var leftWidth = workArea.Width / 2;
        var width = left ? leftWidth : workArea.Width - leftWidth;
        var x = left ? workArea.Left : workArea.Left + leftWidth;

        _ = NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        SetWindowBounds(window, x, workArea.Top, width, workArea.Height);
    }

    private void Center(nint window)
    {
        if (!NativeMethods.GetWindowRect(window, out var rect))
        {
            ThrowLastWin32("Could not read the target window bounds.");
        }

        var workArea = _displayInfo.GetMonitorForWindow(window).WorkArea;
        var width = Math.Min(rect.Right - rect.Left, workArea.Width);
        var height = Math.Min(rect.Bottom - rect.Top, workArea.Height);
        var x = workArea.Left + ((workArea.Width - width) / 2);
        var y = workArea.Top + ((workArea.Height - height) / 2);

        _ = NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        SetWindowBounds(window, x, y, width, height);
    }

    private void MoveToNextMonitor(nint window)
    {
        if (!NativeMethods.GetWindowRect(window, out var rect))
        {
            ThrowLastWin32("Could not read the target window bounds.");
        }

        var monitors = _displayInfo.GetMonitors();
        if (monitors.Count < 2)
        {
            return;
        }

        var current = _displayInfo.GetMonitorForWindow(window);
        var currentIndex = -1;
        for (var index = 0; index < monitors.Count; index++)
        {
            if (monitors[index].Handle == current.Handle)
            {
                currentIndex = index;
                break;
            }
        }

        var target = monitors[(currentIndex + 1 + monitors.Count) % monitors.Count];
        var sourceArea = current.WorkArea;
        var targetArea = target.WorkArea;
        var originalWidth = Math.Max(1, rect.Right - rect.Left);
        var originalHeight = Math.Max(1, rect.Bottom - rect.Top);
        var width = Math.Min(originalWidth, targetArea.Width);
        var height = Math.Min(originalHeight, targetArea.Height);
        var relativeX = sourceArea.Width <= originalWidth
            ? 0d
            : (rect.Left - sourceArea.Left) / (double)(sourceArea.Width - originalWidth);
        var relativeY = sourceArea.Height <= originalHeight
            ? 0d
            : (rect.Top - sourceArea.Top) / (double)(sourceArea.Height - originalHeight);
        var x = targetArea.Left + (int)Math.Round(Math.Clamp(relativeX, 0d, 1d) * (targetArea.Width - width));
        var y = targetArea.Top + (int)Math.Round(Math.Clamp(relativeY, 0d, 1d) * (targetArea.Height - height));

        _ = NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        SetWindowBounds(window, x, y, width, height);
    }

    private static void ToggleAlwaysOnTop(nint window)
    {
        var extendedStyle = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64();
        var currentlyTopmost = (extendedStyle & NativeMethods.WsExTopmost) != 0;
        if (!NativeMethods.SetWindowPos(
                window,
                currentlyTopmost ? NativeMethods.HwndNoTopmost : NativeMethods.HwndTopmost,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate))
        {
            ThrowLastWin32("Could not change the target window's always-on-top state.");
        }
    }

    private static void SetWindowBounds(nint window, int x, int y, int width, int height)
    {
        if (!NativeMethods.SetWindowPos(
                window,
                nint.Zero,
                x,
                y,
                width,
                height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate))
        {
            ThrowLastWin32("Could not move or resize the target window.");
        }
    }

    private static NativeMethods.Input CreateKeyboardInput(int virtualKey, bool keyUp)
    {
        var flags = keyUp ? NativeMethods.KeyeventfKeyup : 0u;
        if (IsExtendedVirtualKey(virtualKey))
        {
            flags |= NativeMethods.KeyeventfExtendedKey;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = checked((ushort)virtualKey),
                    Flags = flags
                }
            }
        };
    }

    private static NativeMethods.Input CreateUnicodeInput(char codeUnit, bool keyUp) =>
        new()
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    ScanCode = codeUnit,
                    Flags = NativeMethods.KeyeventfUnicode | (keyUp ? NativeMethods.KeyeventfKeyup : 0u)
                }
            }
        };

    private static NativeMethods.Input CreateMouseInput(uint flags, uint mouseData) =>
        new()
        {
            Type = NativeMethods.InputMouse,
            Data = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput
                {
                    MouseData = mouseData,
                    Flags = flags
                }
            }
        };

    private static void SendInputs(NativeMethods.Input[] inputs, string operation)
    {
        if (inputs.Length == 0)
        {
            return;
        }

        var sent = NativeMethods.SendInput(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<NativeMethods.Input>());
        if (sent != inputs.Length)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Windows accepted {sent} of {inputs.Length} events for {operation}.");
        }
    }

    private static void TrySendInputs(NativeMethods.Input[] inputs)
    {
        try
        {
            _ = NativeMethods.SendInput(
                checked((uint)inputs.Length),
                inputs,
                Marshal.SizeOf<NativeMethods.Input>());
        }
        catch
        {
            // This is a recovery path for a previous partial SendInput.
        }
    }

    private static void ValidateVirtualKey(int key)
    {
        if (key is <= 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "Virtual-key codes must fit in an unsigned byte.");
        }
    }

    private static void EnsureTargetIsForeground(nint targetWindow)
    {
        if (targetWindow == nint.Zero)
        {
            return;
        }
        if (!NativeMethods.IsWindow(targetWindow))
        {
            throw new InvalidOperationException("The captured input target window is no longer available.");
        }
        if (NativeMethods.GetForegroundWindow() == targetWindow)
        {
            return;
        }
        if (!NativeMethods.SetForegroundWindow(targetWindow) || NativeMethods.GetForegroundWindow() != targetWindow)
        {
            throw new InvalidOperationException("Windows did not restore the captured input target window.");
        }
    }

    private static void EnsureTargetRemainsForeground(nint targetWindow)
    {
        if (targetWindow != nint.Zero && NativeMethods.GetForegroundWindow() != targetWindow)
        {
            throw new InvalidOperationException("Text input stopped because the active window changed.");
        }
    }

    private static bool IsExtendedVirtualKey(int key) => key is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0x90 or 0x91 or
        VirtualKeyMediaNextTrack or VirtualKeyMediaPreviousTrack or VirtualKeyMediaStop or
        VirtualKeyMediaPlayPause or VirtualKeyVolumeMute or VirtualKeyVolumeDown or VirtualKeyVolumeUp;

    private static void ThrowLastWin32(string message) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), message);

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }
}
