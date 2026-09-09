using ActionsRing.App.Services;
using ActionsRing.App.Windows;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class ActionPickerCatalogTests
{
    [TestMethod]
    public void CatalogContainsOnlyActionsAndPreservesCategoryOrder()
    {
        var entries = ActionPickerCatalog.Create(ActionCatalog.Groups);

        Assert.IsTrue(entries.Count > 50);
        Assert.IsTrue(entries.All(entry => entry.Item.Kind == CatalogItemKind.Action));
        CollectionAssert.AreEqual(
            ActionCatalog.Groups.Where(group => group.Items.Any(item => item.Kind == CatalogItemKind.Action))
                .Select(group => group.Title).ToArray(),
            entries.Select(entry => entry.Group).Distinct().ToArray());
        Assert.IsFalse(entries.Any(entry => entry.Item.Kind == CatalogItemKind.Folder));
    }

    [TestMethod]
    public void SearchMatchesCaseInsensitiveWordsAcrossTitleDescriptionAndGroup()
    {
        var entries = SampleEntries();

        var results = ActionPickerCatalog.Filter(entries, " КЛАВИАТУРА   ctrl коп ");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("Копировать", results[0].Title);
    }

    [TestMethod]
    public void SearchTreatsYoAndYeAsEquivalent()
    {
        var entries = SampleEntries();

        Assert.AreEqual("Вперёд", ActionPickerCatalog.Filter(entries, "вперед").Single().Title);
        Assert.AreEqual("Вперёд", ActionPickerCatalog.Filter(entries, "ВПЕРЁД").Single().Title);
    }

    [TestMethod]
    public void CategoryFilterCombinesWithSearchAndClearRestoresOnlyThatCategory()
    {
        var entries = SampleEntries();

        Assert.AreEqual(0, ActionPickerCatalog.Filter(entries, "копировать", "Навигация").Count);
        Assert.AreEqual(1, ActionPickerCatalog.Filter(entries, string.Empty, "Навигация").Count);
        Assert.AreEqual(3, ActionPickerCatalog.Filter(entries, null).Count);
    }

    [TestMethod]
    public void BrowsingSearchAndCancelDoNotCreateOrMutateActions()
    {
        var factoryCalls = 0;
        var action = ActionDefinition.BuiltInCommand("Копировать", BuiltInCommand.Copy);
        IReadOnlyList<ActionCatalogGroup> groups =
        [
            new("Клавиатура", "keyboard", [new ActionCatalogItem("Копировать", "Ctrl + C", "copy", CatalogItemKind.Action,
                () => { factoryCalls++; return RingSlotDefinition.ForAction(action); })]),
        ];

        var entries = ActionPickerCatalog.Create(groups);
        _ = ActionPickerCatalog.Filter(entries, "ctrl");
        _ = ActionPickerCatalog.Filter(entries, "");

        Assert.AreEqual(0, factoryCalls);
        Assert.AreSame(groups[0].Items[0], entries[0].Item);
        Assert.AreEqual("Копировать", action.Name);
    }

    [TestMethod]
    public void PhotoshopActionsStaySeparateAndAreSearchableWhenContextIsProvided()
    {
        var entries = ActionPickerCatalog.Create(ActionCatalog.GetGroups(new ActionCatalogContext("Photoshop")));
        var pack = ContextualActionProvider.GetGroup(new ActionCatalogContext("Photoshop"));

        Assert.IsNotNull(pack);
        Assert.AreEqual(pack.Title, entries[0].Group);
        Assert.IsTrue(ActionPickerCatalog.Filter(entries, "кисть", pack.Title).Count > 0);
        Assert.IsFalse(ActionPickerCatalog.Create(ActionCatalog.Groups).Any(entry => entry.Group == pack.Title));
    }

    [TestMethod]
    public void UnknownSearchAndCategoryReturnNoResults()
    {
        var entries = SampleEntries();

        Assert.AreEqual(0, ActionPickerCatalog.Filter(entries, "несуществующее действие").Count);
        Assert.AreEqual(0, ActionPickerCatalog.Filter(entries, "", "Несуществующая категория").Count);
    }

    private static IReadOnlyList<ActionPickerEntry> SampleEntries() =>
    [
        Entry("Клавиатура", "Копировать", "Ctrl + C"),
        Entry("Клавиатура", "Вставить", "Ctrl + V"),
        Entry("Навигация", "Вперёд", "Следующая страница"),
    ];

    private static ActionPickerEntry Entry(string group, string title, string description) =>
        new(group, new ActionCatalogItem(title, description, "keyboard", CatalogItemKind.Action,
            () => RingSlotDefinition.ForAction(ActionDefinition.Shortcut(title, "C"))));
}
