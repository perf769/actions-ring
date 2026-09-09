import XCTest
@testable import ActionsRingKit

final class CatalogAndGestureTests: XCTestCase {
    func testCatalogActionsAreValidAndHaveDistinctIdentifiers() throws {
        for bundle in [nil, "com.adobe.Photoshop", "com.apple.Safari", "com.google.Chrome"] {
            let categories = ActionCatalog.categories(for: bundle)
            let actions = categories.flatMap(\.items)
            XCTAssertEqual(Set(categories.map(\.id)).count, categories.count)
            XCTAssertEqual(Set(actions.map(\.id)).count, actions.count)
            for action in actions { try ConfigurationCodec.validate(action) }
        }
    }

    func testApplicationActionsPrecedeGeneralActions() {
        XCTAssertEqual(ActionCatalog.categories(for: "com.adobe.Photoshop").first?.id, "photoshop")
        XCTAssertEqual(ActionCatalog.categories(for: "com.apple.Safari").first?.id, "browser")
        XCTAssertFalse(ActionCatalog.categories().contains { $0.id == "photoshop" })
    }

    func testSearchFiltersWithoutLosingContextAndClearsCleanly() {
        let matches = ActionCatalog.search("КИСТЬ", bundleIdentifier: "com.adobe.Photoshop")
        XCTAssertEqual(matches.count, 1)
        XCTAssertEqual(matches.first?.items.first?.value, "B")
        XCTAssertEqual(ActionCatalog.search("  ").count, ActionCatalog.categories().count)
        XCTAssertTrue(ActionCatalog.search("qqq_nonexistent").isEmpty)
        XCTAssertEqual(ActionCatalog.search("вперед").first?.items.first?.value, "forward")
    }

    func testKnownGestureActionsUseMacShortcuts() {
        XCTAssertEqual(ActionCatalog.systemShortcuts["missionControl"], SystemShortcut("Up", [.control]))
        XCTAssertEqual(ActionCatalog.systemShortcuts["appExpose"], SystemShortcut("Down", [.control]))
        XCTAssertEqual(ActionCatalog.systemShortcuts["screenshotOptions"], SystemShortcut("5", [.command, .shift]))
        XCTAssertEqual(ActionCatalog.systemShortcuts["copy"], SystemShortcut("C", [.command]))
    }

    func testPlainScrollIsNeverSuppressed() {
        var recognizer = ScrollGestureRecognizer()
        let result = recognizer.update(deltaX: 0, deltaY: 100, modifiers: [], timestamp: 1, binding: TriggerBinding())
        XCTAssertFalse(result.shouldSuppress)
        XCTAssertFalse(result.triggered)
        let unsafeBinding = TriggerBinding(modifiers: [])
        XCTAssertFalse(recognizer.update(deltaX: 0, deltaY: 100, modifiers: [], timestamp: 2, binding: unsafeBinding).shouldSuppress)
    }

    func testScrollAccumulatesAndFiresOnlyOncePerGesture() {
        var recognizer = ScrollGestureRecognizer()
        let binding = TriggerBinding(scrollThreshold: 45)
        let mods = binding.modifiers
        XCTAssertFalse(recognizer.update(deltaX: 0, deltaY: 20, modifiers: mods, timestamp: 1, binding: binding).triggered)
        XCTAssertTrue(recognizer.update(deltaX: 0, deltaY: 30, modifiers: mods, timestamp: 1.02, binding: binding).triggered)
        XCTAssertFalse(recognizer.update(deltaX: 0, deltaY: 200, modifiers: mods, timestamp: 1.1, binding: binding).triggered)
        XCTAssertFalse(recognizer.update(deltaX: 0, deltaY: 200, modifiers: mods, timestamp: 1.8, isMomentum: true, binding: binding).triggered)
        XCTAssertTrue(recognizer.update(deltaX: 0, deltaY: 50, modifiers: mods, timestamp: 2.2, binding: binding).triggered)
    }

    func testOppositeDirectionAndDiagonalDoNotTrigger() {
        var recognizer = ScrollGestureRecognizer()
        let binding = TriggerBinding()
        XCTAssertFalse(recognizer.update(deltaX: 0, deltaY: -100, modifiers: binding.modifiers, timestamp: 1, binding: binding).triggered)
        recognizer.reset()
        XCTAssertFalse(recognizer.update(deltaX: 90, deltaY: 90, modifiers: binding.modifiers, timestamp: 2, binding: binding).triggered)
    }

    func testHorizontalDirectionAndBindingChangeReset() {
        var recognizer = ScrollGestureRecognizer()
        var binding = TriggerBinding(scrollDirection: .right)
        XCTAssertTrue(recognizer.update(deltaX: -80, deltaY: 0, modifiers: binding.modifiers, timestamp: 1, binding: binding).triggered)
        binding.scrollDirection = .down
        XCTAssertTrue(recognizer.update(deltaX: 0, deltaY: -80, modifiers: binding.modifiers, timestamp: 1.05, binding: binding).triggered)
    }

    func testExtraModifiersAndInertiaDoNotTrigger() {
        var recognizer = ScrollGestureRecognizer()
        let binding = TriggerBinding()
        XCTAssertFalse(recognizer.update(deltaX: 0, deltaY: 80, modifiers: binding.modifiers + [.shift], timestamp: 1, binding: binding).triggered)
        XCTAssertFalse(recognizer.update(deltaX: 0, deltaY: 80, modifiers: binding.modifiers, timestamp: 2, isMomentum: true, binding: binding).triggered)
    }

    func testCooldownPreventsReopeningAfterModifierBounce() {
        var recognizer = ScrollGestureRecognizer()
        let binding = TriggerBinding()
        XCTAssertTrue(recognizer.update(deltaX: 0, deltaY: 80, modifiers: binding.modifiers, timestamp: 1, binding: binding).triggered)
        _ = recognizer.update(deltaX: 0, deltaY: 1, modifiers: [], timestamp: 1.05, binding: binding)
        XCTAssertFalse(recognizer.update(deltaX: 0, deltaY: 80, modifiers: binding.modifiers, timestamp: 1.1, binding: binding).triggered)
    }

    func testInvalidSamplesAndResetDoNotLeakState() {
        var recognizer = ScrollGestureRecognizer()
        let binding = TriggerBinding()
        XCTAssertFalse(recognizer.update(deltaX: .nan, deltaY: 80, modifiers: binding.modifiers, timestamp: 1, binding: binding).triggered)
        _ = recognizer.update(deltaX: 0, deltaY: 20, modifiers: binding.modifiers, timestamp: 2, binding: binding)
        recognizer.reset()
        XCTAssertFalse(recognizer.update(deltaX: 0, deltaY: 30, modifiers: binding.modifiers, timestamp: 3, binding: binding).triggered)
    }

    func testHoldPressRepeatAndReleaseBalance() {
        var state = TriggerActivationState()
        XCTAssertEqual(state.press(mode: .hold), .open)
        XCTAssertEqual(state.press(mode: .hold, isRepeat: true), .none)
        XCTAssertEqual(state.press(mode: .hold), .none)
        XCTAssertEqual(state.release(mode: .hold), .close)
        XCTAssertEqual(state.release(mode: .hold), .none)
    }

    func testToggleOnlyTransitionsOnFreshPress() {
        var state = TriggerActivationState()
        XCTAssertEqual(state.press(mode: .toggle), .toggle)
        XCTAssertEqual(state.release(mode: .toggle), .none)
        XCTAssertEqual(state.press(mode: .toggle), .toggle)
        state.reset()
        XCTAssertFalse(state.isPressed)
    }
}
