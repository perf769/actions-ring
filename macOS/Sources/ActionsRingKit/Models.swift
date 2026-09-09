import Foundation

public enum KeyModifier: String, Codable, CaseIterable, Identifiable, Sendable {
    case command, control, option, shift
    public var id: String { rawValue }
    public var symbol: String {
        switch self {
        case .command: return "⌘"
        case .control: return "⌃"
        case .option: return "⌥"
        case .shift: return "⇧"
        }
    }
}

public enum ActionKind: String, Codable, CaseIterable, Identifiable, Sendable {
    case shortcut, application, url, text, system
    public var id: String { rawValue }
}

public struct SlotIcon: Codable, Equatable, Hashable, Sendable {
    public enum Kind: String, Codable, CaseIterable, Sendable { case symbol, file, application }
    public var kind: Kind
    public var value: String
    public init(kind: Kind = .symbol, value: String) {
        self.kind = kind
        self.value = value
    }
    public static func symbol(_ name: String) -> SlotIcon { .init(value: name) }
}

public struct RingAction: Codable, Equatable, Identifiable, Sendable {
    public var id: String
    public var name: String
    public var kind: ActionKind
    /// A canonical key name, absolute file/app path or bundle identifier, URI, text, or system command ID.
    public var value: String
    public var modifiers: [KeyModifier]
    public var detail: String
    public var icon: SlotIcon?

    public init(id: String = UUID().uuidString, name: String, kind: ActionKind,
                value: String = "", modifiers: [KeyModifier] = [], detail: String = "",
                icon: SlotIcon? = nil) {
        self.id = id
        self.name = name
        self.kind = kind
        self.value = value
        self.modifiers = modifiers
        self.detail = detail
        self.icon = icon
    }
    public var shortcutLabel: String {
        KeyModifier.allCases.filter { modifiers.contains($0) }.map(\.symbol).joined() + value
    }
}

public enum RingTheme: String, Codable, CaseIterable, Identifiable, Sendable {
    case light, dark, ocean, purple, custom
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .light: return "Светлая"
        case .dark: return "Тёмная"
        case .ocean: return "Океан"
        case .purple: return "Фиолетовая"
        case .custom: return "Своя тема"
        }
    }
    public var palette: RingPalette {
        switch self {
        case .light, .custom: return RingPalette()
        case .dark: return RingPalette(bubble: "#242426", hover: "#F5F5F3", icon: "#FFFFFF", hoverIcon: "#101316")
        case .ocean: return RingPalette(bubble: "#DFF6FC", hover: "#006C84", icon: "#164455", hoverIcon: "#FFFFFF")
        case .purple: return RingPalette(bubble: "#EADFFF", hover: "#814DFF", icon: "#57309B", hoverIcon: "#FFFFFF")
        }
    }
}

public struct RingPalette: Codable, Equatable, Sendable {
    public var bubble: String
    public var hover: String
    public var icon: String
    public var hoverIcon: String
    public init(bubble: String = "#F5F5F3", hover: String = "#050607",
                icon: String = "#101316", hoverIcon: String = "#FFFFFF") {
        self.bubble = bubble
        self.hover = hover
        self.icon = icon
        self.hoverIcon = hoverIcon
    }
}

public struct SlotColors: Codable, Equatable, Sendable {
    public var bubble: String?
    public var hover: String?
    public var icon: String?
    public var hoverIcon: String?
    public init(bubble: String? = nil, hover: String? = nil, icon: String? = nil, hoverIcon: String? = nil) {
        self.bubble = bubble
        self.hover = hover
        self.icon = icon
        self.hoverIcon = hoverIcon
    }
    public func resolving(_ palette: RingPalette) -> RingPalette {
        .init(bubble: bubble ?? palette.bubble, hover: hover ?? palette.hover,
              icon: icon ?? palette.icon, hoverIcon: hoverIcon ?? palette.hoverIcon)
    }
}

public struct RingSlot: Codable, Equatable, Identifiable, Sendable {
    public var id: String
    public var label: String
    public var icon: SlotIcon?
    public var action: RingAction?
    public var submenu: [RingSlot]?
    public var colors: SlotColors?
    public init(id: String = UUID().uuidString, label: String = "Добавить действие",
                icon: SlotIcon? = nil, action: RingAction? = nil,
                submenu: [RingSlot]? = nil, colors: SlotColors? = nil) {
        self.id = id
        self.label = label
        self.icon = icon
        self.action = action
        self.submenu = submenu
        self.colors = colors
    }
    public init(action: RingAction) {
        self.init(label: action.name, icon: action.icon, action: action)
    }
    public var hasSubmenu: Bool { !(submenu?.isEmpty ?? true) }
    public var effectiveIcon: SlotIcon {
        icon ?? action?.icon ?? .symbol(hasSubmenu ? "folder" : "plus")
    }
    public func copyWithNewIDs() -> RingSlot {
        var result = self
        result.id = UUID().uuidString
        result.action?.id = UUID().uuidString
        result.submenu = submenu?.map { $0.copyWithNewIDs() }
        return result
    }
}

public struct RingProfile: Codable, Equatable, Identifiable, Sendable {
    public var id: String
    public var name: String
    /// nil denotes the fallback ring, otherwise an exact macOS application bundle identifier.
    public var bundleIdentifier: String?
    public var isEnabled: Bool
    public var theme: RingTheme
    public var palette: RingPalette
    public var slots: [RingSlot]
    public init(id: String = UUID().uuidString, name: String = "Все приложения",
                bundleIdentifier: String? = nil, isEnabled: Bool = true,
                theme: RingTheme = .light, palette: RingPalette = .init(),
                slots: [RingSlot] = []) {
        self.id = id
        self.name = name
        self.bundleIdentifier = bundleIdentifier
        self.isEnabled = isEnabled
        self.theme = theme
        self.palette = palette
        self.slots = slots
    }
    public var effectivePalette: RingPalette { theme == .custom ? palette : theme.palette }
}

public struct UserProfile: Codable, Equatable, Identifiable, Sendable {
    public var id: String
    public var name: String
    public var profiles: [RingProfile]
    public init(id: String = UUID().uuidString, name: String = "Основной", profiles: [RingProfile] = []) {
        self.id = id
        self.name = name
        self.profiles = profiles
    }
    public func profile(for bundleIdentifier: String?) -> RingProfile? {
        if let bundleIdentifier,
           let match = profiles.first(where: { $0.isEnabled && $0.bundleIdentifier == bundleIdentifier }) {
            return match
        }
        return profiles.first { $0.bundleIdentifier == nil && $0.isEnabled }
    }
}

public enum TriggerDevice: String, Codable, CaseIterable, Identifiable, Sendable {
    case keyboard, mouse, scroll
    public var id: String { rawValue }
}
public enum ActivationMode: String, Codable, CaseIterable, Identifiable, Sendable {
    case toggle, hold
    public var id: String { rawValue }
}
public enum ScrollDirection: String, Codable, CaseIterable, Identifiable, Sendable {
    case up, down, left, right
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .up: return "Вверх"
        case .down: return "Вниз"
        case .left: return "Влево"
        case .right: return "Вправо"
        }
    }
}

public struct TriggerBinding: Codable, Equatable, Sendable {
    public var device: TriggerDevice
    public var key: String
    public var modifiers: [KeyModifier]
    /// Core Graphics numbering: left 0, right 1, middle 2, side buttons 3 and 4.
    public var mouseButton: Int
    public var scrollDirection: ScrollDirection
    public var mode: ActivationMode
    public var scrollThreshold: Double
    public init(device: TriggerDevice = .scroll, key: String = "Space",
                modifiers: [KeyModifier] = [.control, .option], mouseButton: Int = 4,
                scrollDirection: ScrollDirection = .up, mode: ActivationMode = .toggle,
                scrollThreshold: Double = 45) {
        self.device = device
        self.key = key
        self.modifiers = modifiers
        self.mouseButton = mouseButton
        self.scrollDirection = scrollDirection
        self.mode = mode
        self.scrollThreshold = scrollThreshold
    }
}

public enum AppAppearance: String, Codable, CaseIterable, Identifiable, Sendable {
    case system, light, dark
    public var id: String { rawValue }
}
public struct AppPreferences: Codable, Equatable, Sendable {
    public var appearance: AppAppearance
    public var ringScale: Double
    public var animations: Bool
    public var reducedMotion: Bool
    public var launchAtLogin: Bool
    public init(appearance: AppAppearance = .system, ringScale: Double = 1,
                animations: Bool = true, reducedMotion: Bool = false, launchAtLogin: Bool = false) {
        self.appearance = appearance
        self.ringScale = ringScale
        self.animations = animations
        self.reducedMotion = reducedMotion
        self.launchAtLogin = launchAtLogin
    }
}

public struct RingConfiguration: Codable, Equatable, Sendable {
    public static let currentSchemaVersion = 1
    public var schemaVersion: Int
    public var activeUserProfileID: String
    public var userProfiles: [UserProfile]
    public var preferences: AppPreferences
    public var trigger: TriggerBinding
    public init(schemaVersion: Int = currentSchemaVersion, activeUserProfileID: String,
                userProfiles: [UserProfile], preferences: AppPreferences = .init(), trigger: TriggerBinding = .init()) {
        self.schemaVersion = schemaVersion
        self.activeUserProfileID = activeUserProfileID
        self.userProfiles = userProfiles
        self.preferences = preferences
        self.trigger = trigger
    }
    public var activeUserProfile: UserProfile? {
        userProfiles.first { $0.id == activeUserProfileID }
    }
    public func profile(for bundleIdentifier: String?) -> RingProfile? {
        activeUserProfile?.profile(for: bundleIdentifier)
    }
    public static func makeDefault() -> RingConfiguration {
        let user = UserProfile(profiles: [RingProfile(slots: ActionCatalog.defaultSlots())])
        return RingConfiguration(activeUserProfileID: user.id, userProfiles: [user])
    }
}

public enum KeyboardKeys {
    private static let letters: [String] = (65...90).compactMap { code -> String? in
        guard let scalar = UnicodeScalar(code) else { return nil }
        return String(scalar)
    }
    private static let digits: [String] = (0...9).map { String($0) }
    private static let functionKeys: [String] = (1...20).map { "F\($0)" }
    private static let namedKeys: [String] = [
        "Space", "Tab", "Return", "Escape", "Delete", "ForwardDelete", "Home", "End",
        "PageUp", "PageDown", "Left", "Right", "Up", "Down", "Minus", "Equal", "LeftBracket",
        "RightBracket", "Backslash", "Semicolon", "Quote", "Comma", "Period", "Slash", "Grave",
    ]
    public static let supported: [String] = letters + digits + functionKeys + namedKeys
}
