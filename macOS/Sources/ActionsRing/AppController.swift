import AppKit
import Combine
import ServiceManagement
import SwiftUI
import ActionsRingKit

@MainActor
final class AppController: NSObject, ObservableObject {
    let store: ConfigurationStore
    @Published var isEnabled = true { didSet { if isEnabled != oldValue { updateInput(); rebuildMenu() } } }
    @Published var accessibilityGranted = false
    @Published var inputMonitoringGranted = false
    @Published var statusMessage: String?
    var version: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0" }
    private let input = MacInputService()
    private let actions = MacActionService()
    let ring = RingPanelController()
    private var statusItem: NSStatusItem?
    private var settingsWindow: NSWindow?
    private var permissionTimer: Timer?
    private var escapeMonitor: Any?
    private var targetApplication: NSRunningApplication?
    private var previewing = false
    private var lastBinding: TriggerBinding?
    private let runtimeEnabled: Bool

    init(store: ConfigurationStore, enableRuntime: Bool = true) {
        self.store = store
        runtimeEnabled = enableRuntime
        super.init()
        input.onTrigger = { [weak self] in
            let foreground = NSWorkspace.shared.frontmostApplication
            DispatchQueue.main.async { self?.handleTrigger(frontmost: foreground) }
        }
        input.onRelease = { [weak self] in
            DispatchQueue.main.async {
                guard let self, self.store.configuration.trigger.mode == .hold else { return }
                self.ring.commit(holdRelease: true)
            }
        }
        input.onError = { [weak self] message in
            DispatchQueue.main.async { self?.ring.hide(); self?.statusMessage = message }
        }
        ring.onAction = { [weak self] action in self?.execute(action) }
        ring.onClose = { [weak self] in self?.rebuildMenu() }
        refreshPermissions()
        applyAppearance()
    }

    func start() {
        guard runtimeEnabled else { return }
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem?.button?.image = NSImage(systemSymbolName: "circle.hexagongrid", accessibilityDescription: "Actions Ring")
        rebuildMenu()
        updateInput()
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                let hadAccess = self.accessibilityGranted && self.inputMonitoringGranted
                self.refreshPermissions()
                if hadAccess != (self.accessibilityGranted && self.inputMonitoringGranted) { self.updateInput() }
            }
        }
        escapeMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            if event.keyCode == 53 { Task { @MainActor in self?.ring.hide() } }
        }
        if !CommandLine.arguments.contains("--background") || !accessibilityGranted || !inputMonitoringGranted { openSettings() }
    }

    func stop() {
        permissionTimer?.invalidate()
        if let escapeMonitor { NSEvent.removeMonitor(escapeMonitor) }
        input.stop()
        ring.hide()
        if let statusItem { NSStatusBar.system.removeStatusItem(statusItem) }
    }

    func openSettings() {
        if settingsWindow == nil {
            let view = SettingsView(controller: self, store: store)
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1220, height: 800),
                                  styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
            window.title = "Actions Ring"
            window.contentView = NSHostingView(rootView: view)
            window.minSize = NSSize(width: 960, height: 620)
            window.isReleasedWhenClosed = false
            window.center()
            settingsWindow = window
        }
        settingsWindow?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func showRingPreview(profile: RingProfile? = nil) {
        guard let chosen = profile ?? store.configuration.profile(for: nil) else { return }
        previewing = true
        targetApplication = nil
        ring.show(profile: chosen, preferences: store.configuration.preferences)
    }

    func saveConfiguration() {
        do {
            try store.save()
            applyAppearance()
            if lastBinding != store.configuration.trigger { updateInput() }
            rebuildMenu()
        } catch { statusMessage = error.localizedDescription }
    }

    func requestPermissions() {
        guard runtimeEnabled else { return }
        MacInputService.requestPermissions()
        refreshPermissions()
        updateInput()
    }

    func captureShortcut(completion: @escaping (String, [KeyModifier]) -> Void) {
        guard runtimeEnabled else { return }
        guard accessibilityGranted && inputMonitoringGranted else {
            statusMessage = "Разрешите Универсальный доступ и Мониторинг ввода или выберите клавишу вручную."
            return
        }
        input.captureShortcut(completion: completion)
    }

    func cancelShortcutCapture() { input.cancelCapture() }

    func setLaunchAtLogin(_ enabled: Bool) {
        guard runtimeEnabled else { return }
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
            store.configuration.preferences.launchAtLogin = enabled
            try store.save()
            if SMAppService.mainApp.status == .requiresApproval {
                statusMessage = "Подтвердите автозапуск Actions Ring в системных настройках → Основные → Объекты входа."
                SMAppService.openSystemSettingsLoginItems()
            }
        } catch {
            store.configuration.preferences.launchAtLogin = SMAppService.mainApp.status == .enabled
            statusMessage = error.localizedDescription
        }
    }

    func revealSettingsFolder() { NSWorkspace.shared.activateFileViewerSelecting([store.configurationURL]) }

    func checkForUpdates() {
        statusMessage = "Проверяем обновления…"
        Task {
            do {
                let configuration = URLSessionConfiguration.ephemeral
                configuration.timeoutIntervalForRequest = 15
                let session = URLSession(configuration: configuration)
                defer { session.invalidateAndCancel() }
                var request = URLRequest(url: URL(string: "https://api.github.com/repos/perf769/actions-ring/releases?per_page=30")!)
                request.setValue("ActionsRing-macOS/\(version)", forHTTPHeaderField: "User-Agent")
                let (data, response) = try await session.data(for: request)
                guard (response as? HTTPURLResponse)?.statusCode == 200, data.count <= 2_000_000 else {
                    throw URLError(.badServerResponse)
                }
                let releases = try JSONDecoder().decode([MacRelease].self, from: data)
                let available = releases.filter { !$0.draft && $0.tag_name.hasPrefix("macos-v") &&
                    $0.assets.contains { $0.name == "ActionsRing-macOS-AppleSilicon.zip" } }
                    .sorted { $0.version.compare($1.version, options: .numeric) == .orderedDescending }
                guard let latest = available.first, latest.version.compare(version, options: .numeric) == .orderedDescending else {
                    statusMessage = "Установлена актуальная версия \(version)."; return
                }
                statusMessage = "Доступна версия \(latest.version)."
                let alert = NSAlert()
                alert.messageText = "Доступна Actions Ring \(latest.version)"
                alert.informativeText = String((latest.body ?? "Новая версия Actions Ring для macOS.").prefix(3500))
                alert.addButton(withTitle: "Открыть загрузку")
                alert.addButton(withTitle: "Позже")
                if alert.runModal() == .alertFirstButtonReturn,
                   let url = URL(string: latest.html_url), url.scheme == "https", url.host == "github.com",
                   url.path.hasPrefix("/perf769/actions-ring/releases/") { NSWorkspace.shared.open(url) }
            } catch { statusMessage = "Не удалось проверить обновления. Проверьте подключение к интернету." }
        }
    }

    private func applyAppearance() {
        switch store.configuration.preferences.appearance {
        case .system: NSApp.appearance = nil
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        }
    }

    private func refreshPermissions() {
        accessibilityGranted = MacInputService.accessibilityGranted
        inputMonitoringGranted = MacInputService.inputMonitoringGranted
    }

    private func updateInput() {
        guard runtimeEnabled else { return }
        input.stop()
        ring.hide()
        lastBinding = store.configuration.trigger
        guard isEnabled else { ring.hide(); return }
        if accessibilityGranted && inputMonitoringGranted {
            if !input.start(binding: store.configuration.trigger) {
                statusMessage = "Не удалось включить вызов кольца. Проверьте разрешения и перезапустите приложение."
            }
        }
    }

    private func handleTrigger(frontmost: NSRunningApplication?) {
        guard isEnabled else { return }
        if ring.isVisible {
            if store.configuration.trigger.mode == .toggle { ring.commit(holdRelease: false) }
            return
        }
        targetApplication = frontmost
        previewing = false
        guard let profile = store.configuration.profile(for: targetApplication?.bundleIdentifier) else { return }
        ring.show(profile: profile, preferences: store.configuration.preferences)
    }

    private func execute(_ action: RingAction) {
        guard !previewing else { statusMessage = "Предпросмотр: \(action.name)"; return }
        let target = targetApplication
        Task {
            do { try await actions.execute(action, target: target) }
            catch { statusMessage = error.localizedDescription }
        }
    }

    private func rebuildMenu() {
        guard let statusItem else { return }
        let menu = NSMenu()
        let settings = NSMenuItem(title: "Открыть настройки", action: #selector(menuSettings), keyEquivalent: ",")
        settings.target = self; menu.addItem(settings)
        let preview = NSMenuItem(title: "Показать кольцо", action: #selector(menuRing), keyEquivalent: "")
        preview.target = self; menu.addItem(preview)
        menu.addItem(.separator())
        let pause = NSMenuItem(title: isEnabled ? "Приостановить" : "Включить кольцо", action: #selector(menuToggle), keyEquivalent: "")
        pause.target = self; menu.addItem(pause)
        let users = NSMenu()
        for user in store.configuration.userProfiles {
            let item = NSMenuItem(title: user.name, action: #selector(menuProfile(_:)), keyEquivalent: "")
            item.target = self; item.representedObject = user.id
            item.state = user.id == store.configuration.activeUserProfileID ? .on : .off
            users.addItem(item)
        }
        let profileItem = NSMenuItem(title: "Профиль", action: nil, keyEquivalent: "")
        profileItem.submenu = users; menu.addItem(profileItem)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "Завершить Actions Ring", action: #selector(menuQuit), keyEquivalent: "q")
        quit.target = self; menu.addItem(quit)
        statusItem.menu = menu
    }

    @objc private func menuSettings() { openSettings() }
    @objc private func menuRing() { showRingPreview() }
    @objc private func menuToggle() { isEnabled.toggle() }
    @objc private func menuQuit() { NSApp.terminate(nil) }
    @objc private func menuProfile(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        store.configuration.activeUserProfileID = id; ring.hide(); saveConfiguration()
    }
}

private struct MacRelease: Decodable {
    struct Asset: Decodable { let name: String }
    let tag_name: String
    let draft: Bool
    let body: String?
    let html_url: String
    let assets: [Asset]
    var version: String { String(tag_name.dropFirst("macos-v".count)) }
}
