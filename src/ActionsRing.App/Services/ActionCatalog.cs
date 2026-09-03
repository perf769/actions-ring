using ActionsRing.Core.Domain;

namespace ActionsRing.App.Services;

public enum CatalogItemKind
{
    Action,
    Folder,
}

public sealed record ActionCatalogItem(
    string Title,
    string Description,
    string Icon,
    CatalogItemKind Kind,
    Func<RingSlotDefinition> CreateSlot,
    bool RequiresConfiguration = false);

public sealed record ActionCatalogGroup(
    string Title,
    string Icon,
    IReadOnlyList<ActionCatalogItem> Items,
    bool IsContextual = false);

public static class ActionCatalog
{
    public const string DragFormat = "ActionsRing.ActionCatalogItem";

    public static IReadOnlyList<ActionCatalogGroup> Groups { get; } = BuildGroups();

    /// <summary>
    /// Returns general actions plus one clearly separated pack for the selected application.
    /// Existing callers can continue using <see cref="Groups"/>.
    /// </summary>
    public static IReadOnlyList<ActionCatalogGroup> GetGroups(ActionCatalogContext? context) =>
        ContextualActionProvider.Compose(Groups, context);

    private static IReadOnlyList<ActionCatalogGroup> BuildGroups() =>
    [
        new("Папки и подменю", "folder",
        [
            new ActionCatalogItem(
                "Папка",
                "Раскрывает дополнительную дугу пузырей",
                "folder",
                CatalogItemKind.Folder,
                () => RingSlotDefinition.ForSubmenu(
                    "Новая папка",
                    new RingDefinition
                    {
                        Name = "Новая папка",
                        SlotCount = 4,
                        Slots = Enumerable.Range(0, 4).Select(RingSlotDefinition.Empty).ToList(),
                    },
                    "folder")),
        ]),
        new("Медиа и громкость", "volume",
        [
            BuiltIn("Воспроизведение / пауза", "Управление текущим медиаплеером", "play", BuiltInCommand.MediaPlayPause),
            BuiltIn("Следующий трек", "Перейти к следующему треку", "media", BuiltInCommand.MediaNext),
            BuiltIn("Предыдущий трек", "Вернуться к предыдущему треку", "media", BuiltInCommand.MediaPrevious),
            BuiltIn("Стоп", "Остановить воспроизведение", "media", BuiltInCommand.MediaStop),
            BuiltIn("Без звука", "Включить или выключить звук", "mute", BuiltInCommand.VolumeMute),
            BuiltIn("Громче", "Увеличить громкость на один шаг", "volume", BuiltInCommand.VolumeUp),
            BuiltIn("Тише", "Уменьшить громкость на один шаг", "volume", BuiltInCommand.VolumeDown),
            Adjustment("Громкость", "Колесо меняет общую громкость", "volume", AdjustableParameter.SystemVolume, 1),
        ]),
        new("Открыть", "folder",
        [
            Configured("Приложение или файл", "Запустить программу, документ или папку", "app", () => new ActionDefinition
            {
                Name = "Открыть приложение",
                Kind = ActionKind.LaunchApplication,
                Icon = "app",
                LaunchApplication = new LaunchApplicationAction(),
            }),
            Configured("Веб-ссылка", "Открыть URL в браузере по умолчанию", "browser", () => new ActionDefinition
            {
                Name = "Открыть ссылку",
                Kind = ActionKind.OpenUri,
                Icon = "browser",
                OpenUri = new OpenUriAction { Uri = "https://" },
            }),
            BuiltIn("Проводник", "Открыть Проводник Windows", "folder", BuiltInCommand.OpenFileExplorer),
            BuiltIn("Параметры Windows", "Открыть системные параметры", "settings", BuiltInCommand.OpenSettings),
            Shortcut("Выполнить", "Открыть окно Windows «Выполнить»", "window", "R", KeyboardModifiers.Windows),
        ]),
        new("Навигация", "browser",
        [
            BuiltIn("Назад", "Предыдущая страница или экран", "undo", BuiltInCommand.Back),
            BuiltIn("Вперёд", "Следующая страница или экран", "redo", BuiltInCommand.Forward),
            Shortcut("Следующая вкладка", "Ctrl + Tab", "browser", "Tab", KeyboardModifiers.Control),
            Shortcut("Предыдущая вкладка", "Ctrl + Shift + Tab", "browser", "Tab", KeyboardModifiers.Control | KeyboardModifiers.Shift),
            BuiltIn("Переключить окно", "Показать следующее приложение", "window", BuiltInCommand.SwitchWindow),
            BuiltIn("Представление задач", "Все окна и рабочие столы", "window", BuiltInCommand.TaskView),
            Shortcut("Новый рабочий стол", "Создать виртуальный рабочий стол", "desktop", "D", KeyboardModifiers.Control | KeyboardModifiers.Windows),
            Shortcut("Следующий рабочий стол", "Перейти на рабочий стол справа", "desktop", "Right", KeyboardModifiers.Control | KeyboardModifiers.Windows),
            Shortcut("Предыдущий рабочий стол", "Перейти на рабочий стол слева", "desktop", "Left", KeyboardModifiers.Control | KeyboardModifiers.Windows),
            Shortcut("Закрыть рабочий стол", "Закрыть текущий виртуальный рабочий стол", "desktop", "F4", KeyboardModifiers.Control | KeyboardModifiers.Windows),
            Shortcut("Строка меню", "Активировать меню текущего приложения", "keyboard", "F10", KeyboardModifiers.None),
        ]),
        new("Система", "settings",
        [
            BuiltIn("Показать рабочий стол", "Свернуть все окна", "desktop", BuiltInCommand.ShowDesktop),
            BuiltIn("Свернуть окно", "Свернуть активное окно", "window", BuiltInCommand.MinimizeWindow),
            BuiltIn("Развернуть / восстановить", "Переключить размер активного окна", "window", BuiltInCommand.MaximizeOrRestoreWindow),
            BuiltIn("Закрыть окно", "Закрыть активное окно", "window", BuiltInCommand.CloseWindow),
            BuiltIn("Снимок области", "Открыть инструмент снимка экрана", "screenshot", BuiltInCommand.TakeScreenshot),
            BuiltIn("Заблокировать компьютер", "Перейти на экран блокировки", "lock", BuiltInCommand.LockWorkstation),
            BuiltIn("Поиск Windows", "Открыть системный поиск", "search", BuiltInCommand.OpenSearch),
            Shortcut("Экранная лупа", "Открыть и увеличить экранную лупу", "search", "Plus", KeyboardModifiers.Windows),
            Adjustment("Яркость экрана", "Колесо меняет яркость встроенного дисплея", "desktop", AdjustableParameter.ScreenBrightness, 5),
        ]),
        new("Клавиатура", "keyboard",
        [
            BuiltIn("Копировать", "Ctrl + C", "copy", BuiltInCommand.Copy),
            BuiltIn("Вставить", "Ctrl + V", "paste", BuiltInCommand.Paste),
            BuiltIn("Вырезать", "Ctrl + X", "cut", BuiltInCommand.Cut),
            BuiltIn("Отменить", "Ctrl + Z", "undo", BuiltInCommand.Undo),
            BuiltIn("Повторить", "Ctrl + Y", "redo", BuiltInCommand.Redo),
            BuiltIn("Выбрать всё", "Ctrl + A", "keyboard", BuiltInCommand.SelectAll),
            BuiltIn("Найти", "Ctrl + F", "search", BuiltInCommand.Find),
            BuiltIn("Caps Lock", "Включить или выключить Caps Lock", "Aa", BuiltInCommand.ToggleCapsLock),
            BuiltIn("Num Lock", "Включить или выключить цифровой блок", "123", BuiltInCommand.ToggleNumLock),
            BuiltIn("Scroll Lock", "Включить или выключить Scroll Lock", "Scr", BuiltInCommand.ToggleScrollLock),
            Shortcut("Эмодзи и символы", "Открыть панель эмодзи Windows", "keyboard", "Period", KeyboardModifiers.Windows),
            Shortcut("Раскладка клавиатуры", "Переключить язык и раскладку", "keyboard", "Space", KeyboardModifiers.Windows),
            Configured("Сочетание клавиш", "Любая клавиша или последовательность сочетаний", "keyboard", () =>
                ActionDefinition.Shortcut("Сочетание клавиш", "Space", KeyboardModifiers.Control, "keyboard")),
            Configured("Вставить текст", "Напечатать заданный текст в активном приложении", "text", () => new ActionDefinition
            {
                Name = "Вставить текст",
                Kind = ActionKind.TypeText,
                Icon = "text",
                TypeText = new TypeTextAction(),
            }),
        ]),
        new("Дата и время", "text",
        [
            BuiltIn("Дата", "Вставить текущую дату", "text", BuiltInCommand.InsertDate),
            BuiltIn("Время", "Вставить текущее время", "text", BuiltInCommand.InsertTime),
            BuiltIn("Дата и время", "Вставить текущие дату и время", "text", BuiltInCommand.InsertDateTime),
            BuiltIn("День года", "Вставить номер дня в году", "text", BuiltInCommand.InsertDayOfYear),
            BuiltIn("Номер недели", "Вставить номер недели ISO", "text", BuiltInCommand.InsertWeekNumber),
            BuiltIn("Фаза Луны", "Вставить название текущей фазы Луны", "text", BuiltInCommand.InsertMoonPhase),
        ]),
        new("Буфер обмена", "copy",
        [
            BuiltIn("Вставить без форматирования", "Оставить только текст и вставить его", "paste", BuiltInCommand.PasteClipboardPlainText),
            BuiltIn("Очистить буфер", "Удалить содержимое буфера обмена", "cut", BuiltInCommand.ClearClipboard),
            BuiltIn("строчные буквы", "Преобразовать текст из буфера и вставить", "text", BuiltInCommand.ClipboardLowercase),
            BuiltIn("Как предложение", "Преобразовать регистр текста и вставить", "text", BuiltInCommand.ClipboardSentenceCase),
            BuiltIn("Каждое Слово", "Преобразовать текст из буфера и вставить", "text", BuiltInCommand.ClipboardTitleCase),
            BuiltIn("ПРОПИСНЫЕ БУКВЫ", "Преобразовать текст из буфера и вставить", "text", BuiltInCommand.ClipboardUppercase),
            BuiltIn("Переключить регистр", "Поменять строчные и прописные буквы", "text", BuiltInCommand.ClipboardToggleCase),
            BuiltIn("URL-кодирование", "Закодировать текст из буфера и вставить", "browser", BuiltInCommand.ClipboardUrlEncode),
            BuiltIn("URL-декодирование", "Декодировать текст из буфера и вставить", "browser", BuiltInCommand.ClipboardUrlDecode),
            BuiltIn("HTML-кодирование", "Заменить специальные HTML-символы", "text", BuiltInCommand.ClipboardHtmlEncode),
            BuiltIn("HTML-декодирование", "Восстановить специальные HTML-символы", "text", BuiltInCommand.ClipboardHtmlDecode),
        ]),
        new("Мышь", "mouse",
        [
            Mouse("Левый клик", "Один левый клик", MouseButton.Left, 1),
            Mouse("Двойной клик", "Два быстрых левых клика", MouseButton.Left, 2),
            Mouse("Правый клик", "Открыть контекстное меню", MouseButton.Right, 1),
            Mouse("Средний клик", "Нажать колёсико", MouseButton.Middle, 1),
            Mouse("Прокрутка вверх", "Один шаг колеса вверх", MouseButton.WheelUp, 1),
            Mouse("Прокрутка вниз", "Один шаг колеса вниз", MouseButton.WheelDown, 1),
            Mouse("Прокрутка влево", "Один шаг горизонтального колеса влево", MouseButton.WheelLeft, 1),
            Mouse("Прокрутка вправо", "Один шаг горизонтального колеса вправо", MouseButton.WheelRight, 1),
            Adjustment("Плавная прокрутка", "Колесо регулирует прокрутку под курсором", "mouse", AdjustableParameter.VerticalScroll, 1),
        ]),
        new("Регулировки", "settings",
        [
            Adjustment("Масштаб", "Колесо меняет масштаб через Ctrl + / −", "search", AdjustableParameter.Zoom, 1),
            Adjustment("Громкость", "Плавная системная громкость", "volume", AdjustableParameter.SystemVolume, 1),
            Adjustment("Яркость", "Яркость встроенного дисплея", "desktop", AdjustableParameter.ScreenBrightness, 5),
            Adjustment("Вертикальная прокрутка", "Регулировка колесом", "mouse", AdjustableParameter.VerticalScroll, 1),
            Adjustment("Горизонтальная прокрутка", "Регулировка колесом", "mouse", AdjustableParameter.HorizontalScroll, 1),
        ]),
        new("Расширенные", "multi",
        [
            Configured("Мульти-действие", "Запустить несколько действий по порядку с задержками", "multi", () => new ActionDefinition
            {
                Name = "Мульти-действие",
                Kind = ActionKind.Sequence,
                Icon = "multi",
                Sequence = new SequenceAction
                {
                    Steps =
                    [
                        new ActionSequenceStep
                        {
                            Action = ActionDefinition.Shortcut("Первый шаг", "Space", KeyboardModifiers.Control),
                        },
                    ],
                },
            }),
        ]),
    ];

    private static ActionCatalogItem BuiltIn(
        string title,
        string description,
        string icon,
        BuiltInCommand command) =>
        new(
            title,
            description,
            icon,
            CatalogItemKind.Action,
            () => RingSlotDefinition.ForAction(ActionDefinition.BuiltInCommand(title, command, icon, description)));

    private static ActionCatalogItem Shortcut(
        string title,
        string description,
        string icon,
        string key,
        KeyboardModifiers modifiers) =>
        new(
            title,
            description,
            icon,
            CatalogItemKind.Action,
            () => RingSlotDefinition.ForAction(ActionDefinition.Shortcut(title, key, modifiers, icon, description)));

    private static ActionCatalogItem Configured(
        string title,
        string description,
        string icon,
        Func<ActionDefinition> create) =>
        new(
            title,
            description,
            icon,
            CatalogItemKind.Action,
            () => RingSlotDefinition.ForAction(create()),
            RequiresConfiguration: true);

    private static ActionCatalogItem Mouse(
        string title,
        string description,
        MouseButton button,
        int clicks) =>
        new(
            title,
            description,
            "mouse",
            CatalogItemKind.Action,
            () => RingSlotDefinition.ForAction(new ActionDefinition
            {
                Name = title,
                Description = description,
                Icon = "mouse",
                Kind = ActionKind.MouseInput,
                MouseInput = new MouseInputAction { Button = button, ClickCount = clicks },
            }));

    private static ActionCatalogItem Adjustment(
        string title,
        string description,
        string icon,
        AdjustableParameter parameter,
        double step) =>
        new(
            title,
            description,
            icon,
            CatalogItemKind.Action,
            () => RingSlotDefinition.ForAction(new ActionDefinition
            {
                Name = title,
                Description = description,
                Icon = icon,
                Kind = ActionKind.AdjustParameter,
                AdjustParameter = new AdjustParameterAction
                {
                    Parameter = parameter,
                    Mode = AdjustmentMode.Relative,
                    Value = step,
                },
            }));
}
