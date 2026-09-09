import Foundation

/// Identifies one visible root ring or one submenu editor. A fresh session ID
/// prevents transfers between windows, profiles, and nested editors.
public struct SlotReorderOwner: Codable, Equatable, Sendable {
    public let sessionID: UUID
    public let containerID: String

    public init(sessionID: UUID, containerID: String) {
        self.sessionID = sessionID
        self.containerID = containerID
    }
}

/// An internal reference, never a replacement slot supplied by a drag provider.
public struct SlotReorderPayload: Codable, Equatable, Sendable {
    public let version: Int
    public let owner: SlotReorderOwner
    public let sourceSlotID: String
    public let slotIDs: [String]

    public init(owner: SlotReorderOwner, sourceSlotID: String, slots: [RingSlot]) {
        version = 1
        self.owner = owner
        self.sourceSlotID = sourceSlotID
        slotIDs = slots.map(\.id)
    }
}

public enum SlotReordering {
    /// Swaps direct siblings only, preserving each complete value and identity.
    /// Invalid, stale, self, and cross-container drops leave the array untouched.
    @discardableResult
    public static func swap(_ slots: inout [RingSlot], using payload: SlotReorderPayload,
                            owner: SlotReorderOwner, targetSlotID: String) -> Bool {
        let ids = slots.map(\.id)
        guard payload.version == 1, payload.owner == owner,
              !owner.containerID.isEmpty, (2...9).contains(slots.count),
              ids.allSatisfy({ !$0.isEmpty }), Set(ids).count == ids.count,
              payload.slotIDs == ids,
              payload.sourceSlotID != targetSlotID,
              let source = ids.firstIndex(of: payload.sourceSlotID),
              let target = ids.firstIndex(of: targetSlotID) else { return false }
        slots.swapAt(source, target)
        return true
    }
}
