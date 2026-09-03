using System.Windows;
using ActionsRing.App.Windows;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;
using ActionsRing.Platform.Windows;
using ActionsRing.Platform.Windows.Actions;
using ActionsRing.Platform.Windows.Display;
using ActionsRing.Platform.Windows.Foreground;
using ActionsRing.Platform.Windows.Input;
using CoreMouseButton = ActionsRing.Core.Domain.MouseButton;
using PlatformMouseButton = ActionsRing.Platform.Windows.Input.MouseButton;

namespace ActionsRing.App.Services;

public sealed class ControllerStatusEventArgs(string message, bool isError = false) : EventArgs
{
    public string Message { get; } = message;
    public bool IsError { get; } = isError;
}

public sealed class ActiveContextEventArgs(string applicationName, string profileName, bool isSpecific) : EventArgs
{
    public string ApplicationName { get; } = applicationName;
    public string ProfileName { get; } = profileName;
    public bool IsSpecific { get; } = isSpecific;
}

public sealed class AutostartApplyException(
    string message,
    Exception innerException,
    bool configurationPersisted)
    : InvalidOperationException(message, innerException)
{
    public bool ConfigurationPersisted { get; } = configurationPersisted;
}

/// <summary>Owns process-wide input, profile resolution, overlay lifetime, and action dispatch.</summary>
public sealed class ApplicationController : IAsyncDisposable
{
    private const string AutostartValueName = "Actions Ring";
    private readonly IConfigurationStore _store;
    private readonly GlobalInputHook _inputHook;
    private readonly IForegroundWindowResolver _foreground;
    private readonly IDisplayInfoService _display;
    private readonly IWindowsAutostartService _autostart;
    private readonly ActionExecutionService _actions;
    private readonly RingOverlayWindow _overlay;
    private readonly KeyStateOverlayWindow _keyStateOverlay;
    private readonly ThemeService _theme;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _pendingHoldCommit;
    private int _started;
    private int _disposed;

    public ApplicationController(
        ActionsRingConfiguration configuration,
        IConfigurationStore store,
        ThemeService theme,
        RingOverlayWindow? overlay = null,
        GlobalInputHook? inputHook = null,
        IForegroundWindowResolver? foreground = null,
        IDisplayInfoService? display = null,
        IWindowsAutostartService? autostart = null,
        IWindowsActionExecutor? actionExecutor = null)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _overlay = overlay ?? new RingOverlayWindow();
        _inputHook = inputHook ?? new GlobalInputHook();
        _foreground = foreground ?? new ForegroundWindowResolver();
        _display = display ?? new WindowsDisplayInfoService();
        _autostart = autostart ?? new WindowsAutostartService();
        _actions = new ActionExecutionService(actionExecutor ?? new WindowsActionExecutor(_display));
        _keyStateOverlay = new KeyStateOverlayWindow();

        _inputHook.ActivationTriggered += OnActivationTriggered;
        _inputHook.InputReceived += OnInputReceived;
        _inputHook.PlatformError += (_, args) => ReportError(args.Operation, args.Exception);
        _overlay.ActionRequested += OnOverlayActionRequested;
        _overlay.AdjustmentRequested += OnAdjustmentRequested;
    }

    public event EventHandler? ConfigurationChanged;
    public event EventHandler<ControllerStatusEventArgs>? StatusChanged;
    public event EventHandler<ActiveContextEventArgs>? ActiveContextChanged;

    public ActionsRingConfiguration Configuration { get; }
    public bool IsPaused { get; private set; }
    public bool IsOverlayVisible => _overlay.IsRingVisible;
    public string SettingsPath => _store.SettingsPath;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        ApplyRuntimeConfiguration();
        await ApplyInitialAutostartAsync(cancellationToken);
        await _inputHook.StartAsync(cancellationToken);
        RaiseStatus("Кольцо готово");
        AppLog.Info($"Input hooks started. Trigger: {InputBindingMapper.Describe(Configuration.Trigger)}");
    }

    public async Task SaveAndApplyAsync(bool updateAutostart = true, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Exception? autostartError = null;
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            // Resolve the platform gesture before touching the durable file so an
            // unsupported hand-edited key can never poison the next startup.
            _ = InputBindingMapper.ToPlatform(Configuration.Trigger);
            await _store.SaveAsync(Configuration, cancellationToken);
            ApplyRuntimeConfiguration();
            if (updateAutostart)
            {
                try
                {
                    ApplyAutostartPreference();
                }
                catch (Exception exception)
                {
                    SynchronizeAutostartPreference();
                    try
                    {
                        await _store.SaveAsync(Configuration, cancellationToken);
                        autostartError = new AutostartApplyException(
                            "Не удалось применить настройку автозапуска.",
                            exception,
                            configurationPersisted: true);
                    }
                    catch (Exception saveException)
                    {
                        autostartError = new AutostartApplyException(
                            "Не удалось применить и сохранить состояние автозапуска.",
                            new AggregateException(exception, saveException),
                            configurationPersisted: false);
                    }
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        if (autostartError is not null)
        {
            throw autostartError;
        }
        RaiseStatus("Изменения сохранены");
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        if (paused && _overlay.IsRingVisible)
        {
            Observe(_overlay.HideAnimatedAsync(), "Не удалось скрыть кольцо");
        }
        RaiseStatus(paused ? "Кольцо приостановлено" : "Кольцо снова активно");
    }

    public async Task<TriggerBinding> CaptureTriggerAsync(
        ActivationMode activationMode,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        RaiseStatus("Нажмите клавишу, сочетание или кнопку мыши…");
        await using var session = _inputHook.BeginCapture(
            new InputCaptureOptions
            {
                CaptureKeyboard = true,
                CaptureMouseButtons = true,
                CaptureMouseWheel = true,
                AllowModifierOnlyGesture = true,
                CancelOnEscape = true,
                SuppressCapturedInput = true,
            },
            cancellationToken);

        var gesture = await session.Completion;
        var binding = InputBindingMapper.ToCore(gesture, activationMode);
        var transaction = ConfigurationMutationTransaction.Capture(Configuration);
        Configuration.Trigger = binding;
        try
        {
            await SaveAndApplyAsync(updateAutostart: false, cancellationToken);
            transaction.Commit();
            return binding;
        }
        catch
        {
            transaction.Rollback();
            ApplyRuntimeConfiguration();
            throw;
        }
    }

    public async Task<KeyChord> CaptureShortcutChordAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        RaiseStatus("Нажмите сочетание клавиш…");
        await using var session = _inputHook.BeginCapture(
            new InputCaptureOptions
            {
                CaptureKeyboard = true,
                CaptureMouseButtons = false,
                CaptureMouseWheel = false,
                AllowModifierOnlyGesture = false,
                CancelOnEscape = true,
                SuppressCapturedInput = true,
                SuppressModifierKeys = true,
            },
            cancellationToken);
        return InputBindingMapper.ToCoreKeyChord(await session.Completion);
    }

    public Task ShowRingAsync() => DispatchAsync(ShowResolvedRing);

    public Task PreviewRingAsync(RingDefinition ring, RingStyleDefinition? style = null) =>
        DispatchAsync(() => ShowRing(ring, style, "Предпросмотр", nint.Zero, isPreview: true));

    public AutostartStatus GetAutostartStatus() =>
        _autostart.GetStatus(AutostartValueName, Environment.ProcessPath);

    internal void ReapplyRuntimeConfiguration()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ApplyRuntimeConfiguration();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        _inputHook.ActivationTriggered -= OnActivationTriggered;
        _inputHook.InputReceived -= OnInputReceived;
        _overlay.ActionRequested -= OnOverlayActionRequested;
        _overlay.AdjustmentRequested -= OnAdjustmentRequested;
        await _overlay.HideAnimatedAsync(animate: false);
        _keyStateOverlay.HideImmediately();
        await _inputHook.DisposeAsync();
        await _actionGate.WaitAsync();
        _actionGate.Release();
        await _saveGate.WaitAsync();
        _saveGate.Release();
        _shutdown.Dispose();
        _actionGate.Dispose();
        _saveGate.Dispose();
    }

    private void ApplyRuntimeConfiguration()
    {
        _theme.Apply(Configuration.Preferences.Appearance);
        _inputHook.ActivationGesture = InputBindingMapper.ToPlatform(Configuration.Trigger);
        _inputHook.SuppressActivationInput = true;
    }

    private async Task ApplyInitialAutostartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var executable = Environment.ProcessPath;
            var status = _autostart.GetStatus(AutostartValueName, executable);
            if (status.IsDisabledByWindows)
            {
                Configuration.Preferences.General.RunAtStartup = false;
                await _store.SaveAsync(Configuration, cancellationToken);
                ConfigurationChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            ApplyAutostartPreference();
        }
        catch (Exception exception)
        {
            SynchronizeAutostartPreference();
            try
            {
                await _store.SaveAsync(Configuration, cancellationToken);
            }
            catch (Exception saveException)
            {
                AppLog.Error("Could not persist the autostart state", saveException);
            }
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
            ReportError("Не удалось изменить автозапуск", exception);
        }
    }

    private void ApplyAutostartPreference()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Windows did not expose the executable path.");
        if (Configuration.Preferences.General.RunAtStartup)
        {
            _autostart.Enable(AutostartValueName, executable, "--background");
        }
        else
        {
            _autostart.Disable(AutostartValueName);
        }

        var status = _autostart.GetStatus(AutostartValueName, executable);
        var applied = Configuration.Preferences.General.RunAtStartup
            ? status.IsEnabled && status.MatchesExpectedExecutable
            : !status.IsEnabled;
        if (!applied)
        {
            throw new InvalidOperationException("Windows did not retain the requested autostart state.");
        }
    }

    private void SynchronizeAutostartPreference()
    {
        try
        {
            var executable = Environment.ProcessPath;
            var status = _autostart.GetStatus(AutostartValueName, executable);
            Configuration.Preferences.General.RunAtStartup = status.IsEnabled && status.MatchesExpectedExecutable;
        }
        catch
        {
            Configuration.Preferences.General.RunAtStartup = false;
        }
    }

    private void OnActivationTriggered(object? sender, ActivationInputEventArgs args)
    {
        if (IsPaused || _shutdown.IsCancellationRequested)
        {
            return;
        }
        Observe(
            DispatchAsync(async () =>
            {
                if (_overlay.IsRingVisible)
                {
                    if (Configuration.Trigger.ActivationMode == ActivationMode.Toggle)
                    {
                        if (_overlay.HoveredSlot is not null)
                        {
                            await _overlay.CommitHoveredAsync();
                        }
                        else
                        {
                            await _overlay.HideAnimatedAsync();
                        }
                    }
                    return;
                }
                ShowResolvedRing();
            }),
            "Не удалось переключить кольцо");
    }

    private void OnInputReceived(object? sender, GlobalInputEventArgs args)
    {
        if (args.Input.Device == InputDeviceKind.Keyboard
            && args.Input.Kind == InputEventKind.KeyUp
            && TryGetKeyStateIndicator(args.Input.VirtualKey, out var indicator))
        {
            Observe(ShowKeyStateAsync(indicator), "Не удалось показать состояние клавиши");
        }

        if (args.Input.IsInjected)
        {
            return;
        }
        if (Configuration.Trigger.ActivationMode != ActivationMode.Hold)
        {
            Interlocked.Exchange(ref _pendingHoldCommit, 0);
            return;
        }

        var isTriggerRelease = IsTriggerRelease(args.Input, Configuration.Trigger);
        if (!isTriggerRelease && Volatile.Read(ref _pendingHoldCommit) == 0)
        {
            return;
        }

        var requiredModifiers = GetTriggerModifiers(Configuration.Trigger);
        if ((args.Input.Modifiers & requiredModifiers) != 0)
        {
            if (isTriggerRelease)
            {
                Interlocked.Exchange(ref _pendingHoldCommit, 1);
            }
            return;
        }

        Interlocked.Exchange(ref _pendingHoldCommit, 0);
        Observe(
            DispatchAsync(async () =>
            {
                if (_overlay.IsRingVisible)
                {
                    await _overlay.CommitHoveredAsync();
                }
            }),
            "Не удалось завершить выбор");
    }

    private Task ShowKeyStateAsync(KeyStateIndicator indicator)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        return DispatchAsync(async () =>
        {
            if (_shutdown.IsCancellationRequested
                || !Configuration.Preferences.General.ShowKeyStateNotifications)
            {
                return;
            }

            var appearance = Configuration.Preferences.Appearance;
            var cursor = _display.GetCursorPosition();
            var monitor = _display.GetMonitorFromPoint(cursor);
            await _keyStateOverlay.ShowStateAsync(
                indicator,
                KeyStateOverlayWindow.ReadState(indicator),
                monitor,
                appearance.EnableAnimations,
                appearance.ReduceMotion);
        });
    }

    private static bool TryGetKeyStateIndicator(int virtualKey, out KeyStateIndicator indicator)
    {
        indicator = virtualKey switch
        {
            0x14 => KeyStateIndicator.CapsLock,
            0x90 => KeyStateIndicator.NumLock,
            0x91 => KeyStateIndicator.ScrollLock,
            _ => default,
        };
        return virtualKey is 0x14 or 0x90 or 0x91;
    }

    private void ShowResolvedRing()
    {
        Interlocked.Exchange(ref _pendingHoldCommit, 0);
        var active = _foreground.GetActiveWindow();
        var isDesktop = active is not null && IsDesktopWindowClass(active.WindowClass);
        var foreground = active is null || isDesktop
            ? null
            : new ForegroundApplication(active.ProcessName, active.ExecutablePath, active.WindowTitle);
        var selection = ProfileResolver.Resolve(Configuration, foreground);
        var style = selection.ApplicationProfile?.Style ?? Configuration.GlobalProfile.Style;
        var actionTargetWindow = isDesktop ? nint.Zero : active?.WindowHandle ?? nint.Zero;
        var appName = isDesktop
            ? "Рабочий стол"
            : active?.ProductName ?? active?.ProcessName ?? "Рабочий стол";
        ShowRing(selection.RootRing, style, selection.Name, actionTargetWindow);
        ActiveContextChanged?.Invoke(
            this,
            new ActiveContextEventArgs(appName, selection.Name, selection.IsApplicationSpecific));
    }

    private void ShowRing(
        RingDefinition ring,
        RingStyleDefinition? style,
        string profileName,
        nint targetWindow,
        bool isPreview = false)
    {
        var cursor = _display.GetCursorPosition();
        var monitor = _display.GetMonitorFromPoint(cursor);
        var appearance = Configuration.Preferences.Appearance;
        var scale = AdaptiveRingScaleCalculator.Calculate(appearance, monitor);
        _overlay.ShowAt(
            cursor,
            monitor,
            ring,
            style,
            scale,
            appearance,
            targetWindow,
            isPreview);
        RaiseStatus($"Профиль: {profileName}");
    }

    private async void OnOverlayActionRequested(object? sender, OverlayActionEventArgs args)
    {
        if (args.IsPreview)
        {
            RaiseStatus($"Предпросмотр: {args.Slot.Label}");
            return;
        }

        var entered = false;
        try
        {
            await _actionGate.WaitAsync(_shutdown.Token);
            entered = true;
            if (args.Slot.Action is not null)
            {
                await _actions.ExecuteAsync(args.Slot.Action, args.TargetWindow, _shutdown.Token);
                RaiseStatus($"Выполнено: {args.Slot.Label}");
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            ReportError($"Ошибка действия «{args.Slot.Label}»", exception);
        }
        finally
        {
            if (entered)
            {
                _actionGate.Release();
            }
        }
    }

    private async void OnAdjustmentRequested(object? sender, int direction)
    {
        var action = _overlay.HoveredSlot?.Action;
        if (action is null)
        {
            return;
        }
        var targetWindow = _overlay.ActionTargetWindow;
        if (_overlay.IsPreview)
        {
            return;
        }
        var entered = false;
        try
        {
            await _actionGate.WaitAsync(_shutdown.Token);
            entered = true;
            await _actions.AdjustAsync(action, direction, targetWindow, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportError("Не удалось изменить параметр", exception);
        }
        finally
        {
            if (entered)
            {
                _actionGate.Release();
            }
        }
    }

    private static bool IsDesktopWindowClass(string windowClass) =>
        string.Equals(windowClass, "Progman", StringComparison.OrdinalIgnoreCase)
        || string.Equals(windowClass, "WorkerW", StringComparison.OrdinalIgnoreCase);

    private static bool IsTriggerRelease(GlobalInputEvent input, TriggerBinding binding)
    {
        if (binding.Kind == InputBindingKind.Keyboard)
        {
            return input.Kind == InputEventKind.KeyUp
                   && input.VirtualKey == InputBindingMapper.KeyNameToVirtualKey(binding.Keyboard?.Key ?? string.Empty);
        }

        if (binding.Button is CoreMouseButton.WheelUp or CoreMouseButton.WheelDown or CoreMouseButton.WheelLeft or CoreMouseButton.WheelRight)
        {
            return false;
        }
        var expected = binding.Button switch
        {
            CoreMouseButton.Left => PlatformMouseButton.Left,
            CoreMouseButton.Right => PlatformMouseButton.Right,
            CoreMouseButton.Middle => PlatformMouseButton.Middle,
            CoreMouseButton.XButton1 => PlatformMouseButton.XButton1,
            CoreMouseButton.XButton2 => PlatformMouseButton.XButton2,
            _ => PlatformMouseButton.None,
        };
        return input.Kind == InputEventKind.MouseButtonUp && input.MouseButton == expected;
    }

    private static InputModifiers GetTriggerModifiers(TriggerBinding binding)
    {
        var modifiers = binding.Kind == InputBindingKind.Keyboard
            ? binding.Keyboard?.Modifiers ?? KeyboardModifiers.None
            : binding.MouseModifiers;
        var result = InputModifiers.None;
        if (modifiers.HasFlag(KeyboardModifiers.Control)) result |= InputModifiers.Control;
        if (modifiers.HasFlag(KeyboardModifiers.Shift)) result |= InputModifiers.Shift;
        return result;
    }

    private Task DispatchAsync(Action action)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Application.Current.Dispatcher.InvokeAsync(action).Task;
    }

    private Task DispatchAsync(Func<Task> action)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            return action();
        }
        return Application.Current.Dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private void RaiseStatus(string message, bool isError = false)
    {
        void Raise() => StatusChanged?.Invoke(this, new ControllerStatusEventArgs(message, isError));
        if (Application.Current.Dispatcher.CheckAccess()) Raise();
        else _ = Application.Current.Dispatcher.BeginInvoke(Raise);
    }

    private void ReportError(string operation, Exception exception)
    {
        AppLog.Error(operation, exception);
        RaiseStatus(operation, isError: true);
    }

    private void Observe(Task task, string operation)
    {
        _ = ObserveCoreAsync(task, operation);
    }

    private async Task ObserveCoreAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            ReportError(operation, exception);
        }
    }
}
