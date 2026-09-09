using System.Windows;
using System.Windows.Media;

namespace ActionsRing.App.Controls;

/// <summary>Continuous droplet geometry shared by the submenu animation and its resting hint.</summary>
internal static class RingSubmenuMotion
{
    public static double Ease(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return progress * progress * (3 - 2 * progress);
    }

    public static Point Lerp(Point from, Point to, double progress) => from + (to - from) * progress;

    public static Geometry AttachedSurface(Point center, double radius, double angle)
    {
        var lobeRadius = radius * 0.25;
        var lobe = center + new Vector(Math.Cos(angle), Math.Sin(angle)) * (radius + lobeRadius * 0.2);
        return Surface(center, radius, lobe, lobeRadius, 1);
    }

    public static Geometry Surface(Point parent, double parentRadius, Point child, double childRadius, double strength) =>
        new CombinedGeometry(GeometryCombineMode.Union,
            new CombinedGeometry(GeometryCombineMode.Union,
                new EllipseGeometry(parent, parentRadius, parentRadius),
                new EllipseGeometry(child, childRadius, childRadius)),
            Bridge(parent, parentRadius, child, childRadius, strength));

    public static Geometry Bridge(Point parent, double parentRadius, Point child, double childRadius, double strength)
    {
        var delta = child - parent;
        var distance = delta.Length;
        if (distance <= Math.Abs(parentRadius - childRadius) + 0.01 || strength <= 0.001)
        {
            return Geometry.Empty;
        }
        var angle = Math.Atan2(delta.Y, delta.X);
        var overlap = distance < parentRadius + childRadius;
        var parentIntersection = overlap
            ? Math.Acos(Math.Clamp((parentRadius * parentRadius + distance * distance - childRadius * childRadius) / (2 * parentRadius * distance), -1, 1))
            : 0;
        var childIntersection = overlap
            ? Math.Acos(Math.Clamp((childRadius * childRadius + distance * distance - parentRadius * parentRadius) / (2 * childRadius * distance), -1, 1))
            : 0;
        var tangentAngle = Math.Acos(Math.Clamp((parentRadius - childRadius) / distance, -1, 1));
        var blend = 0.52 * Math.Clamp(strength, 0, 1);
        var aAngle = angle + parentIntersection + (tangentAngle - parentIntersection) * blend;
        var dAngle = angle - parentIntersection - (tangentAngle - parentIntersection) * blend;
        var bAngle = angle + Math.PI - childIntersection - (Math.PI - childIntersection - tangentAngle) * blend;
        var cAngle = angle - Math.PI + childIntersection + (Math.PI - childIntersection - tangentAngle) * blend;
        var a = parent + Direction(aAngle) * parentRadius;
        var b = child + Direction(bAngle) * childRadius;
        var c = child + Direction(cAngle) * childRadius;
        var d = parent + Direction(dAngle) * parentRadius;
        var handles = Math.Min(blend * 2.4, (a - b).Length / (parentRadius + childRadius))
            * Math.Min(1, distance * 2 / (parentRadius + childRadius));
        var parentHandle = parentRadius * handles;
        var childHandle = childRadius * handles;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(a, true, true);
            context.BezierTo(a + Direction(aAngle - Math.PI / 2) * parentHandle,
                b + Direction(bAngle + Math.PI / 2) * childHandle, b, true, false);
            context.LineTo(c, true, false);
            context.BezierTo(c + Direction(cAngle - Math.PI / 2) * childHandle,
                d + Direction(dAngle + Math.PI / 2) * parentHandle, d, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Vector Direction(double angle) => new(Math.Cos(angle), Math.Sin(angle));
}
