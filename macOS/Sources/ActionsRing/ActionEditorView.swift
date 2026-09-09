import AppKit
import SwiftUI
import UniformTypeIdentifiers
import ActionsRingKit

typealias SettingsShortcutCapture = (@escaping (String, [KeyModifier]) -> Void) -> Void

@MainActor
struct ActionEditorView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var draft: RingAction
    @State private var capturing = false
    @State private var error: String?
    var captureShortcut: SettingsShortcutCapture
    var cancelShortcutCapture: () -> Void
    var onSave: (RingAction) -> Void

    init(action: RingAction, captureShortcut: @escaping SettingsShortcutCapture,
         cancelShortcutCapture: @escaping () -> Void = {}, onSave: @escaping (RingAction) -> Void) {
        _draft = State(initialValue: action)
        self.captureShortcut = captureShortcut
        self.cancelShortcutCapture = cancelShortcutCapture
        self.onSave = onSave
    }

    var body: some View {
        VStack(spacing: 0) {
            editorHeading("Настройка действия", subtitle: "Действие выполняется в приложении, из которого вызвано кольцо.")
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    field("Название") { TextField("Название действия", text: $draft.name) }
                    Picker("Тип действия", selection: $draft.kind) {
                        ForEach(ActionKind.allCases) { Text($0.settingsTitle).tag($0) }
                    }
                    Divider()
                    actionFields
                    Divider()
                    IconChoiceView(icon: $draft.icon)
                    if let error { Text(error).font(.callout).foregroundStyle(.red) }
                }
                .textFieldStyle(.roundedBorder).padding(24)
            }
            editorFooter {
                Button("Отмена", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Сохранить") { save() }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction).disabled(capturing)
            }
        }
        .frame(width: 600, height: 650)
        .background(Color(nsColor: .windowBackgroundColor))
        .tint(RingSettingsStyle.accent)
        .onChange(of: draft.kind) { oldValue, newValue in
            guard oldValue != newValue else { return }
            if capturing { cancelShortcutCapture(); capturing = false }
            draft.value = newValue == .shortcut ? "Space" : newValue == .system ? "copy" : ""
            draft.modifiers = []
            draft.icon = .symbol(newValue.settingsSymbol)
        }
        .onChange(of: draft.value) { oldValue, newValue in
            guard draft.kind == .system, let action = systemActions.first(where: { $0.value == newValue }) else { return }
            let previous = systemActions.first { $0.value == oldValue }
            if draft.name == previous?.name { draft.name = action.name }
            if draft.icon == previous?.icon || draft.icon == .symbol("gearshape") { draft.icon = action.icon }
            draft.detail = action.detail
        }
        .onDisappear { if capturing { cancelShortcutCapture() } }
    }

    @ViewBuilder private var actionFields: some View {
        switch draft.kind {
        case .shortcut:
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Text(draft.shortcutLabel).font(.system(size: 24, weight: .medium, design: .rounded))
                    Spacer()
                    Button(capturing ? "Отменить запись" : "Записать сочетание") {
                        if capturing {
                            cancelShortcutCapture()
                            capturing = false
                            return
                        }
                        guard MacInputService.accessibilityGranted && MacInputService.inputMonitoringGranted else {
                            error = "Для записи разрешите Универсальный доступ и Мониторинг ввода в настройках macOS. Клавишу также можно выбрать вручную."
                            return
                        }
                        capturing = true
                        captureShortcut { key, modifiers in
                            draft.value = key
                            draft.modifiers = modifiers
                            capturing = false
                        }
                    }
                }
                if capturing { Text("Нажмите сочетание. Для ручного выбора отмените запись.").font(.caption).foregroundStyle(.secondary) }
                ModifierSelector(modifiers: $draft.modifiers).disabled(capturing)
                Picker("Клавиша", selection: $draft.value) {
                    ForEach(KeyboardKeys.supported, id: \.self) { Text($0).tag($0) }
                }.disabled(capturing)
            }
        case .application:
            field("Приложение или файл") {
                HStack {
                    TextField("Путь или идентификатор приложения", text: $draft.value)
                    Button("Выбрать…") { chooseApplication() }
                }
            }
            Text("Можно выбрать приложение .app, документ или папку.").font(.caption).foregroundStyle(.secondary)
        case .url:
            field("Адрес сайта") { TextField("https://example.com", text: $draft.value) }
        case .text:
            field("Текст для вставки") {
                TextEditor(text: $draft.value).font(.body).frame(minHeight: 140)
                    .padding(6).background(RingSettingsStyle.card)
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.secondary.opacity(0.2)))
            }
        case .system:
            Picker("Команда macOS", selection: $draft.value) {
                ForEach(systemActions) { Text($0.name).tag($0.value) }
            }
            if let action = systemActions.first(where: { $0.value == draft.value }) {
                Text(action.detail.isEmpty ? "Выполняет стандартную команду macOS." : action.detail)
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var systemActions: [RingAction] {
        var seen: Set<String> = []
        return ActionCatalog.categories().flatMap(\.items).filter { $0.kind == .system && seen.insert($0.value).inserted }
    }
    private func chooseApplication() {
        let panel = NSOpenPanel()
        panel.title = "Выберите приложение, файл или папку"
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        draft.value = url.path
        draft.icon = .init(kind: .application, value: url.path)
        if draft.name.isEmpty || draft.name == "Открыть приложение" || draft.name == "Приложение или файл" {
            draft.name = url.deletingPathExtension().lastPathComponent
        }
    }
    private func save() {
        draft.name = draft.name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !draft.name.isEmpty && draft.name.count <= 120 else { error = "Введите название длиной до 120 символов."; return }
        do { try ConfigurationCodec.validate(draft) }
        catch { self.error = error.localizedDescription; return }
        onSave(draft)
        dismiss()
    }
}

@MainActor
struct SlotEditorView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var draft: RingSlot
    @State private var showingLibrary = false
    @State private var editingAction: RingAction?
    @State private var editingChild: RingSlot?
    @State private var showingRemoveSubmenu = false
    @State private var pendingChildRemoval: RingSlot?
    @State private var childDragSessionID = UUID()
    @State private var childDropTargetID: String?
    @State private var selectedChildID: String?
    @State private var error: String?
    var bundleIdentifier: String?
    var palette: RingPalette
    var depth: Int
    var captureShortcut: SettingsShortcutCapture
    var cancelShortcutCapture: () -> Void
    var onSave: (RingSlot) -> Void

    private var childDragOwner: SlotReorderOwner {
        SlotReorderOwner(sessionID: childDragSessionID, containerID: draft.id)
    }

    init(slot: RingSlot, bundleIdentifier: String?, palette: RingPalette, depth: Int = 0,
         captureShortcut: @escaping SettingsShortcutCapture, cancelShortcutCapture: @escaping () -> Void = {},
         onSave: @escaping (RingSlot) -> Void) {
        _draft = State(initialValue: slot)
        self.bundleIdentifier = bundleIdentifier
        self.palette = palette
        self.depth = depth
        self.captureShortcut = captureShortcut
        self.cancelShortcutCapture = cancelShortcutCapture
        self.onSave = onSave
    }

    var body: some View {
        VStack(spacing: 0) {
            editorHeading(draft.hasSubmenu ? "Пузырь с подменю" : "Настройка пузыря",
                          subtitle: draft.hasSubmenu ? "Наведите курсор для подменю. Нажмите для отдельного действия." : "Выберите действие, иконку и цвета.")
            ScrollView {
                VStack(alignment: .leading, spacing: 22) {
                    field("Название пузыря") { TextField("Название", text: $draft.label).textFieldStyle(.roundedBorder) }
                    actionCard
                    submenuCard
                    IconChoiceView(icon: $draft.icon)
                    colorsCard
                    if let error { Text(error).foregroundStyle(.red).font(.callout) }
                }.padding(24)
            }
            editorFooter {
                Button("Отмена", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Сохранить") {
                    draft.label = draft.label.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !draft.label.isEmpty && draft.label.count <= 120 else { error = "Введите название длиной до 120 символов."; return }
                    onSave(draft)
                    dismiss()
                }
                .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
            }
        }
        .frame(width: 620, height: 700)
        .background(Color(nsColor: .windowBackgroundColor))
        .tint(RingSettingsStyle.accent)
        .onChange(of: draft.submenu?.map(\.id)) { _, _ in childDropTargetID = nil }
        .onDisappear {
            childDropTargetID = nil
            childDragSessionID = UUID()
        }
        .sheet(isPresented: $showingLibrary) {
            ActionLibraryView(bundleIdentifier: bundleIdentifier, captureShortcut: captureShortcut,
                              cancelShortcutCapture: cancelShortcutCapture) { action in
                draft.action = action
                if draft.label == "Добавить действие" { draft.label = action.name }
                if draft.icon == nil { draft.icon = action.icon }
            }
        }
        .sheet(item: $editingAction) { action in
            ActionEditorView(action: action, captureShortcut: captureShortcut,
                             cancelShortcutCapture: cancelShortcutCapture) { draft.action = $0 }
        }
        .sheet(item: $editingChild) { child in
            SlotEditorView(slot: child, bundleIdentifier: bundleIdentifier, palette: palette, depth: depth + 1,
                           captureShortcut: captureShortcut, cancelShortcutCapture: cancelShortcutCapture) { replacement in
                if let index = draft.submenu?.firstIndex(where: { $0.id == replacement.id }) {
                    draft.submenu?[index] = replacement
                }
            }
        }
        .confirmationDialog("Удалить подменю?", isPresented: $showingRemoveSubmenu) {
            Button("Удалить подменю", role: .destructive) { draft.submenu = nil }
        } message: { Text("Все действия внутри него будут удалены. Действие основного пузыря сохранится.") }
        .confirmationDialog("Удалить пузырь из подменю?", isPresented: Binding(
            get: { pendingChildRemoval != nil }, set: { if !$0 { pendingChildRemoval = nil } })) {
                Button("Удалить", role: .destructive) {
                    guard let child = pendingChildRemoval else { return }
                    draft.submenu?.removeAll { $0.id == child.id }
                    pendingChildRemoval = nil
                }
            }
    }

    private var actionCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(draft.hasSubmenu ? "Действие по клику" : "Действие").font(.headline)
            HStack(spacing: 12) {
                SlotIconImage(icon: draft.action?.icon ?? .symbol("cursorarrow.click"), size: 28, foreground: RingSettingsStyle.accent)
                VStack(alignment: .leading, spacing: 5) {
                    Text(draft.action?.name ?? "Не назначено").fontWeight(.medium)
                    Text(draft.action.map { $0.kind == .shortcut ? $0.shortcutLabel : $0.detail.isEmpty ? $0.value : $0.detail }
                         ?? (draft.hasSubmenu ? "Пузырь только открывает подменю." : "Выберите действие из библиотеки."))
                        .font(.caption).foregroundStyle(.secondary).lineLimit(3)
                }
                Spacer()
            }
            HStack {
                Spacer()
                Button(draft.action == nil ? "Выбрать действие" : "Заменить") { showingLibrary = true }
                if draft.action != nil {
                    Button("Изменить") { editingAction = draft.action }
                    Button("Убрать") { draft.action = nil }
                }
            }
            .buttonStyle(.bordered)
        }
        .padding(16).ringCard()
    }

    private var submenuCard: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("Подменю").font(.headline)
                Spacer()
                if draft.hasSubmenu {
                    Text("\(draft.submenu?.count ?? 0) / 9").font(.caption).foregroundStyle(.secondary)
                    Button("Удалить", role: .destructive) { showingRemoveSubmenu = true }
                } else {
                    Button("Добавить подменю") { draft.submenu = [RingSlot(), RingSlot(), RingSlot()] }
                        .disabled(depth >= 6)
                }
            }
            if let children = draft.submenu, !children.isEmpty {
                Text("Перетащите пузырь на другой, чтобы поменять их местами.")
                    .font(.caption).foregroundStyle(.secondary)
                ForEach(children) { child in
                    submenuRow(child, children: children)
                }
                Button { draft.submenu?.append(RingSlot()) } label: { Label("Добавить пузырь", systemImage: "plus") }
                    .disabled(children.count >= 9)
            }
        }
        .padding(16).ringCard()
    }

    private func submenuRow(_ child: RingSlot, children: [RingSlot]) -> some View {
        HStack(spacing: 10) {
            SlotIconImage(icon: child.effectiveIcon, size: 25, foreground: .primary)
            Button { selectedChildID = child.id; editingChild = child } label: {
                VStack(alignment: .leading, spacing: 3) {
                    Text(child.label).fontWeight(.medium)
                    if child.hasSubmenu { Text("Подменю: \(child.submenu?.count ?? 0)").font(.caption).foregroundStyle(.secondary) }
                }.frame(maxWidth: .infinity, alignment: .leading)
            }.buttonStyle(.plain)
            Button { selectedChildID = child.id; editingChild = child } label: { Image(systemName: "pencil") }
                .help("Настроить пузырь").accessibilityLabel("Настроить \(child.label)")
            Button {
                if child.action != nil || child.hasSubmenu { pendingChildRemoval = child }
                else { draft.submenu?.removeAll { $0.id == child.id } }
            } label: { Image(systemName: "minus.circle") }
                .help("Удалить пузырь").accessibilityLabel("Удалить \(child.label)")
        }
        .padding(9)
        .background(selectedChildID == child.id ? RingSettingsStyle.accent.opacity(0.10) : Color.secondary.opacity(0.055),
                    in: RoundedRectangle(cornerRadius: 9))
        .overlay(RoundedRectangle(cornerRadius: 9)
            .stroke(childDropTargetID == child.id ? RingSettingsStyle.accent : Color.clear,
                    style: StrokeStyle(lineWidth: 2, dash: [4, 3])))
        .contentShape(RoundedRectangle(cornerRadius: 9))
        .draggable(SlotDragTransfer(payload: SlotReorderPayload(owner: childDragOwner, sourceSlotID: child.id, slots: children)))
        .dropDestination(for: SlotDragTransfer.self) { items, _ in
            reorderChild(items, targetSlotID: child.id)
        } isTargeted: { targeted in
            if targeted { childDropTargetID = child.id }
            else if childDropTargetID == child.id { childDropTargetID = nil }
        }
        .help("Перетащите на другой пузырь подменю, чтобы поменять их местами")
    }

    private func reorderChild(_ items: [SlotDragTransfer], targetSlotID: String) -> Bool {
        defer { childDropTargetID = nil }
        guard items.count == 1, let transfer = items.first, var children = draft.submenu,
              SlotReordering.swap(&children, using: transfer.payload,
                                  owner: childDragOwner, targetSlotID: targetSlotID) else { return false }
        draft.submenu = children
        selectedChildID = transfer.payload.sourceSlotID
        return true
    }

    private var colorsCard: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("Свои цвета пузыря").font(.headline)
                Spacer()
                Button("Как у кольца") { draft.colors = nil }.disabled(draft.colors == nil)
            }
            Text("Каждый пузырь может отличаться от общей темы.").font(.caption).foregroundStyle(.secondary)
            Grid(alignment: .leading, horizontalSpacing: 28, verticalSpacing: 12) {
                GridRow {
                    colorPicker("Пузырь", key: \.bubble, fallback: palette.bubble)
                    colorPicker("При наведении", key: \.hover, fallback: palette.hover)
                }
                GridRow {
                    colorPicker("Иконка", key: \.icon, fallback: palette.icon)
                    colorPicker("Иконка при наведении", key: \.hoverIcon, fallback: palette.hoverIcon)
                }
            }
        }
        .padding(16).ringCard()
    }
    private func colorPicker(_ label: String, key: WritableKeyPath<SlotColors, String?>, fallback: String) -> some View {
        ColorPicker(label, selection: Binding(
            get: { RingSettingsStyle.color(draft.colors?[keyPath: key] ?? fallback) },
            set: { value in
                var colors = draft.colors ?? SlotColors()
                colors[keyPath: key] = RingSettingsStyle.hex(value)
                draft.colors = colors
            }), supportsOpacity: false)
    }
}

@MainActor
struct ActionLibraryView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var search = ""
    @State private var categoryID = "all"
    @State private var editing: RingAction?
    var bundleIdentifier: String?
    var captureShortcut: SettingsShortcutCapture
    var cancelShortcutCapture: () -> Void
    var onSelect: (RingAction) -> Void

    var body: some View {
        VStack(spacing: 0) {
            editorHeading("Выберите действие", subtitle: "Поиск по названию, описанию или сочетанию клавиш.")
            HStack(spacing: 16) {
                SearchField(text: $search, placeholder: "Поиск действий")
                Picker("Категория", selection: $categoryID) {
                    Text("Все категории").tag("all")
                    ForEach(ActionCatalog.categories(for: bundleIdentifier)) { Text($0.title).tag($0.id) }
                }.labelsHidden().frame(width: 180)
            }.padding(.horizontal, 24).padding(.bottom, 16)
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 18) {
                    ForEach(groups) { group in
                        VStack(alignment: .leading, spacing: 6) {
                            Text(group.title).font(.headline).foregroundStyle(RingSettingsStyle.accent)
                            ForEach(group.items) { action in
                                Button {
                                    var value = action
                                    value.id = UUID().uuidString
                                    editing = value
                                } label: { ActionLibraryRow(action: action) }
                                .buttonStyle(.plain)
                            }
                        }
                    }
                    if groups.isEmpty {
                        Text("Ничего не найдено").foregroundStyle(.secondary).frame(maxWidth: .infinity).padding(30)
                    }
                }.padding(.horizontal, 24).padding(.bottom, 20)
            }
            editorFooter {
                Button("Отмена", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .frame(width: 650, height: 650)
        .background(Color(nsColor: .windowBackgroundColor))
        .tint(RingSettingsStyle.accent)
        .sheet(item: $editing, onDismiss: {
            if selectedAction != nil { dismiss() }
        }) { action in
            ActionEditorView(action: action, captureShortcut: captureShortcut,
                             cancelShortcutCapture: cancelShortcutCapture) { value in
                selectedAction = value
                onSelect(value)
            }
        }
    }

    @State private var selectedAction: RingAction?
    private var groups: [ActionCategory] {
        ActionCatalog.search(search, bundleIdentifier: bundleIdentifier).filter { categoryID == "all" || $0.id == categoryID }
    }
}

@MainActor
struct ActionLibraryRow: View {
    var action: RingAction
    var body: some View {
        HStack(spacing: 10) {
            SlotIconImage(icon: action.icon ?? .symbol(action.kind.settingsSymbol), size: 22, foreground: RingSettingsStyle.accent)
                .frame(width: 34, height: 34)
                .background(RingSettingsStyle.accent.opacity(0.10), in: RoundedRectangle(cornerRadius: 10))
            VStack(alignment: .leading, spacing: 4) {
                Text(action.name).fontWeight(.medium).multilineTextAlignment(.leading)
                Text(action.kind == .shortcut ? action.shortcutLabel : action.detail.isEmpty ? action.kind.settingsTitle : action.detail)
                    .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.leading).lineLimit(2)
            }
            Spacer(minLength: 0)
        }
        .padding(9).frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.secondary.opacity(0.06), in: RoundedRectangle(cornerRadius: 10))
        .contentShape(Rectangle())
    }
}

@MainActor
struct ModifierSelector: View {
    @Binding var modifiers: [KeyModifier]
    var requiresModifier = false
    var body: some View {
        HStack(spacing: 8) {
            Text("Модификаторы").font(.callout).foregroundStyle(.secondary)
            Spacer()
            ForEach(KeyModifier.allCases) { modifier in
                Button {
                    if modifiers.contains(modifier) { modifiers.removeAll { $0 == modifier } }
                    else { modifiers.append(modifier) }
                } label: {
                    Text(modifier.symbol).font(.system(size: 18, weight: .medium))
                        .frame(width: 42, height: 34)
                        .background(modifiers.contains(modifier) ? RingSettingsStyle.accent.opacity(0.2) : Color.secondary.opacity(0.08),
                                    in: RoundedRectangle(cornerRadius: 9))
                        .foregroundStyle(modifiers.contains(modifier) ? RingSettingsStyle.accent : Color.primary)
                }
                .buttonStyle(.plain).help(modifier.rawValue).accessibilityLabel(modifier.rawValue)
                .accessibilityValue(modifiers.contains(modifier) ? "Выбран" : "Не выбран")
                .disabled(requiresModifier && modifiers.count == 1 && modifiers.contains(modifier))
            }
        }
    }
}

@MainActor
struct IconChoiceView: View {
    @Binding var icon: SlotIcon?
    @State private var showingSymbols = false
    @State private var showingApplications = false
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 12) {
                Text("Иконка").font(.headline)
                Spacer()
                SlotIconImage(icon: icon ?? .symbol("sparkles"), size: 28, foreground: .primary)
                    .frame(width: 44, height: 44).background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
            }
            HStack {
                Button("Библиотека") { showingSymbols = true }
                Button("Приложение") { showingApplications = true }
                Button("Свой файл…") { chooseFile() }
                Spacer()
                Button("Авто") { icon = nil }.disabled(icon == nil)
            }.buttonStyle(.bordered)
        }
        .sheet(isPresented: $showingSymbols) {
            SymbolChooserView { value in icon = .symbol(value) }
        }
        .sheet(isPresented: $showingApplications) {
            ApplicationChooserView { app in icon = .init(kind: .application, value: app.url.path) }
        }
    }
    private func chooseFile() {
        let panel = NSOpenPanel()
        panel.title = "Выберите иконку"
        panel.allowedContentTypes = [.image]
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        icon = .init(kind: .file, value: url.path)
    }
}

@MainActor
struct SymbolChooserView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var search = ""
    var onSelect: (String) -> Void
    private let symbols: [(String, String)] = [
        ("star", "Звезда"), ("heart", "Сердце"), ("bolt", "Молния"), ("sparkles", "Искры"),
        ("folder", "Папка"), ("folder.fill", "Папка"), ("doc", "Документ"), ("doc.on.doc", "Копировать"),
        ("clipboard", "Буфер"), ("scissors", "Вырезать"), ("link", "Ссылка"), ("globe", "Интернет"),
        ("safari", "Браузер"), ("camera", "Камера"), ("camera.viewfinder", "Снимок"), ("video", "Видео"),
        ("record.circle", "Запись"), ("photo", "Фото"), ("photo.on.rectangle", "Изображения"), ("play", "Воспроизведение"),
        ("pause", "Пауза"), ("stop", "Стоп"), ("speaker.wave.2", "Громкость"), ("speaker.slash", "Без звука"),
        ("mic", "Микрофон"), ("music.note", "Музыка"), ("headphones", "Наушники"), ("display", "Экран"),
        ("desktopcomputer", "Компьютер"), ("keyboard", "Клавиатура"), ("computermouse", "Мышь"), ("hand.draw", "Жест"),
        ("cursorarrow", "Курсор"), ("cursorarrow.click", "Клик"), ("paintbrush", "Кисть"), ("eyedropper", "Пипетка"),
        ("eraser", "Ластик"), ("crop", "Кадрирование"), ("lasso", "Лассо"), ("pencil", "Карандаш"),
        ("paintpalette", "Цвета"), ("wand.and.stars", "Эффекты"), ("square.dashed", "Выделение"), ("magnifyingglass", "Поиск"),
        ("plus.magnifyingglass", "Увеличить"), ("minus.magnifyingglass", "Уменьшить"), ("textformat", "Текст"), ("character.bubble", "Перевод"),
        ("brain", "ИИ"), ("message", "Сообщение"), ("bubble.left.and.bubble.right", "Чат"), ("paperplane", "Отправить"),
        ("envelope", "Почта"), ("tray", "Входящие"), ("archivebox", "Архив"), ("calendar", "Календарь"),
        ("clock", "Время"), ("timer", "Таймер"), ("checkmark", "Готово"), ("checklist", "Задачи"),
        ("gearshape", "Настройки"), ("slider.horizontal.3", "Регулировки"), ("lock", "Замок"), ("lock.open", "Открыть замок"),
        ("house", "Дом"), ("briefcase", "Работа"), ("gamecontroller", "Игры"), ("book", "Книга"),
        ("terminal", "Терминал"), ("chevron.left.forwardslash.chevron.right", "Код"), ("cpu", "Процессор"), ("externaldrive", "Диск"),
        ("cloud", "Облако"), ("arrow.down.circle", "Скачать"), ("arrow.up.circle", "Загрузить"), ("arrow.clockwise", "Обновить"),
        ("arrow.uturn.backward", "Отменить"), ("arrow.uturn.forward", "Повторить"), ("arrow.left", "Назад"), ("arrow.right", "Вперёд"),
        ("square.grid.2x2", "Приложения"), ("rectangle.3.group", "Окна"), ("rectangle.on.rectangle", "Рабочие столы"), ("rectangle.expand.vertical", "Развернуть"),
    ]
    var body: some View {
        VStack(spacing: 0) {
            editorHeading("Библиотека иконок", subtitle: "Системные символы хорошо читаются в любой теме.")
            SearchField(text: $search, placeholder: "Название или имя символа").padding(.horizontal, 24).padding(.bottom, 16)
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 78))], spacing: 10) {
                    ForEach(filtered, id: \.0) { symbol, title in
                        Button { onSelect(symbol); dismiss() } label: {
                            VStack(spacing: 8) {
                                Image(systemName: symbol).font(.system(size: 24)).frame(height: 30)
                                Text(title).font(.caption2).lineLimit(2).multilineTextAlignment(.center)
                            }
                            .frame(maxWidth: .infinity, minHeight: 75)
                            .background(Color.secondary.opacity(0.07), in: RoundedRectangle(cornerRadius: 10))
                        }.buttonStyle(.plain).help(symbol)
                    }
                }.padding(.horizontal, 24).padding(.bottom, 24)
            }
            editorFooter { Button("Закрыть") { dismiss() }.keyboardShortcut(.cancelAction) }
        }
        .frame(width: 620, height: 560)
        .background(Color(nsColor: .windowBackgroundColor))
        .tint(RingSettingsStyle.accent)
    }
    private var filtered: [(String, String)] {
        symbols.filter { search.isEmpty || "\($0.0) \($0.1)".localizedCaseInsensitiveContains(search) }
    }
}

@MainActor
struct ApplicationChooserView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var applications: [MacInstalledApplication] = []
    @State private var search = ""
    @State private var loaded = false
    @State private var selectedID: String?
    var onSelect: (MacInstalledApplication) -> Void

    var body: some View {
        VStack(spacing: 0) {
            editorHeading("Установленные приложения", subtitle: "Выберите приложение для кольца или его иконку.")
            SearchField(text: $search, placeholder: "Поиск приложения").padding(.horizontal, 24).padding(.bottom, 16)
            if !loaded { ProgressView("Ищем приложения…").frame(maxWidth: .infinity, maxHeight: .infinity) }
            else {
                List(selection: $selectedID) {
                    ForEach(filtered) { app in
                        HStack(spacing: 12) {
                            Image(nsImage: app.icon).resizable().scaledToFit().frame(width: 34, height: 34)
                            VStack(alignment: .leading, spacing: 4) {
                                Text(app.name).fontWeight(.medium)
                                Text(app.bundleIdentifier).font(.caption).foregroundStyle(.secondary)
                            }
                        }.padding(.vertical, 6).tag(app.id)
                    }
                }
                .listStyle(.inset)
                .overlay {
                    if filtered.isEmpty { Text("Приложение не найдено").foregroundStyle(.secondary) }
                }
            }
            HStack {
                Button("Выбрать файл…") { browse() }
                Spacer()
                Button("Отмена", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Выбрать") {
                    guard let app = filtered.first(where: { $0.id == selectedID }) else { return }
                    onSelect(app)
                    dismiss()
                }
                .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                .disabled(!filtered.contains { $0.id == selectedID })
            }
            .padding(20).background(RingSettingsStyle.card).overlay(alignment: .top) { Divider() }
        }
        .frame(width: 620, height: 610)
        .background(Color(nsColor: .windowBackgroundColor))
        .tint(RingSettingsStyle.accent)
        .task {
            applications = await MacApplicationDiscovery.discover()
            loaded = true
        }
    }
    private var filtered: [MacInstalledApplication] {
        applications.filter { search.isEmpty || "\($0.name) \($0.bundleIdentifier)".localizedCaseInsensitiveContains(search) }
    }
    private func browse() {
        let panel = NSOpenPanel()
        panel.title = "Выберите приложение"
        panel.allowedContentTypes = [.applicationBundle]
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url,
              let bundle = Bundle(url: url), let identifier = bundle.bundleIdentifier else { return }
        let name = bundle.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String
            ?? bundle.object(forInfoDictionaryKey: "CFBundleName") as? String
            ?? url.deletingPathExtension().lastPathComponent
        onSelect(MacInstalledApplication(id: identifier, name: name, bundleIdentifier: identifier,
                                        url: url, icon: NSWorkspace.shared.icon(forFile: url.path)))
        dismiss()
    }
}

extension ActionKind {
    var settingsTitle: String {
        switch self {
        case .shortcut: return "Сочетание клавиш"
        case .application: return "Приложение или файл"
        case .url: return "Веб-ссылка"
        case .text: return "Вставить текст"
        case .system: return "Команда macOS"
        }
    }
    var settingsSymbol: String {
        switch self {
        case .shortcut: return "keyboard"
        case .application: return "app"
        case .url: return "globe"
        case .text: return "textformat"
        case .system: return "gearshape"
        }
    }
}

@MainActor
private func field<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
    VStack(alignment: .leading, spacing: 8) {
        Text(title).font(.callout).foregroundStyle(.secondary)
        content()
    }
}
@MainActor
private func editorHeading(_ title: String, subtitle: String) -> some View {
    VStack(alignment: .leading, spacing: 7) {
        Text(title).font(.title2).fontWeight(.semibold)
        Text(subtitle).font(.callout).foregroundStyle(.secondary)
    }
    .frame(maxWidth: .infinity, alignment: .leading).padding(24)
}
@MainActor
private func editorFooter<Content: View>(@ViewBuilder content: () -> Content) -> some View {
    HStack(spacing: 10) { Spacer(); content() }
        .padding(20).background(RingSettingsStyle.card).overlay(alignment: .top) { Divider() }
}
