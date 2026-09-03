using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ActionsRing.Platform.Windows.Display;

namespace ActionsRing.App.Windows;

public enum KeyStateIndicator
{
    CapsLock,
    NumLock,
    ScrollLock,
}

public partial class KeyStateOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);
    private long _presentationVersion;

    public KeyStateOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public async Task ShowStateAsync(
        KeyStateIndicator indicator,
        bool enabled,
        MonitorSnapshot monitor,
        bool animationsEnabled,
        bool reduceMotion)
    {
        var version = Interlocked.Increment(ref _presentationVersion);
        ApplyContent(indicator, enabled);
        StopAnimations();
        Opacity = 0;
        NotificationCard.Opacity = 0;
        CardTranslate.Y = reduceMotion ? 0 : -8;

        var handle = new WindowInteropHelper(this).EnsureHandle();
        var scaleX = monitor.ScaleX > 0 ? monitor.ScaleX : 1d;
        var scaleY = monitor.ScaleY > 0 ? monitor.ScaleY : 1d;
        var width = checked((int)Math.Round(Width * scaleX));
        var height = checked((int)Math.Round(Height * scaleY));
        var marginX = checked((int)Math.Round(18 * scaleX));
        var marginY = checked((int)Math.Round(18 * scaleY));
        var x = monitor.WorkArea.Right - width - marginX;
        var y = monitor.WorkArea.Top + marginY;

        PositionWindow(handle, x, y, width, height, show: false);
        if (!IsVisible)
        {
            Show();
        }
        PositionWindow(handle, x, y, width, height, show: true);

        Opacity = 1;
        if (!animationsEnabled)
        {
            NotificationCard.Opacity = 1;
            CardTranslate.Y = 0;
            await HideAfterDelayAsync(version, 1250, animate: false, reduceMotion);
            return;
        }

        var entranceMilliseconds = reduceMotion ? 90 : 150;
        NotificationCard.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(entranceMilliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        if (!reduceMotion)
        {
            CardTranslate.BeginAnimation(
                System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });
        }
        NotificationCard.Opacity = 1;
        CardTranslate.Y = 0;
        await HideAfterDelayAsync(version, 1350, animate: true, reduceMotion);
    }

    public void HideImmediately()
    {
        Interlocked.Increment(ref _presentationVersion);
        StopAnimations();
        Hide();
    }

    public static bool ReadState(KeyStateIndicator indicator)
    {
        var virtualKey = indicator switch
        {
            KeyStateIndicator.CapsLock => 0x14,
            KeyStateIndicator.NumLock => 0x90,
            KeyStateIndicator.ScrollLock => 0x91,
            _ => throw new ArgumentOutOfRangeException(nameof(indicator)),
        };
        return (GetKeyState(virtualKey) & 1) != 0;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current?.Dispatcher.HasShutdownStarted == false)
        {
            e.Cancel = true;
            HideImmediately();
        }
        base.OnClosing(e);
    }

    private async Task HideAfterDelayAsync(long version, int delayMilliseconds, bool animate, bool reduceMotion)
    {
        await Task.Delay(delayMilliseconds);
        if (version != Volatile.Read(ref _presentationVersion) || !IsVisible)
        {
            return;
        }

        if (animate)
        {
            var exitMilliseconds = reduceMotion ? 100 : 220;
            var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(exitMilliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd,
            };
            NotificationCard.BeginAnimation(OpacityProperty, animation);
            await Task.Delay(exitMilliseconds);
        }

        if (version == Volatile.Read(ref _presentationVersion))
        {
            Hide();
            StopAnimations();
            NotificationCard.Opacity = 1;
        }
    }

    private void ApplyContent(KeyStateIndicator indicator, bool enabled)
    {
        (KeyNameText.Text, KeyGlyph.Text) = indicator switch
        {
            KeyStateIndicator.CapsLock => ("Caps Lock", "A"),
            KeyStateIndicator.NumLock => ("Num Lock", "1"),
            KeyStateIndicator.ScrollLock => ("Scroll Lock", "↕"),
            _ => throw new ArgumentOutOfRangeException(nameof(indicator)),
        };
        StateText.Text = enabled ? "Включён" : "Выключен";
        StateDot.Fill = enabled
            ? FindBrush("AccentBrush", Brushes.MediumPurple)
            : FindBrush("TextMutedBrush", Brushes.Gray);
    }

    private void StopAnimations()
    {
        NotificationCard.BeginAnimation(OpacityProperty, null);
        CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
    }

    private Brush FindBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        var style = GetWindowLongPtr(source.Handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(source.Handle, GwlExStyle, new nint(style | WsExNoActivate | WsExToolWindow));
    }

    private static void PositionWindow(
        nint handle,
        int x,
        int y,
        int width,
        int height,
        bool show)
    {
        var flags = SwpNoActivate | (show ? SwpShowWindow : 0);
        if (!SetWindowPos(handle, HwndTopmost, x, y, width, height, flags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not position the key-state indicator.");
        }
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint newValue);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
