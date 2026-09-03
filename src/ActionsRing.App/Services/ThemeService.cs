using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using ActionsRing.Core.Configuration;
using Microsoft.Win32;

namespace ActionsRing.App.Services;

public sealed class ThemeService
{
    private static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = "#F7F7F9",
            ["SurfaceBrush"] = "#FFFFFF",
            ["SurfaceRaisedBrush"] = "#F1F1F4",
            ["SurfaceHoverBrush"] = "#E9E9ED",
            ["SidebarBrush"] = "#F0F0F3",
            ["BorderBrush"] = "#E1E1E6",
            ["TextPrimaryBrush"] = "#15151A",
            ["TextSecondaryBrush"] = "#6F7078",
            ["TextMutedBrush"] = "#999AA1",
            ["AccentSoftBrush"] = "#E8DCFF",
            ["SuccessBrush"] = "#158765",
            ["DangerBrush"] = "#DE6687",
            ["RingBubbleBrush"] = "#F5F5F3",
            ["RingBubbleHoverBrush"] = "#050607",
            ["RingIconBrush"] = "#101316",
            ["RingIconHoverBrush"] = "#FFFFFF",
            ["RingTooltipBrush"] = "#FFFFFF",
            ["RingTooltipTextBrush"] = "#15151A",
            ["OverlayHintBrush"] = "#15171A",
        };

    private static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = "#101315",
            ["SurfaceBrush"] = "#181C1F",
            ["SurfaceRaisedBrush"] = "#22272B",
            ["SurfaceHoverBrush"] = "#2A3035",
            ["SidebarBrush"] = "#141719",
            ["BorderBrush"] = "#32383D",
            ["TextPrimaryBrush"] = "#F4F6F5",
            ["TextSecondaryBrush"] = "#A9B0AD",
            ["TextMutedBrush"] = "#737B78",
            ["AccentSoftBrush"] = "#2C274D",
            ["SuccessBrush"] = "#65D9B0",
            ["DangerBrush"] = "#FF7895",
            ["RingBubbleBrush"] = "#F5F5F3",
            ["RingBubbleHoverBrush"] = "#050607",
            ["RingIconBrush"] = "#101316",
            ["RingIconHoverBrush"] = "#FFFFFF",
            ["RingTooltipBrush"] = "#FFFFFF",
            ["RingTooltipTextBrush"] = "#15151A",
            ["OverlayHintBrush"] = "#111416",
        };

    public bool IsDark { get; private set; }
    public Color AccentColor { get; private set; } = ParseColor("#824EF9");

    public void Apply(AppearancePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var dark = preferences.Theme switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => IsSystemDarkTheme(),
        };

        var accent = preferences.UseSystemAccentColor
            ? TryGetSystemAccent() ?? ParseColor(preferences.AccentColor, "#824EF9")
            : ParseColor(preferences.AccentColor, "#824EF9");
        Apply(dark, accent);
    }

    public void Apply(bool dark, Color accent)
    {
        IsDark = dark;
        AccentColor = accent;
        var resources = Application.Current.Resources;
        foreach (var entry in dark ? DarkPalette : LightPalette)
        {
            resources[entry.Key] = new SolidColorBrush(ParseColor(entry.Value));
        }
        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["AccentSoftBrush"] = new SolidColorBrush(Blend(
            accent,
            dark ? Color.FromRgb(20, 22, 25) : Colors.White,
            dark ? 0.27 : 0.18));
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);
            return key?.GetValue("AppsUseLightTheme") is int useLight && useLight == 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static Color? TryGetSystemAccent()
    {
        try
        {
            if (DwmGetColorizationColor(out var value, out _) != 0)
            {
                return null;
            }
            return Color.FromArgb(
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    private static Color Blend(Color foreground, Color background, double foregroundWeight)
    {
        static byte Channel(byte foreground, byte background, double weight) =>
            (byte)Math.Round(foreground * weight + background * (1d - weight));
        return Color.FromRgb(
            Channel(foreground.R, background.R, foregroundWeight),
            Channel(foreground.G, background.G, foregroundWeight),
            Channel(foreground.B, background.B, foregroundWeight));
    }

    private static Color ParseColor(string value, string? fallback = null)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value)!;
        }
        catch (FormatException) when (fallback is not null)
        {
            return (Color)ColorConverter.ConvertFromString(fallback)!;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint colorization, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);
}
