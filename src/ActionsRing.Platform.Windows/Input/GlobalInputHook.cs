using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ActionsRing.Platform.Windows.Interop;

namespace ActionsRing.Platform.Windows.Input;

/// <summary>
/// Installs WH_KEYBOARD_LL and WH_MOUSE_LL on a dedicated message-loop thread.
/// Suppression decisions are made synchronously; public events are dispatched on
/// the thread pool so slow consumers cannot stall the Windows input hook.
/// </summary>
public sealed class GlobalInputHook : IGlobalInputHook
{
    private readonly object _captureGate = new();
    private readonly object _inputStateGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly HashSet<int> _pressedKeys = [];
    private readonly HashSet<int> _suppressedKeys = [];
    private readonly HashSet<int> _pendingReleaseActivationKeys = [];
    private readonly HashSet<MouseButton> _suppressedButtons = [];
    private readonly HashSet<MouseButton> _pendingReleaseActivationButtons = [];
    private readonly ConcurrentQueue<Action> _notificationQueue = new();
    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly NativeMethods.HookProc _mouseCallback;

    private InputCaptureSession? _captureSession;
    private InputGesture? _activationGesture;
    private Thread? _hookThread;
    private TaskCompletionSource<object?>? _started;
    private TaskCompletionSource<object?>? _stopped;
    private uint _hookThreadId;
    private int _running;
    private int _disposed;
    private bool _suppressActivationInput;
    private bool _ignoreInjectedInputForActivation = true;
    private int _notificationDrainScheduled;

    public GlobalInputHook()
    {
        _keyboardCallback = KeyboardHookCallback;
        _mouseCallback = MouseHookCallback;
    }

    public event EventHandler<GlobalInputEventArgs>? InputReceived;
    public event EventHandler<ActivationInputEventArgs>? ActivationTriggered;
    public event EventHandler<PlatformErrorEventArgs>? PlatformError;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public InputGesture? ActivationGesture
    {
        get => Volatile.Read(ref _activationGesture);
        set => Volatile.Write(ref _activationGesture, value);
    }

    public bool SuppressActivationInput
    {
        get => Volatile.Read(ref _suppressActivationInput);
        set => Volatile.Write(ref _suppressActivationInput, value);
    }

    /// <summary>
    /// Injected input is still published through InputReceived, but is deliberately
    /// excluded from activation matching to prevent SendInput feedback loops.
    /// </summary>
    public bool IgnoreInjectedInputForActivation
    {
        get => Volatile.Read(ref _ignoreInjectedInputForActivation);
        set => Volatile.Write(ref _ignoreInjectedInputForActivation, value);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsRunning)
            {
                return;
            }

            _started = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopped = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "Actions Ring global input hooks"
            };
            _hookThread.Start();

            try
            {
                await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                RequestHookThreadStop();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopped = _stopped;
            if (stopped is null || (!IsRunning && stopped.Task.IsCompleted))
            {
                return;
            }

            CancelActiveCapture();
            RequestHookThreadStop();

            // A callback is allowed to request shutdown without deadlocking itself.
            if (_hookThreadId == NativeMethods.GetCurrentThreadId())
            {
                return;
            }

            await stopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public InputCaptureSession BeginCapture(
        InputCaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lifecycleGate.Wait(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!IsRunning)
            {
                throw new InvalidOperationException("The global input hook must be running before capture begins.");
            }

            var session = new InputCaptureSession(this, options ?? new InputCaptureOptions(), cancellationToken);
            lock (_captureGate)
            {
                if (_captureSession is { IsCompleted: false })
                {
                    throw new InvalidOperationException("An input capture session is already active.");
                }

                _captureSession = session;
            }

            session.ArmCancellation();
            return session;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal void EndCapture(InputCaptureSession session)
    {
        lock (_captureGate)
        {
            if (ReferenceEquals(_captureSession, session))
            {
                _captureSession = null;
            }
        }
    }

    private void HookThreadMain()
    {
        SafeWindowsHookHandle? keyboardHook = null;
        SafeWindowsHookHandle? mouseHook = null;

        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();

            // Force creation of this thread's Win32 message queue before StartAsync
            // is released; PostThreadMessage is then race-free.
            _ = NativeMethods.PeekMessage(out _, nint.Zero, 0, 0, NativeMethods.PmNoRemove);

            var module = NativeMethods.GetModuleHandle(null);
            InitializeModifierState();
            keyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhKeyboardLl,
                _keyboardCallback,
                module,
                0);

            if (keyboardHook.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");
            }

            mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhMouseLl,
                _mouseCallback,
                module,
                0);

            if (mouseHook.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the mouse hook.");
            }

            Volatile.Write(ref _running, 1);
            _started?.TrySetResult(null);

            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, nint.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result < 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The input hook message loop failed.");
                }

                _ = NativeMethods.TranslateMessage(in message);
                _ = NativeMethods.DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            _started?.TrySetException(exception);
            QueueError("Global input hook thread", exception);
        }
        finally
        {
            mouseHook?.Dispose();
            keyboardHook?.Dispose();
            CancelActiveCapture();

            lock (_inputStateGate)
            {
                _pressedKeys.Clear();
                _suppressedKeys.Clear();
                _pendingReleaseActivationKeys.Clear();
                _suppressedButtons.Clear();
                _pendingReleaseActivationButtons.Clear();
            }

            Volatile.Write(ref _running, 0);
            _hookThreadId = 0;
            _stopped?.TrySetResult(null);
        }
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code < NativeMethods.HcAction)
        {
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        try
        {
            var message = unchecked((uint)(nuint)wParam);
            var isDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            var isUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
            if (!isDown && !isUp)
            {
                return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
            }

            var data = Marshal.PtrToStructure<NativeMethods.KeyboardLowLevelHookData>(lParam);
            var virtualKey = checked((int)data.VirtualKey);
            var isInjected = (data.Flags & NativeMethods.LlkhfInjected) != 0;
            bool isRepeat;
            bool isTrackedForSuppression;
            bool isPendingReleaseActivation = false;
            InputModifiers modifiers;

            lock (_inputStateGate)
            {
                isRepeat = !isInjected && isDown && _pressedKeys.Contains(virtualKey);
                isTrackedForSuppression = !isInjected && _suppressedKeys.Contains(virtualKey);
                if (!isInjected && isDown)
                {
                    _pressedKeys.Add(virtualKey);
                }
                else if (!isInjected)
                {
                    _pressedKeys.Remove(virtualKey);
                }

                if (!isInjected && isUp)
                {
                    isTrackedForSuppression = _suppressedKeys.Remove(virtualKey);
                    isPendingReleaseActivation = _pendingReleaseActivationKeys.Remove(virtualKey);
                }

                modifiers = GetModifiersExcept(virtualKey);
            }

            _ = NativeMethods.GetCursorPos(out var point);
            var input = new GlobalInputEvent(
                InputDeviceKind.Keyboard,
                isDown ? InputEventKind.KeyDown : InputEventKind.KeyUp,
                DateTimeOffset.UtcNow,
                new ScreenPoint(point.X, point.Y),
                modifiers,
                virtualKey,
                data.ScanCode,
                IsExtended: (data.Flags & NativeMethods.LlkhfExtended) != 0,
                IsInjected: isInjected,
                IsRepeat: isRepeat);

            if (isTrackedForSuppression)
            {
                if (isPendingReleaseActivation || ShouldProcessSuppressedCaptureRelease(input))
                {
                    _ = ProcessInput(input);
                }
                else
                {
                    QueueInput(input);
                }

                return new nint(1);
            }

            var suppress = ProcessInput(input);

            return suppress
                ? new nint(1)
                : NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }
        catch (Exception exception)
        {
            QueueError("Keyboard hook callback", exception);
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        if (code < NativeMethods.HcAction)
        {
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        try
        {
            var message = unchecked((uint)(nuint)wParam);
            if (!TryMapMouseMessage(message, lParam, out var kind, out var button, out var wheelDelta, out var data))
            {
                return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
            }

            InputModifiers modifiers;
            lock (_inputStateGate)
            {
                modifiers = GetModifiersExcept(0);
            }

            var input = new GlobalInputEvent(
                InputDeviceKind.Mouse,
                kind,
                DateTimeOffset.UtcNow,
                new ScreenPoint(data.Point.X, data.Point.Y),
                modifiers,
                MouseButton: button,
                WheelDelta: wheelDelta,
                IsInjected: (data.Flags & NativeMethods.LlmhfInjected) != 0);
            var isInjected = input.IsInjected;

            var isTrackedForSuppression = false;
            var isPendingReleaseActivation = false;
            if (!isInjected && kind is (InputEventKind.MouseButtonDown or InputEventKind.MouseButtonUp))
            {
                lock (_inputStateGate)
                {
                    isTrackedForSuppression = _suppressedButtons.Contains(button);
                    if (kind == InputEventKind.MouseButtonUp)
                    {
                        isTrackedForSuppression = _suppressedButtons.Remove(button);
                        isPendingReleaseActivation = _pendingReleaseActivationButtons.Remove(button);
                    }
                }
            }

            if (isTrackedForSuppression)
            {
                if (isPendingReleaseActivation)
                {
                    _ = ProcessInput(input);
                }
                else
                {
                    QueueInput(input);
                }

                return new nint(1);
            }

            var suppress = ProcessInput(input);

            return suppress
                ? new nint(1)
                : NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }
        catch (Exception exception)
        {
            QueueError("Mouse hook callback", exception);
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }
    }

    private bool ProcessInput(GlobalInputEvent input)
    {
        var suppress = false;
        InputCaptureSession? capture;
        lock (_captureGate)
        {
            capture = _captureSession;
        }

        if (capture is { IsCompleted: false })
        {
            suppress = TryCompleteCapture(capture, input);
        }
        else
        {
            var gesture = Volatile.Read(ref _activationGesture);
            if (gesture is not null && (!IgnoreInjectedInputForActivation || !input.IsInjected))
            {
                var shouldSuppress = Volatile.Read(ref _suppressActivationInput);

                if (shouldSuppress && IsReleasePrelude(gesture, input))
                {
                    TrackSuppressedPress(input, releaseActivationPending: true);
                    suppress = true;
                }

                if (gesture.Matches(input))
                {
                    QueueActivation(gesture, input);
                    if (shouldSuppress)
                    {
                        TrackSuppressedPress(input);
                        suppress = true;
                    }
                }
            }
        }

        QueueInput(input);
        return suppress;
    }

    private bool TryCompleteCapture(InputCaptureSession session, GlobalInputEvent input)
    {
        var options = session.Options;
        if (input.IsInjected && !options.CaptureInjectedInput)
        {
            return false;
        }

        InputGesture? gesture = null;
        var isCancel = false;
        var completedByModifierRelease = false;

        if (input.Device == InputDeviceKind.Keyboard && input.Kind == InputEventKind.KeyDown)
        {
            if (options.CancelOnEscape && input.VirtualKey == NativeMethods.VkEscape)
            {
                isCancel = true;
            }
            else if (options.CaptureKeyboard
                     && options.AllowModifierOnlyGesture
                     && IsModifier(input.VirtualKey))
            {
                session.RememberModifier(input.VirtualKey, input.Modifiers);
                if (options.SuppressCapturedInput && options.SuppressModifierKeys)
                {
                    TrackSuppressedPress(input);
                    return true;
                }
            }
            else if (options.CaptureKeyboard && !IsModifier(input.VirtualKey))
            {
                gesture = InputGesture.Keyboard(input.VirtualKey, input.Modifiers);
            }
            else if (options.CaptureKeyboard
                     && options.SuppressCapturedInput
                     && options.SuppressModifierKeys
                     && IsModifier(input.VirtualKey))
            {
                TrackSuppressedPress(input);
                return true;
            }
        }
        else if (input.Device == InputDeviceKind.Keyboard
                 && input.Kind == InputEventKind.KeyUp
                 && options.CaptureKeyboard
                 && options.AllowModifierOnlyGesture
                 && IsModifier(input.VirtualKey)
                 && session.TryCreateRememberedModifierGesture(input.VirtualKey, out var remembered))
        {
            gesture = remembered;
            completedByModifierRelease = true;
        }
        else if (input.Device == InputDeviceKind.Mouse &&
                 input.Kind == InputEventKind.MouseButtonDown &&
                 options.CaptureMouseButtons)
        {
            gesture = InputGesture.Mouse(input.MouseButton, input.Modifiers);
        }
        else if (input.Device == InputDeviceKind.Mouse &&
                 input.Kind is InputEventKind.MouseWheel or InputEventKind.MouseHorizontalWheel &&
                 options.CaptureMouseWheel)
        {
            gesture = InputGesture.Wheel(input.WheelDirection, input.Modifiers);
        }

        if (isCancel)
        {
            session.Cancel();
            if (options.SuppressCapturedInput)
            {
                TrackSuppressedPress(input);
            }

            return options.SuppressCapturedInput;
        }

        if (gesture is null || !session.TryComplete(gesture))
        {
            return false;
        }

        if (options.SuppressCapturedInput)
        {
            TrackSuppressedPress(input);
        }

        return options.SuppressCapturedInput
               && (!completedByModifierRelease || options.SuppressModifierKeys);
    }

    private bool ShouldProcessSuppressedCaptureRelease(GlobalInputEvent input)
    {
        if (input.Device != InputDeviceKind.Keyboard
            || input.Kind != InputEventKind.KeyUp
            || !IsModifier(input.VirtualKey))
        {
            return false;
        }

        lock (_captureGate)
        {
            return _captureSession is { IsCompleted: false } capture
                   && capture.Options.AllowModifierOnlyGesture
                   && capture.PendingModifierVirtualKey == input.VirtualKey;
        }
    }

    private static bool IsReleasePrelude(InputGesture gesture, GlobalInputEvent input)
    {
        if (gesture.Edge != InputTriggerEdge.Released)
        {
            return false;
        }

        if (gesture.Device == InputDeviceKind.Keyboard)
        {
            return input.Kind == InputEventKind.KeyDown &&
                   input.VirtualKey == gesture.VirtualKey &&
                   (gesture.AllowAdditionalModifiers
                       ? (input.Modifiers & gesture.Modifiers) == gesture.Modifiers
                       : input.Modifiers == gesture.Modifiers);
        }

        return gesture.WheelDirection == MouseWheelDirection.None &&
               input.Kind == InputEventKind.MouseButtonDown &&
               input.MouseButton == gesture.MouseButton &&
               (gesture.AllowAdditionalModifiers
                   ? (input.Modifiers & gesture.Modifiers) == gesture.Modifiers
                   : input.Modifiers == gesture.Modifiers);
    }

    private void TrackSuppressedPress(GlobalInputEvent input, bool releaseActivationPending = false)
    {
        lock (_inputStateGate)
        {
            if (input.Kind == InputEventKind.KeyDown)
            {
                _suppressedKeys.Add(input.VirtualKey);
                if (releaseActivationPending)
                {
                    _pendingReleaseActivationKeys.Add(input.VirtualKey);
                }
            }
            else if (input.Kind == InputEventKind.MouseButtonDown)
            {
                _suppressedButtons.Add(input.MouseButton);
                if (releaseActivationPending)
                {
                    _pendingReleaseActivationButtons.Add(input.MouseButton);
                }
            }
        }
    }

    private InputModifiers GetModifiersExcept(int excludedVirtualKey)
    {
        var modifiers = InputModifiers.None;
        foreach (var key in _pressedKeys)
        {
            if (key == excludedVirtualKey)
            {
                continue;
            }

            modifiers |= GetModifier(key);
        }

        return modifiers;
    }

    private static InputModifiers GetModifier(int virtualKey) => virtualKey switch
    {
        NativeMethods.VkShift or NativeMethods.VkLShift or NativeMethods.VkRShift => InputModifiers.Shift,
        NativeMethods.VkControl or NativeMethods.VkLControl or NativeMethods.VkRControl => InputModifiers.Control,
        NativeMethods.VkMenu or NativeMethods.VkLMenu or NativeMethods.VkRMenu => InputModifiers.Alt,
        NativeMethods.VkLWin or NativeMethods.VkRWin => InputModifiers.Windows,
        _ => InputModifiers.None
    };

    private static bool IsModifier(int virtualKey) => GetModifier(virtualKey) != InputModifiers.None;

    private void InitializeModifierState()
    {
        lock (_inputStateGate)
        {
            _pressedKeys.Clear();
            foreach (var key in new[]
                     {
                         NativeMethods.VkLShift,
                         NativeMethods.VkRShift,
                         NativeMethods.VkLControl,
                         NativeMethods.VkRControl,
                         NativeMethods.VkLMenu,
                         NativeMethods.VkRMenu,
                         NativeMethods.VkLWin,
                         NativeMethods.VkRWin
                     })
            {
                if ((NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0)
                {
                    _pressedKeys.Add(key);
                }
            }
        }
    }

    private static bool TryMapMouseMessage(
        uint message,
        nint lParam,
        out InputEventKind kind,
        out MouseButton button,
        out int wheelDelta,
        out NativeMethods.MouseLowLevelHookData data)
    {
        data = Marshal.PtrToStructure<NativeMethods.MouseLowLevelHookData>(lParam);
        button = MouseButton.None;
        wheelDelta = 0;

        switch (message)
        {
            case NativeMethods.WmMouseMove:
                kind = InputEventKind.MouseMove;
                return false;
            case NativeMethods.WmLButtonDown:
                kind = InputEventKind.MouseButtonDown;
                button = MouseButton.Left;
                return true;
            case NativeMethods.WmLButtonUp:
                kind = InputEventKind.MouseButtonUp;
                button = MouseButton.Left;
                return true;
            case NativeMethods.WmRButtonDown:
                kind = InputEventKind.MouseButtonDown;
                button = MouseButton.Right;
                return true;
            case NativeMethods.WmRButtonUp:
                kind = InputEventKind.MouseButtonUp;
                button = MouseButton.Right;
                return true;
            case NativeMethods.WmMButtonDown:
                kind = InputEventKind.MouseButtonDown;
                button = MouseButton.Middle;
                return true;
            case NativeMethods.WmMButtonUp:
                kind = InputEventKind.MouseButtonUp;
                button = MouseButton.Middle;
                return true;
            case NativeMethods.WmXButtonDown:
                kind = InputEventKind.MouseButtonDown;
                button = MapXButton(HighWord(data.MouseData));
                return button != MouseButton.None;
            case NativeMethods.WmXButtonUp:
                kind = InputEventKind.MouseButtonUp;
                button = MapXButton(HighWord(data.MouseData));
                return button != MouseButton.None;
            case NativeMethods.WmMouseWheel:
                kind = InputEventKind.MouseWheel;
                wheelDelta = SignedHighWord(data.MouseData);
                return true;
            case NativeMethods.WmMouseHWheel:
                kind = InputEventKind.MouseHorizontalWheel;
                wheelDelta = SignedHighWord(data.MouseData);
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static uint HighWord(uint value) => (value >> 16) & 0xffff;
    private static int SignedHighWord(uint value) => unchecked((short)HighWord(value));
    private static MouseButton MapXButton(uint value) => value switch
    {
        NativeMethods.XButton1 => MouseButton.XButton1,
        NativeMethods.XButton2 => MouseButton.XButton2,
        _ => MouseButton.None
    };

    private void RequestHookThreadStop()
    {
        var threadId = Volatile.Read(ref _hookThreadId);
        if (threadId == 0)
        {
            return;
        }

        if (!NativeMethods.PostThreadMessage(threadId, NativeMethods.WmQuit, nint.Zero, nint.Zero))
        {
            QueueError(
                "Stop global input hook",
                new Win32Exception(Marshal.GetLastWin32Error(), "Could not post WM_QUIT to the hook thread."));
        }
    }

    private void CancelActiveCapture()
    {
        InputCaptureSession? capture;
        lock (_captureGate)
        {
            capture = _captureSession;
        }

        capture?.Cancel();
    }

    private void QueueInput(GlobalInputEvent input) =>
        QueueHandlers(InputReceived, new GlobalInputEventArgs(input), "InputReceived handler");

    private void QueueActivation(InputGesture gesture, GlobalInputEvent input) =>
        QueueHandlers(
            ActivationTriggered,
            new ActivationInputEventArgs(gesture, input),
            "ActivationTriggered handler");

    private void QueueHandlers<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs args,
        string operation)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        EnqueueNotification(() =>
        {
            foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception exception)
                {
                    QueueError(operation, exception);
                }
            }
        });
    }

    private void QueueError(string operation, Exception exception)
    {
        var handlers = PlatformError;
        if (handlers is null)
        {
            return;
        }

        EnqueueNotification(() =>
        {
            var args = new PlatformErrorEventArgs(operation, exception);
            foreach (EventHandler<PlatformErrorEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch
                {
                    // Platform-error observers must never destabilize the native callback path.
                }
            }
        });
    }

    private void EnqueueNotification(Action notification)
    {
        _notificationQueue.Enqueue(notification);
        if (Interlocked.Exchange(ref _notificationDrainScheduled, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(static state => ((GlobalInputHook)state!).DrainNotifications(), this);
        }
    }

    private void DrainNotifications()
    {
        while (true)
        {
            while (_notificationQueue.TryDequeue(out var notification))
            {
                try
                {
                    notification();
                }
                catch
                {
                    // Every notification already contains its own error boundary.
                }
            }

            Volatile.Write(ref _notificationDrainScheduled, 0);
            if (_notificationQueue.IsEmpty || Interlocked.Exchange(ref _notificationDrainScheduled, 1) != 0)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelActiveCapture();
        StopAsync().GetAwaiter().GetResult();
        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelActiveCapture();
        await StopAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
