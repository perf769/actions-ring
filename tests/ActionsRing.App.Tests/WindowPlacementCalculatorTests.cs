using System.Windows;
using ActionsRing.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class WindowPlacementCalculatorTests
{
    [TestMethod]
    public void FitCentered_MainWindowFits1366By768At150PercentScaling()
    {
        var workArea = new Rect(0, 0, 1366d / 1.5d, 720d / 1.5d);

        var result = WindowPlacementCalculator.FitCentered(
            requestedWidth: 1180,
            requestedHeight: 740,
            minimumWidth: 820,
            minimumHeight: 440,
            workArea);

        Assert.AreEqual(1366d / 1.5d - 24d, result.Width, 0.01d);
        Assert.AreEqual(456d, result.Height, 0.01d);
        Assert.IsTrue(result.Left >= workArea.Left);
        Assert.IsTrue(result.Top >= workArea.Top);
        Assert.IsTrue(result.Right <= workArea.Right);
        Assert.IsTrue(result.Bottom <= workArea.Bottom);
    }

    [TestMethod]
    public void FitCentered_ActionEditorFits1366By768At150PercentScaling()
    {
        // A 48 px taskbar leaves 1366x720 physical pixels, or about
        // 911x480 WPF DIPs at 150% display scaling.
        var workArea = new Rect(0, 0, 1366d / 1.5d, 720d / 1.5d);

        var result = WindowPlacementCalculator.FitCentered(
            requestedWidth: 650,
            requestedHeight: 650,
            minimumWidth: 480,
            minimumHeight: 420,
            workArea);

        Assert.AreEqual(650d, result.Width, 0.01d);
        Assert.AreEqual(456d, result.Height, 0.01d);
        Assert.IsTrue(result.Left >= workArea.Left);
        Assert.IsTrue(result.Top >= workArea.Top);
        Assert.IsTrue(result.Right <= workArea.Right);
        Assert.IsTrue(result.Bottom <= workArea.Bottom);
    }

    [TestMethod]
    public void FitCentered_FolderEditorFits1366By768At200PercentScaling()
    {
        var workArea = new Rect(0, 0, 1366d / 2d, 720d / 2d);

        var result = WindowPlacementCalculator.FitCentered(
            requestedWidth: 520,
            requestedHeight: 390,
            minimumWidth: 480,
            minimumHeight: 360,
            workArea);

        Assert.AreEqual(520d, result.Width, 0.01d);
        Assert.AreEqual(336d, result.Height, 0.01d);
        Assert.IsTrue(result.Left >= workArea.Left);
        Assert.IsTrue(result.Top >= workArea.Top);
        Assert.IsTrue(result.Right <= workArea.Right);
        Assert.IsTrue(result.Bottom <= workArea.Bottom);
    }

    [TestMethod]
    public void FitCentered_WorkAreaSmallerThanDesignMinimumStillWins()
    {
        var workArea = new Rect(-400, 20, 400, 300);

        var result = WindowPlacementCalculator.FitCentered(
            requestedWidth: 650,
            requestedHeight: 650,
            minimumWidth: 480,
            minimumHeight: 420,
            workArea);

        Assert.AreEqual(376d, result.Width, 0.01d);
        Assert.AreEqual(276d, result.Height, 0.01d);
        Assert.IsTrue(result.Left >= workArea.Left);
        Assert.IsTrue(result.Top >= workArea.Top);
        Assert.IsTrue(result.Right <= workArea.Right);
        Assert.IsTrue(result.Bottom <= workArea.Bottom);
    }
}
