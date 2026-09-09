import Foundation

public struct ActionCategory: Identifiable, Sendable {
    public var id: String
    public var title: String
    public var items: [RingAction]
    public init(id: String, title: String, items: [RingAction]) {
        self.id = id
        self.title = title
        self.items = items
    }
}

public struct SystemShortcut: Equatable, Sendable {
    public var key: String
    public var modifiers: [KeyModifier]
    public init(_ key: String, _ modifiers: [KeyModifier] = []) {
        self.key = key
        self.modifiers = modifiers
    }
}

public enum ActionCatalog {
    /// Documented macOS defaults. Explicit custom shortcuts remain independent of this mapping.
    public static let systemShortcuts: [String: SystemShortcut] = [
        "copy": .init("C", [.command]), "paste": .init("V", [.command]),
        "cut": .init("X", [.command]), "undo": .init("Z", [.command]),
        "redo": .init("Z", [.command, .shift]), "selectAll": .init("A", [.command]),
        "find": .init("F", [.command]), "save": .init("S", [.command]),
        "missionControl": .init("Up", [.control]), "appExpose": .init("Down", [.control]),
        "spaceLeft": .init("Left", [.control]), "spaceRight": .init("Right", [.control]),
        "showDesktop": .init("F11"), "switchApp": .init("Tab", [.command]),
        "closeWindow": .init("W", [.command]), "minimizeWindow": .init("M", [.command]),
        "hideApp": .init("H", [.command]), "toggleFullScreen": .init("F", [.command, .control]),
        "screenshot": .init("3", [.command, .shift]), "screenshotArea": .init("4", [.command, .shift]),
        "screenshotOptions": .init("5", [.command, .shift]), "lockScreen": .init("Q", [.command, .control]),
        "spotlight": .init("Space", [.command]), "emoji": .init("Space", [.command, .control]),
        "back": .init("LeftBracket", [.command]), "forward": .init("RightBracket", [.command]),
        "zoomIn": .init("Equal", [.command]), "zoomOut": .init("Minus", [.command]),
        "zoomReset": .init("0", [.command]), "quickLook": .init("Space"),
    ]

    public static var systemCommandIDs: Set<String> {
        Set(systemShortcuts.keys).union(["finder", "settings", "volumeUp", "volumeDown", "volumeMute"])
    }

    private static func system(_ id: String, _ name: String, _ symbol: String, _ detail: String = "") -> RingAction {
        RingAction(id: "system.\(id)", name: name, kind: .system, value: id, detail: detail, icon: .symbol(symbol))
    }

    private static func shortcut(_ id: String, _ name: String, _ key: String, _ modifiers: [KeyModifier], _ symbol: String) -> RingAction {
        RingAction(id: id, name: name, kind: .shortcut, value: key,
                   modifiers: modifiers, icon: .symbol(symbol))
    }

    public static func categories(for bundleIdentifier: String? = nil) -> [ActionCategory] {
        var groups: [ActionCategory] = []
        let bundle = bundleIdentifier?.lowercased() ?? ""
        if bundle.contains("photoshop") {
            groups.append(ActionCategory(id: "photoshop", title: "Adobe Photoshop", items: [
                shortcut("ps.move", "Перемещение", "V", [], "arrow.up.and.down.and.arrow.left.and.right"),
                shortcut("ps.marquee", "Прямоугольное выделение", "M", [], "rectangle.dashed"),
                shortcut("ps.lasso", "Лассо", "L", [], "lasso"),
                shortcut("ps.crop", "Кадрирование", "C", [], "crop"),
                shortcut("ps.eyedropper", "Пипетка", "I", [], "eyedropper"),
                shortcut("ps.brush", "Кисть", "B", [], "paintbrush.pointed"),
                shortcut("ps.eraser", "Ластик", "E", [], "eraser"),
                shortcut("ps.hand", "Рука", "H", [], "hand.draw"),
                shortcut("ps.zoom", "Масштаб", "Z", [], "magnifyingglass"),
                shortcut("ps.deselect", "Снять выделение", "D", [.command], "rectangle.dashed.badge.record"),
                shortcut("ps.newLayer", "Новый слой", "N", [.command, .shift], "square.3.layers.3d"),
            ]))
        }
        if isBrowser(bundle) {
            groups.append(ActionCategory(id: "browser", title: "Браузер", items: [
                shortcut("browser.newTab", "Новая вкладка", "T", [.command], "plus.square"),
                shortcut("browser.closeTab", "Закрыть вкладку", "W", [.command], "xmark.square"),
                shortcut("browser.reopen", "Вернуть закрытую вкладку", "T", [.command, .shift], "arrow.uturn.backward"),
                shortcut("browser.reload", "Обновить страницу", "R", [.command], "arrow.clockwise"),
                shortcut("browser.address", "Адресная строка", "L", [.command], "link"),
                shortcut("browser.nextTab", "Следующая вкладка", "Tab", [.control], "arrow.right.square"),
                shortcut("browser.previousTab", "Предыдущая вкладка", "Tab", [.control, .shift], "arrow.left.square"),
            ]))
        }
        groups += [
            ActionCategory(id: "custom", title: "Свои действия", items: [
                RingAction(id: "custom.application", name: "Открыть приложение или файл", kind: .application,
                           value: "com.apple.finder", detail: "Приложение, документ или папка", icon: .symbol("arrow.up.forward.app")),
                RingAction(id: "custom.url", name: "Открыть сайт", kind: .url,
                           value: "https://example.com", detail: "Ссылка в браузере по умолчанию", icon: .symbol("globe")),
                RingAction(id: "custom.text", name: "Вставить текст", kind: .text,
                           detail: "Ваш текст в активном приложении", icon: .symbol("text.cursor")),
                RingAction(id: "custom.shortcut", name: "Сочетание клавиш", kind: .shortcut,
                           value: "Space", modifiers: [.command], detail: "Любая клавиша и модификаторы", icon: .symbol("keyboard")),
            ]),
            ActionCategory(id: "editing", title: "Редактирование", items: [
                system("copy", "Копировать", "doc.on.doc"), system("paste", "Вставить", "clipboard"),
                system("cut", "Вырезать", "scissors"), system("undo", "Отменить", "arrow.uturn.backward"),
                system("redo", "Повторить", "arrow.uturn.forward"), system("selectAll", "Выбрать всё", "selection.pin.in.out"),
                system("find", "Найти", "magnifyingglass"), system("save", "Сохранить", "square.and.arrow.down"),
            ]),
            ActionCategory(id: "gestures", title: "Окна и жесты macOS", items: [
                system("missionControl", "Mission Control", "rectangle.3.group", "Все окна · Control ↑"),
                system("appExpose", "Окна приложения", "macwindow.on.rectangle", "App Exposé · Control ↓"),
                system("spaceLeft", "Рабочее пространство слева", "rectangle.lefthalf.inset.filled", "Control ←"),
                system("spaceRight", "Рабочее пространство справа", "rectangle.righthalf.inset.filled", "Control →"),
                system("showDesktop", "Показать рабочий стол", "desktopcomputer", "F11"),
                system("switchApp", "Следующее приложение", "rectangle.on.rectangle", "Command Tab"),
                system("toggleFullScreen", "Полный экран", "arrow.up.left.and.arrow.down.right"),
                system("minimizeWindow", "Свернуть окно", "minus.rectangle"),
                system("hideApp", "Скрыть приложение", "eye.slash"), system("closeWindow", "Закрыть окно", "xmark.rectangle"),
            ]),
            ActionCategory(id: "capture", title: "Снимки и запись экрана", items: [
                system("screenshot", "Снимок всего экрана", "camera.viewfinder"),
                system("screenshotArea", "Снимок области", "viewfinder"),
                system("screenshotOptions", "Снимки и запись экрана", "record.circle", "Панель инструментов macOS · Command Shift 5"),
            ]),
            ActionCategory(id: "system", title: "Система", items: [
                system("finder", "Finder", "folder"), system("settings", "Системные настройки", "gearshape"),
                system("spotlight", "Spotlight", "magnifyingglass"), system("emoji", "Эмодзи и символы", "face.smiling"),
                system("quickLook", "Быстрый просмотр", "eye"), system("lockScreen", "Заблокировать экран", "lock"),
            ]),
            ActionCategory(id: "volume", title: "Громкость", items: [
                system("volumeUp", "Увеличить громкость", "speaker.wave.3"),
                system("volumeDown", "Уменьшить громкость", "speaker.wave.1"),
                system("volumeMute", "Включить или выключить звук", "speaker.slash"),
            ]),
            ActionCategory(id: "navigation", title: "Навигация и масштаб", items: [
                system("back", "Назад", "arrow.left"), system("forward", "Вперёд", "arrow.right"),
                system("zoomIn", "Увеличить масштаб", "plus.magnifyingglass"),
                system("zoomOut", "Уменьшить масштаб", "minus.magnifyingglass"),
                system("zoomReset", "Масштаб 100%", "1.magnifyingglass"),
            ]),
        ]
        return groups
    }

    public static func search(_ query: String, bundleIdentifier: String? = nil) -> [ActionCategory] {
        let terms = normalized(query).split(whereSeparator: \.isWhitespace).map(String.init)
        return categories(for: bundleIdentifier).compactMap { category in
            guard !terms.isEmpty else { return category }
            let items = category.items.filter { action in
                let text = normalized("\(category.title) \(action.name) \(action.detail) \(action.value) \(action.shortcutLabel)")
                return terms.allSatisfy { text.contains($0) }
            }
            return items.isEmpty ? nil : ActionCategory(id: category.id, title: category.title, items: items)
        }
    }

    public static func defaultSlots() -> [RingSlot] {
        let all = categories().flatMap(\.items)
        return ["copy", "paste", "undo", "missionControl", "finder", "screenshotOptions", "spotlight", "lockScreen"]
            .compactMap { id in all.first { $0.kind == .system && $0.value == id } }
            .map { item in
                var action = item
                action.id = UUID().uuidString
                return RingSlot(action: action)
            }
    }

    private static func normalized(_ value: String) -> String {
        value.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: Locale(identifier: "ru_RU"))
            .lowercased().replacingOccurrences(of: "ё", with: "е")
    }

    private static func isBrowser(_ bundle: String) -> Bool {
        ["safari", "chrome", "chromium", "firefox", "brave", "vivaldi", "opera", "edgemac", "company.thebrowser.browser"]
            .contains { bundle.contains($0) }
    }
}
