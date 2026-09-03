using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;

namespace ActionsRing.App.Services;

/// <summary>Application identity used to select a dedicated action pack.</summary>
public sealed record ActionCatalogContext(
    string? ProcessName,
    string? ExecutablePath = null,
    string? DisplayName = null)
{
    public static ActionCatalogContext FromForeground(ForegroundApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return new ActionCatalogContext(
            application.ProcessName,
            application.ExecutablePath,
            DisplayName: null);
    }

    public static ActionCatalogContext FromExecutable(string executablePath, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return new ActionCatalogContext(
            Path.GetFileNameWithoutExtension(executablePath),
            executablePath,
            displayName);
    }
}

/// <summary>Creates application-specific actions without mixing packs from unrelated programs.</summary>
public static class ContextualActionProvider
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "firefox",
        "brave",
        "brave-browser",
        "opera",
        "opera_gx",
        "vivaldi",
        "arc",
    };

    public static IReadOnlyList<ActionCatalogGroup> Compose(
        IReadOnlyList<ActionCatalogGroup> generalGroups,
        ActionCatalogContext? context)
    {
        ArgumentNullException.ThrowIfNull(generalGroups);
        var contextual = GetGroup(context);
        if (contextual is null)
        {
            return generalGroups;
        }

        var result = new List<ActionCatalogGroup>(generalGroups.Count + 1) { contextual };
        result.AddRange(generalGroups);
        return result;
    }

    public static ActionCatalogGroup? GetGroup(ActionCatalogContext? context)
    {
        var process = NormalizeProcessName(context?.ProcessName, context?.ExecutablePath);
        if (process.Length == 0)
        {
            return null;
        }

        if (process.StartsWith("photoshop", StringComparison.OrdinalIgnoreCase))
        {
            return PhotoshopPack();
        }

        if (BrowserProcesses.Contains(process))
        {
            return BrowserPack(BrowserDisplayName(process, context?.DisplayName));
        }

        return process.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            ? ExplorerPack()
            : null;
    }

    private static ActionCatalogGroup PhotoshopPack() => new(
        "Adobe Photoshop",
        "Ps",
        [
            Shortcut("Кисть", "Инструмент «Кисть» · B", "text", "B"),
            Shortcut("Пипетка", "Инструмент «Пипетка» · I", "copy", "I"),
            Shortcut("Перемещение", "Инструмент «Перемещение» · V", "mouse", "V"),
            Shortcut("Прямоугольная область", "Инструмент выделения · M", "screenshot", "M"),
            Shortcut("Лассо", "Инструмент «Лассо» · L", "mouse", "L"),
            Shortcut("Рамка", "Инструмент кадрирования · C", "screenshot", "C"),
            Shortcut("Ластик", "Инструмент «Ластик» · E", "cut", "E"),
            Shortcut("Рука", "Перемещение холста · H", "mouse", "H"),
            Shortcut("Масштаб", "Инструмент масштабирования · Z", "search", "Z"),
            Shortcut(
                "Новый слой",
                "Создать новый слой · Ctrl + Shift + N",
                "window",
                "N",
                KeyboardModifiers.Control | KeyboardModifiers.Shift),
        ],
        IsContextual: true);

    private static ActionCatalogGroup BrowserPack(string title) => new(
        title,
        "browser",
        [
            Shortcut("Новая вкладка", "Открыть новую вкладку · Ctrl + T", "browser", "T", KeyboardModifiers.Control),
            Shortcut("Вернуть вкладку", "Открыть закрытую вкладку · Ctrl + Shift + T", "redo", "T", KeyboardModifiers.Control | KeyboardModifiers.Shift),
            Shortcut("Закрыть вкладку", "Закрыть текущую вкладку · Ctrl + W", "close", "W", KeyboardModifiers.Control),
            Shortcut("Следующая вкладка", "Перейти к следующей вкладке · Ctrl + Tab", "redo", "Tab", KeyboardModifiers.Control),
            Shortcut("Предыдущая вкладка", "Перейти к предыдущей вкладке · Ctrl + Shift + Tab", "undo", "Tab", KeyboardModifiers.Control | KeyboardModifiers.Shift),
            Shortcut("Адресная строка", "Перейти к адресу и поиску · Ctrl + L", "search", "L", KeyboardModifiers.Control),
            Shortcut("Обновить страницу", "Перезагрузить текущую страницу · Ctrl + R", "redo", "R", KeyboardModifiers.Control),
            Shortcut("Добавить закладку", "Сохранить страницу в закладки · Ctrl + D", "browser", "D", KeyboardModifiers.Control),
            Shortcut("Инструменты разработчика", "Открыть инструменты разработчика · F12", "settings", "F12"),
        ],
        IsContextual: true);

    private static ActionCatalogGroup ExplorerPack() => new(
        "Проводник",
        "folder",
        [
            Shortcut("Новая папка", "Создать папку · Ctrl + Shift + N", "folder", "N", KeyboardModifiers.Control | KeyboardModifiers.Shift),
            Shortcut("Переименовать", "Переименовать выбранный объект · F2", "text", "F2"),
            Shortcut("Адресная строка", "Выделить текущий путь · Alt + D", "search", "D", KeyboardModifiers.Alt),
            Shortcut("Поиск", "Перейти к поиску в папке · Ctrl + F", "search", "F", KeyboardModifiers.Control),
            Shortcut("Обновить", "Обновить содержимое папки · F5", "redo", "F5"),
            Shortcut("Свойства", "Открыть свойства объекта · Alt + Enter", "settings", "Enter", KeyboardModifiers.Alt),
            Shortcut("Представление", "Переключить варианты представления · Ctrl + Shift + 5", "window", "5", KeyboardModifiers.Control | KeyboardModifiers.Shift),
            Shortcut("Панель просмотра", "Показать или скрыть панель просмотра · Alt + P", "window", "P", KeyboardModifiers.Alt),
        ],
        IsContextual: true);

    private static ActionCatalogItem Shortcut(
        string title,
        string description,
        string icon,
        string key,
        KeyboardModifiers modifiers = KeyboardModifiers.None) =>
        new(
            title,
            description,
            icon,
            CatalogItemKind.Action,
            () => RingSlotDefinition.ForAction(
                ActionDefinition.Shortcut(title, key, modifiers, icon, description)));

    private static string NormalizeProcessName(string? processName, string? executablePath)
    {
        var candidate = string.IsNullOrWhiteSpace(processName)
            ? Path.GetFileNameWithoutExtension(executablePath)
            : Path.GetFileNameWithoutExtension(processName.Trim());
        return candidate?.Trim() ?? string.Empty;
    }

    private static string BrowserDisplayName(string process, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName)
            && displayName.Length <= 40
            && !displayName.Contains('—'))
        {
            return displayName.Trim();
        }

        return process.ToLowerInvariant() switch
        {
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "firefox" => "Mozilla Firefox",
            "brave" or "brave-browser" => "Brave",
            "opera" => "Opera",
            "opera_gx" => "Opera GX",
            "vivaldi" => "Vivaldi",
            "arc" => "Arc",
            _ => "Браузер",
        };
    }
}
