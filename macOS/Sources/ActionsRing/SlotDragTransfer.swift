import CoreTransferable
import UniformTypeIdentifiers
import ActionsRingKit

/// A private data format, deliberately without a text/file proxy representation.
struct SlotDragTransfer: Codable, Transferable {
    let payload: SlotReorderPayload

    static var transferRepresentation: some TransferRepresentation {
        CodableRepresentation(contentType: .actionsRingSlotPosition)
            .visibility(.ownProcess)
    }
}

private extension UTType {
    static let actionsRingSlotPosition = UTType(exportedAs: "com.actionsring.slot-position", conformingTo: .data)
}
