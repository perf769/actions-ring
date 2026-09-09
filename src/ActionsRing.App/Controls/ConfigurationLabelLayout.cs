using System.Windows;

namespace ActionsRing.App.Controls;

internal readonly record struct ConfigurationLabel(int Index, Point Center, double Diameter, Size Size, Rect Preferred, int Priority);

/// <summary>Places editor captions as a group, keeping labels clear of every clickable bubble.</summary>
internal static class ConfigurationLabelLayout
{
    public static IReadOnlyDictionary<int, Rect> Arrange(
        IEnumerable<ConfigurationLabel> labels, IReadOnlyList<Rect> bubbles, Rect bounds, double margin)
    {
        var result = new Dictionary<int, Rect>();
        if (bounds.IsEmpty || bounds.Width <= margin * 2 || bounds.Height <= margin * 2) return result;
        var safe = new Rect(bounds.X + margin, bounds.Y + margin, bounds.Width - margin * 2, bounds.Height - margin * 2);
        var occupied = bubbles.Select(bubble => Inflate(bubble, 5)).ToList();
        foreach (var label in labels.OrderByDescending(item => item.Priority)
                     .ThenByDescending(item => item.Size.Height).ThenBy(item => item.Index))
        {
            if (label.Size.Width > safe.Width || label.Size.Height > safe.Height) continue;
            var bestScore = double.PositiveInfinity;
            Rect? best = null;
            foreach (var candidate in Candidates(label, safe))
            {
                if (occupied.Any(obstacle => obstacle.IntersectsWith(candidate))) continue;
                var delta = candidate.TopLeft - label.Preferred.TopLeft;
                var score = delta.LengthSquared;
                if (score >= bestScore) continue;
                bestScore = score;
                best = candidate;
            }
            if (best is not { } placement) continue;
            result[label.Index] = placement;
            occupied.Add(Inflate(placement, 5));
        }
        return result;
    }

    private static IEnumerable<Rect> Candidates(ConfigurationLabel label, Rect safe)
    {
        var half = label.Diameter / 2;
        const double gap = 18;
        var center = label.Center;
        var size = label.Size;
        Point[] anchors =
        [
            label.Preferred.TopLeft,
            new(center.X - half - gap - size.Width, center.Y - size.Height / 2),
            new(center.X + half + gap, center.Y - size.Height / 2),
            new(center.X - size.Width / 2, center.Y - half - gap - size.Height),
            new(center.X - size.Width / 2, center.Y + half + gap),
        ];
        foreach (var anchor in anchors)
        {
            for (var distance = 0; distance <= 112; distance += 8)
            {
                foreach (var offset in distance == 0 ? new[] { new Vector() } : new[]
                         { new Vector(0, distance), new Vector(0, -distance), new Vector(distance, 0), new Vector(-distance, 0) })
                {
                    var point = anchor + offset;
                    yield return new Rect(new Point(
                        Math.Clamp(point.X, safe.Left, safe.Right - size.Width),
                        Math.Clamp(point.Y, safe.Top, safe.Bottom - size.Height)), size);
                }
            }
        }
    }

    private static Rect Inflate(Rect value, double amount)
    {
        value.Inflate(amount, amount);
        return value;
    }
}
