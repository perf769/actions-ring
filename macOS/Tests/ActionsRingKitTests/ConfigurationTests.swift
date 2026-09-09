import Foundation
import XCTest
@testable import ActionsRingKit

final class ConfigurationTests: XCTestCase {
    func testDefaultConfigurationRoundTrips() throws {
        let original = RingConfiguration.makeDefault()
        let copy = try ConfigurationCodec.decode(ConfigurationCodec.encode(original))
        XCTAssertEqual(original, copy)
        XCTAssertEqual(copy.activeUserProfile?.profiles.first?.slots.count, 8)
        XCTAssertEqual(copy.trigger.device, .scroll)
        XCTAssertEqual(Set(copy.trigger.modifiers), [.control, .option])
    }

    func testApplicationContextUsesEnabledExactBundleThenFallback() {
        var user = UserProfile(profiles: [
            RingProfile(id: "global", slots: ActionCatalog.defaultSlots()),
            RingProfile(id: "photoshop", name: "Photoshop", bundleIdentifier: "com.adobe.Photoshop", slots: ActionCatalog.defaultSlots()),
        ])
        XCTAssertEqual(user.profile(for: "com.adobe.Photoshop")?.id, "photoshop")
        XCTAssertEqual(user.profile(for: "com.adobe.Photoshop.Other")?.id, "global")
        XCTAssertEqual(user.profile(for: nil)?.id, "global")
        user.profiles[1].isEnabled = false
        XCTAssertEqual(user.profile(for: "com.adobe.Photoshop")?.id, "global")
    }

    func testHybridSubmenuAndColorsRoundTrip() throws {
        var configuration = RingConfiguration.makeDefault()
        let child = RingSlot(action: RingAction(name: "Сайт", kind: .url, value: "https://chatgpt.com"))
        configuration.userProfiles[0].profiles[0].slots[0].submenu = [child]
        configuration.userProfiles[0].profiles[0].slots[0].colors = SlotColors(bubble: "#8040FF", icon: "#FFFFFF")
        let result = try ConfigurationCodec.decode(ConfigurationCodec.encode(configuration))
        let slot = result.userProfiles[0].profiles[0].slots[0]
        XCTAssertTrue(slot.hasSubmenu)
        XCTAssertEqual(slot.action?.value, "copy")
        XCTAssertEqual(slot.submenu?.first?.action?.value, "https://chatgpt.com")
        XCTAssertEqual(slot.colors?.resolving(RingPalette()).bubble, "#8040FF")
        XCTAssertEqual(slot.colors?.resolving(RingPalette()).hover, RingPalette().hover)
    }

    func testRejectsNewerSchemaInsteadOfReplacingSettings() throws {
        var value = RingConfiguration.makeDefault()
        value.schemaVersion = 2
        let data = try JSONEncoder().encode(value)
        XCTAssertThrowsError(try ConfigurationCodec.decode(data)) { error in
            guard case ConfigurationError.unsupportedVersion(2) = error else { return XCTFail("Unexpected error: \(error)") }
        }
    }

    func testRejectsMalformedAndOversizedSettings() {
        XCTAssertThrowsError(try ConfigurationCodec.decode(Data("{broken".utf8)))
        XCTAssertThrowsError(try ConfigurationCodec.decode(Data(repeating: 32, count: ConfigurationCodec.maximumFileBytes + 1)))
    }

    func testRejectsDuplicateSlotIDsAndMissingFallback() {
        var value = RingConfiguration.makeDefault()
        value.userProfiles[0].profiles[0].slots[1].id = value.userProfiles[0].profiles[0].slots[0].id
        XCTAssertThrowsError(try ConfigurationCodec.validate(value))
        value = .makeDefault()
        value.userProfiles[0].profiles[0].bundleIdentifier = "com.apple.Safari"
        XCTAssertThrowsError(try ConfigurationCodec.validate(value))
    }

    func testRejectsExcessiveSubmenuDepth() {
        var value = RingConfiguration.makeDefault()
        var slot = RingSlot(label: "Последний")
        for _ in 0..<8 { slot = RingSlot(label: "Подменю", submenu: [slot]) }
        value.userProfiles[0].profiles[0].slots[0] = slot
        XCTAssertThrowsError(try ConfigurationCodec.validate(value))
    }

    func testScrollCannotStealUnmodifiedScrolling() {
        let trigger = TriggerBinding(modifiers: [])
        XCTAssertThrowsError(try ConfigurationCodec.validate(trigger))
    }

    func testScrollCanRemainOpenUntilModifierRelease() throws {
        try ConfigurationCodec.validate(TriggerBinding(mode: .hold))
    }

    func testCommandOptionKeyboardTriggerRemainsValid() throws {
        let trigger = TriggerBinding(device: .keyboard, key: "Space", modifiers: [.command, .option], mode: .hold)
        try ConfigurationCodec.validate(trigger)
        var config = RingConfiguration.makeDefault()
        config.trigger = trigger
        XCTAssertEqual(try ConfigurationCodec.decode(ConfigurationCodec.encode(config)).trigger, trigger)
    }

    func testRejectsInvalidActionsAndColors() {
        for action in [
            RingAction(name: "Command", kind: .system, value: "runShell"),
            RingAction(name: "Website", kind: .url, value: "javascript:alert(1)"),
            RingAction(name: "Website", kind: .url, value: "https://user:secret@example.com"),
            RingAction(name: "App", kind: .application, value: "relative/folder"),
            RingAction(name: "Key", kind: .shortcut, value: "Unknown"),
        ] { XCTAssertThrowsError(try ConfigurationCodec.validate(action)) }
        for value in ["red", "#fff", "#GGGGGG", "#12345678"] {
            XCTAssertThrowsError(try ConfigurationCodec.validateColor(value))
        }
    }

    func testAcceptsApplicationBundleAndPathWithSpaces() throws {
        try ConfigurationCodec.validate(RingAction(name: "Finder", kind: .application, value: "com.apple.finder"))
        try ConfigurationCodec.validate(RingAction(name: "App", kind: .application, value: "/Applications/An App.app"))
        try ConfigurationCodec.validate(RingAction(name: "Folder", kind: .application, value: "/Users/example/Downloads"))
    }

    func testCopyCreatesNewIDsAtEverySubmenuLevel() {
        let original = RingSlot(action: RingAction(name: "Копировать", kind: .system, value: "copy"))
        let parent = RingSlot(label: "Папка", submenu: [original])
        let copied = parent.copyWithNewIDs()
        XCTAssertNotEqual(parent.id, copied.id)
        XCTAssertNotEqual(parent.submenu?.first?.id, copied.submenu?.first?.id)
        XCTAssertNotEqual(parent.submenu?.first?.action?.id, copied.submenu?.first?.action?.id)
        XCTAssertEqual(copied.submenu?.first?.action?.value, "copy")
    }

    func testStoreBackupImportAndExport() async throws {
        try await MainActor.run {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ActionsRingTests-\(UUID().uuidString)", isDirectory: true)
            defer { try? FileManager.default.removeItem(at: directory) }
            let store = try ConfigurationStore(directory: directory)
            let original = store.configuration
            store.configuration.userProfiles[0].name = "Работа"
            try store.save()
            XCTAssertEqual(try ConfigurationCodec.decode(Data(contentsOf: store.backupURL)), original)
            XCTAssertEqual(try ConfigurationStore(directory: directory).configuration.userProfiles[0].name, "Работа")
            let exportURL = directory.appendingPathComponent("export.json")
            try store.exportConfiguration(to: exportURL)
            try store.restoreBackup()
            XCTAssertEqual(store.configuration, original)
            try store.importConfiguration(from: exportURL)
            XCTAssertEqual(store.configuration.userProfiles[0].name, "Работа")
        }
    }

    func testInvalidSavePreservesBothFiles() async throws {
        try await MainActor.run {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ActionsRingTests-\(UUID().uuidString)", isDirectory: true)
            defer { try? FileManager.default.removeItem(at: directory) }
            let store = try ConfigurationStore(directory: directory)
            try store.save()
            let original = try Data(contentsOf: store.configurationURL)
            let backup = try Data(contentsOf: store.backupURL)
            store.configuration.userProfiles.removeAll()
            XCTAssertThrowsError(try store.save())
            XCTAssertEqual(try Data(contentsOf: store.configurationURL), original)
            XCTAssertEqual(try Data(contentsOf: store.backupURL), backup)
        }
    }

    func testCorruptedStoreIsNotOverwritten() async throws {
        try await MainActor.run {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ActionsRingTests-\(UUID().uuidString)", isDirectory: true)
            defer { try? FileManager.default.removeItem(at: directory) }
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let url = directory.appendingPathComponent("settings-macos.json")
            let corrupted = Data("{not valid}".utf8)
            try corrupted.write(to: url)
            XCTAssertThrowsError(try ConfigurationStore(directory: directory))
            XCTAssertEqual(try Data(contentsOf: url), corrupted)
        }
    }

    func testExternalChangesAreNotOverwritten() async throws {
        try await MainActor.run {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ActionsRingTests-\(UUID().uuidString)", isDirectory: true)
            defer { try? FileManager.default.removeItem(at: directory) }
            let store = try ConfigurationStore(directory: directory)
            var external = store.configuration
            external.userProfiles[0].name = "Другой профиль"
            let data = try ConfigurationCodec.encode(external)
            try data.write(to: store.configurationURL)
            XCTAssertThrowsError(try store.save())
            XCTAssertEqual(try Data(contentsOf: store.configurationURL), data)
        }
    }
}
