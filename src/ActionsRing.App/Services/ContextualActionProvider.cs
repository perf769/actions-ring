using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;
using System.Text.RegularExpressions;

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
        if (context is null)
        {
            return null;
        }

        // A saved application can carry a display name or shell identity in ProcessName.
        // Try each independent identity before falling back to a known product display name.
        foreach (var identity in new[] { context.ProcessName, context.ExecutablePath })
        {
            var process = NormalizeProcessName(identity);
            if (process.Equals("photoshop", StringComparison.OrdinalIgnoreCase)
                || process.Equals("photoshopbeta", StringComparison.OrdinalIgnoreCase))
            {
                return PhotoshopPack();
            }

            if (BrowserProcesses.Contains(process))
            {
                return BrowserPack(BrowserDisplayName(process, context.DisplayName));
            }

            if (process.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            {
                return ExplorerPack();
            }
        }

        foreach (var displayName in new[] { context.DisplayName, context.ProcessName })
        {
            if (IsPhotoshopDisplayName(displayName))
            {
                return PhotoshopPack();
            }
        }

        return null;
    }

    private static ActionCatalogGroup PhotoshopPack() => new(
        "Adobe Photoshop",
        "Ps",
        [
            Shortcut("Кисть", "Инструмент «Кисть» · B", "lucide:brush", "B"),
            Shortcut("Пипетка", "Инструмент «Пипетка» · I", "lucide:pipette", "I"),
            Shortcut("Перемещение", "Инструмент «Перемещение» · V", "lucide:move", "V"),
            Shortcut("Прямоугольная область", "Инструмент выделения · M", "lucide:scan", "M"),
            Shortcut("Лассо", "Инструмент «Лассо» · L", "lucide:lasso", "L"),
            Shortcut("Рамка", "Инструмент кадрирования · C", "lucide:crop", "C"),
            Shortcut("Ластик", "Инструмент «Ластик» · E", "lucide:eraser", "E"),
            Shortcut("Рука", "Перемещение холста · H", "lucide:hand", "H"),
            Shortcut("Масштаб", "Инструмент масштабирования · Z", "lucide:zoom-in", "Z"),
            Shortcut(
                "Новый слой",
                "Создать новый слой · Ctrl + Shift + N",
                "lucide:layers",
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

    private static string NormalizeProcessName(string? identity)
    {
        var candidate = string.IsNullOrWhiteSpace(identity)
            ? null
            : Path.GetFileNameWithoutExtension(identity.Trim().Trim('"'));
        return candidate?.Trim() ?? string.Empty;
    }

    private static bool IsPhotoshopDisplayName(string? displayName) =>
        !string.IsNullOrWhiteSpace(displayName)
        && displayName.Length <= 120
        && Regex.IsMatch(
            displayName.Trim(),
            @"^(?:Adobe\s+)?Photoshop(?:\s+(?:CC|CS[1-6]|Beta))?(?:\s+\d{2,4}(?:\.\d+)*)?(?:\s*\((?:64[- ]?bit|x64|Beta)\))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));

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
