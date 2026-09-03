using ActionsRing.App.Controls;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class AdjustmentVisualFormatterTests
{
    [DataTestMethod]
    [DataRow(AdjustableParameter.SystemVolume, "Громкость")]
    [DataRow(AdjustableParameter.ScreenBrightness, "Яркость")]
    [DataRow(AdjustableParameter.Zoom, "Масштаб")]
    [DataRow(AdjustableParameter.VerticalScroll, "Прокрутка ↑↓")]
    [DataRow(AdjustableParameter.HorizontalScroll, "Прокрутка ←→")]
    [DataRow(AdjustableParameter.Custom, "Параметр")]
    public void Format_UsesLocalizedCompactParameterLabel(AdjustableParameter parameter, string expected)
    {
        var result = AdjustmentVisualFormatter.Format(Adjustment(parameter, 5), 0);

        Assert.AreEqual(expected, result.ParameterLabel);
    }

    [TestMethod]
    public void Format_ShowsNeutralWheelHintAndConfiguredPercentageStep()
    {
        var result = AdjustmentVisualFormatter.Format(
            Adjustment(AdjustableParameter.SystemVolume, -2.5),
            0);

        Assert.AreEqual("↕ ±2,5%", result.FeedbackText);
    }

    [DataTestMethod]
    [DataRow(1, "↑ +5%")]
    [DataRow(-1, "↓ −5%")]
    public void Format_ShowsVerticalWheelDirection(int direction, string expected)
    {
        var result = AdjustmentVisualFormatter.Format(
            Adjustment(AdjustableParameter.ScreenBrightness, 5),
            direction);

        Assert.AreEqual(expected, result.FeedbackText);
    }

    [DataTestMethod]
    [DataRow(1, "→ +3")]
    [DataRow(-1, "← −3")]
    public void Format_ShowsHorizontalWheelDirection(int direction, string expected)
    {
        var result = AdjustmentVisualFormatter.Format(
            Adjustment(AdjustableParameter.HorizontalScroll, 3),
            direction);

        Assert.AreEqual(expected, result.FeedbackText);
    }

    [TestMethod]
    public void Format_HandlesNonFiniteImportedValueDefensively()
    {
        var result = AdjustmentVisualFormatter.Format(
            Adjustment(AdjustableParameter.Zoom, double.NaN),
            1);

        Assert.AreEqual("↑ +0", result.FeedbackText);
    }

    [TestMethod]
    public void PreferredWidth_ReservesMoreSpaceForScrollLabels()
    {
        Assert.IsTrue(
            AdjustmentVisualFormatter.PreferredWidth(AdjustableParameter.VerticalScroll)
            > AdjustmentVisualFormatter.PreferredWidth(AdjustableParameter.SystemVolume));
    }

    private static AdjustParameterAction Adjustment(AdjustableParameter parameter, double value) => new()
    {
        Parameter = parameter,
        Value = value,
    };
}
