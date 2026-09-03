using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ActionsRing.App.Services;
using ActionsRing.Core.Domain;

namespace ActionsRing.App.Windows;

public partial class RingThemeEditorWindow : Window
{
    private readonly RingAppearanceDefinition _appearance;

    public RingThemeEditorWindow(RingAppearanceDefinition appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        _appearance = appearance.Clone();
        _appearance.BubbleColor = ColorValue.NormalizeOrDefault(
            _appearance.BubbleColor,
            RingAppearanceDefinition.DefaultBubbleColor);
        _appearance.BubbleHoverColor = ColorValue.NormalizeOrDefault(
            _appearance.BubbleHoverColor,
            RingAppearanceDefinition.DefaultBubbleHoverColor);
        _appearance.IconColor = ColorValue.NormalizeOrDefault(
            _appearance.IconColor,
            RingAppearanceDefinition.DefaultIconColor);
        _appearance.IconHoverColor = ColorValue.NormalizeOrDefault(
            _appearance.IconHoverColor,
            RingAppearanceDefinition.DefaultIconHoverColor);
        InitializeComponent();
        Loaded += (_, _) => RefreshPreview();
        SourceInitialized += (_, _) => FitToMonitorWorkArea();
    }

    public RingAppearanceDefinition EditedAppearance => _appearance.Clone();

    private void OnChooseColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string channel })
        {
            return;
        }

        var (current, reset) = channel switch
        {
            "Bubble" => (_appearance.BubbleColor, RingAppearanceDefinition.DefaultBubbleColor),
            "BubbleHover" => (_appearance.BubbleHoverColor, RingAppearanceDefinition.DefaultBubbleHoverColor),
            "Icon" => (_appearance.IconColor, RingAppearanceDefinition.DefaultIconColor),
            "IconHover" => (_appearance.IconHoverColor, RingAppearanceDefinition.DefaultIconHoverColor),
            _ => (string.Empty, string.Empty),
        };
        if (current.Length == 0)
        {
            return;
        }

        var picker = new ColorPickerWindow(current, reset) { Owner = this };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        switch (channel)
        {
            case "Bubble":
                _appearance.BubbleColor = picker.SelectedColor;
                break;
            case "BubbleHover":
                _appearance.BubbleHoverColor = picker.SelectedColor;
                break;
            case "Icon":
                _appearance.IconColor = picker.SelectedColor;
                break;
            case "IconHover":
                _appearance.IconHoverColor = picker.SelectedColor;
                break;
        }

        RefreshPreview();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _appearance.ResetToDefaults();
        RefreshPreview();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void RefreshPreview()
    {
        BubbleSwatch.Background = Brush(_appearance.BubbleColor);
        BubbleHoverSwatch.Background = Brush(_appearance.BubbleHoverColor);
        IconSwatch.Background = Brush(_appearance.IconColor);
        IconHoverSwatch.Background = Brush(_appearance.IconHoverColor);
        BubbleHex.Text = _appearance.BubbleColor;
        BubbleHoverHex.Text = _appearance.BubbleHoverColor;
        IconHex.Text = _appearance.IconColor;
        IconHoverHex.Text = _appearance.IconHoverColor;

        RestBubble.Background = Brush(_appearance.BubbleColor);
        RestIcon.Foreground = Brush(_appearance.IconColor);
        HoverBubble.Background = Brush(_appearance.BubbleHoverColor);
        HoverIcon.Foreground = Brush(_appearance.IconHoverColor);
        HoverCaption.Foreground = Brush(_appearance.IconHoverColor);
    }

    private static SolidColorBrush Brush(string value)
    {
        var color = ColorValue.TryParse(value, out var parsed)
            ? parsed
            : new RgbColor(0, 0, 0);
        return new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
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
