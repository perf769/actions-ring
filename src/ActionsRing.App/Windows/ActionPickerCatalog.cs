using ActionsRing.App.Services;

namespace ActionsRing.App.Windows;

public sealed record ActionPickerEntry(string Group, ActionCatalogItem Item)
{
    public string Title => Item.Title;
    public string Description => Item.Description;
}

/// <summary>A read-only catalog projection: browsing never invokes action factories.</summary>
public static class ActionPickerCatalog
{
    public static IReadOnlyList<ActionPickerEntry> Create(IReadOnlyList<ActionCatalogGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return groups.SelectMany(group => group.Items
                .Where(item => item.Kind == CatalogItemKind.Action)
                .Select(item => new ActionPickerEntry(group.Title, item)))
            .ToArray();
    }

    public static IReadOnlyList<ActionPickerEntry> Filter(
        IReadOnlyList<ActionPickerEntry> entries, string? query, string? group = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var tokens = Normalize(query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return entries.Where(entry => (group is null || entry.Group == group)
                && tokens.All(token => Normalize($"{entry.Group} {entry.Title} {entry.Description}")
                    .Contains(token, StringComparison.CurrentCultureIgnoreCase)))
            .ToArray();
    }

    private static string Normalize(string value) => value.Replace('ё', 'е').Replace('Ё', 'Е');
}
