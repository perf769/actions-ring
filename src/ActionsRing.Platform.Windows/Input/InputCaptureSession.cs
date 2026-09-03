namespace ActionsRing.Platform.Windows.Input;

public sealed record InputCaptureOptions
{
    public bool CaptureKeyboard { get; init; } = true;
    public bool CaptureMouseButtons { get; init; } = true;
    public bool CaptureMouseWheel { get; init; } = true;
    public bool CaptureInjectedInput { get; init; }
    public bool AllowModifierOnlyGesture { get; init; }
    public bool CancelOnEscape { get; init; } = true;
    public bool SuppressCapturedInput { get; init; } = true;
    public bool SuppressModifierKeys { get; init; } = true;
}

/// <summary>
/// Represents an exclusive settings capture. Await <see cref="Completion"/> or
/// cancel/dispose the session. Only one capture can be active per hook service.
/// </summary>
public sealed class InputCaptureSession : IDisposable, IAsyncDisposable
{
    private readonly GlobalInputHook _owner;
    private CancellationTokenRegistration _registration;
    private int _ended;

    internal InputCaptureSession(
        GlobalInputHook owner,
        InputCaptureOptions options,
        CancellationToken cancellationToken)
    {
        _owner = owner;
        Options = options;
        CompletionSource = new TaskCompletionSource<InputGesture>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationToken = cancellationToken;
    }

    public InputCaptureOptions Options { get; }
    public Task<InputGesture> Completion => CompletionSource.Task;
    public bool IsCompleted => Completion.IsCompleted;

    internal TaskCompletionSource<InputGesture> CompletionSource { get; }
    private CancellationToken CancellationToken { get; }
    internal int PendingModifierVirtualKey { get; private set; }
    internal InputModifiers PendingModifierModifiers { get; private set; }

    internal void RememberModifier(int virtualKey, InputModifiers modifiers)
    {
        PendingModifierVirtualKey = virtualKey;
        PendingModifierModifiers = modifiers;
    }

    internal bool TryCreateRememberedModifierGesture(int releasedVirtualKey, out InputGesture? gesture)
    {
        if (PendingModifierVirtualKey == releasedVirtualKey)
        {
            gesture = InputGesture.Keyboard(PendingModifierVirtualKey, PendingModifierModifiers);
            return true;
        }

        gesture = null;
        return false;
    }

    internal void ArmCancellation()
    {
        if (CancellationToken.CanBeCanceled)
        {
            var registration = CancellationToken.Register(
                static state => ((InputCaptureSession)state!).Cancel(),
                this);
            _registration = registration;
            if (IsCompleted)
            {
                registration.Dispose();
            }
        }
    }

    public void Cancel()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        _owner.EndCapture(this);
        CompletionSource.TrySetCanceled();
        _registration.Dispose();
    }

    internal bool TryComplete(InputGesture gesture)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return false;
        }

        _owner.EndCapture(this);
        var completed = CompletionSource.TrySetResult(gesture);
        _registration.Dispose();
        return completed;
    }

    public void Dispose() => Cancel();

    public ValueTask DisposeAsync()
    {
        Cancel();
        return ValueTask.CompletedTask;
    }
}
