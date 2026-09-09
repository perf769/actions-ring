using System.Collections.Concurrent;
using System.Text.Json;
using ActionsRing.Core.Domain;

namespace ActionsRing.App.Services;

public sealed record IconLibraryEntry(string Reference, string Title, string Keywords, string Svg)
{
    public bool IsBrand => Reference.StartsWith("brand:", StringComparison.Ordinal);
    public string DisplayName => IconLibrary.RussianName(Reference) ?? Title;
    public string Category => IconLibrary.CategoryFor(this);
}

public static class IconLibrary
{
    private static readonly Lazy<IReadOnlyList<IconLibraryEntry>> Catalog = new(LoadCatalog);
    private static readonly Lazy<Dictionary<string, IconLibraryEntry>> Index = new(() => Catalog.Value.ToDictionary(icon => icon.Reference, StringComparer.OrdinalIgnoreCase));
    private static readonly ConcurrentDictionary<string, SafeSvgIcon> Parsed = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<IconLibraryEntry> All => Catalog.Value;
    public static readonly string[] Categories = ["Все иконки", "Интерфейс", "Файлы", "Графика", "Медиа", "Общение", "Разработка", "Устройства", "Бренды"];

    private static readonly Dictionary<string, string> RussianNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brush"] = "Кисть",
        ["paintbrush"] = "Кисть для рисования",
        ["pipette"] = "Пипетка",
        ["eraser"] = "Ластик",
        ["crop"] = "Кадрирование",
        ["lasso"] = "Лассо",
        ["scan"] = "Выделение области",
        ["hand"] = "Рука",
        ["move"] = "Перемещение",
        ["zoom-in"] = "Увеличить",
        ["zoom-out"] = "Уменьшить",
        ["layers"] = "Слои",
        ["palette"] = "Палитра",
        ["pen-tool"] = "Перо",
        ["image"] = "Изображение",
        ["camera"] = "Камера",
        ["video"] = "Видео",
        ["screen-share"] = "Запись экрана",
        ["scan-line"] = "Снимок экрана",
        ["scissors"] = "Ножницы",
        ["folder"] = "Папка",
        ["folder-open"] = "Открытая папка",
        ["folder-heart"] = "Избранная папка",
        ["folder-image"] = "Папка изображений",
        ["folder-video"] = "Папка видео",
        ["folder-code"] = "Папка проекта",
        ["file"] = "Файл",
        ["file-text"] = "Документ",
        ["copy"] = "Копировать",
        ["clipboard"] = "Буфер обмена",
        ["clipboard-paste"] = "Вставить",
        ["undo-2"] = "Отменить",
        ["redo-2"] = "Повторить",
        ["search"] = "Поиск",
        ["globe"] = "Сайт",
        ["app-window"] = "Приложение",
        ["keyboard"] = "Клавиатура",
        ["mouse"] = "Мышь",
        ["play"] = "Воспроизведение",
        ["pause"] = "Пауза",
        ["volume-2"] = "Громкость",
        ["volume-x"] = "Без звука",
        ["monitor"] = "Рабочий стол",
        ["panels-top-left"] = "Окна",
        ["settings"] = "Настройки",
        ["lock-keyhole"] = "Блокировка",
        ["type"] = "Текст",
        ["list-ordered"] = "Последовательность",
        ["sparkles"] = "Искусственный интеллект",
        ["bot"] = "Ассистент",
        ["terminal"] = "Терминал",
        ["code"] = "Код",
        ["download"] = "Загрузить",
        ["upload"] = "Отправить",
        ["link"] = "Ссылка",
        ["mail"] = "Почта",
        ["message-circle"] = "Сообщение",
        ["heart"] = "Избранное",
        ["star"] = "Звезда",
        ["plus"] = "Добавить",
        ["x"] = "Закрыть",
        ["trash-2"] = "Удалить",
        ["check"] = "Готово",
        ["save"] = "Сохранить",
        ["calendar"] = "Дата",
        ["clock"] = "Время",
        ["sun"] = "Яркость",
        ["moon"] = "Ночь",
        ["arrow-left"] = "Назад",
        ["arrow-right"] = "Вперёд",
        ["gamepad-2"] = "Игры",
        ["music"] = "Музыка",
        ["mic"] = "Микрофон",
        ["headphones"] = "Наушники",
        ["network"] = "Сеть",
        ["wifi"] = "Wi-Fi",
        ["power"] = "Питание",
        ["hard-drive"] = "Диск",
        ["cpu"] = "Процессор",
        ["memory-stick"] = "Память",
        ["calculator"] = "Калькулятор",
        ["notebook-pen"] = "Заметки",
        ["book-open"] = "Книга",
        ["briefcase"] = "Работа",
        ["home"] = "Дом",
        ["house"] = "Дом",
        ["coffee"] = "Перерыв",
        ["rocket"] = "Запуск",
        ["external-link"] = "Открыть",
        ["square-dashed"] = "Прямоугольное выделение",
        ["rectangle-ellipsis"] = "Пароль",
        ["workflow"] = "Автоматизация",
        ["wand-sparkles"] = "Волшебная палочка",
        ["paint-bucket"] = "Заливка",
        ["stamp"] = "Штамп",
        ["blend"] = "Смешивание",
        ["contrast"] = "Контраст",
        ["sliders-horizontal"] = "Регулировки",
    };
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["paste"] = "clipboard-paste",
        ["cut"] = "scissors",
        ["undo"] = "undo-2",
        ["redo"] = "redo-2",
        ["browser"] = "globe",
        ["web"] = "globe",
        ["url"] = "globe",
        ["app"] = "app-window",
        ["media"] = "play",
        ["volume"] = "volume-2",
        ["mute"] = "volume-x",
        ["screenshot"] = "scan-line",
        ["desktop"] = "monitor",
        ["window"] = "panels-top-left",
        ["lock"] = "lock-keyhole",
        ["text"] = "type",
        ["multi"] = "list-ordered",
    };

    public static string? RussianName(string reference) => reference.StartsWith("lucide:", StringComparison.OrdinalIgnoreCase) && RussianNames.TryGetValue(reference[7..], out var name) ? name : null;

    public static string CategoryFor(IconLibraryEntry icon)
    {
        if (icon.IsBrand) return "Бренды";
        var value = icon.Reference + " " + icon.Keywords;
        if (ContainsAny(value, "folder", "file", "clipboard", "archive", "document")) return "Файлы";
        if (ContainsAny(value, "paint", "brush", "image", "design", "draw", "photo", "color", "crop", "pipette", "eraser", "lasso", "layers", "vector")) return "Графика";
        if (ContainsAny(value, "music", "audio", "video", "volume", "play", "media", "camera", "film", "headphone")) return "Медиа";
        if (ContainsAny(value, "message", "chat", "mail", "phone", "contact", "user", "person", "share")) return "Общение";
        if (ContainsAny(value, "code", "git", "terminal", "database", "program", "bug", "webhook", "api")) return "Разработка";
        if (ContainsAny(value, "monitor", "device", "mouse", "keyboard", "printer", "cpu", "memory", "wifi", "bluetooth", "usb", "battery")) return "Устройства";
        return "Интерфейс";
    }

    public static IReadOnlyList<IconLibraryEntry> Search(string? query, string? category = null)
    {
        var words = (query ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All.Where(icon => (string.IsNullOrEmpty(category) || category == "Все иконки" || icon.Category == category)
            && words.All(word => (icon.Reference + " " + icon.Title + " " + icon.DisplayName + " " + icon.Keywords + " " + icon.Category)
                .Contains(word, StringComparison.CurrentCultureIgnoreCase))).ToArray();
    }

    public static SafeSvgIcon? FindSvg(string? reference)
    {
        if (reference is null || !Index.Value.TryGetValue(reference, out var icon)) return null;
        return Parsed.GetOrAdd(icon.Reference, _ => SafeSvgIcon.Parse(icon.Svg));
    }

    public static string ResolveReference(string? reference, ActionDefinition? action)
    {
        reference = string.IsNullOrWhiteSpace(reference) ? action?.Icon : reference.Trim();
        if (reference?.StartsWith("lucide:", StringComparison.OrdinalIgnoreCase) == true
            || reference?.StartsWith("brand:", StringComparison.OrdinalIgnoreCase) == true
            || reference?.StartsWith("image:", StringComparison.OrdinalIgnoreCase) == true) return reference;
        var explicitFile = reference is not null && (Path.IsPathRooted(reference) || reference.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            && !string.Equals(reference, action?.Icon, StringComparison.OrdinalIgnoreCase);
        var explicitAlias = reference is not null && reference is not ("app" or "browser" or "web" or "url")
            && Index.Value.ContainsKey("lucide:" + (Aliases.GetValueOrDefault(reference) ?? reference));
        var brand = KnownBrandReference(action);
        if (!explicitFile && !explicitAlias && brand is not null) return brand;
        var legacyPlaceholder = string.IsNullOrWhiteSpace(reference) || reference is "keyboard" or "media" or "text" or "window" or "volume" or "app";
        if (!explicitFile && legacyPlaceholder && action?.Kind == ActionKind.BuiltIn && action.BuiltIn is not null)
            return "lucide:" + BuiltInIcon(action.BuiltIn.Command);
        if (!explicitFile && legacyPlaceholder && action?.Kind == ActionKind.AdjustParameter && action.AdjustParameter is not null)
            return "lucide:" + (action.AdjustParameter.Parameter switch { AdjustableParameter.SystemVolume => "volume-2", AdjustableParameter.ScreenBrightness => "sun", AdjustableParameter.Zoom => "zoom-in", AdjustableParameter.VerticalScroll => "move-vertical", AdjustableParameter.HorizontalScroll => "move-horizontal", _ => "sliders-horizontal" });
        if (!string.IsNullOrWhiteSpace(reference))
        {
            if (Aliases.TryGetValue(reference, out var alias)) return "lucide:" + alias;
            if (Index.Value.ContainsKey("lucide:" + reference)) return "lucide:" + reference;
            return reference;
        }
        return "lucide:" + (action?.Kind switch
        {
            ActionKind.LaunchApplication => "app-window",
            ActionKind.OpenUri => "globe",
            ActionKind.TypeText => "type",
            ActionKind.MouseInput => "mouse",
            ActionKind.Sequence => "list-ordered",
            ActionKind.KeyboardShortcut => "keyboard",
            ActionKind.AdjustParameter => "sliders-horizontal",
            ActionKind.BuiltIn => BuiltInIcon(action.BuiltIn?.Command ?? BuiltInCommand.None),
            _ => "plus",
        });
    }

    public static string? KnownBrandReference(ActionDefinition? action)
    {
        if (action?.Kind == ActionKind.OpenUri && action.OpenUri?.Uri is { } address && Uri.TryCreate(address, UriKind.Absolute, out var uri)) return KnownWebsiteBrand(uri.Host);
        var target = action?.Kind == ActionKind.LaunchApplication ? action.LaunchApplication?.ExecutablePath : null;
        if (string.IsNullOrWhiteSpace(target)) return null;
        var name = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
        if (ContainsAny(name, "chatgpt", "openai", "codex")) return "brand:openai";
        foreach (var (key, slug) in ApplicationBrands)
            if ((name.Equals(key, StringComparison.OrdinalIgnoreCase) || name.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase)) && Index.Value.ContainsKey("brand:" + slug)) return "brand:" + slug;
        return null;
    }

    private static readonly (string, string)[] ApplicationBrands = [("telegram", "telegram"), ("discord", "discord"), ("chrome", "googlechrome"), ("firefox", "firefoxbrowser"), ("msedge", "microsoftedge"), ("spotify", "spotify"), ("obs64", "obsstudio"), ("obs32", "obsstudio"), ("sharex", "sharex"), ("figma", "figma"), ("notion", "notion"), ("slack", "slack"), ("steam", "steam"), ("vlc", "vlcmediaplayer"), ("blender", "blender"), ("gimp", "gimp"), ("krita", "krita"), ("code", "visualstudiocode"), ("cursor", "cursor")];

    public static string? KnownWebsiteBrand(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        foreach (var (domain, slug) in WebsiteBrands)
            if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal)) return "brand:" + slug;
        return null;
    }
    private static readonly (string, string)[] WebsiteBrands = [("chatgpt.com", "openai"), ("openai.com", "openai"), ("gemini.google.com", "googlegemini"), ("claude.ai", "claude"), ("anthropic.com", "anthropic"), ("perplexity.ai", "perplexity"), ("github.com", "github"), ("youtube.com", "youtube"), ("youtu.be", "youtube"), ("telegram.org", "telegram"), ("t.me", "telegram"), ("discord.com", "discord"), ("notion.so", "notion"), ("figma.com", "figma"), ("spotify.com", "spotify"), ("google.com", "google"), ("twitch.tv", "twitch"), ("reddit.com", "reddit"), ("whatsapp.com", "whatsapp")];

    private static string BuiltInIcon(BuiltInCommand command) => command switch
    {
        BuiltInCommand.Copy => "copy",
        BuiltInCommand.Paste or BuiltInCommand.PasteClipboardPlainText => "clipboard-paste",
        BuiltInCommand.Cut => "scissors",
        BuiltInCommand.Undo => "undo-2",
        BuiltInCommand.Redo => "redo-2",
        BuiltInCommand.Back => "arrow-left",
        BuiltInCommand.Forward => "arrow-right",
        BuiltInCommand.Find or BuiltInCommand.OpenSearch => "search",
        BuiltInCommand.TakeScreenshot => "scan-line",
        BuiltInCommand.OpenFileExplorer => "folder",
        BuiltInCommand.OpenSettings => "settings",
        BuiltInCommand.ShowDesktop => "monitor",
        BuiltInCommand.TaskView or BuiltInCommand.SwitchWindow => "panels-top-left",
        BuiltInCommand.LockWorkstation => "lock-keyhole",
        BuiltInCommand.VolumeUp or BuiltInCommand.VolumeDown => "volume-2",
        BuiltInCommand.VolumeMute => "volume-x",
        BuiltInCommand.MediaPlayPause => "play",
        BuiltInCommand.MediaNext => "skip-forward",
        BuiltInCommand.MediaPrevious => "skip-back",
        BuiltInCommand.MediaStop => "square",
        BuiltInCommand.CloseWindow => "x",
        BuiltInCommand.MinimizeWindow => "minus",
        BuiltInCommand.MaximizeOrRestoreWindow => "maximize",
        BuiltInCommand.InsertDate or BuiltInCommand.InsertDateTime => "calendar",
        BuiltInCommand.InsertDayOfYear or BuiltInCommand.InsertWeekNumber => "calendar-days",
        BuiltInCommand.InsertTime => "clock",
        BuiltInCommand.InsertMoonPhase => "moon",
        BuiltInCommand.ClearClipboard => "clipboard-x",
        BuiltInCommand.ClipboardLowercase => "case-lower",
        BuiltInCommand.ClipboardUppercase => "case-upper",
        BuiltInCommand.ClipboardSentenceCase or BuiltInCommand.ClipboardTitleCase => "case-sensitive",
        BuiltInCommand.ClipboardToggleCase => "case-sensitive",
        BuiltInCommand.ClipboardUrlEncode or BuiltInCommand.ClipboardUrlDecode => "link",
        BuiltInCommand.ClipboardHtmlEncode or BuiltInCommand.ClipboardHtmlDecode => "code-xml",
        BuiltInCommand.SelectAll => "square-dashed",
        BuiltInCommand.ToggleCapsLock => "arrow-up-from-line",
        BuiltInCommand.ToggleNumLock => "hash",
        BuiltInCommand.ToggleScrollLock => "move-vertical",
        _ => "keyboard",
    };
    private static bool ContainsAny(string value, params string[] words) => words.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));
    private static IReadOnlyList<IconLibraryEntry> LoadCatalog()
    {
        using var stream = typeof(IconLibrary).Assembly.GetManifestResourceStream("ActionsRing.App.Assets.Icons.catalog.json")
            ?? throw new InvalidOperationException("The icon collection is unavailable.");
        return JsonSerializer.Deserialize<IconLibraryEntry[]>(stream) ?? [];
    }
}
