import SwiftUI
import ActionsRingKit

@MainActor
struct SettingsRingPreview: View {
    var profile: RingProfile
    var selectedSlotID: String?
    var select: (RingSlot?) -> Void

    var body: some View {
        GeometryReader { geometry in
            let diameter = min(min(geometry.size.width - 30, geometry.size.height - 20), 320.0)
            let radius = max(65, diameter * 0.34)
            let bubbleSize = min(56.0, max(40, diameter * 0.17))
            let center = CGPoint(x: geometry.size.width / 2, y: geometry.size.height / 2)
            ZStack {
                ForEach(Array(profile.slots.enumerated()), id: \.element.id) { index, slot in
                    let angle = (Double(index) / Double(max(1, profile.slots.count))) * .pi * 2 - .pi / 2
                    let palette = slot.colors?.resolving(profile.effectivePalette) ?? profile.effectivePalette
                    Button { select(slot) } label: {
                        SlotIconImage(icon: slot.effectiveIcon, size: bubbleSize * 0.48,
                                      foreground: RingSettingsStyle.color(selectedSlotID == slot.id ? palette.hoverIcon : palette.icon))
                            .frame(width: bubbleSize, height: bubbleSize)
                            .background(RingSettingsStyle.color(selectedSlotID == slot.id ? palette.hover : palette.bubble), in: Circle())
                            .overlay(Circle().stroke(selectedSlotID == slot.id ? RingSettingsStyle.accent : .clear, lineWidth: 3))
                            .shadow(color: .black.opacity(0.12), radius: 5, y: 2)
                            .overlay(alignment: .topTrailing) {
                                if slot.hasSubmenu {
                                    Image(systemName: "chevron.right").font(.system(size: 8, weight: .bold))
                                        .foregroundStyle(RingSettingsStyle.color(palette.icon))
                                        .padding(4).background(RingSettingsStyle.color(palette.bubble), in: Circle())
                                }
                            }
                    }
                    .buttonStyle(.plain).help(slot.label)
                    .accessibilityLabel(slot.label)
                    .position(x: center.x + CGFloat(cos(angle)) * radius, y: center.y + CGFloat(sin(angle)) * radius)
                }
                Button { select(nil) } label: {
                    Image(systemName: "xmark").font(.system(size: 12, weight: .medium))
                        .foregroundStyle(.secondary).frame(width: 32, height: 32)
                        .background(RingSettingsStyle.card, in: Circle()).shadow(color: .black.opacity(0.08), radius: 4, y: 1)
                }
                .buttonStyle(.plain).help("Снять выделение").position(center)
            }
        }
        .frame(minHeight: 230)
    }
}
