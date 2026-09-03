using System.Windows;
using System.Windows.Controls;
using ActionsRing.App.Controls;
using ActionsRing.Core.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class RingLayoutGeometryTests
{
    private const double NodeDiameter = 48d;
    private const double Margin = 4d;
    private const double MinimumSpacing = 52.79d;

    [STATestMethod]
    public void RingScaleClampsToUiSupportedRange()
    {
        var control = new RingMenuControl();

        control.RingScale = 0.15d;
        Assert.AreEqual(0.60d, control.RingScale, 0.001d);

        control.RingScale = 2.75d;
        Assert.AreEqual(2.00d, control.RingScale, 0.001d);
    }

    [STATestMethod]
    public void PresentBeforeFirstLayoutBuildsRingWhenSizeBecomesAvailable()
    {
        var control = new RingMenuControl { AnimationsEnabled = false };
        var ring = ConfigurationDefaults.CreateDefaultRing();

        control.Present(ring, animate: false);
        control.Measure(new Size(420, 320));
        control.Arrange(new Rect(0, 0, 420, 320));
        control.UpdateLayout();

        var canvas = (Canvas)control.FindName("RingCanvas");
        Assert.AreEqual(ring.SlotCount + 1, canvas.Children.Count);
    }

    [TestMethod]
    public void RootInOpenSpacePreservesCircularLayout()
    {
        var center = new Point(960, 540);

        var points = RingLayoutGeometry.ArrangeRoot(center, new Size(1920, 1080), 8, NodeDiameter, 82, Margin);

        Assert.AreEqual(8, points.Count);
        Assert.AreEqual(center.X, points[0].X, 0.001);
        Assert.AreEqual(center.Y - 82, points[0].Y, 0.001);
        foreach (var point in points)
        {
            Assert.AreEqual(82, Distance(center, point), 0.001);
        }
        AssertSeparated(points, MinimumSpacing);
    }

    [DataTestMethod]
    [DataRow(0d, 0d)]
    [DataRow(800d, 0d)]
    [DataRow(0d, 600d)]
    [DataRow(800d, 600d)]
    [DataRow(400d, 0d)]
    [DataRow(0d, 300d)]
    [DataRow(800d, 300d)]
    [DataRow(400d, 600d)]
    public void RootAtMonitorEdgesStaysVisibleAndSeparated(double centerX, double centerY)
    {
        var points = RingLayoutGeometry.ArrangeRoot(
            new Point(centerX, centerY),
            new Size(800, 600),
            8,
            NodeDiameter,
            82,
            Margin);

        AssertInside(points, new Size(800, 600), NodeDiameter, Margin);
        AssertSeparated(points, MinimumSpacing);
    }

    [TestMethod]
    public void LargeScaledRootFitsInCompactMonitorCorner()
    {
        const double scale = 1.65d;
        var diameter = NodeDiameter * scale;
        var points = RingLayoutGeometry.ArrangeRoot(
            new Point(0, 0),
            new Size(800, 600),
            8,
            diameter,
            82 * scale,
            Margin * scale);

        AssertInside(points, new Size(800, 600), diameter, Margin * scale);
        AssertSeparated(points, diameter + Math.Max(4d, diameter * 0.10d) - 0.02d);
    }

    [TestMethod]
    public void RootGeometryIsStableAcrossSupportedCountsScalesAndEdgePositions()
    {
        var canvas = new Size(1366, 768);
        var centers = new[]
        {
            new Point(0, 0),
            new Point(canvas.Width, 0),
            new Point(0, canvas.Height),
            new Point(canvas.Width, canvas.Height),
            new Point(canvas.Width / 2d, 0),
            new Point(0, canvas.Height / 2d),
            new Point(74, 74),
            new Point(canvas.Width - 74, canvas.Height - 74),
        };

        foreach (var scale in new[] { 0.62d, 1d, 1.65d })
        {
            var diameter = NodeDiameter * scale;
            var margin = Margin * scale;
            var minimum = diameter + Math.Max(4d, diameter * 0.10d) - 0.02d;
            foreach (var count in Enumerable.Range(4, 5))
            {
                foreach (var center in centers)
                {
                    var points = RingLayoutGeometry.ArrangeRoot(
                        center,
                        canvas,
                        count,
                        diameter,
                        82 * scale,
                        margin);

                    Assert.AreEqual(count, points.Count);
                    AssertInside(points, canvas, diameter, margin);
                    AssertSeparated(points, minimum);
                }
            }
        }
    }

    [DataTestMethod]
    [DataRow(0d, 0d)]
    [DataRow(1920d, 0d)]
    [DataRow(0d, 1080d)]
    [DataRow(1920d, 1080d)]
    [DataRow(960d, 0d)]
    [DataRow(0d, 540d)]
    public void NineItemSubmenuAtEdgesAvoidsRootAndMonitorEdges(double centerX, double centerY)
    {
        var canvas = new Size(1920, 1080);
        var center = new Point(centerX, centerY);
        var root = RingLayoutGeometry.ArrangeRoot(center, canvas, 8, NodeDiameter, 82, Margin);
        var parent = root.OrderByDescending(point => Distance(point, center)).First();
        var parentAngle = Math.Atan2(parent.Y - center.Y, parent.X - center.X);
        var obstacles = root.Append(center).ToArray();

        var submenu = RingLayoutGeometry.ArrangeSubmenu(
            center,
            parent,
            canvas,
            9,
            NodeDiameter,
            183,
            parentAngle,
            obstacles,
            Margin);

        Assert.AreEqual(9, submenu.Count);
        AssertInside(submenu, canvas, NodeDiameter, Margin);
        AssertSeparated(submenu, MinimumSpacing);
        foreach (var point in submenu)
        {
            Assert.IsTrue(obstacles.All(obstacle => Distance(point, obstacle) >= MinimumSpacing));
        }
    }

    [DataTestMethod]
    [DataRow(-80d, -40d, 4d, 4d)]
    [DataRow(780d, 590d, 674d, 556d)]
    public void TooltipTopLeftIsClampedIntoCanvas(
        double desiredX,
        double desiredY,
        double expectedX,
        double expectedY)
    {
        var result = RingLayoutGeometry.ClampElementTopLeft(
            new Point(desiredX, desiredY),
            new Size(122, 40),
            new Size(800, 600),
            4);

        Assert.AreEqual(expectedX, result.X, 0.001);
        Assert.AreEqual(expectedY, result.Y, 0.001);
    }

    [TestMethod]
    public void LongUpperRightTooltipMovesAboveInsteadOfOverlappingBubble()
    {
        var center = new Point(400, 135);
        var bounds = new Rect(0, 0, 600, 420);
        var labelSize = new Size(205, 34);

        var placement = RingTooltipGeometry.Place(
            center,
            NodeDiameter,
            labelSize,
            bounds,
            -Math.PI / 4d,
            12,
            8);

        Assert.AreEqual(RingTooltipSide.Top, placement.Side);
        AssertTooltipInsideAndClear(placement.TopLeft, labelSize, center, NodeDiameter, bounds, 8);
    }

    [DataTestMethod]
    [DataRow(28d, 28d, -2.356194490192345d)]
    [DataRow(772d, 28d, -0.7853981633974483d)]
    [DataRow(28d, 572d, 2.356194490192345d)]
    [DataRow(772d, 572d, 0.7853981633974483d)]
    public void TooltipAtMonitorCornersStaysVisibleAndClearOfBubble(
        double centerX,
        double centerY,
        double angle)
    {
        var center = new Point(centerX, centerY);
        var bounds = new Rect(0, 0, 800, 600);
        var labelSize = new Size(230, 58);

        var placement = RingTooltipGeometry.Place(
            center,
            NodeDiameter,
            labelSize,
            bounds,
            angle,
            12,
            8);

        AssertTooltipInsideAndClear(placement.TopLeft, labelSize, center, NodeDiameter, bounds, 8);
    }

    [TestMethod]
    public void TooltipHonorsOffsetMonitorWorkArea()
    {
        var center = new Point(310, 260);
        var workArea = new Rect(48, 24, 752, 536);
        var labelSize = new Size(260, 40);

        var placement = RingTooltipGeometry.Place(
            center,
            NodeDiameter * 2,
            labelSize,
            workArea,
            Math.PI,
            24,
            16);

        AssertTooltipInsideAndClear(
            placement.TopLeft,
            labelSize,
            center,
            NodeDiameter * 2,
            workArea,
            16);
    }

    private static void AssertInside(
        IReadOnlyList<Point> points,
        Size canvas,
        double diameter,
        double margin)
    {
        var inset = diameter / 2d + margin;
        foreach (var point in points)
        {
            Assert.IsTrue(point.X >= inset - 0.01, $"X={point.X} is left of {inset}.");
            Assert.IsTrue(point.X <= canvas.Width - inset + 0.01, $"X={point.X} is right of {canvas.Width - inset}.");
            Assert.IsTrue(point.Y >= inset - 0.01, $"Y={point.Y} is above {inset}.");
            Assert.IsTrue(point.Y <= canvas.Height - inset + 0.01, $"Y={point.Y} is below {canvas.Height - inset}.");
        }
    }

    private static void AssertSeparated(IReadOnlyList<Point> points, double minimum)
    {
        for (var left = 0; left < points.Count; left++)
        {
            for (var right = left + 1; right < points.Count; right++)
            {
                Assert.IsTrue(
                    Distance(points[left], points[right]) >= minimum,
                    $"Items {left} and {right} are too close.");
            }
        }
    }

    private static void AssertTooltipInsideAndClear(
        Point topLeft,
        Size labelSize,
        Point nodeCenter,
        double nodeDiameter,
        Rect bounds,
        double margin)
    {
        var label = new Rect(topLeft, labelSize);
        var bubble = new Rect(
            nodeCenter.X - nodeDiameter / 2d,
            nodeCenter.Y - nodeDiameter / 2d,
            nodeDiameter,
            nodeDiameter);
        Assert.IsTrue(label.Left >= bounds.Left + margin - 0.001);
        Assert.IsTrue(label.Top >= bounds.Top + margin - 0.001);
        Assert.IsTrue(label.Right <= bounds.Right - margin + 0.001);
        Assert.IsTrue(label.Bottom <= bounds.Bottom - margin + 0.001);
        Assert.IsFalse(label.IntersectsWith(bubble), $"Tooltip {label} overlaps bubble {bubble}.");
    }

    private static double Distance(Point left, Point right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
