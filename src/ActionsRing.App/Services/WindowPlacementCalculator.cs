using System.Windows;

namespace ActionsRing.App.Services;

/// <summary>Calculates an initial WPF window rectangle constrained to a monitor work area.</summary>
public static class WindowPlacementCalculator
{
    public static Rect FitCentered(
        double requestedWidth,
        double requestedHeight,
        double minimumWidth,
        double minimumHeight,
        Rect workArea,
        double margin = 12d)
    {
        ValidateDimension(requestedWidth, nameof(requestedWidth));
        ValidateDimension(requestedHeight, nameof(requestedHeight));
        ValidateDimension(minimumWidth, nameof(minimumWidth));
        ValidateDimension(minimumHeight, nameof(minimumHeight));
        if (workArea.IsEmpty
            || !double.IsFinite(workArea.X)
            || !double.IsFinite(workArea.Y)
            || !double.IsFinite(workArea.Width)
            || !double.IsFinite(workArea.Height)
            || workArea.Width <= 0
            || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }
        if (!double.IsFinite(margin) || margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(margin));
        }

        var horizontalInset = Math.Min(margin, Math.Max(0, (workArea.Width - 1d) / 2d));
        var verticalInset = Math.Min(margin, Math.Max(0, (workArea.Height - 1d) / 2d));
        var availableWidth = Math.Max(1d, workArea.Width - (horizontalInset * 2d));
        var availableHeight = Math.Max(1d, workArea.Height - (verticalInset * 2d));
        var effectiveMinimumWidth = Math.Min(minimumWidth, availableWidth);
        var effectiveMinimumHeight = Math.Min(minimumHeight, availableHeight);
        var width = Math.Clamp(requestedWidth, effectiveMinimumWidth, availableWidth);
        var height = Math.Clamp(requestedHeight, effectiveMinimumHeight, availableHeight);

        return new Rect(
            workArea.Left + ((workArea.Width - width) / 2d),
            workArea.Top + ((workArea.Height - height) / 2d),
            width,
            height);
    }

    private static void ValidateDimension(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
