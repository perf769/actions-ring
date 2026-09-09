import Combine
import Foundation

public enum ConfigurationError: LocalizedError {
    case unsupportedVersion(Int)
    case invalid(String)
    case oversized
    case malformed
    case externallyChanged

    public var errorDescription: String? {
        switch self {
        case .unsupportedVersion(let version):
            return "Этот файл настроек создан для другой версии приложения (формат \(version))."
        case .invalid(let field): return "Проверьте настройки: \(field)."
        case .oversized: return "Файл настроек слишком большой. Допустимый размер — 2 МБ."
        case .malformed: return "Не удалось прочитать настройки. Исходный файл сохранён без изменений."
        case .externallyChanged: return "Файл настроек изменён вне приложения. Откройте Actions Ring заново перед сохранением."
        }
    }
}

public enum ConfigurationCodec {
    public static let maximumFileBytes = 2 * 1024 * 1024

    public static func encode(_ configuration: RingConfiguration) throws -> Data {
        try validate(configuration)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(configuration)
        guard data.count <= maximumFileBytes else { throw ConfigurationError.oversized }
        return data
    }

    public static func decode(_ data: Data) throws -> RingConfiguration {
        guard data.count <= maximumFileBytes else { throw ConfigurationError.oversized }
        let object: Any
        do { object = try JSONSerialization.jsonObject(with: data) }
        catch { throw ConfigurationError.malformed }
        guard let root = object as? [String: Any], let version = root["schemaVersion"] as? Int else {
            throw ConfigurationError.malformed
        }
        guard version == RingConfiguration.currentSchemaVersion else {
            throw ConfigurationError.unsupportedVersion(version)
        }
        var budget = 50_000
        try inspectJSON(object, depth: 0, budget: &budget)
        let result: RingConfiguration
        do { result = try JSONDecoder().decode(RingConfiguration.self, from: data) }
        catch { throw ConfigurationError.malformed }
        try validate(result)
        return result
    }

    public static func validate(_ configuration: RingConfiguration) throws {
        guard configuration.schemaVersion == RingConfiguration.currentSchemaVersion else {
            throw ConfigurationError.unsupportedVersion(configuration.schemaVersion)
        }
        guard (1...24).contains(configuration.userProfiles.count), configuration.activeUserProfile != nil else {
            throw ConfigurationError.invalid("профиль пользователя")
        }
        guard configuration.preferences.ringScale.isFinite,
              (0.65...1.8).contains(configuration.preferences.ringScale) else {
            throw ConfigurationError.invalid("размер кольца")
        }
        try validate(configuration.trigger)
        var identifiers = Set<String>()
        var slotBudget = 512
        for user in configuration.userProfiles {
            try identifier(user.id, seen: &identifiers)
            try name(user.name)
            guard (1...128).contains(user.profiles.count),
                  user.profiles.filter({ $0.bundleIdentifier == nil }).count == 1,
                  user.profiles.contains(where: { $0.bundleIdentifier == nil && $0.isEnabled }) else {
                throw ConfigurationError.invalid("общее кольцо пользователя")
            }
            var bundles = Set<String>()
            for profile in user.profiles {
                try identifier(profile.id, seen: &identifiers)
                try name(profile.name)
                if let bundle = profile.bundleIdentifier {
                    guard !bundle.isEmpty, bundle.count <= 512, !bundle.contains("/"),
                          bundles.insert(bundle).inserted else {
                        throw ConfigurationError.invalid("идентификатор приложения")
                    }
                }
                try palette(profile.palette)
                try slots(profile.slots, depth: 0, budget: &slotBudget, identifiers: &identifiers)
            }
        }
    }

    public static func validate(_ trigger: TriggerBinding) throws {
        guard Set(trigger.modifiers).count == trigger.modifiers.count else {
            throw ConfigurationError.invalid("модификаторы вызова кольца")
        }
        guard (0...31).contains(trigger.mouseButton), trigger.scrollThreshold.isFinite,
              (10...500).contains(trigger.scrollThreshold) else {
            throw ConfigurationError.invalid("вызов кольца")
        }
        switch trigger.device {
        case .scroll:
            guard !trigger.modifiers.isEmpty else {
                throw ConfigurationError.invalid("для жеста прокрутки выберите хотя бы один модификатор")
            }
        case .keyboard:
            guard KeyboardKeys.supported.contains(trigger.key) else {
                throw ConfigurationError.invalid("клавиша вызова кольца")
            }
        case .mouse: break
        }
    }

    private static func inspectJSON(_ value: Any, depth: Int, budget: inout Int) throws {
        budget -= 1
        guard depth <= 48, budget >= 0 else { throw ConfigurationError.invalid("слишком сложная структура файла") }
        if let dictionary = value as? [String: Any] {
            for child in dictionary.values { try inspectJSON(child, depth: depth + 1, budget: &budget) }
        } else if let array = value as? [Any] {
            for child in array { try inspectJSON(child, depth: depth + 1, budget: &budget) }
        }
    }

    private static func slots(_ values: [RingSlot], depth: Int, budget: inout Int, identifiers: inout Set<String>) throws {
        guard depth <= 6, (depth == 0 ? 4...8 : 1...9).contains(values.count) else {
            throw ConfigurationError.invalid("число пузырей или глубина подменю")
        }
        for slot in values {
            budget -= 1
            guard budget >= 0 else { throw ConfigurationError.invalid("слишком много пузырей") }
            try identifier(slot.id, seen: &identifiers)
            try name(slot.label)
            try icon(slot.icon)
            if let colors = slot.colors {
                for color in [colors.bubble, colors.hover, colors.icon, colors.hoverIcon].compactMap({ $0 }) {
                    try validateColor(color)
                }
            }
            if let action = slot.action { try validate(action) }
            if let children = slot.submenu, !children.isEmpty {
                try slots(children, depth: depth + 1, budget: &budget, identifiers: &identifiers)
            }
        }
    }

    public static func validate(_ action: RingAction) throws {
        try name(action.name)
        guard !action.id.isEmpty, action.id.count <= 200, action.detail.count <= 2048,
              action.value.count <= 16_384, Set(action.modifiers).count == action.modifiers.count else {
            throw ConfigurationError.invalid("параметры действия")
        }
        try icon(action.icon)
        switch action.kind {
        case .shortcut:
            guard KeyboardKeys.supported.contains(action.value) else { throw ConfigurationError.invalid("сочетание клавиш") }
        case .system:
            guard ActionCatalog.systemCommandIDs.contains(action.value) else { throw ConfigurationError.invalid("системное действие") }
        case .url:
            guard let url = URL(string: action.value), let scheme = url.scheme?.lowercased(),
                  ["https", "http", "mailto"].contains(scheme),
                  (scheme == "mailto" ? !url.path.isEmpty : !(url.host?.isEmpty ?? true)),
                  url.user == nil, url.password == nil else { throw ConfigurationError.invalid("адрес сайта") }
        case .application:
            guard !action.value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  action.value.count <= 4096, !action.value.contains("\0"),
                  action.value.hasPrefix("/")
                    || (!action.value.contains("/") && action.value.contains(".") && !action.value.contains(" ")) else {
                throw ConfigurationError.invalid("путь к файлу или идентификатор приложения")
            }
        case .text: break
        }
    }

    private static func identifier(_ value: String, seen: inout Set<String>) throws {
        guard !value.isEmpty, value.count <= 200, seen.insert(value).inserted else {
            throw ConfigurationError.invalid("повторяющийся идентификатор профиля или пузыря")
        }
    }
    private static func name(_ value: String) throws {
        guard !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, value.count <= 120 else {
            throw ConfigurationError.invalid("название, от 1 до 120 символов")
        }
    }
    private static func icon(_ value: SlotIcon?) throws {
        guard let value else { return }
        guard !value.value.isEmpty, value.value.count <= 4096, !value.value.contains("\0") else {
            throw ConfigurationError.invalid("иконка")
        }
        if value.kind == .file, !value.value.hasPrefix("/") {
            throw ConfigurationError.invalid("путь к иконке")
        }
    }
    private static func palette(_ value: RingPalette) throws {
        for color in [value.bubble, value.hover, value.icon, value.hoverIcon] { try validateColor(color) }
    }
    public static func validateColor(_ value: String) throws {
        guard value.utf8.count == 7, value.first == "#", UInt32(value.dropFirst(), radix: 16) != nil else {
            throw ConfigurationError.invalid("цвет в формате #RRGGBB")
        }
    }
}

@MainActor
public final class ConfigurationStore: ObservableObject {
    @Published public var configuration: RingConfiguration
    public let directory: URL
    public var configurationURL: URL { directory.appendingPathComponent("settings-macos.json") }
    public var backupURL: URL { directory.appendingPathComponent("settings-macos.json.bak") }
    private var persistedData: Data?

    public init(directory: URL? = nil) throws {
        self.directory = directory ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ActionsRing", isDirectory: true)
        let settingsURL = self.directory.appendingPathComponent("settings-macos.json")
        if FileManager.default.fileExists(atPath: settingsURL.path) {
            let data = try Self.readBounded(settingsURL)
            configuration = try ConfigurationCodec.decode(data)
            persistedData = data
        } else {
            configuration = .makeDefault()
            persistedData = nil
            try save()
        }
    }

    /// Keeps the previous valid document as a backup. Failed validation never modifies either file.
    public func save() throws {
        try persist(configuration)
    }

    public func importConfiguration(from url: URL) throws {
        let incoming = try ConfigurationCodec.decode(Self.readBounded(url))
        try persist(incoming)
        configuration = incoming
    }

    public func exportConfiguration(to url: URL) throws {
        let data = try ConfigurationCodec.encode(configuration)
        try data.write(to: url, options: .atomic)
    }

    public func restoreBackup() throws {
        let restored = try ConfigurationCodec.decode(Self.readBounded(backupURL))
        try persist(restored)
        configuration = restored
    }

    private func persist(_ value: RingConfiguration) throws {
        let data = try ConfigurationCodec.encode(value)
        let exists = FileManager.default.fileExists(atPath: configurationURL.path)
        let current = exists ? try Self.readBounded(configurationURL) : nil
        guard current == persistedData else { throw ConfigurationError.externallyChanged }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        // Protect all settings and backups before replacing a document, including temporary atomic-write files.
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        if let current {
            _ = try ConfigurationCodec.decode(current)
            try current.write(to: backupURL, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: backupURL.path)
        }
        try data.write(to: configurationURL, options: .atomic)
        persistedData = data
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: configurationURL.path)
    }

    private static func readBounded(_ url: URL) throws -> Data {
        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
        guard size <= ConfigurationCodec.maximumFileBytes else { throw ConfigurationError.oversized }
        let data = try Data(contentsOf: url)
        guard data.count <= ConfigurationCodec.maximumFileBytes else { throw ConfigurationError.oversized }
        return data
    }
}
