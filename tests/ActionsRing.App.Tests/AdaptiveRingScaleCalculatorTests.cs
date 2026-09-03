using ActionsRing.App.Services;
using ActionsRing.Core.Configuration;
using ActionsRing.Platform.Windows.Display;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class AdaptiveRingScaleCalculatorTests
{
    [TestMethod]
    public void Calculate_UsesEffectiveMonitorSizeAfterDpiScaling()
    {
        var appearance = new AppearancePreferences { RingDiameter = 212, AutoScaleRing = true };
        var fullHd = Monitor(1920, 1080, 96);
        var fourKAtTwoHundredPercent = Monitor(3840, 2160, 192);

        Assert.AreEqual(1d, AdaptiveRingScaleCalculator.Calculate(appearance, fullHd), 0.001);
        Assert.AreEqual(1d, AdaptiveRingScaleCalculator.Calculate(appearance, fourKAtTwoHundredPercent), 0.001);
    }

    [TestMethod]
    public void Calculate_GrowsOnDenseWorkspaceAndShrinksOnSmallDisplay()
    {
        var appearance = new AppearancePreferences { RingDiameter = 212, AutoScaleRing = true };

        Assert.AreEqual(1.25d, AdaptiveRingScaleCalculator.Calculate(appearance, Monitor(3840, 2160, 96)), 0.001);
        Assert.IsTrue(AdaptiveRingScaleCalculator.Calculate(appearance, Monitor(1366, 768, 96)) < 0.90d);
    }

    [TestMethod]
    public void Calculate_DisabledAutoScaleUsesUserBaselineOnly()
    {
        var appearance = new AppearancePreferences { RingDiameter = 265, AutoScaleRing = false };

        Assert.AreEqual(1.25d, AdaptiveRingScaleCalculator.Calculate(appearance, Monitor(1024, 768, 144)), 0.001);
    }

    private static MonitorSnapshot Monitor(int width, int height, uint dpi) =>
        new(nint.Zero, "test", new ScreenRect(0, 0, width, height), new ScreenRect(0, 0, width, height), true, dpi, dpi);
}
