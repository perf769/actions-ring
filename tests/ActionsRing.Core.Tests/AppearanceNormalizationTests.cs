using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.Core.Tests;

[TestClass]
public sealed class AppearanceNormalizationTests
{
    [TestMethod]
    public void Defaults_IncludeCompleteCustomPalette()
    {
        var ring = ConfigurationDefaults.CreateDefaultRing();

        Assert.AreEqual("#F5F5F3", ring.Appearance.BubbleColor);
        Assert.AreEqual("#050607", ring.Appearance.BubbleHoverColor);
        Assert.AreEqual("#101316", ring.Appearance.IconColor);
        Assert.AreEqual("#FFFFFF", ring.Appearance.IconHoverColor);
        Assert.IsTrue(ConfigurationValidator.Validate(ConfigurationDefaults.Create()).IsValid);
    }

    [TestMethod]
    public void Normalize_RepairsRingPaletteAndDropsInvalidSlotOverrides()
    {
        var configuration = ConfigurationDefaults.Create();
        var ring = configuration.GlobalProfile.RootRing;
        ring.Appearance = new RingAppearanceDefinition
        {
            BubbleColor = "#aabbcc",
            BubbleHoverColor = "broken",
            IconColor = "#80112233",
            IconHoverColor = string.Empty,
        };
        ring.Slots[0].AppearanceOverride = new RingSlotAppearanceDefinition
        {
            BubbleColor = "#010203",
            BubbleHoverColor = "invalid",
        };
        ring.Slots[1].AppearanceOverride = new RingSlotAppearanceDefinition
        {
            IconColor = "invalid",
        };

        var result = ConfigurationNormalizer.Normalize(configuration);

        Assert.AreEqual("#AABBCC", ring.Appearance.BubbleColor);
        Assert.AreEqual(RingAppearanceDefinition.DefaultBubbleHoverColor, ring.Appearance.BubbleHoverColor);
        Assert.AreEqual("#80112233", ring.Appearance.IconColor);
        Assert.AreEqual(RingAppearanceDefinition.DefaultIconHoverColor, ring.Appearance.IconHoverColor);
        Assert.AreEqual("#010203", ring.Slots[0].AppearanceOverride!.BubbleColor);
        Assert.IsNull(ring.Slots[0].AppearanceOverride!.BubbleHoverColor);
        Assert.IsNull(ring.Slots[1].AppearanceOverride);
        Assert.IsTrue(result.Issues.Count >= 4);
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).IsValid);
    }

    [TestMethod]
    public void Json_RoundTripPreservesPerSlotPaletteOverrides()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.GlobalProfile.RootRing.Slots[0].AppearanceOverride = new RingSlotAppearanceDefinition
        {
            BubbleColor = "#112233",
            IconHoverColor = "#445566",
        };

        var roundTripped = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(configuration));
        var appearance = roundTripped.GlobalProfile.RootRing.Slots[0].AppearanceOverride;

        Assert.IsNotNull(appearance);
        Assert.AreEqual("#112233", appearance.BubbleColor);
        Assert.AreEqual("#445566", appearance.IconHoverColor);
        Assert.IsNull(appearance.IconColor);
    }

    [TestMethod]
    public void Validator_RejectsInvalidRawPalette()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.GlobalProfile.RootRing.Appearance.IconHoverColor = "white";

        var validation = ConfigurationValidator.Validate(configuration);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Issues.Any(issue =>
            issue.Path.EndsWith("appearance.iconHoverColor", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RestoreSlots_KeepsRingIdentityAndCustomPalette()
    {
        var ring = new RingDefinition
        {
            Id = "my-ring",
            Name = "Работа",
            SlotCount = 4,
            Slots = Enumerable.Range(0, 4).Select(RingSlotDefinition.Empty).ToList(),
            Appearance = new RingAppearanceDefinition { BubbleColor = "#112233" },
        };

        RingDefaults.RestoreSlots(ring);

        Assert.AreEqual("my-ring", ring.Id);
        Assert.AreEqual("Работа", ring.Name);
        Assert.AreEqual("#112233", ring.Appearance.BubbleColor);
        Assert.AreEqual(8, ring.SlotCount);
        Assert.AreEqual(BuiltInCommand.Copy, ring.Slots[0].Action!.BuiltIn!.Command);
    }

    [TestMethod]
    public void Resolver_LayersOnlyConfiguredSlotChannels()
    {
        var ring = new RingAppearanceDefinition
        {
            BubbleColor = "#111111",
            BubbleHoverColor = "#222222",
            IconColor = "#333333",
            IconHoverColor = "#444444",
        };

        var resolved = RingAppearanceResolver.Resolve(ring, new RingSlotAppearanceDefinition
        {
            BubbleColor = "#AAAAAA",
            IconHoverColor = "#BBBBBB",
        });

        Assert.AreEqual("#AAAAAA", resolved.BubbleColor);
        Assert.AreEqual("#222222", resolved.BubbleHoverColor);
        Assert.AreEqual("#333333", resolved.IconColor);
        Assert.AreEqual("#BBBBBB", resolved.IconHoverColor);
    }
}
