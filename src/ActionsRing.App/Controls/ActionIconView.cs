using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ActionsRing.App.Services;
using ActionsRing.Core.Domain;

namespace ActionsRing.App.Controls;

/// <summary>A shared, theme-aware icon for ring slots and their editors.</summary>
public sealed class ActionIconView : ContentControl
{
    private static readonly ApplicationVisualService SharedVisuals = new();
    private readonly ApplicationVisualService _visuals;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly Border _surface = new() { CornerRadius = new CornerRadius(6), IsHitTestVisible = false };
    private string? _reference;
    private SafeSvgIcon? _vector;
    private ImageSource? _bitmap;
    private bool _monochrome;
    private long _request;
    private double? _imageLuminance;
    private string? _text;
    static ActionIconView()
    {
        ForegroundProperty.OverrideMetadata(typeof(ActionIconView), new FrameworkPropertyMetadata(Brushes.Black, OnPaletteChanged));
        BackgroundProperty.OverrideMetadata(typeof(ActionIconView), new FrameworkPropertyMetadata(Brushes.Transparent, OnPaletteChanged));
    }
    public ActionIconView() : this(SharedVisuals) { }
    public ActionIconView(ApplicationVisualService visuals)
    {
        _visuals = visuals ?? throw new ArgumentNullException(nameof(visuals));
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsHitTestVisible = false;
        _surface.Child = _image;
        Content = _surface;
    }
    public void SetIcon(string? iconReference, ActionDefinition? action = null)
    {
        var request = ++_request;
        _reference = IconLibrary.ResolveReference(iconReference, action);
        _vector = null; _bitmap = null; _imageLuminance = null; _text = null;
        try { _vector = IconLibrary.FindSvg(_reference); }
        catch (Exception exception) when (exception is FormatException or ArgumentException or System.Xml.XmlException) { AppLog.Error("Could not render a library icon", exception); }
        _monochrome = _vector is not null;
        var originalReference = iconReference ?? action?.Icon ?? string.Empty;
        var explicitVector = originalReference.StartsWith("lucide:", StringComparison.OrdinalIgnoreCase)
            || originalReference.StartsWith("brand:", StringComparison.OrdinalIgnoreCase)
            || originalReference.StartsWith("image:", StringComparison.OrdinalIgnoreCase);
        var automaticPayload = !explicitVector && action?.Kind is (ActionKind.LaunchApplication or ActionKind.OpenUri)
            && _reference is "lucide:app-window" or "lucide:globe";
        if (_vector is not null)
        {
            RefreshPalette();
            if (automaticPayload) _ = LoadAsync(request, originalReference, action);
            return;
        }
        if (_reference.Length <= 3 && action?.Kind is not (ActionKind.LaunchApplication or ActionKind.OpenUri))
        { _text = _reference.ToUpperInvariant(); _monochrome = true; RefreshPalette(); return; }
        var defaultReference = IconLibrary.ResolveReference(null, action is null ? null : new ActionDefinition { Kind = action.Kind, BuiltIn = action.BuiltIn });
        _vector = IconLibrary.FindSvg(defaultReference) ?? IconLibrary.FindSvg("lucide:app-window");
        _monochrome = true;
        RefreshPalette();
        _ = LoadAsync(request, _reference, action);
    }

    private async Task LoadAsync(long request, string reference, ActionDefinition? action)
    {
        try
        {
            var path = reference.StartsWith("image:", StringComparison.OrdinalIgnoreCase) ? new IconImportService().ResolvePath(reference) : reference;
            if (Uri.TryCreate(path, UriKind.Absolute, out var fileUri) && fileUri.IsFile) path = fileUri.LocalPath;
            if (path?.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) == true && File.Exists(path))
            {
                var svg = await Task.Run(() => { var parsed = SafeSvgIcon.Parse(File.ReadAllText(path)); _ = parsed.CreateImage(Brushes.Black); return parsed; });
                await Dispatcher.InvokeAsync(() =>
                {
                    if (request != _request) return;
                    _vector = svg; _monochrome = false; _bitmap = null; RefreshPalette();
                });
                return;
            }
            var source = await Task.Run(() => _visuals.TryLoadIcon(path));
            if (source is null && action?.LaunchApplication?.ExecutablePath is { } executable)
                source = await Task.Run(() => _visuals.TryLoadIcon(executable));
            if (source is null && action?.OpenUri?.Uri is { } uri)
            {
                var favicon = await _visuals.GetFaviconAsync(uri);
                source = await Task.Run(() => _visuals.TryLoadIcon(favicon));
            }
            if (source is null) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (request != _request) return;
                _bitmap = source; _vector = null; _monochrome = false; _imageLuminance = null; RefreshPalette();
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or NotSupportedException or System.Xml.XmlException or OperationCanceledException)
        { AppLog.Error("Could not resolve an action icon", exception); }
    }

    private static void OnPaletteChanged(DependencyObject value, DependencyPropertyChangedEventArgs e) => ((ActionIconView)value).RefreshPalette();
    private void RefreshPalette()
    {
        if (_surface is null) return;
        if (_text is not null)
        {
            _surface.Child = new Viewbox { Child = new TextBlock { Text = _text, Foreground = Foreground, FontFamily = new FontFamily(_text.Any(character => character >= '\uE000') ? "Segoe MDL2 Assets" : "Segoe UI"), FontSize = 24, FontWeight = FontWeights.SemiBold } };
            _surface.Background = null; _surface.Padding = new Thickness(0); return;
        }
        _surface.Child = _image;
        var source = _vector?.CreateImage(Foreground ?? Brushes.Black, _monochrome) ?? _bitmap;
        if (_vector is not null && !_monochrome) _imageLuminance = null;
        _image.Source = source;
        var plate = !_monochrome && source is not null ? ContrastPlate(source) : null;
        _surface.Background = plate;
        _surface.Padding = plate is null ? new Thickness(0) : new Thickness(3);
    }

    private Brush? ContrastPlate(ImageSource source)
    {
        _imageLuminance ??= AverageLuminance(source);
        if (_imageLuminance is not { } luminance) return null;
        var background = Background as SolidColorBrush;
        var backdrop = background is { Color.A: > 0 } ? background.Color : (Foreground as SolidColorBrush)?.Color is { } foreground && Luminance(foreground) > 0.5 ? Colors.Black : Colors.White;
        var behind = Luminance(backdrop);
        var ratio = (Math.Max(luminance, behind) + 0.05) / (Math.Min(luminance, behind) + 0.05);
        return ratio >= 2.15 ? null : luminance > 0.45 ? new SolidColorBrush(Color.FromRgb(44, 47, 53)) : new SolidColorBrush(Color.FromRgb(249, 249, 251));
    }

    private static double? AverageLuminance(ImageSource source)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawImage(source, new Rect(0, 0, 32, 32));
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var pixels = new byte[32 * 32 * 4]; bitmap.CopyPixels(pixels, 32 * 4, 0);
        double sum = 0, weight = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3] / 255d;
            if (alpha < 0.15) continue;
            sum += Luminance(Color.FromRgb((byte)Math.Clamp(pixels[i + 2] / alpha, 0, 255), (byte)Math.Clamp(pixels[i + 1] / alpha, 0, 255), (byte)Math.Clamp(pixels[i] / alpha, 0, 255))) * alpha;
            weight += alpha;
        }
        return weight > 0 ? sum / weight : null;
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte value) { var n = value / 255d; return n <= 0.04045 ? n / 12.92 : Math.Pow((n + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}
