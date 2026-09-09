import Foundation
import XCTest
@testable import ActionsRingKit

final class SlotReorderingTests: XCTestCase {
    private let owner = SlotReorderOwner(sessionID: UUID(), containerID: "root-ring")

    private func slots() -> [RingSlot] {
        let nested = RingSlot(id: "nested", label: "Вложенное", submenu: [
            RingSlot(id: "leaf", label: "Потомок", action: RingAction(id: "leaf-action", name: "Сайт", kind: .url, value: "https://example.com"))
        ])
        return [
            RingSlot(id: "hybrid", label: "Гибрид", icon: .symbol("folder"),
                     action: RingAction(id: "copy-action", name: "Копировать", kind: .system, value: "copy"),
                     submenu: [nested], colors: SlotColors(bubble: "#8040FF", hover: "#050607", icon: "#FFFFFF", hoverIcon: "#CCCCCC")),
            RingSlot(id: "empty"),
            RingSlot(id: "action", label: "Вставить", icon: SlotIcon(kind: .file, value: "/tmp/icon.png"),
                     action: RingAction(id: "paste-action", name: "Вставить", kind: .system, value: "paste"))
        ]
    }

    func testRootSwapPreservesWholeSlotsIDsAndSelectedIdentity() {
        let original = slots()
        var result = original
        let selectedID = original[0].id
        let payload = SlotReorderPayload(owner: owner, sourceSlotID: selectedID, slots: original)
        XCTAssertTrue(SlotReordering.swap(&result, using: payload, owner: owner, targetSlotID: original[2].id))
        XCTAssertEqual(result, [original[2], original[1], original[0]])
        XCTAssertEqual(result.first(where: { $0.id == selectedID }), original[0])
        XCTAssertEqual(result[2].submenu?.first?.submenu?.first?.id, "leaf")
        XCTAssertEqual(result[2].action?.id, "copy-action")
    }

    func testSubmenuSwapPreservesNestedSubmenusAndParent() {
        var parent = RingSlot(id: "parent", label: "Родитель", submenu: slots())
        let original = parent
        let submenuOwner = SlotReorderOwner(sessionID: UUID(), containerID: parent.id)
        var children = parent.submenu!
        let payload = SlotReorderPayload(owner: submenuOwner, sourceSlotID: children[0].id, slots: children)
        XCTAssertTrue(SlotReordering.swap(&children, using: payload, owner: submenuOwner, targetSlotID: children[1].id))
        parent.submenu = children
        XCTAssertEqual(parent.id, original.id)
        XCTAssertEqual(parent.label, original.label)
        XCTAssertEqual(parent.submenu, [original.submenu![1], original.submenu![0], original.submenu![2]])
    }

    func testEmptySlotCanBeSwappedWithoutAssigningOrCloningAnAction() {
        let original = slots()
        var result = original
        let payload = SlotReorderPayload(owner: owner, sourceSlotID: "empty", slots: original)
        XCTAssertTrue(SlotReordering.swap(&result, using: payload, owner: owner, targetSlotID: "hybrid"))
        XCTAssertEqual(result[0], original[1])
        XCTAssertEqual(result[1], original[0])
        XCTAssertNil(result[0].action)
    }

    func testStartingThenCancellingADragDoesNotMutateSlots() throws {
        let original = slots()
        let payload = SlotReorderPayload(owner: owner, sourceSlotID: "hybrid", slots: original)
        _ = try JSONDecoder().decode(SlotReorderPayload.self, from: JSONEncoder().encode(payload))
        XCTAssertEqual(original, slots())
    }

    func testInvalidContainerAndSlotCountsAreNoOps() {
        let original = slots()
        let emptyOwner = SlotReorderOwner(sessionID: owner.sessionID, containerID: "")
        var result = original
        let payload = SlotReorderPayload(owner: emptyOwner, sourceSlotID: "hybrid", slots: original)
        XCTAssertFalse(SlotReordering.swap(&result, using: payload, owner: emptyOwner, targetSlotID: "empty"))
        XCTAssertEqual(result, original)
        for invalid in [[], [original[0]], (0..<10).map { RingSlot(id: "slot-\($0)") }] {
            var result = invalid
            let payload = SlotReorderPayload(owner: owner, sourceSlotID: invalid.first?.id ?? "", slots: invalid)
            XCTAssertFalse(SlotReordering.swap(&result, using: payload, owner: owner, targetSlotID: invalid.last?.id ?? ""))
            XCTAssertEqual(result, invalid)
        }
    }

    func testSelfMissingOutsideAndDescendantTargetsAreNoOps() {
        let original = slots()
        let payload = SlotReorderPayload(owner: owner, sourceSlotID: "hybrid", slots: original)
        for target in ["hybrid", "", "outside", "nested", "leaf"] {
            var result = original
            XCTAssertFalse(SlotReordering.swap(&result, using: payload, owner: owner, targetSlotID: target))
            XCTAssertEqual(result, original)
        }
        var result = original
        let childPayload = SlotReorderPayload(owner: owner, sourceSlotID: "leaf", slots: original)
        XCTAssertFalse(SlotReordering.swap(&result, using: childPayload, owner: owner, targetSlotID: "hybrid"))
        XCTAssertEqual(result, original)
    }

    func testDifferentOwnerSessionProfileAndDepthAreNoOps() {
        let original = slots()
        let payload = SlotReorderPayload(owner: owner, sourceSlotID: "hybrid", slots: original)
        for other in [
            SlotReorderOwner(sessionID: UUID(), containerID: owner.containerID),
            SlotReorderOwner(sessionID: owner.sessionID, containerID: "another-profile"),
            SlotReorderOwner(sessionID: owner.sessionID, containerID: "hybrid")
        ] {
            var result = original
            XCTAssertFalse(SlotReordering.swap(&result, using: payload, owner: other, targetSlotID: "empty"))
            XCTAssertEqual(result, original)
        }
    }

    func testStaleOrderRemovedSourceAndReplayedDropAreNoOps() {
        let original = slots()
        let payload = SlotReorderPayload(owner: owner, sourceSlotID: "hybrid", slots: original)
        for current in [[original[1], original[0], original[2]], Array(original.dropFirst())] {
            var result = current
            XCTAssertFalse(SlotReordering.swap(&result, using: payload, owner: owner, targetSlotID: "action"))
            XCTAssertEqual(result, current)
        }
        var result = original
        XCTAssertTrue(SlotReordering.swap(&result, using: payload, owner: owner, targetSlotID: "empty"))
        let afterFirstDrop = result
        XCTAssertFalse(SlotReordering.swap(&result, using: payload, owner: owner, targetSlotID: "empty"))
        XCTAssertEqual(result, afterFirstDrop)
    }

    func testUsesCurrentSlotValuesInsteadOfOverwritingEditsWithDragData() {
        var result = slots()
        let payload = SlotReorderPayload(owner: owner, sourceSlotID: "hybrid", slots: result)
        result[0].label = "Изменено во время переноса"
        let edited = result[0]
        XCTAssertTrue(SlotReordering.swap(&result, using: payload, owner: owner, targetSlotID: "empty"))
        XCTAssertEqual(result[1], edited)
    }

    func testMalformedUnsupportedAndAmbiguousPayloadsAreRejected() throws {
        let original = slots()
        let payload = SlotReorderPayload(owner: owner, sourceSlotID: "hybrid", slots: original)
        let data = try JSONEncoder().encode(payload)
        XCTAssertEqual(try JSONDecoder().decode(SlotReorderPayload.self, from: data), payload)
        XCTAssertThrowsError(try JSONDecoder().decode(SlotReorderPayload.self, from: Data("{broken}".utf8)))
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        object["version"] = 2
        let unsupported = try JSONDecoder().decode(SlotReorderPayload.self, from: JSONSerialization.data(withJSONObject: object))
        var result = original
        XCTAssertFalse(SlotReordering.swap(&result, using: unsupported, owner: owner, targetSlotID: "empty"))
        XCTAssertEqual(result, original)
        result[1].id = result[0].id
        let ambiguous = result
        let duplicatePayload = SlotReorderPayload(owner: owner, sourceSlotID: "hybrid", slots: result)
        XCTAssertFalse(SlotReordering.swap(&result, using: duplicatePayload, owner: owner, targetSlotID: "action"))
        XCTAssertEqual(result, ambiguous)
    }

    func testSwapPersistsAndReloadsWithoutChangingOtherProfiles() async throws {
        try await MainActor.run {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ActionsRingReorderTests-\(UUID().uuidString)", isDirectory: true)
            defer { try? FileManager.default.removeItem(at: directory) }
            let store = try ConfigurationStore(directory: directory)
            let unchangedSlots = ActionCatalog.defaultSlots()
            store.configuration.userProfiles[0].profiles.append(RingProfile(name: "Safari", bundleIdentifier: "com.apple.Safari", slots: unchangedSlots))
            try store.save()
            let original = store.configuration
            let ring = original.userProfiles[0].profiles[0]
            let owner = SlotReorderOwner(sessionID: UUID(), containerID: ring.id)
            let payload = SlotReorderPayload(owner: owner, sourceSlotID: ring.slots[0].id, slots: ring.slots)
            XCTAssertTrue(SlotReordering.swap(&store.configuration.userProfiles[0].profiles[0].slots,
                                              using: payload, owner: owner, targetSlotID: ring.slots[7].id))
            try store.save()
            let reloaded = try ConfigurationStore(directory: directory).configuration
            XCTAssertEqual(reloaded, store.configuration)
            XCTAssertEqual(reloaded.userProfiles[0].profiles[0].slots[7], ring.slots[0])
            XCTAssertEqual(reloaded.userProfiles[0].profiles[1], original.userProfiles[0].profiles[1])
            XCTAssertEqual(try ConfigurationCodec.decode(Data(contentsOf: store.backupURL)), original)
        }
    }
}
