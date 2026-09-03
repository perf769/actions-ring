using ActionsRing.Core.Configuration;
using ActionsRing.Platform.Windows.Display;

namespace ActionsRing.App.Services;

/// <summary>
/// Converts the user's baseline ring diameter into a per-monitor scale. Windows gives the
/// monitor geometry in physical pixels, so dividing by its DPI scale yields the usable size
/// in device-independent pixels. A square-root curve avoids making 4K rings comically large.
/// </summary>
public static class AdaptiveRingScaleCalculator
{
    private const double ReferenceWidthDip = 1920d;
    private const double ReferenceHeightDip = 1080d;

    public static double Calculate(AppearancePreferences appearance, MonitorSnapshot monitor)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        var baseline = appearance.RingDiameter / 212d;
        if (!appearance.AutoScaleRing)
        {
            return Math.Clamp(baseline, 0.60d, 2.00d);
        }

        var scaleX = monitor.ScaleX > 0 ? monitor.ScaleX : 1d;
        var scaleY = monitor.ScaleY > 0 ? monitor.ScaleY : 1d;
        var effectiveWidth = monitor.WorkArea.Width / scaleX;
        var effectiveHeight = monitor.WorkArea.Height / scaleY;
        var limitingRatio = Math.Min(
            effectiveWidth / ReferenceWidthDip,
            effectiveHeight / ReferenceHeightDip);
        var monitorFactor = Math.Clamp(Math.Sqrt(Math.Max(limitingRatio, 0.01d)), 0.78d, 1.25d);

        return Math.Clamp(baseline * monitorFactor, 0.60d, 2.00d);
    }
}
