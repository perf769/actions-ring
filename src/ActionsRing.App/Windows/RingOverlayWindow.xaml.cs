using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ActionsRing.App.Controls;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;
using ActionsRing.Platform.Windows.Display;
using ActionsRing.Platform.Windows.Input;

namespace ActionsRing.App.Windows;

public sealed class OverlayActionEventArgs(
    RingSlotDefinition slot,
    nint targetWindow,
    bool isPreview) : EventArgs
{
    public RingSlotDefinition Slot { get; } = slot;
    public nint TargetWindow { get; } = targetWindow;
    public bool IsPreview { get; } = isPreview;
}

public partial class RingOverlayWindow : Window
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);
    private bool _closingAnimation;
    private bool _effectiveAnimations = true;
    private long _presentationVersion;
    private nint _actionTargetWindow;
    private bool _isPreview;

    public RingOverlayWindow()
    {
        InitializeComponent();
        Ring.InteractionMode = RingInteractionMode.Execute;
        Ring.SlotInvoked += OnSlotInvoked;
        Ring.CloseRequested += async (_, _) => await HideAnimatedAsync();
        Ring.AdjustmentRequested += (_, delta) => AdjustmentRequested?.Invoke(this, delta);
        SourceInitialized += OnSourceInitialized;
    }

    public event EventHandler<OverlayActionEventArgs>? ActionRequested;
    public event EventHandler<int>? AdjustmentRequested;
    public event EventHandler? OverlayHidden;

    public bool IsRingVisible => IsVisible && !_closingAnimation;
    public nint ActionTargetWindow => _actionTargetWindow;
    public bool IsPreview => _isPreview;
    public RingSlotDefinition? HoveredSlot => Ring.HoveredSlot;

    public void ShowAt(
        ScreenPoint cursor,
        MonitorSnapshot monitor,
        RingDefinition definition,
        RingStyleDefinition? style,
        double scale,
        AppearancePreferences appearance,
        nint actionTargetWindow,
        bool isPreview = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Interlocked.Increment(ref _presentationVersion);
        _closingAnimation = false;
        _actionTargetWindow = actionTargetWindow;
        _isPreview = isPreview;
        _effectiveAnimations = appearance.EnableAnimations;

        // A reusable transparent HWND can retain its previous compositor surface for one frame.
        // Remove and hide that surface before moving the window, including when a close animation
        // is being interrupted by a new invocation.
        OverlayRoot.Visibility = Visibility.Hidden;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Ring.Clear();
        if (IsVisible)
        {
            Hide();
        }

        Ring.RingScale = scale;
        Ring.AnimationsEnabled = _effectiveAnimations;
        Ring.ReduceMotion = appearance.EnableAnimations && appearance.ReduceMotion;
        Ring.OpenAnimationMilliseconds = appearance.ReduceMotion ? 90 : appearance.OpenAnimationMilliseconds;
        Ring.SubmenuAnimationMilliseconds = appearance.ReduceMotion ? 90 : appearance.SubmenuAnimationMilliseconds;
        Ring.CenterDiameter = appearance.CenterCloseDiameter;
        Ring.ShowTooltips = appearance.ShowTooltips;
        Ring.TooltipDelayMilliseconds = appearance.TooltipDelayMilliseconds;
        Ring.ApplyStyle(style);

        var handle = new WindowInteropHelper(this).EnsureHandle();
        PositionWindow(handle, monitor, show: false);
        Show();
        // WPF may apply its initial placement during the first Show call, so reaffirm the exact
        // physical monitor rectangle while the root remains invisible.
        PositionWindow(handle, monitor, show: true);

        UpdateLayout();
        var center = PointFromScreen(new Point(cursor.X, cursor.Y));
        var workAreaTopLeft = PointFromScreen(new Point(monitor.WorkArea.Left, monitor.WorkArea.Top));
        var workAreaBottomRight = PointFromScreen(new Point(monitor.WorkArea.Right, monitor.WorkArea.Bottom));
        Ring.TooltipBounds = new Rect(workAreaTopLeft, workAreaBottomRight);
        Ring.RingCenter = center;
        Ring.Present(definition, _effectiveAnimations);
        Ring.UpdateLayout();
        OverlayRoot.Visibility = Visibility.Visible;
        Opacity = 1;
        Ring.IsHitTestVisible = true;
    }

    private static void PositionWindow(nint handle, MonitorSnapshot monitor, bool show)
    {
        var flags = SwpNoActivate | (show ? SwpShowWindow : 0);
        if (!SetWindowPos(
                handle,
                HwndTopmost,
                monitor.Bounds.X,
                monitor.Bounds.Y,
                monitor.Bounds.Width,
                monitor.Bounds.Height,
                flags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not position the Actions Ring overlay.");
        }
    }

    public async Task<bool> HideAnimatedAsync(bool? animate = null)
    {
        if (!IsVisible || _closingAnimation)
        {
            return false;
        }

        var version = Volatile.Read(ref _presentationVersion);
        _closingAnimation = true;
        Ring.IsHitTestVisible = false;
        if (animate ?? _effectiveAnimations)
        {
            await Ring.AnimateOutAsync();
        }
        if (version != Volatile.Read(ref _presentationVersion))
        {
            return false;
        }
        Hide();
        Ring.Clear();
        _closingAnimation = false;
        OverlayHidden?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        source.AddHook(WindowMessageHook);
        var style = GetWindowLongPtr(source.Handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(source.Handle, GwlExStyle, new nint(style | WsExNoActivate | WsExToolWindow));
    }

    private static nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return new nint(MaNoActivate);
        }
        return nint.Zero;
    }

    public async Task CommitHoveredAsync()
    {
        var hovered = Ring.HoveredSlot;
        var targetWindow = _actionTargetWindow;
        if (hovered?.Submenu is not null && hovered.Action is not { Kind: not ActionKind.None })
        {
            Ring.OpenSubmenu(hovered, animate: true);
            return;
        }
        if (hovered?.Action is { Kind: not ActionKind.None })
        {
            var wasClosed = await HideAnimatedAsync();
            if (wasClosed && (hovered.Action.Kind != ActionKind.AdjustParameter || hovered.Submenu is not null))
            {
                ActionRequested?.Invoke(this, new OverlayActionEventArgs(hovered, targetWindow, _isPreview));
            }
            return;
        }
        await HideAnimatedAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The overlay is a reusable process resource. App shutdown calls Application.Shutdown,
        // at which point no cancellation is needed because the dispatcher is already exiting.
        if (Application.Current?.Dispatcher.HasShutdownStarted == false)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private async void OnSlotInvoked(object? sender, RingSlotEventArgs args)
    {
        if (args.Slot.Action is not { Kind: not ActionKind.None })
        {
            return;
        }
        if (args.Slot.Action.Kind == ActionKind.AdjustParameter && args.Slot.Submenu is null)
        {
            return;
        }
        var targetWindow = _actionTargetWindow;
        if (await HideAnimatedAsync())
        {
            ActionRequested?.Invoke(this, new OverlayActionEventArgs(args.Slot, targetWindow, _isPreview));
        }
    }

    private async void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        await HideAnimatedAsync();
        e.Handled = true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint newValue);
}
