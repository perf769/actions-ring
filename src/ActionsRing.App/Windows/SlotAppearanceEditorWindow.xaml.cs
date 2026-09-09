using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ActionsRing.App.Services;
using ActionsRing.Core.Domain;

namespace ActionsRing.App.Windows;

public partial class SlotAppearanceEditorWindow : Window
{
    private readonly RingAppearanceDefinition _inherited;
    private string _bubbleColor;
    private string _bubbleHoverColor;
    private string _iconColor;
    private string _iconHoverColor;
    private bool _ready;

    public SlotAppearanceEditorWindow(
        RingSlotDefinition slot,
        RingAppearanceDefinition inheritedAppearance)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(inheritedAppearance);

        _inherited = inheritedAppearance.Clone();
        _inherited.BubbleColor = ColorValue.NormalizeOrDefault(
            _inherited.BubbleColor,
            RingAppearanceDefinition.DefaultBubbleColor);
        _inherited.BubbleHoverColor = ColorValue.NormalizeOrDefault(
            _inherited.BubbleHoverColor,
            RingAppearanceDefinition.DefaultBubbleHoverColor);
        _inherited.IconColor = ColorValue.NormalizeOrDefault(
            _inherited.IconColor,
            RingAppearanceDefinition.DefaultIconColor);
        _inherited.IconHoverColor = ColorValue.NormalizeOrDefault(
            _inherited.IconHoverColor,
            RingAppearanceDefinition.DefaultIconHoverColor);
        var appearance = slot.AppearanceOverride;
        _bubbleColor = ColorValue.NormalizeOrDefault(appearance?.BubbleColor, _inherited.BubbleColor);
        _bubbleHoverColor = ColorValue.NormalizeOrDefault(appearance?.BubbleHoverColor, _inherited.BubbleHoverColor);
        _iconColor = ColorValue.NormalizeOrDefault(appearance?.IconColor, _inherited.IconColor);
        _iconHoverColor = ColorValue.NormalizeOrDefault(appearance?.IconHoverColor, _inherited.IconHoverColor);

        InitializeComponent();
        var iconReference = slot.Icon ?? (slot.Submenu is not null && slot.Action?.Kind is null or ActionKind.None ? "folder" : null);
        RestIcon.SetIcon(iconReference, slot.Action);
        HoverIcon.SetIcon(iconReference, slot.Action);
        SlotCaption.Text = $"«{slot.Label}» — отключённые параметры наследуют цвета темы кольца.";
        BubbleEnabled.IsChecked = appearance?.BubbleColor is not null;
        BubbleHoverEnabled.IsChecked = appearance?.BubbleHoverColor is not null;
        IconEnabled.IsChecked = appearance?.IconColor is not null;
        IconHoverEnabled.IsChecked = appearance?.IconHoverColor is not null;
        _ready = true;
        Loaded += (_, _) => RefreshPreview();
        SourceInitialized += (_, _) => FitToMonitorWorkArea();
    }

    public RingSlotAppearanceDefinition? EditedAppearanceOverride
    {
        get
        {
            var result = new RingSlotAppearanceDefinition
            {
                BubbleColor = BubbleEnabled.IsChecked == true ? _bubbleColor : null,
                BubbleHoverColor = BubbleHoverEnabled.IsChecked == true ? _bubbleHoverColor : null,
                IconColor = IconEnabled.IsChecked == true ? _iconColor : null,
                IconHoverColor = IconHoverEnabled.IsChecked == true ? _iconHoverColor : null,
            };
            return result.IsEmpty ? null : result;
        }
    }

    private void OnOverrideChanged(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            RefreshPreview();
        }
    }

    private void OnChooseColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string channel })
        {
            return;
        }

        var (current, inherited) = channel switch
        {
            "Bubble" => (_bubbleColor, _inherited.BubbleColor),
            "BubbleHover" => (_bubbleHoverColor, _inherited.BubbleHoverColor),
            "Icon" => (_iconColor, _inherited.IconColor),
            "IconHover" => (_iconHoverColor, _inherited.IconHoverColor),
            _ => (string.Empty, string.Empty),
        };
        if (current.Length == 0)
        {
            return;
        }

        var picker = new ColorPickerWindow(current, inherited) { Owner = this };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        switch (channel)
        {
            case "Bubble":
                _bubbleColor = picker.SelectedColor;
                break;
            case "BubbleHover":
                _bubbleHoverColor = picker.SelectedColor;
                break;
            case "Icon":
                _iconColor = picker.SelectedColor;
                break;
            case "IconHover":
                _iconHoverColor = picker.SelectedColor;
                break;
        }

        RefreshPreview();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _ready = false;
        BubbleEnabled.IsChecked = false;
        BubbleHoverEnabled.IsChecked = false;
        IconEnabled.IsChecked = false;
        IconHoverEnabled.IsChecked = false;
        _ready = true;
        RefreshPreview();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void RefreshPreview()
    {
        BubbleButton.IsEnabled = BubbleEnabled.IsChecked == true;
        BubbleHoverButton.IsEnabled = BubbleHoverEnabled.IsChecked == true;
        IconButton.IsEnabled = IconEnabled.IsChecked == true;
        IconHoverButton.IsEnabled = IconHoverEnabled.IsChecked == true;

        BubbleSwatch.Background = Brush(_bubbleColor);
        BubbleHoverSwatch.Background = Brush(_bubbleHoverColor);
        IconSwatch.Background = Brush(_iconColor);
        IconHoverSwatch.Background = Brush(_iconHoverColor);
        BubbleHex.Text = DisplayValue(BubbleEnabled, _bubbleColor);
        BubbleHoverHex.Text = DisplayValue(BubbleHoverEnabled, _bubbleHoverColor);
        IconHex.Text = DisplayValue(IconEnabled, _iconColor);
        IconHoverHex.Text = DisplayValue(IconHoverEnabled, _iconHoverColor);

        var bubble = BubbleEnabled.IsChecked == true ? _bubbleColor : _inherited.BubbleColor;
        var bubbleHover = BubbleHoverEnabled.IsChecked == true ? _bubbleHoverColor : _inherited.BubbleHoverColor;
        var icon = IconEnabled.IsChecked == true ? _iconColor : _inherited.IconColor;
        var iconHover = IconHoverEnabled.IsChecked == true ? _iconHoverColor : _inherited.IconHoverColor;
        RestBubble.Background = Brush(bubble);
        RestIcon.Foreground = Brush(icon);
        RestIcon.Background = RestBubble.Background;
        HoverBubble.Background = Brush(bubbleHover);
        HoverIcon.Foreground = Brush(iconHover);
        HoverIcon.Background = HoverBubble.Background;
    }

    private static string DisplayValue(System.Windows.Controls.Primitives.ToggleButton toggle, string color) =>
        toggle.IsChecked == true ? color : "Цвет темы";

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
