using System.Windows;
using ActionsRing.App.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class ConfigurationLabelLayoutTests
{
    [TestMethod]
    public void CrowdedSubmenuCaptionsDoNotOverlapEachOtherOrBubbles()
    {
        Point[] centers = [new(186, 75), new(151, 115), new(140, 167), new(151, 221), new(186, 264)];
        var bubbles = centers.Select(center => new Rect(center.X - 24, center.Y - 24, 48, 48)).ToArray();
        var labels = centers.Select((center, index) => new ConfigurationLabel(index, center, 48,
            new Size(110, index % 2 == 0 ? 43 : 30), new Rect(14, center.Y - 15, 110, index % 2 == 0 ? 43 : 30), 0));
        var placements = ConfigurationLabelLayout.Arrange(labels, bubbles, new Rect(0, 0, 580, 340), 14);
        Assert.AreEqual(5, placements.Count);
        var bounds = placements.Values.ToArray();
        for (var i = 0; i < bounds.Length; i++)
        {
            Assert.IsTrue(new Rect(14, 14, 552, 312).Contains(bounds[i]));
            Assert.IsFalse(bubbles.Any(bubble => bubble.IntersectsWith(bounds[i])));
            for (var j = i + 1; j < bounds.Length; j++) Assert.IsFalse(bounds[i].IntersectsWith(bounds[j]));
        }
    }

    [TestMethod]
    public void FocusedCaptionHasPriorityWhenSpaceIsLimited()
    {
        var bounds = new Rect(0, 0, 200, 80);
        var preferred = new Rect(20, 20, 150, 40);
        ConfigurationLabel[] labels = [new(0, new(100, 100), 48, preferred.Size, preferred, 0), new(1, new(100, 100), 48, preferred.Size, preferred, 2)];
        var placements = ConfigurationLabelLayout.Arrange(labels, [], bounds, 14);
        Assert.AreEqual(1, placements.Count);
        Assert.IsTrue(placements.ContainsKey(1));
    }
}
