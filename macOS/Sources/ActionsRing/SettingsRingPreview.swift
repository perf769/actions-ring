import SwiftUI
import ActionsRingKit

@MainActor
struct SettingsRingPreview: View {
    @State private var dropTargetID: String?
    var profile: RingProfile
    var selectedSlotID: String?
    var dragOwner: SlotReorderOwner
    var select: (RingSlot?) -> Void
    var reorder: (SlotReorderPayload, String) -> Bool

    var body: some View {
        GeometryReader { geometry in
            content(in: geometry.size)
        }
        .frame(minHeight: 230)
        .onChange(of: dragOwner) { _, _ in dropTargetID = nil }
        .onChange(of: profile.slots.map(\.id)) { _, _ in dropTargetID = nil }
        .onDisappear { dropTargetID = nil }
    }

    private func content(in size: CGSize) -> some View {
        let layout = SettingsPreviewLayout(size: size)
        return ZStack {
            ForEach(profile.slots) { slot in
                bubble(slot, layout: layout)
            }
            closeButton.position(layout.center)
        }
        .frame(width: size.width, height: size.height)
    }

    private func bubble(_ slot: RingSlot, layout: SettingsPreviewLayout) -> some View {
        let index: Int = profile.slots.firstIndex(where: { $0.id == slot.id }) ?? 0
        let palette: RingPalette = slot.colors?.resolving(profile.effectivePalette) ?? profile.effectivePalette
        let position: CGPoint = layout.position(index: index, count: profile.slots.count)
        return SettingsPreviewBubble(slot: slot, palette: palette,
                                     isSelected: selectedSlotID == slot.id, isDropTarget: dropTargetID == slot.id,
                                     diameter: layout.bubbleDiameter) {
            select(slot)
        }
        .draggable(SlotDragTransfer(payload: SlotReorderPayload(owner: dragOwner, sourceSlotID: slot.id, slots: profile.slots)))
        .dropDestination(for: SlotDragTransfer.self) { items, _ in
            defer { dropTargetID = nil }
            guard items.count == 1, let transfer = items.first else { return false }
            var slots = profile.slots
            guard SlotReordering.swap(&slots, using: transfer.payload, owner: dragOwner, targetSlotID: slot.id) else { return false }
            return reorder(transfer.payload, slot.id)
        } isTargeted: { targeted in
            if targeted { dropTargetID = slot.id }
            else if dropTargetID == slot.id { dropTargetID = nil }
        }
        .position(position)
    }

    private var closeButton: some View {
        Button { select(nil) } label: {
            Image(systemName: "xmark")
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(Color.secondary)
                .frame(width: 32, height: 32)
                .background(RingSettingsStyle.card, in: Circle())
                .shadow(color: Color.black.opacity(0.08), radius: 4, y: 1)
        }
        .buttonStyle(.plain)
        .help("Снять выделение")
        .accessibilityLabel("Снять выделение")
    }
}

private struct SettingsPreviewLayout {
    let center: CGPoint
    let radius: CGFloat
    let bubbleDiameter: CGFloat

    init(size: CGSize) {
        let usableWidth: CGFloat = size.width - 30
        let usableHeight: CGFloat = size.height - 20
        let diameter: CGFloat = min(min(usableWidth, usableHeight), CGFloat(320))
        center = CGPoint(x: size.width / 2, y: size.height / 2)
        radius = max(CGFloat(65), diameter * CGFloat(0.34))
        bubbleDiameter = min(CGFloat(56), max(CGFloat(40), diameter * CGFloat(0.17)))
    }

    func position(index: Int, count: Int) -> CGPoint {
        let fraction: Double = Double(index) / Double(max(1, count))
        let angle: Double = fraction * Double.pi * 2.0 - Double.pi / 2.0
        let x: CGFloat = center.x + CGFloat(cos(angle)) * radius
        let y: CGFloat = center.y + CGFloat(sin(angle)) * radius
        return CGPoint(x: x, y: y)
    }
}

@MainActor
private struct SettingsPreviewBubble: View {
    var slot: RingSlot
    var palette: RingPalette
    var isSelected: Bool
    var isDropTarget: Bool
    var diameter: CGFloat
    var onSelect: () -> Void

    private var backgroundColor: Color { RingSettingsStyle.color(isSelected ? palette.hover : palette.bubble) }
    private var foregroundColor: Color { RingSettingsStyle.color(isSelected ? palette.hoverIcon : palette.icon) }
    private var outlineColor: Color { isSelected ? RingSettingsStyle.accent : Color.clear }

    var body: some View {
        Button(action: onSelect) {
            face.overlay(alignment: .topTrailing) {
                if slot.hasSubmenu { disclosure }
            }
        }
        .buttonStyle(.plain)
        .help("\(slot.label) — перетащите на другой пузырь, чтобы поменять их местами")
        .accessibilityLabel(slot.label)
        .accessibilityValue(isSelected ? "Выбран" : "Не выбран")
    }

    private var face: some View {
        SlotIconImage(icon: slot.effectiveIcon, size: diameter * CGFloat(0.48), foreground: foregroundColor)
            .frame(width: diameter, height: diameter)
            .background(backgroundColor, in: Circle())
            .overlay(Circle().stroke(outlineColor, lineWidth: 3))
            .overlay(Circle().inset(by: -5).stroke(isDropTarget ? RingSettingsStyle.accent : Color.clear, style: StrokeStyle(lineWidth: 2, dash: [4, 3])))
            .shadow(color: Color.black.opacity(0.12), radius: 5, y: 2)
    }

    private var disclosure: some View {
        Image(systemName: "chevron.right")
            .font(.system(size: 8, weight: .bold))
            .foregroundStyle(foregroundColor)
            .padding(4)
            .background(backgroundColor, in: Circle())
    }
}
