using System.Text.Json;
using ActionsRing.App.Services;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class RingSlotEditingTests
{
    [TestMethod]
    public void AssignActionToFolder_PreservesWholeFolderAndReplacesOnlyClickAction()
    {
        var target = CreateFolder();
        var replacement = RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Новая кисть", "B", icon: "lucide:brush"));
        var originalTarget = Serialize(target);
        var originalReplacement = Serialize(replacement);

        var result = RingSlotEditing.ComposeAssignment(target, replacement);

        Assert.IsNotNull(result);
        Assert.AreEqual(target.Id, result.Id);
        Assert.AreEqual(target.Label, result.Label);
        Assert.AreEqual(target.Icon, result.Icon);
        Assert.AreEqual(target.AppearanceOverride!.BubbleColor, result.AppearanceOverride!.BubbleColor);
        Assert.AreEqual(target.AppearanceOverride.IconHoverColor, result.AppearanceOverride.IconHoverColor);
        Assert.AreEqual(Serialize(target.Submenu), Serialize(result.Submenu));
        Assert.AreEqual(Serialize(replacement.Action), Serialize(result.Action));
        Assert.AreEqual(originalTarget, Serialize(target));
        Assert.AreEqual(originalReplacement, Serialize(replacement));

        result.Submenu!.Slots[0].Action!.Name = "Изменённый потомок";
        result.Submenu.Appearance.IconColor = "#FFFFFF";
        result.AppearanceOverride.BubbleColor = "#FFFFFF";
        result.Action!.Name = "Изменённое действие";
        Assert.AreEqual(originalTarget, Serialize(target));
        Assert.AreEqual(originalReplacement, Serialize(replacement));
    }

    [TestMethod]
    public void AddFolderToAction_PreservesActionNameAndCustomIconAndNamesSubmenuConsistently()
    {
        var target = RingSlotDefinition.ForAction(new ActionDefinition
        {
            Name = "Photoshop",
            Kind = ActionKind.LaunchApplication,
            Icon = "lucide:paintbrush",
            LaunchApplication = new LaunchApplicationAction { ExecutablePath = @"C:\Apps\Photoshop.exe" },
        });
        target.Label = "Работа с графикой";
        target.Icon = "image:custom-graphic.svg";
        var replacement = CreateFolder();
        var originalTarget = Serialize(target);
        var originalReplacement = Serialize(replacement);

        var result = RingSlotEditing.ComposeAssignment(target, replacement);

        Assert.IsNotNull(result);
        Assert.AreEqual(target.Label, result.Label);
        Assert.AreEqual(target.Icon, result.Icon);
        Assert.AreEqual(Serialize(target.Action), Serialize(result.Action));
        Assert.AreEqual(target.Label, result.Submenu!.Name);
        Assert.AreEqual(replacement.Submenu!.Id, result.Submenu.Id);
        Assert.AreEqual(Serialize(replacement.Submenu.Slots), Serialize(result.Submenu.Slots));
        Assert.AreEqual(Serialize(replacement.Submenu.Appearance), Serialize(result.Submenu.Appearance));
        Assert.AreEqual(originalTarget, Serialize(target));
        Assert.AreEqual(originalReplacement, Serialize(replacement));

        result.Action!.LaunchApplication!.ExecutablePath = @"C:\Changed.exe";
        result.Submenu.Slots[0].Label = "Изменённый потомок";
        Assert.AreEqual(originalTarget, Serialize(target));
        Assert.AreEqual(originalReplacement, Serialize(replacement));
    }

    [TestMethod]
    public void AssignFolderToExistingFolder_RejectsWithoutDeletingNestedActions()
    {
        var target = CreateFolder();
        var replacement = CreateFolder();
        var originalTarget = Serialize(target);
        var originalReplacement = Serialize(replacement);

        var result = RingSlotEditing.ComposeAssignment(target, replacement);

        Assert.IsNull(result);
        Assert.AreEqual(originalTarget, Serialize(target));
        Assert.AreEqual(originalReplacement, Serialize(replacement));
    }

    [TestMethod]
    public void AssignActionToAction_ReturnsIndependentReplacement()
    {
        var target = RingSlotDefinition.ForAction(ActionDefinition.Shortcut("До", "A"));
        var replacement = RingSlotDefinition.ForAction(ActionDefinition.Shortcut("После", "B", icon: "lucide:brush"));
        replacement.AppearanceOverride = new RingSlotAppearanceDefinition { BubbleColor = "#112233" };
        var originalTarget = Serialize(target);
        var originalReplacement = Serialize(replacement);

        var result = RingSlotEditing.ComposeAssignment(target, replacement);

        Assert.IsNotNull(result);
        Assert.AreEqual(originalReplacement, Serialize(result));
        result.Label = "Новое имя";
        result.Action!.KeyboardShortcut!.Chords[0].Key = "C";
        result.AppearanceOverride!.BubbleColor = "#FFFFFF";
        Assert.AreEqual(originalTarget, Serialize(target));
        Assert.AreEqual(originalReplacement, Serialize(replacement));
    }

    [TestMethod]
    public void AddFolderToEmptySlot_KeepsFolderTemplateNameAndAction()
    {
        var target = RingSlotDefinition.Empty(0);
        var replacement = CreateFolder();

        var result = RingSlotEditing.ComposeAssignment(target, replacement);

        Assert.IsNotNull(result);
        Assert.AreEqual(Serialize(replacement), Serialize(result));
        Assert.AreNotSame(replacement.Submenu, result.Submenu);
    }

    [TestMethod]
    public void PlainFolderClone_PreservesAbsentClickAction()
    {
        var replacement = CreateFolder();
        replacement.Action = null;
        replacement.Submenu!.Slots[0].Action = null;

        var result = RingSlotEditing.ComposeAssignment(RingSlotDefinition.Empty(0), replacement);

        Assert.IsNotNull(result);
        Assert.IsNull(result.Action);
        Assert.IsNull(result.Submenu!.Slots[0].Action);
        Assert.AreEqual(Serialize(replacement), Serialize(result));
    }

    private static RingSlotDefinition CreateFolder()
    {
        var folder = RingSlotDefinition.ForSubmenu("Снимки экрана", new RingDefinition
        {
            Name = "Снимки экрана",
            SlotCount = 1,
            Appearance = new RingAppearanceDefinition { IconColor = "#ABCDEF", BubbleHoverColor = "#123456" },
            Slots = [RingSlotDefinition.ForSubmenu("Запись", new RingDefinition
            {
                Name = "Запись",
                SlotCount = 1,
                Slots = [RingSlotDefinition.ForAction(ActionDefinition.Shortcut("Записать экран", "F9", icon: "lucide:video"))],
            }, "lucide:video", ActionDefinition.Shortcut("Область", "F8"))],
        }, "lucide:camera", ActionDefinition.Shortcut("Снимок", "F7"));
        folder.AppearanceOverride = new RingSlotAppearanceDefinition
        {
            BubbleColor = "#223344",
            IconHoverColor = "#DDEEFF",
        };
        return folder;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ConfigurationJson.Options);
}
