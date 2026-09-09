import AppKit
import ApplicationServices
import CoreAudio
import CoreGraphics
import Foundation
import ActionsRingKit

enum MacActionError: LocalizedError {
    case permissionRequired
    case unsupportedKey(String)
    case invalidApplication
    case invalidURL
    case targetUnavailable
    case eventCreationFailed
    case unsupportedCommand
    case outputUnavailable
    case outputNotAdjustable
    case audioFailure(OSStatus)

    var errorDescription: String? {
        switch self {
        case .permissionRequired: return "Разрешите Actions Ring универсальный доступ в настройках macOS."
        case .unsupportedKey(let key): return "Неизвестная клавиша: \(key)."
        case .invalidApplication: return "Приложение, файл или папка не найдены. Выберите их снова."
        case .invalidURL: return "Укажите корректную ссылку HTTP, HTTPS или mailto."
        case .targetUnavailable: return "Исходное приложение недоступно. Вызовите кольцо в нужном окне ещё раз."
        case .eventCreationFailed: return "Не удалось отправить нажатие клавиши."
        case .unsupportedCommand: return "Это действие недоступно в macOS."
        case .outputUnavailable: return "Устройство вывода звука не найдено."
        case .outputNotAdjustable: return "Громкость этого устройства регулируется на самом устройстве."
        case .audioFailure: return "Не удалось изменить громкость устройства вывода звука."
        }
    }
}

@MainActor
final class MacActionService {
    func execute(_ action: RingAction, target: NSRunningApplication?) async throws {
        switch action.kind {
        case .application:
            try await openApplication(action.value)
        case .url:
            guard let components = URLComponents(string: action.value.trimmingCharacters(in: .whitespacesAndNewlines)),
                  let scheme = components.scheme?.lowercased(), ["http", "https", "mailto"].contains(scheme),
                  (scheme == "mailto" ? !components.path.isEmpty : !(components.host?.isEmpty ?? true)),
                  let url = components.url, NSWorkspace.shared.open(url) else { throw MacActionError.invalidURL }
        case .shortcut:
            try await sendShortcut(action.value, modifiers: action.modifiers, target: target)
        case .text:
            try await insertText(action.value, target: target)
        case .system:
            if let shortcut = ActionCatalog.systemShortcuts[action.value] {
                try await sendShortcut(shortcut.key, modifiers: shortcut.modifiers, target: target)
                return
            }
            switch action.value {
            case "finder": try await openApplication("com.apple.finder")
            case "settings": try await openApplication("com.apple.systempreferences")
            case "volumeUp": try MacOutputVolume.adjust(by: 1 / 16)
            case "volumeDown": try MacOutputVolume.adjust(by: -1 / 16)
            case "volumeMute": try MacOutputVolume.toggleMute()
            default: throw MacActionError.unsupportedCommand
            }
        }
    }

    private func openApplication(_ value: String) async throws {
        guard let url = MacIconLoader.applicationURL(value),
              FileManager.default.fileExists(atPath: url.path) else { throw MacActionError.invalidApplication }
        if url.pathExtension.caseInsensitiveCompare("app") != .orderedSame {
            guard NSWorkspace.shared.open(url) else { throw MacActionError.invalidApplication }
            return
        }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            NSWorkspace.shared.openApplication(at: url, configuration: configuration) { _, error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume(returning: ()) }
            }
        }
    }

    private func prepareTarget(_ target: NSRunningApplication?) async throws {
        guard MacInputService.accessibilityGranted else { throw MacActionError.permissionRequired }
        guard let target, !target.isTerminated else { throw MacActionError.targetUnavailable }
        if NSWorkspace.shared.frontmostApplication?.processIdentifier != target.processIdentifier {
            guard target.activate() else { throw MacActionError.targetUnavailable }
            for _ in 0..<25 {
                if NSWorkspace.shared.frontmostApplication?.processIdentifier == target.processIdentifier { break }
                try await Task.sleep(nanoseconds: 12_000_000)
            }
        }
        try Task.checkCancellation()
        guard !target.isTerminated,
              NSWorkspace.shared.frontmostApplication?.processIdentifier == target.processIdentifier else {
            throw MacActionError.targetUnavailable
        }
    }

    private func sendShortcut(_ key: String, modifiers: [KeyModifier], target: NSRunningApplication?) async throws {
        guard let code = MacKeyboardMap.code(for: key) else { throw MacActionError.unsupportedKey(key) }
        try await prepareTarget(target)
        guard let source = CGEventSource(stateID: .privateState),
              let down = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: true),
              let up = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: false) else {
            throw MacActionError.eventCreationFailed
        }
        let flags = MacKeyboardMap.flags(for: modifiers)
        down.flags = flags
        up.flags = flags
        post(down)
        post(up)
    }

    private func insertText(_ text: String, target: NSRunningApplication?) async throws {
        guard !text.isEmpty else { return }
        try await prepareTarget(target)
        guard let source = CGEventSource(stateID: .privateState) else { throw MacActionError.eventCreationFailed }
        // Small scalar-aligned chunks preserve surrogate pairs without touching the
        // clipboard or introducing a dependency on the active keyboard layout.
        var chunks: [[UniChar]] = []
        var chunk: [UniChar] = []
        for scalar in text.unicodeScalars {
            let units = Array(String(scalar).utf16)
            if chunk.count + units.count > 20 { chunks.append(chunk); chunk = [] }
            chunk.append(contentsOf: units)
        }
        if !chunk.isEmpty { chunks.append(chunk) }
        for (index, units) in chunks.enumerated() {
            try Task.checkCancellation()
            guard let target, !target.isTerminated,
                  NSWorkspace.shared.frontmostApplication?.processIdentifier == target.processIdentifier else {
                throw MacActionError.targetUnavailable
            }
            guard let down = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: true),
                  let up = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: false) else {
                throw MacActionError.eventCreationFailed
            }
            down.flags = []
            up.flags = []
            units.withUnsafeBufferPointer { buffer in
                down.keyboardSetUnicodeString(stringLength: buffer.count, unicodeString: buffer.baseAddress)
                up.keyboardSetUnicodeString(stringLength: buffer.count, unicodeString: buffer.baseAddress)
            }
            post(down)
            post(up)
            if index % 16 == 15 { try await Task.sleep(nanoseconds: 2_000_000) }
        }
    }

    private func post(_ event: CGEvent) {
        event.setIntegerValueField(.eventSourceUserData, value: MacSyntheticEvent.tag)
        event.post(tap: .cgSessionEventTap)
    }
}

/// Public Core Audio controls; digital outputs may legitimately have no writable volume.
private enum MacOutputVolume {
    static func adjust(by delta: Float32) throws {
        let device = try defaultOutput()
        let controls = try writableControls(device, selector: kAudioDevicePropertyVolumeScalar)
        let values: [Float32] = try controls.map { address in try read(device, address, initial: Float32(0)) }
        for (address, value) in zip(controls, values) {
            try write(device, address, value: min(1, max(0, value + delta)))
        }
    }

    static func toggleMute() throws {
        let device = try defaultOutput()
        let controls = try writableControls(device, selector: kAudioDevicePropertyMute)
        let values: [UInt32] = try controls.map { address in try read(device, address, initial: UInt32(0)) }
        let newValue: UInt32 = values.contains(0) ? 1 : 0
        for address in controls { try write(device, address, value: newValue) }
    }

    private static func defaultOutput() throws -> AudioDeviceID {
        let address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        let device: AudioDeviceID = try read(AudioObjectID(kAudioObjectSystemObject), address, initial: AudioDeviceID(0))
        guard device != kAudioObjectUnknown else { throw MacActionError.outputUnavailable }
        return device
    }

    private static func writableControls(_ device: AudioDeviceID, selector: AudioObjectPropertySelector) throws -> [AudioObjectPropertyAddress] {
        func writable(_ element: AudioObjectPropertyElement) -> AudioObjectPropertyAddress? {
            var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioDevicePropertyScopeOutput, mElement: element)
            var settable = DarwinBoolean(false)
            guard AudioObjectHasProperty(device, &address),
                  AudioObjectIsPropertySettable(device, &address, &settable) == noErr,
                  settable.boolValue else { return nil }
            return address
        }
        if let master = writable(kAudioObjectPropertyElementMain) { return [master] }
        // Per-channel properties are contiguous; check a bounded hardware channel range
        // when no main control exists. This also handles ordinary stereo USB devices.
        let channels = (1...64).compactMap { writable(AudioObjectPropertyElement($0)) }
        guard !channels.isEmpty else { throw MacActionError.outputNotAdjustable }
        return channels
    }

    private static func read<T>(_ object: AudioObjectID, _ property: AudioObjectPropertyAddress, initial: T) throws -> T {
        var address = property
        var value = initial
        var size = UInt32(MemoryLayout<T>.size)
        let status = withUnsafeMutablePointer(to: &value) { pointer in
            AudioObjectGetPropertyData(object, &address, 0, nil, &size, pointer)
        }
        guard status == noErr else { throw MacActionError.audioFailure(status) }
        return value
    }

    private static func write<T>(_ object: AudioObjectID, _ property: AudioObjectPropertyAddress, value: T) throws {
        var address = property
        var value = value
        let status = withUnsafePointer(to: &value) { pointer in
            AudioObjectSetPropertyData(object, &address, 0, nil, UInt32(MemoryLayout<T>.size), pointer)
        }
        guard status == noErr else { throw MacActionError.audioFailure(status) }
    }
}

struct MacInstalledApplication: Identifiable {
    let id: String
    let name: String
    let bundleIdentifier: String
    let url: URL
    let icon: NSImage
}

@MainActor
enum MacApplicationDiscovery {
    private struct Entry: Sendable {
        let name: String
        let bundleIdentifier: String
        let url: URL
    }

    static func discover() async -> [MacInstalledApplication] {
        let entries = await Task.detached(priority: .utility) { scanApplications() }.value
        var result: [MacInstalledApplication] = []
        for (index, entry) in entries.enumerated() {
            guard !Task.isCancelled else { return [] }
            result.append(MacInstalledApplication(id: entry.bundleIdentifier + "|" + entry.url.path,
                name: entry.name, bundleIdentifier: entry.bundleIdentifier, url: entry.url,
                icon: NSWorkspace.shared.icon(forFile: entry.url.path)))
            if index % 20 == 19 { await Task.yield() }
        }
        return result
    }

    private nonisolated static func scanApplications() -> [Entry] {
        let files = FileManager.default
        let roots = [URL(fileURLWithPath: "/Applications", isDirectory: true),
            URL(fileURLWithPath: "/System/Applications", isDirectory: true),
            files.homeDirectoryForCurrentUser.appendingPathComponent("Applications", isDirectory: true)]
        var entries: [String: Entry] = [:]
        var visited = 0
        for root in roots {
            guard let enumerator = files.enumerator(at: root, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey],
                options: [.skipsHiddenFiles, .skipsPackageDescendants], errorHandler: { _, _ in true }) else { continue }
            for case let url as URL in enumerator {
                if Task.isCancelled { return [] }
                visited += 1
                if visited > 12_000 { break }
                if enumerator.level > 5 { enumerator.skipDescendants(); continue }
                if (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                    enumerator.skipDescendants()
                    continue
                }
                guard url.pathExtension.caseInsensitiveCompare("app") == .orderedSame else { continue }
                enumerator.skipDescendants()
                let canonical = url.standardizedFileURL.resolvingSymlinksInPath()
                guard let bundle = Bundle(url: canonical), let identifier = bundle.bundleIdentifier,
                      !identifier.isEmpty, bundle.executableURL != nil else { continue }
                let name = (bundle.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String)
                    ?? (bundle.object(forInfoDictionaryKey: "CFBundleName") as? String)
                    ?? url.deletingPathExtension().lastPathComponent
                let key = identifier + "|" + canonical.path
                entries[key] = Entry(name: name, bundleIdentifier: identifier, url: canonical)
            }
        }
        return entries.values.sorted {
            let order = $0.name.localizedStandardCompare($1.name)
            return order == .orderedSame ? $0.url.path < $1.url.path : order == .orderedAscending
        }
    }
}

@MainActor
enum MacIconLoader {
    private static let cache = NSCache<NSString, NSImage>()

    static func image(for icon: SlotIcon) -> NSImage? {
        let key = "\(icon.kind.rawValue)|\(icon.value)" as NSString
        if let image = cache.object(forKey: key) { return image }
        let image: NSImage?
        switch icon.kind {
        case .symbol:
            image = NSImage(systemSymbolName: icon.value, accessibilityDescription: nil)
        case .application:
            image = applicationURL(icon.value).map { NSWorkspace.shared.icon(forFile: $0.path) }
        case .file:
            let path = (icon.value as NSString).expandingTildeInPath
            let url = URL(fileURLWithPath: path)
            let allowed = ["png", "jpg", "jpeg", "tif", "tiff", "gif", "heic", "icns", "pdf", "webp"]
            guard allowed.contains(url.pathExtension.lowercased()),
                  let attributes = try? FileManager.default.attributesOfItem(atPath: path),
                  let size = attributes[.size] as? NSNumber, size.int64Value <= 8 * 1024 * 1024 else { return nil }
            image = NSImage(contentsOf: url)
        }
        if let image {
            cache.countLimit = 512
            cache.setObject(image, forKey: key)
        }
        return image
    }

    static func applicationURL(_ value: String) -> URL? {
        let value = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.hasPrefix("/") || value.hasPrefix("~/") {
            let url = URL(fileURLWithPath: (value as NSString).expandingTildeInPath).standardizedFileURL
            return FileManager.default.fileExists(atPath: url.path) ? url : nil
        }
        return NSWorkspace.shared.urlForApplication(withBundleIdentifier: value)
    }
}
