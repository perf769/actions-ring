using System.Text.Json;
using ActionsRing.App.Services;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class RingSlotSwapTests
{
    [TestMethod]
    public void RootSwapPreservesEntireSlotObjectsAndAllNestedData()
    {
        var root = CreateRing();
        var source = root.Slots[0];
        var target = root.Slots[2];
        var sourceJson = Json(source);
        var targetJson = Json(target);

        Assert.IsTrue(RingSlotEditing.TrySwap(root, source, target));

        Assert.AreSame(source, root.Slots[2]);
        Assert.AreSame(target, root.Slots[0]);
        Assert.AreEqual(sourceJson, Json(root.Slots[2]));
        Assert.AreEqual(targetJson, Json(root.Slots[0]));
        Assert.AreEqual(4, root.SlotCount);
    }

    [TestMethod]
    public void SubmenuPeersSwapIncludingNestedFolderWithoutChangingRootOrder()
    {
        var root = CreateRing();
        var rootOrder = root.Slots.ToArray();
        var submenu = root.Slots[0].Submenu!;
        var source = submenu.Slots[0];
        var target = submenu.Slots[1];

        Assert.IsTrue(RingSlotEditing.TrySwap(root, source, target));

        Assert.AreSame(source, submenu.Slots[1]);
        Assert.AreSame(target, submenu.Slots[0]);
        CollectionAssert.AreEqual(rootOrder, root.Slots);
    }

    [TestMethod]
    public void SameSlotCrossLevelForeignAndStaleTargetsAreRejectedWithoutMutation()
    {
        var root = CreateRing();
        var before = Json(root);
        var parent = root.Slots[0];
        var child = parent.Submenu!.Slots[0];
        var foreign = CreateRing().Slots[0];

        Assert.IsFalse(RingSlotEditing.TrySwap(root, parent, parent));
        Assert.IsFalse(RingSlotEditing.TrySwap(root, parent, child));
        Assert.IsFalse(RingSlotEditing.TrySwap(root, child, root.Slots[1]));
        Assert.IsFalse(RingSlotEditing.TrySwap(root, parent, foreign));
        Assert.IsFalse(RingSlotEditing.TrySwap(root, foreign, parent));
        Assert.AreEqual(before, Json(root));
    }

    [TestMethod]
    public async Task TransactionalSaveSerializesNewOrderWithCompleteSlotsAndReloadsIt()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.GlobalProfile.RootRing = CreateRing();
        var root = configuration.GlobalProfile.RootRing;
        var source = root.Slots[0];
        var target = root.Slots[2];
        var sourceJson = Json(source);
        var targetJson = Json(target);
        var transaction = ConfigurationMutationTransaction.Capture(configuration);
        string? saved = null;

        Assert.IsTrue(RingSlotEditing.TrySwap(root, source, target));
        Assert.IsTrue(await transaction.TryCommitAsync(() => { saved = Json(configuration); return Task.FromResult(true); }));

        var reloaded = JsonSerializer.Deserialize<ActionsRingConfiguration>(saved!, ConfigurationJson.Options)!;
        Assert.AreEqual(sourceJson, Json(reloaded.GlobalProfile.RootRing.Slots[2]));
        Assert.AreEqual(targetJson, Json(reloaded.GlobalProfile.RootRing.Slots[0]));
        Assert.AreSame(source, root.Slots[2]);
    }

    [TestMethod]
    public async Task FailedSaveRestoresOrderAndCompleteConfiguration()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.GlobalProfile.RootRing = CreateRing();
        var before = Json(configuration);
        var root = configuration.GlobalProfile.RootRing;
        var transaction = ConfigurationMutationTransaction.Capture(configuration);

        Assert.IsTrue(RingSlotEditing.TrySwap(root, root.Slots[0], root.Slots[2]));
        Assert.IsFalse(await transaction.TryCommitAsync(() => Task.FromResult(false)));

        Assert.AreEqual(before, Json(configuration));
    }

    internal static RingDefinition CreateRing()
    {
        var nested = RingSlotDefinition.ForSubmenu("Вложенное", new RingDefinition
        {
            Name = "Вложенное", SlotCount = 1, Slots = [RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Кисть", "B", icon: "lucide:brush"))],
        }, "lucide:palette", ActionDefinition.Shortcut("Сохранить", "S", KeyboardModifiers.Control));
        var folder = RingSlotDefinition.ForSubmenu("Инструменты", new RingDefinition
        {
            Name = "Инструменты", SlotCount = 2, Slots = [nested, RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Перо", "P", icon: "lucide:pen-tool"))],
            Appearance = new RingAppearanceDefinition { BubbleColor = "#ABCDEF", IconHoverColor = "#123456" },
        }, "lucide:folder", ActionDefinition.BuiltInCommand("Копировать", BuiltInCommand.Copy));
        folder.AppearanceOverride = new RingSlotAppearanceDefinition { BubbleColor = "#112233", IconColor = "#445566", BubbleHoverColor = "#778899", IconHoverColor = "#AABBCC" };
        return new RingDefinition
        {
            SlotCount = 4,
            Slots = [folder, RingSlotDefinition.Empty(1), RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Отменить", "Z", KeyboardModifiers.Control, "lucide:undo-2")), RingSlotDefinition.Empty(3)],
        };
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ConfigurationJson.Options);
}
