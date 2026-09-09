using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace ActionsRing.App.Services;

/// <summary>Renders a deliberately passive SVG subset without scripts, external resources or XAML.</summary>
public sealed class SafeSvgIcon
{
    public const int MaximumBytes = 1024 * 1024;
    private readonly XDocument _document;
    private readonly Rect _viewBox;
    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    { "svg", "g", "path", "circle", "ellipse", "rect", "line", "polyline", "polygon", "title", "desc", "defs", "linearGradient", "radialGradient", "stop", "clipPath" };
    private static readonly Regex Numbers = new(@"[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private SafeSvgIcon(XDocument document, Rect viewBox) { _document = document; _viewBox = viewBox; }

    public static SafeSvgIcon Parse(string text)
    {
        if (text.Length > MaximumBytes) throw new FormatException("SVG слишком большой.");
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
        var document = XDocument.Load(reader, LoadOptions.None);
        document.DescendantNodes().OfType<XProcessingInstruction>().Remove();
        var root = document.Root ?? throw new FormatException("Пустой SVG.");
        if (root.Name.LocalName != "svg") throw new FormatException("Файл не является SVG.");
        root.Descendants().Where(element => element.Name.LocalName is "metadata" or "namedview").Remove();
        var elements = root.DescendantsAndSelf().ToArray();
        if (elements.Length > 4096) throw new FormatException("SVG содержит слишком много элементов.");
        foreach (var element in elements)
        {
            if (!Elements.Contains(element.Name.LocalName)) throw new FormatException($"SVG содержит неподдерживаемый элемент: {element.Name.LocalName}. Сохраните иконку как SVG с контурами или PNG.");
            if (element.Ancestors().Take(33).Count() > 32) throw new FormatException("Слишком сложная структура SVG.");
            foreach (var attribute in element.Attributes())
            {
                var name = attribute.Name.LocalName;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name is "href" or "src")
                    throw new FormatException("SVG не должен содержать ссылки или обработчики событий.");
                if (name == "stop-color" && attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Цвет градиента SVG должен быть обычным цветом.");
                if (attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(attribute.Value, @"^url\(#[a-zA-Z0-9_-]+\)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
                    throw new FormatException("Внешние ресурсы SVG не поддерживаются.");
            }
        }
        var bounds = ParseNumbers(root.Attribute("viewBox")?.Value);
        var viewBox = bounds.Length == 4 ? new Rect(bounds[0], bounds[1], bounds[2], bounds[3])
            : new Rect(0, 0, Number(root, "width", 24), Number(root, "height", 24));
        if (viewBox.Width <= 0 || viewBox.Height <= 0 || viewBox.Width > 100000 || viewBox.Height > 100000)
            throw new FormatException("Некорректный размер SVG.");
        return new SafeSvgIcon(document, viewBox);
    }

    public string ToSanitizedString() => _document.ToString(SaveOptions.DisableFormatting);

    public DrawingImage CreateImage(Brush foreground, bool monochrome = false)
    {
        foreground = foreground.IsFrozen ? foreground : foreground.CloneCurrentValue();
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(_viewBox)));
        DrawElement(_document.Root!, drawing, foreground, monochrome, "black", "none", 1, 1);
        drawing.ClipGeometry = new RectangleGeometry(_viewBox);
        var image = new DrawingImage(drawing);
        if (image.CanFreeze) image.Freeze();
        return image;
    }

    private void DrawElement(XElement element, DrawingGroup parent, Brush foreground, bool monochrome,
        string inheritedFill, string inheritedStroke, double inheritedWidth, double inheritedOpacity)
    {
        var name = element.Name.LocalName;
        if (name is "defs" or "title" or "desc" or "linearGradient" or "radialGradient" or "stop" or "clipPath") return;
        var fill = Attribute(element, "fill") ?? inheritedFill;
        var stroke = Attribute(element, "stroke") ?? inheritedStroke;
        var width = AttributeNumber(element, "stroke-width", inheritedWidth);
        var opacity = AttributeNumber(element, "opacity", 1) * inheritedOpacity;
        var group = new DrawingGroup { Opacity = Math.Clamp(opacity, 0, 1) };
        group.Transform = ParseTransform(element.Attribute("transform")?.Value);
        parent.Children.Add(group);
        Geometry? geometry = name switch
        {
            "path" => Geometry.Parse(element.Attribute("d")?.Value ?? ""),
            "circle" => new EllipseGeometry(new Point(Number(element, "cx"), Number(element, "cy")), Number(element, "r"), Number(element, "r")),
            "ellipse" => new EllipseGeometry(new Point(Number(element, "cx"), Number(element, "cy")), Number(element, "rx"), Number(element, "ry")),
            "rect" => new RectangleGeometry(new Rect(Number(element, "x"), Number(element, "y"), Math.Max(0, Number(element, "width")), Math.Max(0, Number(element, "height"))), Number(element, "rx"), Number(element, "ry", Number(element, "rx"))),
            "line" => new LineGeometry(new Point(Number(element, "x1"), Number(element, "y1")), new Point(Number(element, "x2"), Number(element, "y2"))),
            "polyline" or "polygon" => Polyline(element.Attribute("points")?.Value, name == "polygon"),
            _ => null,
        };
        if (geometry is not null)
        {
            if (geometry.IsFrozen) geometry = geometry.CloneCurrentValue();
            if (geometry is PathGeometry pathGeometry) pathGeometry.FillRule = Attribute(element, "fill-rule") == "evenodd" ? FillRule.EvenOdd : FillRule.Nonzero;
            if (geometry is StreamGeometry streamGeometry) streamGeometry.FillRule = Attribute(element, "fill-rule") == "evenodd" ? FillRule.EvenOdd : FillRule.Nonzero;
            var fillBrush = Paint(fill, foreground, monochrome);
            var strokeBrush = Paint(stroke, foreground, monochrome);
            Pen? pen = strokeBrush is null ? null : new Pen(strokeBrush, Math.Max(0, width))
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            group.Children.Add(new GeometryDrawing(fillBrush, pen, geometry));
        }
        foreach (var child in element.Elements()) DrawElement(child, group, foreground, monochrome, fill, stroke, width, 1);
    }

    private Brush? Paint(string value, Brush foreground, bool monochrome)
    {
        if (value == "none") return null;
        if (monochrome || value.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return foreground;
        if (value.StartsWith("url(#", StringComparison.Ordinal) && value.EndsWith(')'))
        {
            var id = value[5..^1];
            var gradient = _document.Descendants().FirstOrDefault(node => node.Attribute("id")?.Value == id);
            GradientBrush brush = gradient?.Name.LocalName == "radialGradient" ? new RadialGradientBrush() : new LinearGradientBrush();
            foreach (var stop in gradient?.Elements().Where(node => node.Name.LocalName == "stop") ?? [])
            {
                var stopColor = Attribute(stop, "stop-color") ?? "black";
                if (stopColor.Contains("url(", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Цвет градиента SVG должен быть обычным цветом.");
                var color = Paint(stopColor, foreground, false) as SolidColorBrush;
                if (color is null) continue;
                var offset = Attribute(stop, "offset") ?? "0";
                var amount = double.Parse(offset.TrimEnd('%'), CultureInfo.InvariantCulture) / (offset.EndsWith('%') ? 100 : 1);
                brush.GradientStops.Add(new GradientStop(color.Color, Math.Clamp(amount, 0, 1)));
            }
            return brush;
        }
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
        catch (Exception exception) when (exception is FormatException or NotSupportedException) { return foreground; }
    }

    private static Geometry Polyline(string? value, bool close)
    {
        var points = ParseNumbers(value);
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        if (points.Length >= 2)
        {
            context.BeginFigure(new Point(points[0], points[1]), close, close);
            for (var i = 2; i + 1 < points.Length; i += 2) context.LineTo(new Point(points[i], points[i + 1]), true, false);
        }
        return geometry;
    }

    private static Transform ParseTransform(string? value)
    {
        var result = new TransformGroup();
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (Match match in Regex.Matches(value, @"([a-zA-Z]+)\s*\(([^)]*)\)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            var n = ParseNumbers(match.Groups[2].Value);
            Transform? transform = match.Groups[1].Value switch
            {
                "matrix" when n.Length == 6 => new MatrixTransform(n[0], n[1], n[2], n[3], n[4], n[5]),
                "translate" when n.Length > 0 => new TranslateTransform(n[0], n.Length > 1 ? n[1] : 0),
                "scale" when n.Length > 0 => new ScaleTransform(n[0], n.Length > 1 ? n[1] : n[0]),
                "rotate" when n.Length > 0 => new RotateTransform(n[0], n.Length > 2 ? n[1] : 0, n.Length > 2 ? n[2] : 0),
                "skewX" when n.Length == 1 => new SkewTransform(n[0], 0),
                "skewY" when n.Length == 1 => new SkewTransform(0, n[0]),
                _ => null,
            };
            if (transform is not null) result.Children.Insert(0, transform);
        }
        return result;
    }

    private static string? Attribute(XElement element, string name)
    {
        var direct = element.Attribute(name)?.Value;
        if (direct is not null) return direct;
        return element.Attribute("style")?.Value.Split(';').Select(value => value.Split(':', 2))
            .Where(pair => pair.Length == 2 && pair[0].Trim() == name).Select(pair => pair[1].Trim()).LastOrDefault();
    }
    private static double AttributeNumber(XElement e, string name, double fallback) => TryNumber(Attribute(e, name), fallback);
    private static double Number(XElement e, string name, double fallback = 0) => TryNumber(e.Attribute(name)?.Value, fallback);
    private static double TryNumber(string? value, double fallback) => double.TryParse(value?.TrimEnd('p', 'x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : fallback;
    private static double[] ParseNumbers(string? value)
    {
        var values = Numbers.Matches(value ?? "").Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        if (values.Any(number => !double.IsFinite(number) || Math.Abs(number) > 1_000_000_000)) throw new FormatException("SVG содержит некорректные координаты.");
        return values;
    }
}
