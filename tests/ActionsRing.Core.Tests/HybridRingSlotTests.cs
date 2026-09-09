using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.Core.Tests;

[TestClass]
public sealed class HybridRingSlotTests
{
    [TestMethod]
    public async Task SaveAndLoad_PreservesClickActionSubmenuIconAndAppearance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ActionsRing.Hybrid.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new JsonConfigurationStore(Path.Combine(directory, "settings.json"));
            var configuration = ConfigurationDefaults.Create();
            var folder = CreateHybridSlot();
            configuration.GlobalProfile.RootRing.Slots[0] = folder;

            await store.SaveAsync(configuration);
            var result = await store.LoadAsync();
            var restored = result.Configuration.GlobalProfile.RootRing.Slots[0];

            Assert.IsTrue(ConfigurationValidator.Validate(result.Configuration).IsValid);
            Assert.AreEqual(folder.Id, restored.Id);
            Assert.AreEqual("Работа с графикой", restored.Label);
            Assert.AreEqual("brand:adobephotoshop", restored.Icon);
            Assert.AreEqual(folder.Action!.Id, restored.Action!.Id);
            Assert.AreEqual(ActionKind.LaunchApplication, restored.Action.Kind);
            Assert.AreEqual(@"C:\Programs\Photoshop.exe", restored.Action.LaunchApplication!.ExecutablePath);
            Assert.AreEqual(folder.Submenu!.Id, restored.Submenu!.Id);
            Assert.AreEqual("B", restored.Submenu.Slots[0].Action!.KeyboardShortcut!.Chords[0].Key);
            Assert.AreEqual("#123456", restored.AppearanceOverride!.BubbleColor);
            Assert.AreEqual("#ABCDEF", restored.Submenu.Appearance.IconColor);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void VersionFourMigration_PreservesBothTargetsAndExistingUserSettings()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.SchemaVersion = 4;
        configuration.Preferences.Updates.DownloadAutomatically = true;
        configuration.GlobalProfile.RootRing.Slots[0] = CreateHybridSlot();
        configuration.GlobalProfile.RootRing.Slots[1] = RingSlotDefinition.ForSubmenu("Папка", CreateSubmenu());
        var originalAction = configuration.GlobalProfile.RootRing.Slots[2].Action!;

        var result = ConfigurationDocumentParser.Parse(ConfigurationJson.Serialize(configuration));

        Assert.IsTrue(result.WasMigrated);
        Assert.AreEqual(5, result.Configuration.SchemaVersion);
        Assert.IsTrue(result.Configuration.Preferences.Updates.DownloadAutomatically);
        var slots = result.Configuration.GlobalProfile.RootRing.Slots;
        Assert.AreEqual(ActionKind.LaunchApplication, slots[0].Action!.Kind);
        Assert.IsNotNull(slots[0].Submenu);
        Assert.IsTrue(slots[1].Action?.Kind is null or ActionKind.None);
        Assert.IsNotNull(slots[1].Submenu);
        Assert.AreEqual(originalAction.Id, slots[2].Action!.Id);
        Assert.AreEqual(originalAction.Kind, slots[2].Action!.Kind);
    }

    [TestMethod]
    public void Validator_ReportsInvalidClickActionAndInvalidSubmenuIndependently()
    {
        var configuration = ConfigurationDefaults.Create();
        var hybrid = CreateHybridSlot();
        hybrid.Action = ActionDefinition.Shortcut("Invalid", "Banana");
        hybrid.Submenu!.SlotCount = 0;
        configuration.GlobalProfile.RootRing.Slots[0] = hybrid;

        var result = ConfigurationValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Path.Contains("slots[0].action.keyboardShortcut", StringComparison.Ordinal)));
        Assert.IsTrue(result.Issues.Any(issue => issue.Path.EndsWith("submenu.slotCount", StringComparison.Ordinal)));
        Assert.IsFalse(result.Issues.Any(issue => issue.Code == "slot.target"));
    }

    [TestMethod]
    public void Normalizer_RepairsClickActionWithoutDiscardingItsSubmenu()
    {
        var configuration = ConfigurationDefaults.Create();
        var hybrid = CreateHybridSlot();
        hybrid.Action = ActionDefinition.Shortcut("Invalid", "Banana");
        configuration.GlobalProfile.RootRing.Slots[0] = hybrid;

        ConfigurationNormalizer.Normalize(configuration);

        Assert.AreEqual(ActionKind.None, hybrid.Action.Kind);
        Assert.IsNotNull(hybrid.Submenu);
        Assert.AreEqual("B", hybrid.Submenu.Slots[0].Action!.KeyboardShortcut!.Chords[0].Key);
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).IsValid);
    }

    [TestMethod]
    public void Normalizer_ExcessiveSubmenuDepthDoesNotDiscardValidClickAction()
    {
        var configuration = ConfigurationDefaults.Create();
        var ring = configuration.GlobalProfile.RootRing;
        RingSlotDefinition? deepest = null;
        for (var depth = 0; depth <= ConfigurationNormalizer.MaximumSubmenuDepth; depth++)
        {
            deepest = CreateHybridSlot();
            ring.Slots[0] = deepest;
            ring = deepest.Submenu!;
        }

        ConfigurationNormalizer.Normalize(configuration);

        Assert.IsNull(deepest!.Submenu);
        Assert.AreEqual(ActionKind.LaunchApplication, deepest.Action!.Kind);
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).IsValid);
    }

    [TestMethod]
    public void VersionFiveMigration_UpgradesOnlyRecognizedPhotoshopDefaults()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.SchemaVersion = 4;
        var slots = configuration.GlobalProfile.RootRing.Slots;
        slots[0] = RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Ластик", "E", icon: "cut"));
        slots[1] = RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Ластик", "E", icon: "image:my-eraser.png"));
        slots[2] = RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Ластик", "E", KeyboardModifiers.Control, "cut"));
        slots[3] = RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Удалить", "E", icon: "cut"));
        slots[4] = RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Кисть", "B", icon: "text"));
        slots[4].Icon = "lucide:paintbrush";

        var migrated = ConfigurationDocumentParser.Parse(ConfigurationJson.Serialize(configuration));
        var restored = migrated.Configuration.GlobalProfile.RootRing.Slots;

        Assert.AreEqual("lucide:eraser", restored[0].Icon);
        Assert.AreEqual("lucide:eraser", restored[0].Action!.Icon);
        Assert.AreEqual("image:my-eraser.png", restored[1].Icon);
        Assert.AreEqual("image:my-eraser.png", restored[1].Action!.Icon);
        Assert.AreEqual("cut", restored[2].Icon);
        Assert.AreEqual("cut", restored[3].Icon);
        Assert.AreEqual("lucide:paintbrush", restored[4].Icon);
        Assert.AreEqual("lucide:brush", restored[4].Action!.Icon);
    }

    private static RingSlotDefinition CreateHybridSlot()
    {
        var slot = RingSlotDefinition.ForSubmenu(
            "Работа с графикой",
            CreateSubmenu(),
            "brand:adobephotoshop",
            new ActionDefinition
            {
                Name = "Photoshop",
                Kind = ActionKind.LaunchApplication,
                LaunchApplication = new LaunchApplicationAction { ExecutablePath = @"C:\Programs\Photoshop.exe" },
            });
        slot.AppearanceOverride = new RingSlotAppearanceDefinition
        {
            BubbleColor = "#123456",
        };
        return slot;
    }

    private static RingDefinition CreateSubmenu() => new()
    {
        Name = "Работа с графикой",
        SlotCount = 1,
        Slots = [RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Кисть", "B", icon: "lucide:brush"))],
        Appearance = new RingAppearanceDefinition { IconColor = "#ABCDEF" },
    };
}
