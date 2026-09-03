using ActionsRing.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class ColorValueTests
{
    [TestMethod]
    public void TryParse_AcceptsOpaqueAndAlphaHex()
    {
        Assert.IsTrue(ColorValue.TryParse("#12ABEF", out var opaque));
        Assert.AreEqual(new RgbColor(0x12, 0xAB, 0xEF), opaque);
        Assert.AreEqual("#12ABEF", opaque.ToHex());

        Assert.IsTrue(ColorValue.TryParse("#80112233", out var alpha));
        Assert.AreEqual(new RgbColor(0x11, 0x22, 0x33, 0x80), alpha);
        Assert.AreEqual("#80112233", alpha.ToHex());
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("#FFF")]
    [DataRow("112233")]
    [DataRow("#GG1122")]
    public void TryParse_RejectsInvalidPersistedColors(string? value)
    {
        Assert.IsFalse(ColorValue.TryParse(value, out _));
    }

    [TestMethod]
    public void HsvConversion_RoundTripsRepresentativeColors()
    {
        var colors = new[]
        {
            new RgbColor(255, 0, 0),
            new RgbColor(0, 255, 255),
            new RgbColor(130, 78, 249),
            new RgbColor(17, 34, 51, 128),
            new RgbColor(240, 240, 240),
        };

        foreach (var color in colors)
        {
            Assert.AreEqual(color, ColorValue.FromHsv(ColorValue.ToHsv(color)));
        }
    }

    [TestMethod]
    public void FromHsv_NormalizesHueAndClampsChannels()
    {
        var color = ColorValue.FromHsv(new HsvColor(720 + 120, 2, 2));

        Assert.AreEqual(new RgbColor(0, 255, 0), color);
    }
}
