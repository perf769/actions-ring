using System.Globalization;

namespace ActionsRing.App.Services;

public readonly record struct RgbColor(byte R, byte G, byte B, byte A = byte.MaxValue)
{
    public string ToHex() => A == byte.MaxValue
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

public readonly record struct HsvColor(double Hue, double Saturation, double Value, byte A = byte.MaxValue);

/// <summary>Strict persisted-color parsing and lossless RGB/HSV conversion for the color editor.</summary>
public static class ColorValue
{
    public static bool TryParse(string? value, out RgbColor color)
    {
        color = default;
        if (value is null || value.Length is not (7 or 9) || value[0] != '#')
        {
            return false;
        }

        var span = value.AsSpan(1);
        if (span.ToString().Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        var offset = span.Length == 8 ? 2 : 0;
        var alpha = offset == 2 ? ParseByte(span[..2]) : byte.MaxValue;
        color = new RgbColor(
            ParseByte(span.Slice(offset, 2)),
            ParseByte(span.Slice(offset + 2, 2)),
            ParseByte(span.Slice(offset + 4, 2)),
            alpha);
        return true;
    }

    public static string NormalizeOrDefault(string? value, string fallback)
    {
        if (TryParse(value, out var parsed))
        {
            return parsed.ToHex();
        }

        if (!TryParse(fallback, out parsed))
        {
            throw new ArgumentException("Fallback must use #RRGGBB or #AARRGGBB format.", nameof(fallback));
        }

        return parsed.ToHex();
    }

    public static HsvColor ToHsv(RgbColor color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;

        var hue = 0d;
        if (delta > double.Epsilon)
        {
            if (maximum == red)
            {
                hue = 60d * (((green - blue) / delta) % 6d);
            }
            else if (maximum == green)
            {
                hue = 60d * (((blue - red) / delta) + 2d);
            }
            else
            {
                hue = 60d * (((red - green) / delta) + 4d);
            }
        }

        if (hue < 0)
        {
            hue += 360d;
        }

        var saturation = maximum <= double.Epsilon ? 0d : delta / maximum;
        return new HsvColor(hue, saturation, maximum, color.A);
    }

    public static RgbColor FromHsv(HsvColor color)
    {
        var hue = NormalizeHue(color.Hue);
        var saturation = Math.Clamp(color.Saturation, 0d, 1d);
        var value = Math.Clamp(color.Value, 0d, 1d);
        var chroma = value * saturation;
        var section = hue / 60d;
        var secondary = chroma * (1d - Math.Abs(section % 2d - 1d));
        var (red, green, blue) = section switch
        {
            < 1d => (chroma, secondary, 0d),
            < 2d => (secondary, chroma, 0d),
            < 3d => (0d, chroma, secondary),
            < 4d => (0d, secondary, chroma),
            < 5d => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary),
        };
        var match = value - chroma;

        return new RgbColor(
            ToByte(red + match),
            ToByte(green + match),
            ToByte(blue + match),
            color.A);
    }

    private static byte ParseByte(ReadOnlySpan<char> value) =>
        byte.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value * 255d), 0, 255);

    private static double NormalizeHue(double hue)
    {
        if (!double.IsFinite(hue))
        {
            return 0d;
        }

        hue %= 360d;
        return hue < 0d ? hue + 360d : hue;
    }
}
