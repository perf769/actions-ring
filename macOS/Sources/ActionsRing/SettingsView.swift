import AppKit
import SwiftUI
import ActionsRingKit

enum SettingsSection: String, CaseIterable, Identifiable {
    case ring, profiles, trigger, settings, about
    var id: String { rawValue }
    var title: String {
        switch self {
        case .ring: return "Кольцо действий"
        case .profiles: return "Профили"
        case .trigger: return "Вызов кольца"
        case .settings: return "Настройки"
        case .about: return "О программе"
        }
    }
    var symbol: String {
        switch self {
        case .ring: return "bolt"
        case .profiles: return "person.2"
        case .trigger: return "hand.draw"
        case .settings: return "gearshape"
        case .about: return "info.circle"
        }
    }
}

@MainActor
struct SettingsView: View {
    @ObservedObject var controller: AppController
    @ObservedObject var store: ConfigurationStore
    @State private var section: SettingsSection
    @State private var selectedProfileID: String?
    @State private var selectedSlotID: String?
    @State private var editingSlot: RingSlot?
    @State private var editingAction: RingAction?
    @State private var showingApplications = false
    @State private var showingNewUser = false
    @State private var showingRenameUser = false
    @State private var showingReset = false
    @State private var showingDeleteUser = false
    @State private var profileName = ""
    @State private var search = ""
    @State private var categoryID = "all"
    @State private var capturing = false

    init(controller: AppController, store: ConfigurationStore, initialSection: SettingsSection = .ring) {
        self.controller = controller
        self.store = store
        _section = State(initialValue: initialSection)
    }

    private var user: UserProfile? { store.configuration.activeUserProfile }
    private var profile: RingProfile {
        user?.profiles.first { $0.id == selectedProfileID }
            ?? user?.profiles.first ?? RingProfile(slots: ActionCatalog.defaultSlots())
    }
    private var selectedSlot: RingSlot? { profile.slots.first { $0.id == selectedSlotID } }
    private var categories: [ActionCategory] { ActionCatalog.categories(for: profile.bundleIdentifier) }

    var body: some View {
        HStack(spacing: 0) {
            sidebar
            Divider()
            VStack(spacing: 0) {
                if section == .ring { profileBar }
                Group {
                    switch section {
                    case .ring: ringPage
                    case .profiles: profilesPage
                    case .trigger: triggerPage
                    case .settings: preferencesPage
                    case .about: aboutPage
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .frame(minWidth: 900, minHeight: 600)
        .background(Color(nsColor: .windowBackgroundColor))
        .tint(RingSettingsStyle.accent)
        .preferredColorScheme(colorScheme)
        .sheet(item: $editingSlot) { slot in
            SlotEditorView(slot: slot, bundleIdentifier: profile.bundleIdentifier,
                           palette: profile.effectivePalette, captureShortcut: controller.captureShortcut,
                           cancelShortcutCapture: controller.cancelShortcutCapture) { replacement in
                changeProfile { value in
                    if let index = value.slots.firstIndex(where: { $0.id == replacement.id }) {
                        value.slots[index] = replacement
                    }
                }
            }
        }
        .sheet(item: $editingAction) { action in
            ActionEditorView(action: action, captureShortcut: controller.captureShortcut,
                             cancelShortcutCapture: controller.cancelShortcutCapture) { value in assign(value) }
        }
        .sheet(isPresented: $showingApplications) {
            ApplicationChooserView { application in addApplication(application) }
        }
        .alert("Новый профиль", isPresented: $showingNewUser) {
            TextField("Название", text: $profileName)
            Button("Отмена", role: .cancel) {}
            Button("Создать") { createUser() }
                .disabled(profileName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        } message: { Text("У каждого профиля — свои кольца для приложений.") }
        .alert("Название профиля", isPresented: $showingRenameUser) {
            TextField("Название", text: $profileName)
            Button("Отмена", role: .cancel) {}
            Button("Сохранить") {
                let name = profileName.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !name.isEmpty else { return }
                mutate { config in
                    if let index = config.userProfiles.firstIndex(where: { $0.id == config.activeUserProfileID }) {
                        config.userProfiles[index].name = name
                    }
                }
            }
        }
        .confirmationDialog("Вернуть стандартные действия?", isPresented: $showingReset) {
            Button("Вернуть по умолчанию", role: .destructive) {
                changeProfile { $0.slots = ActionCatalog.defaultSlots() }
                selectedSlotID = nil
            }
        } message: { Text("Действия текущего кольца будут заменены. Остальные приложения и профили сохранятся.") }
        .confirmationDialog("Удалить профиль «\(user?.name ?? "")»?", isPresented: $showingDeleteUser) {
            Button("Удалить профиль", role: .destructive) { deleteUser() }
        } message: { Text("Все кольца этого профиля будут удалены.") }
        .onChange(of: store.configuration.activeUserProfileID) { _, _ in
            selectedProfileID = nil
            selectedSlotID = nil
            search = ""
            categoryID = "all"
        }
        .onChange(of: section) { _, _ in
            if capturing { controller.cancelShortcutCapture(); capturing = false }
        }
        .onDisappear { controller.cancelShortcutCapture() }
    }

    private var colorScheme: ColorScheme? {
        switch store.configuration.preferences.appearance {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Image(systemName: "circle.hexagongrid").font(.title2).foregroundStyle(RingSettingsStyle.accent)
                Text("Actions Ring").font(.headline)
            }
            .padding(.vertical, 22)
            Text("НАСТРОЙКА").font(.caption2).foregroundStyle(.secondary).padding(.bottom, 6)
            ForEach(SettingsSection.allCases) { item in
                Button {
                    section = item
                } label: {
                    Label(item.title, systemImage: item.symbol)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 12).padding(.vertical, 13)
                        .foregroundStyle(section == item ? RingSettingsStyle.accent : Color.primary)
                        .background(section == item ? RingSettingsStyle.accent.opacity(0.14) : .clear,
                                    in: RoundedRectangle(cornerRadius: 13))
                }
                .buttonStyle(.plain)
            }
            Spacer()
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 8) {
                    Circle().fill(controller.isEnabled ? Color.green : Color.secondary).frame(width: 7, height: 7)
                    Text(controller.isEnabled ? "Кольцо активно" : "Кольцо приостановлено").font(.caption)
                }
                Text(user?.name ?? "Основной").font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
            .padding(14).frame(maxWidth: .infinity, alignment: .leading)
            .background(RingSettingsStyle.card, in: RoundedRectangle(cornerRadius: 14))
        }
        .padding(.horizontal, 16).padding(.bottom, 18).frame(width: 196)
        .background(Color(nsColor: .underPageBackgroundColor).opacity(0.55))
    }

    private var profileBar: some View {
        HStack(spacing: 16) {
            VStack(alignment: .leading, spacing: 5) {
                Text("Профиль пользователя").font(.caption).foregroundStyle(.secondary)
                Picker("Профиль пользователя", selection: activeUserBinding) {
                    ForEach(store.configuration.userProfiles) { profile in Text(profile.name).tag(profile.id) }
                }
                .labelsHidden().frame(width: 148)
            }
            ScrollView(.horizontal) {
                HStack(spacing: 8) {
                    ForEach(user?.profiles ?? []) { item in
                        Button {
                            selectedProfileID = item.id
                            selectedSlotID = nil
                            search = ""
                            categoryID = "all"
                        } label: {
                            HStack(spacing: 7) {
                                SlotIconImage(icon: item.bundleIdentifier.map { SlotIcon(kind: .application, value: $0) }
                                              ?? .symbol("globe"), size: 20, foreground: RingSettingsStyle.accent)
                                Text(item.name).lineLimit(1)
                            }
                            .padding(.horizontal, 12).padding(.vertical, 12)
                            .background(profile.id == item.id ? RingSettingsStyle.accent.opacity(0.12) : RingSettingsStyle.card,
                                        in: RoundedRectangle(cornerRadius: 10))
                            .overlay(RoundedRectangle(cornerRadius: 10).stroke(profile.id == item.id ? RingSettingsStyle.accent : Color.secondary.opacity(0.2)))
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(2)
            }
            .scrollIndicators(.visible)
            Button { showingApplications = true } label: { Label("Приложение", systemImage: "plus") }
                .buttonStyle(.bordered).controlSize(.large)
        }
        .padding(.horizontal, 22).padding(.vertical, 14)
        .background(RingSettingsStyle.card)
        .overlay(alignment: .bottom) { Divider() }
    }

    private var ringPage: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 14) {
                ScrollView {
                    VStack(alignment: .leading, spacing: 14) {
                        Text("Настройте своё кольцо").font(.system(size: 25, weight: .semibold))
                        Text("Выберите пузырь, затем назначьте действие из библиотеки.").font(.callout).foregroundStyle(.secondary)
                        ViewThatFits(in: .horizontal) {
                            HStack { ringThemePicker; Spacer(minLength: 0); ringToolbarButtons }
                            VStack(alignment: .trailing, spacing: 10) {
                                ringThemePicker
                                HStack { Spacer(minLength: 0); ringToolbarButtons }
                            }
                        }
                        SettingsRingPreview(profile: profile, selectedSlotID: selectedSlotID) { slot in
                            selectedSlotID = selectedSlotID == slot?.id ? nil : slot?.id
                        }
                        .frame(height: 280)
                        if profile.theme == .custom {
                            Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 10) {
                                GridRow {
                                    palettePicker("Пузыри", key: \.bubble)
                                    palettePicker("Наведение", key: \.hover)
                                }
                                GridRow {
                                    palettePicker("Иконки", key: \.icon)
                                    palettePicker("Иконки при наведении", key: \.hoverIcon)
                                }
                            }
                            .font(.caption)
                        }
                    }
                    .padding(.trailing, 4)
                }
                selectedSlotCard
            }
            .padding(22).frame(maxWidth: .infinity, maxHeight: .infinity)
            Divider()
            VStack(alignment: .leading, spacing: 14) {
                Text("Все действия").font(.title3).fontWeight(.semibold)
                SearchField(text: $search, placeholder: "Поиск действий")
                Picker("Категория", selection: $categoryID) {
                    Text("Все категории").tag("all")
                    ForEach(categories) { Text($0.title).tag($0.id) }
                }
                .labelsHidden()
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 16) {
                        Button {
                            guard let slot = selectedSlot else { return }
                            var folder = slot
                            folder.label = slot.action == nil ? "Подменю" : slot.label
                            folder.submenu = (0..<4).map { _ in RingSlot() }
                            editingSlot = folder
                        } label: {
                            Label("Добавить подменю", systemImage: "folder.badge.plus")
                                .frame(maxWidth: .infinity, alignment: .leading).padding(12)
                        }
                        .buttonStyle(.plain).background(RingSettingsStyle.accent.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
                        .disabled(selectedSlot == nil || selectedSlot?.hasSubmenu == true)
                        ForEach(filteredCategories) { group in
                            VStack(alignment: .leading, spacing: 6) {
                                Text(group.title).font(.caption).fontWeight(.semibold).foregroundStyle(RingSettingsStyle.accent)
                                ForEach(group.items) { action in
                                    Button { selectLibraryAction(action) } label: {
                                        ActionLibraryRow(action: action)
                                    }
                                    .buttonStyle(.plain).disabled(selectedSlot == nil)
                                }
                            }
                        }
                        if filteredCategories.isEmpty { Text("Ничего не найдено").foregroundStyle(.secondary).padding(.vertical, 20) }
                    }
                }
                .scrollIndicators(.visible)
            }
            .padding(18).frame(width: 278).background(RingSettingsStyle.card)
        }
    }

    private var ringThemePicker: some View {
        Picker("Тема кольца", selection: profileBinding(\.theme)) {
            ForEach(RingTheme.allCases) { Text($0.title).tag($0) }
        }.labelsHidden().frame(width: 145)
    }
    private var ringToolbarButtons: some View {
        Group {
            Button("По умолчанию") { showingReset = true }.buttonStyle(.bordered)
            Button("Проверить") { controller.showRingPreview(profile: profile) }.buttonStyle(.borderedProminent)
        }
    }

    private var filteredCategories: [ActionCategory] {
        ActionCatalog.search(search, bundleIdentifier: profile.bundleIdentifier).filter { categoryID == "all" || $0.id == categoryID }
    }

    private var selectedSlotCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                SlotIconImage(icon: selectedSlot?.effectiveIcon ?? .symbol("plus"), size: 30, foreground: RingSettingsStyle.accent)
                    .frame(width: 44, height: 44)
                    .background(RingSettingsStyle.accent.opacity(0.12), in: Circle())
                VStack(alignment: .leading, spacing: 4) {
                    Text(selectedSlot?.label ?? "Выберите пузырь").fontWeight(.semibold).lineLimit(2)
                    Text(selectedSlot.map { $0.hasSubmenu ? "Подменю: \($0.submenu?.count ?? 0) · \($0.action == nil ? "Без действия по клику" : "Действие по клику")" : "Действие и оформление" }
                         ?? "Затем выберите действие в библиотеке справа.")
                        .font(.caption).foregroundStyle(.secondary).lineLimit(2)
                }
                Spacer(minLength: 0)
            }
            HStack {
                Spacer()
                Button("Изменить") { editingSlot = selectedSlot }.disabled(selectedSlot == nil)
                Button("Очистить") {
                    guard let id = selectedSlotID else { return }
                    changeProfile { value in
                        if let index = value.slots.firstIndex(where: { $0.id == id }) { value.slots[index] = RingSlot(id: id) }
                    }
                }
                .disabled(selectedSlot == nil)
            }
            .buttonStyle(.bordered)
        }
        .padding(16).ringCard()
    }

    private var profilesPage: some View {
        settingsScroll(title: "Ваши профили", subtitle: "Разные наборы колец для работы, творчества и отдыха.") {
            VStack(alignment: .leading, spacing: 16) {
                HStack {
                    Picker("Активный профиль", selection: activeUserBinding) {
                        ForEach(store.configuration.userProfiles) { Text($0.name).tag($0.id) }
                    }
                    Button { profileName = ""; showingNewUser = true } label: { Label("Создать", systemImage: "plus") }
                }
                HStack {
                    Button("Переименовать") { profileName = user?.name ?? ""; showingRenameUser = true }
                    Button("Создать копию") { duplicateUser() }
                    Spacer()
                    Button("Удалить", role: .destructive) { showingDeleteUser = true }
                        .disabled(store.configuration.userProfiles.count < 2)
                }
                .buttonStyle(.bordered)
            }
            .padding(20).ringCard()
            HStack {
                Text("Кольца для приложений").font(.title3).fontWeight(.semibold)
                Spacer()
                Button("Добавить приложение") { showingApplications = true }.buttonStyle(.borderedProminent)
            }
            ForEach(user?.profiles ?? []) { item in
                HStack(spacing: 12) {
                    SlotIconImage(icon: item.bundleIdentifier.map { SlotIcon(kind: .application, value: $0) }
                                  ?? .symbol("globe"), size: 34, foreground: RingSettingsStyle.accent)
                    VStack(alignment: .leading, spacing: 5) {
                        Text(item.name).fontWeight(.semibold)
                        Text(item.bundleIdentifier ?? "Используется, когда для приложения нет отдельного кольца.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if item.bundleIdentifier != nil {
                        Toggle("Включено", isOn: Binding(get: { item.isEnabled }, set: { enabled in
                            changeProfile(id: item.id) { $0.isEnabled = enabled }
                        })).labelsHidden().toggleStyle(.switch)
                    }
                    Button("Настроить") { selectedProfileID = item.id; selectedSlotID = nil; section = .ring }
                    if item.bundleIdentifier != nil {
                        Button(role: .destructive) {
                            mutate { config in
                                guard let index = config.userProfiles.firstIndex(where: { $0.id == config.activeUserProfileID }) else { return }
                                config.userProfiles[index].profiles.removeAll { $0.id == item.id }
                            }
                        } label: { Image(systemName: "trash") }.help("Удалить кольцо приложения")
                    }
                }
                .padding(18).ringCard()
            }
        }
    }

    private var triggerPage: some View {
        settingsScroll(title: "Вызов кольца", subtitle: "Выберите удобный ввод для трекпада, мыши или клавиатуры.") {
            VStack(alignment: .leading, spacing: 18) {
                Picker("Устройство", selection: triggerBinding(\.device)) {
                    Text("Трекпад и прокрутка").tag(TriggerDevice.scroll)
                    Text("Мышь").tag(TriggerDevice.mouse)
                    Text("Клавиатура").tag(TriggerDevice.keyboard)
                }
                .pickerStyle(.segmented)
                if store.configuration.trigger.device == .scroll {
                    Text("Удерживайте выбранные клавиши и проведите двумя пальцами по трекпаду. С мышью работает прокрутка колеса.")
                        .foregroundStyle(.secondary)
                    HStack {
                        Text("Направление")
                        Spacer()
                        Picker("Направление", selection: triggerBinding(\.scrollDirection)) {
                            ForEach(ScrollDirection.allCases) { Text($0.title).tag($0) }
                        }.labelsHidden().frame(width: 150)
                    }
                    ModifierSelector(modifiers: triggerBinding(\.modifiers), requiresModifier: true)
                    VStack(alignment: .leading) {
                        Text("Длина жеста")
                        Slider(value: triggerBinding(\.scrollThreshold), in: 15...160, step: 5)
                        HStack { Text("Короткий"); Spacer(); Text("Длинный") }.font(.caption).foregroundStyle(.secondary)
                    }
                    Text("Системные жесты тремя и четырьмя пальцами остаются под управлением macOS.")
                        .font(.caption).foregroundStyle(.secondary)
                    Button("⌃⌥ + свайп вверх") {
                        mutate { $0.trigger = TriggerBinding() }
                    }.buttonStyle(.bordered)
                } else if store.configuration.trigger.device == .mouse {
                    Picker("Кнопка", selection: triggerBinding(\.mouseButton)) {
                        Text("Боковая — вперёд").tag(4)
                        Text("Боковая — назад").tag(3)
                        Text("Средняя кнопка").tag(2)
                        Text("Правая кнопка").tag(1)
                        Text("Левая кнопка").tag(0)
                    }
                    ModifierSelector(modifiers: triggerBinding(\.modifiers))
                } else {
                    HStack {
                        Text("Сочетание клавиш")
                        Spacer()
                        Text(triggerLabel).font(.system(.title3, design: .rounded)).fontWeight(.medium)
                        Button(capturing ? "Нажмите сочетание…" : "Записать") {
                            capturing = true
                            controller.captureShortcut { key, modifiers in
                                mutate { $0.trigger.key = key; $0.trigger.modifiers = modifiers }
                                capturing = false
                            }
                        }.disabled(capturing)
                    }
                    ModifierSelector(modifiers: triggerBinding(\.modifiers))
                    Picker("Клавиша", selection: triggerBinding(\.key)) {
                        ForEach(KeyboardKeys.supported, id: \.self) { Text($0).tag($0) }
                    }
                }
                Picker("Поведение", selection: triggerBinding(\.mode)) {
                    Text("Повторный вызов закрывает").tag(ActivationMode.toggle)
                    Text(store.configuration.trigger.device == .scroll ? "До отпускания модификаторов" : "Открыто, пока удерживаю").tag(ActivationMode.hold)
                }
            }
            .padding(22).ringCard()
            permissionCard
        }
    }

    private var triggerLabel: String {
        KeyModifier.allCases.filter { store.configuration.trigger.modifiers.contains($0) }.map(\.symbol).joined()
            + store.configuration.trigger.key
    }

    private var preferencesPage: some View {
        settingsScroll(title: "Настройки", subtitle: "Оформление, размер кольца и запуск приложения.") {
            VStack(alignment: .leading, spacing: 20) {
                Picker("Оформление приложения", selection: preferenceBinding(\.appearance)) {
                    Text("Как в macOS").tag(AppAppearance.system)
                    Text("Светлое").tag(AppAppearance.light)
                    Text("Тёмное").tag(AppAppearance.dark)
                }
                Divider()
                VStack(alignment: .leading, spacing: 8) {
                    HStack { Text("Размер кольца"); Spacer(); Text("\(Int(store.configuration.preferences.ringScale * 100)) %").foregroundStyle(.secondary) }
                    Slider(value: preferenceBinding(\.ringScale), in: 0.7...1.8, step: 0.05)
                    Text("Размер учитывает масштаб экрана. Предпросмотр в настройках остаётся компактным.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Divider()
                Toggle("Анимации", isOn: preferenceBinding(\.animations))
                Toggle("Уменьшить движение", isOn: preferenceBinding(\.reducedMotion))
                    .disabled(!store.configuration.preferences.animations)
                Text("Уменьшение движения сохраняет плавное появление без разворачивания пузырей.")
                    .font(.caption).foregroundStyle(.secondary)
                Divider()
                Toggle("Запускать при входе в macOS", isOn: Binding(
                    get: { store.configuration.preferences.launchAtLogin },
                    set: { controller.setLaunchAtLogin($0) }))
                Toggle("Кольцо активно", isOn: $controller.isEnabled)
            }
            .toggleStyle(.switch).padding(22).ringCard()
            permissionCard
        }
    }

    private var permissionCard: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Разрешения macOS").font(.headline)
            permissionRow("Универсальный доступ", granted: controller.accessibilityGranted)
            permissionRow("Мониторинг ввода", granted: controller.inputMonitoringGranted)
            Text("Нужны для глобального вызова кольца и выполнения сочетаний в других приложениях.")
                .font(.caption).foregroundStyle(.secondary)
            Button("Открыть разрешения") { controller.requestPermissions() }.buttonStyle(.bordered)
        }
        .padding(22).ringCard()
    }

    private func permissionRow(_ title: String, granted: Bool) -> some View {
        HStack {
            Text(title)
            Spacer()
            Label(granted ? "Разрешено" : "Требуется разрешение", systemImage: granted ? "checkmark.circle.fill" : "exclamationmark.circle")
                .font(.caption).foregroundStyle(granted ? Color.green : Color.orange)
        }
    }

    private var aboutPage: some View {
        settingsScroll(title: "О программе", subtitle: "Версия и данные приложения.") {
            HStack(spacing: 20) {
                Image(systemName: "circle.hexagongrid").font(.system(size: 42)).foregroundStyle(RingSettingsStyle.accent)
                    .frame(width: 78, height: 78).background(RingSettingsStyle.accent.opacity(0.14), in: RoundedRectangle(cornerRadius: 23))
                VStack(alignment: .leading, spacing: 8) {
                    Text("Actions Ring").font(.title).fontWeight(.semibold)
                    Text("Быстрое контекстное кольцо действий").foregroundStyle(.secondary)
                    Text("macOS 14+ · Apple Silicon").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                VStack(alignment: .trailing, spacing: 8) {
                    Text("Версия \(controller.version)").fontWeight(.medium)
                    Text("Preview").font(.caption).foregroundStyle(RingSettingsStyle.accent)
                }
            }
            .padding(24).ringCard()
            VStack(alignment: .leading, spacing: 14) {
                Text("Обновления").font(.headline)
                Text("Новые версии Actions Ring для Mac доступны на странице выпусков.")
                    .foregroundStyle(.secondary)
                Button("Проверить обновления") { controller.checkForUpdates() }.buttonStyle(.borderedProminent)
            }
            .padding(22).ringCard()
            HStack {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Локальные данные").font(.headline)
                    Text("Профили и настройки хранятся на этом Mac.").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Папка настроек") { controller.revealSettingsFolder() }
                Link("GitHub", destination: URL(string: "https://github.com/perf769/actions-ring")!)
            }
            .padding(22).ringCard()
            if let status = controller.statusMessage, !status.isEmpty {
                Text(status).font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
            }
        }
    }

    private func settingsScroll<Content: View>(title: String, subtitle: String, @ViewBuilder content: () -> Content) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                VStack(alignment: .leading, spacing: 7) {
                    Text(title).font(.system(size: 28, weight: .semibold))
                    Text(subtitle).foregroundStyle(.secondary)
                }.padding(.bottom, 8)
                content()
            }
            .padding(32).frame(maxWidth: 960, alignment: .leading).frame(maxWidth: .infinity)
        }
    }

    private var activeUserBinding: Binding<String> {
        Binding(get: { store.configuration.activeUserProfileID }, set: { id in mutate { $0.activeUserProfileID = id } })
    }
    private func preferenceBinding<T>(_ key: WritableKeyPath<AppPreferences, T>) -> Binding<T> {
        Binding(get: { store.configuration.preferences[keyPath: key] }, set: { value in mutate { $0.preferences[keyPath: key] = value } })
    }
    private func triggerBinding<T>(_ key: WritableKeyPath<TriggerBinding, T>) -> Binding<T> {
        Binding(get: { store.configuration.trigger[keyPath: key] }, set: { value in
            mutate {
                $0.trigger[keyPath: key] = value
                if $0.trigger.device == .scroll && $0.trigger.modifiers.isEmpty { $0.trigger.modifiers = [.control, .option] }
            }
        })
    }
    private func profileBinding<T>(_ key: WritableKeyPath<RingProfile, T>) -> Binding<T> {
        Binding(get: { profile[keyPath: key] }, set: { value in changeProfile { $0[keyPath: key] = value } })
    }
    private func palettePicker(_ title: String, key: WritableKeyPath<RingPalette, String>) -> some View {
        ColorPicker(title, selection: Binding(
            get: { RingSettingsStyle.color(profile.palette[keyPath: key]) },
            set: { color in changeProfile { $0.palette[keyPath: key] = RingSettingsStyle.hex(color) } }),
                    supportsOpacity: false)
    }
    private func mutate(_ update: (inout RingConfiguration) -> Void) {
        var value = store.configuration
        update(&value)
        store.configuration = value
        controller.saveConfiguration()
    }
    private func changeProfile(id: String? = nil, _ update: (inout RingProfile) -> Void) {
        let targetID = id ?? profile.id
        mutate { config in
            guard let userIndex = config.userProfiles.firstIndex(where: { $0.id == config.activeUserProfileID }),
                  let profileIndex = config.userProfiles[userIndex].profiles.firstIndex(where: { $0.id == targetID }) else { return }
            update(&config.userProfiles[userIndex].profiles[profileIndex])
        }
    }
    private func assign(_ action: RingAction) {
        guard let id = selectedSlotID else { return }
        changeProfile { value in
            guard let index = value.slots.firstIndex(where: { $0.id == id }) else { return }
            var copy = action
            copy.id = UUID().uuidString
            value.slots[index].action = copy
            if !value.slots[index].hasSubmenu {
                value.slots[index].label = copy.name
                value.slots[index].icon = copy.icon
            }
        }
    }
    private func selectLibraryAction(_ action: RingAction) {
        guard selectedSlot != nil else { return }
        if action.kind == .application || action.kind == .url || action.kind == .text || action.id == "custom.shortcut" {
            editingAction = action
        } else {
            assign(action)
        }
    }
    private func createUser() {
        let name = profileName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return }
        let user = UserProfile(name: name, profiles: [RingProfile(slots: ActionCatalog.defaultSlots())])
        mutate { $0.userProfiles.append(user); $0.activeUserProfileID = user.id }
    }
    private func duplicateUser() {
        guard let source = user else { return }
        let duplicate = UserProfile(name: "\(source.name) — копия", profiles: source.profiles.map { profile in
            var value = profile
            value.id = UUID().uuidString
            value.slots = profile.slots.map { $0.copyWithNewIDs() }
            return value
        })
        mutate { $0.userProfiles.append(duplicate); $0.activeUserProfileID = duplicate.id }
    }
    private func deleteUser() {
        guard store.configuration.userProfiles.count > 1 else { return }
        mutate { value in
            value.userProfiles.removeAll { $0.id == value.activeUserProfileID }
            value.activeUserProfileID = value.userProfiles[0].id
        }
    }
    private func addApplication(_ app: MacInstalledApplication) {
        if let existing = user?.profiles.first(where: { $0.bundleIdentifier == app.bundleIdentifier }) {
            selectedProfileID = existing.id
        } else {
            let item = RingProfile(name: app.name, bundleIdentifier: app.bundleIdentifier, slots: ActionCatalog.defaultSlots())
            mutate { config in
                guard let index = config.userProfiles.firstIndex(where: { $0.id == config.activeUserProfileID }) else { return }
                config.userProfiles[index].profiles.append(item)
            }
            selectedProfileID = item.id
        }
        selectedSlotID = nil
        categoryID = "all"
        search = ""
        section = .ring
    }
}

enum RingSettingsStyle {
    static let accent = Color(red: 0.50, green: 0.29, blue: 0.99)
    static var card: Color { Color(nsColor: .controlBackgroundColor) }
    static func color(_ hex: String) -> Color {
        let text = hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
        guard let value = UInt64(text, radix: 16), text.count == 6 else { return .primary }
        return Color(red: Double((value >> 16) & 255) / 255, green: Double((value >> 8) & 255) / 255, blue: Double(value & 255) / 255)
    }
    static func hex(_ color: Color) -> String {
        guard let value = NSColor(color).usingColorSpace(.sRGB) else { return "#FFFFFF" }
        return String(format: "#%02X%02X%02X", Int((value.redComponent * 255).rounded()),
                      Int((value.greenComponent * 255).rounded()), Int((value.blueComponent * 255).rounded()))
    }
}

extension View {
    func ringCard() -> some View {
        background(RingSettingsStyle.card, in: RoundedRectangle(cornerRadius: 17))
            .overlay(RoundedRectangle(cornerRadius: 17).stroke(Color.secondary.opacity(0.16)))
    }
}

@MainActor
struct SearchField: View {
    @Binding var text: String
    var placeholder: String
    var body: some View {
        HStack(spacing: 7) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField(placeholder, text: $text).textFieldStyle(.plain)
            if !text.isEmpty {
                Button { text = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }
                    .buttonStyle(.plain).help("Очистить поиск")
            }
        }
        .padding(10).background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
    }
}
