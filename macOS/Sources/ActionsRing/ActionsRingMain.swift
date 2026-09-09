import AppKit
import CoreServices
import SwiftUI
import ActionsRingKit

@main
@MainActor
enum ActionsRingMain {
    static func main() {
        let application = NSApplication.shared
        application.setActivationPolicy(.accessory)
        if let index = CommandLine.arguments.firstIndex(of: "--smoke-test"), CommandLine.arguments.count > index + 1 {
            application.finishLaunching()
            do {
                try MacVisualSmoke.run(directory: URL(fileURLWithPath: CommandLine.arguments[index + 1]))
                print("MACOS_VISUAL_SMOKE_OK")
                exit(0)
            } catch { fputs("Smoke check failed: \(error)\n", stderr); exit(1) }
        }
        let delegate = RingApplicationDelegate()
        application.delegate = delegate
        withExtendedLifetime(delegate) { application.run() }
    }
}

@MainActor
private final class RingApplicationDelegate: NSObject, NSApplicationDelegate {
    private var controller: AppController?
    func applicationDidFinishLaunching(_ notification: Notification) {
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "app.actionsring.macos")
            .filter { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }
        if let running = others.first { running.activate(); NSApp.terminate(nil); return }
        do {
            let store = try ConfigurationStore()
            controller = AppController(store: store)
            // SMAppService launches the main app without custom CLI arguments.
            // Inspect the launch Apple event while applicationDidFinishLaunching is
            // handling it, so login launches remain quiet in the menu bar.
            let event = NSAppleEventManager.shared().currentAppleEvent
            let launchedAtLogin = event?.eventID == AEEventID(kAEOpenApplication)
                && event?.paramDescriptor(forKeyword: AEKeyword(keyAEPropData))?.enumCodeValue == OSType(keyAELaunchedAsLogInItem)
            controller?.start(startInBackground: launchedAtLogin || CommandLine.arguments.contains("--background"))
        } catch {
            let alert = NSAlert()
            alert.messageText = "Не удалось открыть Actions Ring"
            alert.informativeText = error.localizedDescription + "\nФайл настроек не был изменён."
            alert.addButton(withTitle: "Закрыть")
            alert.runModal()
            NSApp.terminate(nil)
        }
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        controller?.openSettings(); return true
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationWillTerminate(_ notification: Notification) { controller?.stop() }
}

@MainActor
private enum MacVisualSmoke {
    static func run(directory: URL) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let store = try ConfigurationStore(directory: directory.appendingPathComponent("isolated-settings"))
        let controller = AppController(store: store, enableRuntime: false)
        let original = store.configuration
        let originalData = try Data(contentsOf: store.configurationURL)
        store.configuration.trigger.mouseButton = 100
        controller.saveConfiguration()
        guard store.configuration == original, try Data(contentsOf: store.configurationURL) == originalData else {
            throw CocoaError(.coderInvalidValue)
        }
        var imported = original
        imported.userProfiles[0].name = "Работа"
        let importURL = directory.appendingPathComponent("import-check.json")
        try ConfigurationCodec.encode(imported).write(to: importURL, options: .atomic)
        try store.importConfiguration(from: importURL)
        let backupBeforeApply = try Data(contentsOf: store.backupURL)
        controller.configurationDidChange()
        guard store.configuration == imported, try Data(contentsOf: store.backupURL) == backupBeforeApply,
              backupBeforeApply == originalData else { throw CocoaError(.coderInvalidValue) }
        try store.restoreBackup()
        controller.configurationDidChange()
        controller.statusMessage = nil
        print("MACOS_SETTINGS_TRANSACTION_OK")
        for dark in [false, true] {
            NSApp.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            for section in SettingsSection.allCases {
                let view = SettingsView(controller: controller, store: store, initialSection: section)
                    .environment(\.colorScheme, dark ? .dark : .light)
                let host = NSHostingView(rootView: view)
                let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1220, height: 800),
                                      styleMask: [.borderless], backing: .buffered, defer: false)
                window.contentView = host
                window.setFrameOrigin(NSPoint(x: -3000, y: -3000))
                window.orderBack(nil)
                RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.2))
                host.layoutSubtreeIfNeeded()
                try capture(host, to: directory.appendingPathComponent("\(section.rawValue)-\(dark ? "dark" : "light").png"))
                window.orderOut(nil)
            }
        }
        var fixture = RingProfile(slots: ActionCatalog.defaultSlots())
        fixture.slots[6] = RingSlot(label: "Быстрые действия", icon: .symbol("circle.hexagongrid"), submenu: [
            RingSlot(action: RingAction(name: "Перевод текста", kind: .shortcut, value: "T", icon: .symbol("character.bubble"))),
            RingSlot(action: RingAction(name: "Запись экрана", kind: .shortcut, value: "5", icon: .symbol("video"))),
            RingSlot(action: RingAction(name: "Снимок области", kind: .shortcut, value: "4", icon: .symbol("camera.viewfinder"))),
            RingSlot(action: RingAction(name: "Снимок экрана", kind: .shortcut, value: "3", icon: .symbol("camera"))),
            RingSlot(action: RingAction(name: "Finder", kind: .system, value: "finder", icon: .symbol("folder")))
        ])
        for dark in [false, true] {
            let suffix = dark ? "dark" : "light"
            NSApp.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            try render(ActionEditorView(action: RingAction(name: "Копировать", kind: .shortcut, value: "C", modifiers: [.command]),
                                        captureShortcut: { _ in }, onSave: { _ in }),
                       size: NSSize(width: 620, height: 700), dark: dark,
                       to: directory.appendingPathComponent("action-editor-\(suffix).png"))
            try render(SlotEditorView(slot: fixture.slots[6], bundleIdentifier: "com.adobe.Photoshop", palette: fixture.effectivePalette,
                                      captureShortcut: { _ in }, onSave: { _ in }),
                       size: NSSize(width: 700, height: 740), dark: dark,
                       to: directory.appendingPathComponent("submenu-editor-\(suffix).png"))
            try render(ActionLibraryView(bundleIdentifier: "com.adobe.Photoshop", captureShortcut: { _ in },
                                         cancelShortcutCapture: {}, onSelect: { _ in }),
                       size: NSSize(width: 780, height: 700), dark: dark,
                       to: directory.appendingPathComponent("action-library-\(suffix).png"))
        }
        for theme in [RingTheme.light, .dark, .purple] {
            fixture.theme = theme
            controller.showRingPreview(profile: fixture)
            RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.5))
            guard let content = controller.ring.panel?.contentView else { throw CocoaError(.fileReadUnknown) }
            try capture(content, to: directory.appendingPathComponent("ring-overlay-\(theme.rawValue).png"))
            controller.ring.previewSubmenu(at: 6)
            RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.5))
            try capture(content, to: directory.appendingPathComponent("ring-submenu-\(theme.rawValue).png"))
            controller.ring.commit(holdRelease: true)
            RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.4))
            guard !controller.ring.isVisible else { throw CocoaError(.coderInvalidValue) }
        }
        if let screen = NSScreen.main {
            let center = NSPoint(x: screen.visibleFrame.midX, y: screen.visibleFrame.midY)
            for theme in [RingTheme.light, .dark] {
                fixture.theme = theme
                controller.ring.show(profile: fixture, preferences: store.configuration.preferences, cursorOverride: center)
                RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.4))
                controller.ring.previewSubmenu(at: 6)
                RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.3))
                guard let content = controller.ring.panel?.contentView else { throw CocoaError(.fileReadUnknown) }
                try capture(content, to: directory.appendingPathComponent("ring-submenu-center-\(theme.rawValue).png"))
                controller.ring.hide()
            }
        }
        controller.ring.hide()
        guard !controller.ring.isVisible else { throw CocoaError(.coderInvalidValue) }
        try store.save()
    }

    private static func render<Content: View>(_ view: Content, size: NSSize, dark: Bool, to url: URL) throws {
        let host = NSHostingView(rootView: view.environment(\.colorScheme, dark ? .dark : .light))
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless], backing: .buffered, defer: false)
        window.contentView = host
        window.setFrameOrigin(NSPoint(x: -3000, y: -3000))
        window.orderBack(nil)
        RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.3))
        host.layoutSubtreeIfNeeded()
        try capture(host, to: url)
        window.orderOut(nil)
    }

    private static func capture(_ view: NSView, to url: URL) throws {
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { throw CocoaError(.fileWriteUnknown) }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let data = bitmap.representation(using: .png, properties: [:]), data.count > 2000 else { throw CocoaError(.fileWriteUnknown) }
        try data.write(to: url, options: .atomic)
    }
}
