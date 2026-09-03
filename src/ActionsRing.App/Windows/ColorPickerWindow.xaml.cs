using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ActionsRing.App.Services;

namespace ActionsRing.App.Windows;

public partial class ColorPickerWindow : Window
{
    private readonly RgbColor _resetColor;
    private HsvColor _hsv;
    private RgbColor _color;
    private bool _dragging;
    private bool _ready;
    private bool _updating;

    public ColorPickerWindow(string initialColor, string? resetColor = null)
    {
        if (!ColorValue.TryParse(initialColor, out _color))
        {
            _color = new RgbColor(130, 78, 249);
        }

        _resetColor = ColorValue.TryParse(resetColor ?? initialColor, out var parsedReset)
            ? parsedReset
            : _color;
        _hsv = ColorValue.ToHsv(_color);

        InitializeComponent();
        _ready = true;
        Loaded += (_, _) => RefreshVisuals(refreshFields: true);
        SourceInitialized += (_, _) => FitToMonitorWorkArea();
    }

    public string SelectedColor => _color.ToHex();

    private void OnColorAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        ColorArea.CaptureMouse();
        UpdateFromColorArea(e.GetPosition(ColorArea));
        e.Handled = true;
    }

    private void OnColorAreaMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateFromColorArea(e.GetPosition(ColorArea));
        }
    }

    private void OnColorAreaMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            UpdateFromColorArea(e.GetPosition(ColorArea));
        }

        _dragging = false;
        ColorArea.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnColorAreaLostCapture(object sender, MouseEventArgs e) => _dragging = false;

    private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating)
        {
            return;
        }

        _hsv = _hsv with { Hue = e.NewValue };
        _color = ColorValue.FromHsv(_hsv);
        RefreshVisuals(refreshFields: true);
    }

    private void OnHexChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_ready || _updating)
        {
            return;
        }

        if (ColorValue.TryParse(HexBox.Text.Trim(), out var parsed))
        {
            SetColor(parsed, refreshFields: false);
            ValidationText.Text = string.Empty;
        }
    }

    private void OnRgbChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_ready || _updating)
        {
            return;
        }

        if (TryReadRgb(out var parsed))
        {
            SetColor(parsed, refreshFields: false);
            ValidationText.Text = string.Empty;
        }
    }

    private void OnHexLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitHex();
    }

    private void CommitHex()
    {
        if (!ColorValue.TryParse(HexBox.Text.Trim(), out var parsed))
        {
            ValidationText.Text = "Введите цвет в формате #RRGGBB.";
            RefreshVisuals(refreshFields: true);
            return;
        }

        SetColor(parsed, refreshFields: true);
        ValidationText.Text = string.Empty;
    }

    private void OnRgbLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitRgb();
    }

    private void CommitRgb()
    {
        if (!TryReadRgb(out var parsed))
        {
            ValidationText.Text = "Значения RGB должны быть целыми числами от 0 до 255.";
            RefreshVisuals(refreshFields: true);
            return;
        }

        SetColor(parsed, refreshFields: true);
        ValidationText.Text = string.Empty;
    }

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (ReferenceEquals(sender, HexBox))
        {
            CommitHex();
        }
        else
        {
            CommitRgb();
        }

        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        SetColor(_resetColor, refreshFields: true);
        ValidationText.Text = string.Empty;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        DialogResult = true;
    }

    private void UpdateFromColorArea(Point point)
    {
        var width = Math.Max(ColorArea.ActualWidth, 1d);
        var height = Math.Max(ColorArea.ActualHeight, 1d);
        _hsv = _hsv with
        {
            Saturation = Math.Clamp(point.X / width, 0d, 1d),
            Value = 1d - Math.Clamp(point.Y / height, 0d, 1d),
        };
        _color = ColorValue.FromHsv(_hsv);
        RefreshVisuals(refreshFields: true);
    }

    private void SetColor(RgbColor color, bool refreshFields)
    {
        _color = color;
        _hsv = ColorValue.ToHsv(color);
        RefreshVisuals(refreshFields);
    }

    private void RefreshVisuals(bool refreshFields)
    {
        if (!_ready)
        {
            return;
        }

        _updating = true;
        try
        {
            HueSlider.Value = _hsv.Hue;
            var hueColor = ColorValue.FromHsv(new HsvColor(_hsv.Hue, 1d, 1d));
            HueBase.Background = Brush(hueColor);
            PreviewBorder.Background = Brush(_color);
            if (PreviewBorder.Child is System.Windows.Controls.TextBlock previewText)
            {
                previewText.Foreground = ContrastBrush(_color);
            }

            var pointer = ColorPointer.RenderTransform as TranslateTransform;
            if (pointer is null)
            {
                pointer = new TranslateTransform();
                ColorPointer.RenderTransform = pointer;
            }

            pointer.X = Math.Clamp(_hsv.Saturation * ColorArea.ActualWidth - ColorPointer.Width / 2d, -ColorPointer.Width / 2d, Math.Max(-ColorPointer.Width / 2d, ColorArea.ActualWidth - ColorPointer.Width / 2d));
            pointer.Y = Math.Clamp((1d - _hsv.Value) * ColorArea.ActualHeight - ColorPointer.Height / 2d, -ColorPointer.Height / 2d, Math.Max(-ColorPointer.Height / 2d, ColorArea.ActualHeight - ColorPointer.Height / 2d));

            if (refreshFields)
            {
                HexBox.Text = _color.ToHex();
                RedBox.Text = _color.R.ToString(CultureInfo.InvariantCulture);
                GreenBox.Text = _color.G.ToString(CultureInfo.InvariantCulture);
                BlueBox.Text = _color.B.ToString(CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private bool TryReadRgb(out RgbColor color)
    {
        color = default;
        if (!byte.TryParse(RedBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var red)
            || !byte.TryParse(GreenBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var green)
            || !byte.TryParse(BlueBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var blue))
        {
            return false;
        }

        color = new RgbColor(red, green, blue, _color.A);
        return true;
    }

    private static SolidColorBrush Brush(RgbColor color) => new(
        Color.FromArgb(color.A, color.R, color.G, color.B));

    private static SolidColorBrush ContrastBrush(RgbColor background)
    {
        var luminance = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255d;
        return new SolidColorBrush(luminance > 0.58 ? Color.FromRgb(16, 19, 22) : Colors.White);
    }

    private void FitToMonitorWorkArea()
    {
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var workArea = System.Windows.Forms.Screen.FromHandle(source.Handle).WorkingArea;
        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new Point(workArea.Left, workArea.Top));
        var bottomRight = transform.Transform(new Point(workArea.Right, workArea.Bottom));
        var bounds = WindowPlacementCalculator.FitCentered(
            Width,
            Height,
            MinWidth,
            MinHeight,
            new Rect(topLeft, bottomRight));
        MinWidth = Math.Min(MinWidth, bounds.Width);
        MinHeight = Math.Min(MinHeight, bounds.Height);
        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;
    }
}
