using ActionsRing.App.Services;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class ContextualActionProviderTests
{
    [TestMethod]
    public void PhotoshopContext_AddsDedicatedPackBeforeGeneralActions()
    {
        var groups = ActionCatalog.GetGroups(new ActionCatalogContext("Photoshop"));

        Assert.AreEqual("Adobe Photoshop", groups[0].Title);
        Assert.IsTrue(groups[0].IsContextual);
        CollectionAssert.AreEqual(
            new[] { "Кисть", "Пипетка", "Перемещение", "Прямоугольная область", "Лассо", "Рамка", "Ластик", "Рука", "Масштаб", "Новый слой" },
            groups[0].Items.Select(item => item.Title).ToArray());
        var brush = groups[0].Items[0].CreateSlot().Action!;
        Assert.AreEqual(ActionKind.KeyboardShortcut, brush.Kind);
        Assert.AreEqual("B", brush.KeyboardShortcut!.Chords[0].Key);
        Assert.AreEqual("Инструмент «Кисть» · B", brush.Description);
        Assert.AreSame(ActionCatalog.Groups[0], groups[1]);
    }

    [DataTestMethod]
    [DataRow("chrome", "Google Chrome")]
    [DataRow("msedge.exe", "Microsoft Edge")]
    [DataRow("firefox", "Mozilla Firefox")]
    [DataRow("brave", "Brave")]
    public void BrowserContext_AddsOnlyMatchingBrowserPack(string process, string title)
    {
        var groups = ActionCatalog.GetGroups(new ActionCatalogContext(process));

        Assert.AreEqual(title, groups[0].Title);
        Assert.IsTrue(groups[0].Items.Any(item => item.Title == "Новая вкладка"));
        Assert.IsFalse(groups.Any(group => group.Title == "Adobe Photoshop"));
    }

    [TestMethod]
    public void ExplorerContext_ProvidesFileManagerShortcuts()
    {
        var group = ContextualActionProvider.GetGroup(
            ActionCatalogContext.FromExecutable(@"C:\Windows\explorer.exe"));

        Assert.IsNotNull(group);
        Assert.AreEqual("Проводник", group.Title);
        Assert.IsTrue(group.Items.Any(item => item.Title == "Новая папка"));
        Assert.IsTrue(group.Items.Select(item => item.CreateSlot().Action!).All(action =>
            action.KeyboardShortcut!.Chords.All(chord => KeyNames.IsSupported(chord.Key))));
    }

    [TestMethod]
    public void UnknownContext_DoesNotCreateUnrelatedApplicationActions()
    {
        var groups = ActionCatalog.GetGroups(new ActionCatalogContext("notepad"));

        Assert.AreSame(ActionCatalog.Groups, groups);
    }

    [TestMethod]
    public void KeyboardGroup_ContainsKeyStateToggles()
    {
        var keyboard = ActionCatalog.Groups.Single(group => group.Title == "Клавиатура");
        var commands = keyboard.Items
            .Select(item => item.CreateSlot().Action?.BuiltIn?.Command)
            .Where(command => command is not null)
            .ToArray();

        CollectionAssert.IsSubsetOf(
            new BuiltInCommand?[]
            {
                BuiltInCommand.ToggleCapsLock,
                BuiltInCommand.ToggleNumLock,
                BuiltInCommand.ToggleScrollLock,
            },
            commands);
    }

    [TestMethod]
    public void FolderAction_IsProminentAndNotHiddenInAdvancedGroup()
    {
        Assert.AreEqual("Папки и подменю", ActionCatalog.Groups[0].Title);
        var folder = ActionCatalog.Groups[0].Items.Single();
        Assert.AreEqual(CatalogItemKind.Folder, folder.Kind);
        Assert.AreEqual("Раскрывает дополнительную дугу пузырей", folder.Description);
        Assert.IsFalse(ActionCatalog.Groups.Single(group => group.Title == "Расширенные")
            .Items.Any(item => item.Kind == CatalogItemKind.Folder));
    }
}
