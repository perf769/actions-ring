using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ActionsRing.App.Windows;

public partial class TrayContextMenu : ContextMenu
{
    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText),
        typeof(string),
        typeof(TrayContextMenu),
        new PropertyMetadata("Кольцо активно"));

    public static readonly DependencyProperty StatusBrushProperty = DependencyProperty.Register(
        nameof(StatusBrush),
        typeof(Brush),
        typeof(TrayContextMenu),
        new PropertyMetadata(Brushes.Transparent));

    private bool _isPaused;
    private bool _isStartupEnabled;

    public TrayContextMenu()
    {
        InitializeComponent();

        ShowSettingsItem.Click += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
        ShowRingItem.Click += (_, _) => ShowRingRequested?.Invoke(this, EventArgs.Empty);
        PauseItem.Click += (_, _) => PauseToggled?.Invoke(this, EventArgs.Empty);
        StartupItem.Click += (_, _) => StartupToggled?.Invoke(this, EventArgs.Empty);
        ExitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        Opened += OnOpened;
        SetResourceReference(StatusBrushProperty, "SuccessBrush");
    }

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ShowRingRequested;
    public event EventHandler? PauseToggled;
    public event EventHandler? StartupToggled;
    public event EventHandler? ExitRequested;

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        private set => SetValue(StatusTextProperty, value);
    }

    public Brush StatusBrush
    {
        get => (Brush)GetValue(StatusBrushProperty);
        private set => SetValue(StatusBrushProperty, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            PauseItem.IsChecked = value;
            PauseItem.Header = value ? "Возобновить кольцо" : "Приостановить кольцо";
            StatusText = value ? "Кольцо приостановлено" : "Кольцо активно";
            SetResourceReference(StatusBrushProperty, value ? "TextMutedBrush" : "SuccessBrush");
        }
    }

    public bool IsStartupEnabled
    {
        get => _isStartupEnabled;
        set
        {
            _isStartupEnabled = value;
            StartupItem.IsChecked = value;
        }
    }

    public void ShowAtCursor()
    {
        Dispatcher.VerifyAccess();

        if (IsOpen)
        {
            IsOpen = false;
        }

        PlacementTarget = null;
        PlacementRectangle = Rect.Empty;
        Placement = PlacementMode.MousePoint;
        HorizontalOffset = 0;
        VerticalOffset = 0;
        IsOpen = true;
    }

    public void CloseMenu()
    {
        if (Dispatcher.CheckAccess())
        {
            IsOpen = false;
            return;
        }

        _ = Dispatcher.BeginInvoke(() => IsOpen = false);
    }

    private void OnOpened(object sender, RoutedEventArgs args)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (IsOpen)
                {
                    _ = ShowSettingsItem.Focus();
                    _ = Keyboard.Focus(ShowSettingsItem);
                }
            }));
    }
}
