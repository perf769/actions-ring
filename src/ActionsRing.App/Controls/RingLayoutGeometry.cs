using System.Windows;

namespace ActionsRing.App.Controls;

/// <summary>
/// Deterministic geometry for keeping radial-menu elements inside the current monitor.
/// All coordinates are expressed in the owning WPF canvas coordinate space.
/// </summary>
public static class RingLayoutGeometry
{
    private const double Epsilon = 0.01;

    public static IReadOnlyList<Point> ArrangeRoot(
        Point center,
        Size canvasSize,
        int count,
        double nodeDiameter,
        double desiredRadius,
        double margin)
    {
        if (count <= 0)
        {
            return [];
        }

        var safe = SafeCenterBounds(canvasSize, nodeDiameter, margin);
        var ideal = Enumerable.Range(0, count)
            .Select(index => PointOnCircle(center, desiredRadius, -Math.PI / 2d + index * Math.PI * 2d / count))
            .ToArray();
        var spacing = nodeDiameter + Math.Max(4d, nodeDiameter * 0.10d);

        if (AllInside(ideal, safe) && AreSeparated(ideal, spacing))
        {
            return ideal;
        }

        var blocked = BlockedEdges(ideal, safe);
        Point[]? adaptive = null;
        if (blocked.Count == 1)
        {
            adaptive = ArrangeSemicircle(center, count, desiredRadius, spacing, safe, blocked[0]);
        }
        else if (blocked.Count == 2 && blocked[0].IsHorizontal() != blocked[1].IsHorizontal())
        {
            adaptive = ArrangeCorner(center, count, desiredRadius, spacing, safe, blocked);
        }

        if (adaptive is not null && AllInside(adaptive, safe) && AreSeparated(adaptive, spacing))
        {
            return adaptive;
        }

        return ArrangeGridFallback(center, count, spacing, desiredRadius, safe);
    }

    public static IReadOnlyList<Point> ArrangeSubmenu(
        Point center,
        Point parent,
        Size canvasSize,
        int count,
        double nodeDiameter,
        double desiredRadius,
        double preferredAngle,
        IReadOnlyList<Point> obstacles,
        double margin)
    {
        if (count <= 0)
        {
            return [];
        }

        var safe = SafeCenterBounds(canvasSize, nodeDiameter, margin);
        var spacing = nodeDiameter + Math.Max(4d, nodeDiameter * 0.10d);
        var maximumRadius = Math.Max(canvasSize.Width, canvasSize.Height) * 1.15d;
        var radiusStep = Math.Max(12d, spacing * 0.42d);
        var inwardAngle = MostAvailableDirection(center, safe);
        var orientations = CandidateOrientations(preferredAngle, inwardAngle);

        for (var radius = desiredRadius; radius <= maximumRadius; radius += radiusStep)
        {
            var minimumStep = count <= 1
                ? 0d
                : 2d * Math.Asin(Math.Clamp(spacing / (2d * radius), 0d, 0.999d));
            var spread = count <= 1
                ? 0d
                : Math.Max(DegreesToRadians(68d), minimumStep * (count - 1) + DegreesToRadians(2d));
            if (spread > DegreesToRadians(170d))
            {
                continue;
            }

            foreach (var orientation in orientations)
            {
                var points = PointsOnArc(center, radius, orientation, spread, count);
                if (AllInside(points, safe)
                    && AreSeparated(points, spacing)
                    && ClearOfObstacles(points, obstacles, spacing))
                {
                    return points;
                }
            }
        }

        return ArrangeSubmenuGridFallback(
            center,
            parent,
            count,
            spacing,
            preferredAngle,
            safe,
            obstacles);
    }

    public static Point ClampElementTopLeft(
        Point desiredTopLeft,
        Size elementSize,
        Size canvasSize,
        double margin)
    {
        var minX = Math.Max(0d, margin);
        var minY = Math.Max(0d, margin);
        var maxX = Math.Max(minX, canvasSize.Width - Math.Max(0d, elementSize.Width) - minX);
        var maxY = Math.Max(minY, canvasSize.Height - Math.Max(0d, elementSize.Height) - minY);
        return new Point(
            Math.Clamp(desiredTopLeft.X, minX, maxX),
            Math.Clamp(desiredTopLeft.Y, minY, maxY));
    }

    private static Rect SafeCenterBounds(Size canvasSize, double nodeDiameter, double margin)
    {
        var inset = Math.Max(0d, nodeDiameter / 2d + margin);
        var minX = Math.Min(inset, Math.Max(0d, canvasSize.Width / 2d));
        var minY = Math.Min(inset, Math.Max(0d, canvasSize.Height / 2d));
        var maxX = Math.Max(minX, canvasSize.Width - inset);
        var maxY = Math.Max(minY, canvasSize.Height - inset);
        return new Rect(new Point(minX, minY), new Point(maxX, maxY));
    }

    private static IReadOnlyList<Edge> BlockedEdges(IReadOnlyList<Point> points, Rect safe)
    {
        var result = new List<Edge>(4);
        if (points.Any(point => point.X < safe.Left - Epsilon))
        {
            result.Add(Edge.Left);
        }
        if (points.Any(point => point.X > safe.Right + Epsilon))
        {
            result.Add(Edge.Right);
        }
        if (points.Any(point => point.Y < safe.Top - Epsilon))
        {
            result.Add(Edge.Top);
        }
        if (points.Any(point => point.Y > safe.Bottom + Epsilon))
        {
            result.Add(Edge.Bottom);
        }
        return result;
    }

    private static Point[] ArrangeSemicircle(
        Point center,
        int count,
        double desiredRadius,
        double spacing,
        Rect safe,
        Edge edge)
    {
        var radiusForSpacing = count <= 1
            ? desiredRadius
            : spacing / (2d * Math.Sin(Math.PI / (2d * Math.Max(1, count - 1))));
        var radius = Math.Max(desiredRadius, radiusForSpacing * 1.015d);
        var inwardAngle = edge switch
        {
            Edge.Left => 0d,
            Edge.Right => Math.PI,
            Edge.Top => Math.PI / 2d,
            Edge.Bottom => -Math.PI / 2d,
            _ => 0d,
        };

        var anchor = edge switch
        {
            Edge.Left => new Point(Math.Max(center.X, safe.Left), ClampAxis(center.Y, safe.Top, safe.Bottom, radius)),
            Edge.Right => new Point(Math.Min(center.X, safe.Right), ClampAxis(center.Y, safe.Top, safe.Bottom, radius)),
            Edge.Top => new Point(ClampAxis(center.X, safe.Left, safe.Right, radius), Math.Max(center.Y, safe.Top)),
            Edge.Bottom => new Point(ClampAxis(center.X, safe.Left, safe.Right, radius), Math.Min(center.Y, safe.Bottom)),
            _ => center,
        };

        var rankedSlots = Enumerable.Range(0, count)
            .Select(index => new
            {
                Index = index,
                Delta = NormalizeAngle((-Math.PI / 2d + index * Math.PI * 2d / count) - (inwardAngle + Math.PI)),
            })
            .OrderBy(item => item.Delta)
            .ToArray();
        var result = new Point[count];
        for (var rank = 0; rank < rankedSlots.Length; rank++)
        {
            var offset = count == 1
                ? 0d
                : -Math.PI / 2d + rank * Math.PI / (count - 1d);
            var angle = inwardAngle - offset;
            result[rankedSlots[rank].Index] = PointOnCircle(anchor, radius, angle);
        }
        return result;
    }

    private static Point[] ArrangeCorner(
        Point center,
        int count,
        double desiredRadius,
        double spacing,
        Rect safe,
        IReadOnlyList<Edge> blocked)
    {
        var isLeft = blocked.Contains(Edge.Left);
        var isTop = blocked.Contains(Edge.Top);
        var xSign = isLeft ? 1d : -1d;
        var ySign = isTop ? 1d : -1d;
        var anchor = new Point(
            isLeft ? Math.Max(center.X, safe.Left) : Math.Min(center.X, safe.Right),
            isTop ? Math.Max(center.Y, safe.Top) : Math.Min(center.Y, safe.Bottom));

        var candidates = new List<Point>(count);
        if (count <= 4)
        {
            var step = count <= 1 ? 0d : Math.PI / 2d / (count - 1d);
            var required = count <= 1 ? desiredRadius : spacing / (2d * Math.Sin(Math.Max(step, Epsilon) / 2d));
            AddQuarterArc(candidates, anchor, count, Math.Max(desiredRadius, required * 1.015d), xSign, ySign);
        }
        else
        {
            var innerCount = Math.Min(3, count / 2);
            var outerCount = count - innerCount;
            var innerRadius = Math.Max(desiredRadius, RequiredQuarterRadius(innerCount, spacing));
            var outerRadius = Math.Max(
                desiredRadius * 1.88d,
                Math.Max(RequiredQuarterRadius(outerCount, spacing), innerRadius + spacing * 1.30d));
            AddQuarterArc(candidates, anchor, innerCount, innerRadius, xSign, ySign, endpointInsetDegrees: 18d);
            AddQuarterArc(candidates, anchor, outerCount, outerRadius, xSign, ySign);
        }

        var orderedTargets = candidates
            .OrderBy(point => Math.Atan2(point.Y - center.Y, point.X - center.X))
            .ThenBy(point => DistanceSquared(point, center))
            .ToArray();
        var slotOrder = Enumerable.Range(0, count)
            .OrderBy(index => NormalizePositive(-Math.PI / 2d + index * Math.PI * 2d / count))
            .ToArray();
        var result = new Point[count];
        for (var index = 0; index < count; index++)
        {
            result[slotOrder[index]] = orderedTargets[index];
        }
        return result;
    }

    private static void AddQuarterArc(
        ICollection<Point> target,
        Point anchor,
        int count,
        double radius,
        double xSign,
        double ySign,
        double endpointInsetDegrees = 0d)
    {
        if (count <= 0)
        {
            return;
        }
        var inset = DegreesToRadians(endpointInsetDegrees);
        var span = Math.Max(0d, Math.PI / 2d - inset * 2d);
        for (var index = 0; index < count; index++)
        {
            var angle = count == 1 ? Math.PI / 4d : inset + index * span / (count - 1d);
            target.Add(new Point(
                anchor.X + xSign * Math.Cos(angle) * radius,
                anchor.Y + ySign * Math.Sin(angle) * radius));
        }
    }

    private static double RequiredQuarterRadius(int count, double spacing)
    {
        if (count <= 1)
        {
            return 0d;
        }
        var step = Math.PI / 2d / (count - 1d);
        return spacing / (2d * Math.Sin(step / 2d)) * 1.015d;
    }

    private static Point[] PointsOnArc(Point center, double radius, double orientation, double spread, int count)
    {
        var points = new Point[count];
        for (var index = 0; index < count; index++)
        {
            var angle = count == 1
                ? orientation
                : orientation - spread / 2d + index * spread / (count - 1d);
            points[index] = PointOnCircle(center, radius, angle);
        }
        return points;
    }

    private static IReadOnlyList<double> CandidateOrientations(double preferred, double inward)
    {
        var candidates = new List<double> { preferred, inward };
        for (var degrees = 15; degrees <= 180; degrees += 15)
        {
            candidates.Add(preferred + DegreesToRadians(degrees));
            candidates.Add(preferred - DegreesToRadians(degrees));
        }
        for (var degrees = 15; degrees <= 90; degrees += 15)
        {
            candidates.Add(inward + DegreesToRadians(degrees));
            candidates.Add(inward - DegreesToRadians(degrees));
        }
        return candidates
            .Select(NormalizeAngle)
            .DistinctBy(angle => Math.Round(angle, 6))
            .OrderBy(angle => AngularDistance(angle, preferred) + AngularDistance(angle, inward) * 0.35d)
            .ToArray();
    }

    private static IReadOnlyList<Point> ArrangeGridFallback(
        Point center,
        int count,
        double spacing,
        double desiredRadius,
        Rect safe)
    {
        var candidates = GridCandidates(safe, spacing)
            .Where(point => Distance(point, center) >= Math.Min(desiredRadius * 0.58d, spacing * 1.1d))
            .OrderBy(point => Math.Abs(Distance(point, center) - desiredRadius * 1.25d))
            .ThenBy(point => Math.Atan2(point.Y - center.Y, point.X - center.X))
            .Take(count)
            .OrderBy(point => NormalizePositive(Math.Atan2(point.Y - center.Y, point.X - center.X) + Math.PI / 2d))
            .ToArray();
        return candidates.Length == count
            ? candidates
            : ClampIdealFallback(center, count, desiredRadius, safe);
    }

    private static IReadOnlyList<Point> ArrangeSubmenuGridFallback(
        Point center,
        Point parent,
        int count,
        double spacing,
        double preferredAngle,
        Rect safe,
        IReadOnlyList<Point> obstacles)
    {
        var direction = new Vector(Math.Cos(preferredAngle), Math.Sin(preferredAngle));
        var candidates = GridCandidates(safe, spacing)
            .Where(point => obstacles.All(obstacle => Distance(point, obstacle) + Epsilon >= spacing))
            .OrderBy(point =>
            {
                var fromParent = point - parent;
                var backwardsPenalty = Vector.Multiply(fromParent, direction) < 0d ? spacing * 4d : 0d;
                var angularPenalty = AngularDistance(Math.Atan2(fromParent.Y, fromParent.X), preferredAngle) * spacing;
                return fromParent.Length + backwardsPenalty + angularPenalty;
            })
            .Take(count)
            .OrderBy(point => Math.Atan2(point.Y - center.Y, point.X - center.X))
            .ToArray();
        if (candidates.Length == count)
        {
            return candidates;
        }

        var unclipped = PointsOnArc(center, spacing * 2.4d, preferredAngle, DegreesToRadians(150d), count);
        return unclipped.Select(point => Clamp(point, safe)).ToArray();
    }

    private static IEnumerable<Point> GridCandidates(Rect safe, double spacing)
    {
        var rowStep = spacing * Math.Sqrt(3d) / 2d;
        var row = 0;
        for (var y = safe.Top; y <= safe.Bottom + Epsilon; y += rowStep, row++)
        {
            var offset = row % 2 == 0 ? 0d : spacing / 2d;
            for (var x = safe.Left + offset; x <= safe.Right + Epsilon; x += spacing)
            {
                yield return new Point(x, y);
            }
        }
    }

    private static Point[] ClampIdealFallback(Point center, int count, double radius, Rect safe) =>
        Enumerable.Range(0, count)
            .Select(index => Clamp(
                PointOnCircle(center, radius, -Math.PI / 2d + index * Math.PI * 2d / count),
                safe))
            .ToArray();

    private static double ClampAxis(double value, double minimum, double maximum, double radius)
    {
        if (maximum - minimum < radius * 2d)
        {
            return (minimum + maximum) / 2d;
        }
        return Math.Clamp(value, minimum + radius, maximum - radius);
    }

    private static double MostAvailableDirection(Point center, Rect safe)
    {
        var horizontal = (safe.Right - center.X) - (center.X - safe.Left);
        var vertical = (safe.Bottom - center.Y) - (center.Y - safe.Top);
        if (Math.Abs(horizontal) < Epsilon && Math.Abs(vertical) < Epsilon)
        {
            return -Math.PI / 2d;
        }
        return Math.Atan2(vertical, horizontal);
    }

    private static bool AllInside(IEnumerable<Point> points, Rect safe) =>
        points.All(point => point.X >= safe.Left - Epsilon
            && point.X <= safe.Right + Epsilon
            && point.Y >= safe.Top - Epsilon
            && point.Y <= safe.Bottom + Epsilon);

    private static bool AreSeparated(IReadOnlyList<Point> points, double spacing)
    {
        for (var left = 0; left < points.Count; left++)
        {
            for (var right = left + 1; right < points.Count; right++)
            {
                if (Distance(points[left], points[right]) + Epsilon < spacing)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool ClearOfObstacles(
        IReadOnlyList<Point> points,
        IReadOnlyList<Point> obstacles,
        double spacing) =>
        points.All(point => obstacles.All(obstacle => Distance(point, obstacle) + Epsilon >= spacing));

    private static Point Clamp(Point point, Rect bounds) => new(
        Math.Clamp(point.X, bounds.Left, bounds.Right),
        Math.Clamp(point.Y, bounds.Top, bounds.Bottom));

    private static Point PointOnCircle(Point center, double radius, double angle) =>
        new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);

    private static double Distance(Point left, Point right) => Math.Sqrt(DistanceSquared(left, right));

    private static double DistanceSquared(Point left, Point right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return dx * dx + dy * dy;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private static double AngularDistance(double left, double right) => Math.Abs(NormalizeAngle(left - right));

    private static double NormalizeAngle(double angle)
    {
        while (angle <= -Math.PI)
        {
            angle += Math.PI * 2d;
        }
        while (angle > Math.PI)
        {
            angle -= Math.PI * 2d;
        }
        return angle;
    }

    private static double NormalizePositive(double angle)
    {
        angle %= Math.PI * 2d;
        return angle < 0d ? angle + Math.PI * 2d : angle;
    }

    private enum Edge
    {
        Left,
        Right,
        Top,
        Bottom,
    }

    private static bool IsHorizontal(this Edge edge) => edge is Edge.Left or Edge.Right;
}
