import AppKit
import SwiftUI
import ActionsRingKit

@MainActor
extension MacIconLoader {
    static func image(for icon: SlotIcon?) -> NSImage? {
        guard let icon else { return NSImage(systemSymbolName: "plus", accessibilityDescription: nil) }
        return image(for: icon)
    }

    static func needsDarkPlate(_ image: NSImage) -> Bool {
        guard let data = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: data) else { return false }
        var luminance = 0.0, count = 0
        for y in stride(from: 0, to: bitmap.pixelsHigh, by: max(1, bitmap.pixelsHigh / 12)) {
            for x in stride(from: 0, to: bitmap.pixelsWide, by: max(1, bitmap.pixelsWide / 12)) {
                guard let color = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB), color.alphaComponent > 0.3 else { continue }
                luminance += color.redComponent * 0.21 + color.greenComponent * 0.72 + color.blueComponent * 0.07
                count += 1
            }
        }
        return count > 0 && luminance / Double(count) > 0.85
    }
}

@MainActor
struct SlotIconImage: View {
    var icon: SlotIcon?
    var size: CGFloat = 24
    var foreground: Color = .primary
    var body: some View {
        if let image = MacIconLoader.image(for: icon) {
            if icon?.kind == .symbol || icon == nil {
                Image(nsImage: image).resizable().renderingMode(.template).scaledToFit()
                    .foregroundStyle(foreground).frame(width: size, height: size)
            } else {
                Image(nsImage: image).resizable().scaledToFit().padding(2)
                    .frame(width: size, height: size)
                    .background(MacIconLoader.needsDarkPlate(image) ? Color(white: 0.18) : Color.clear, in: RoundedRectangle(cornerRadius: size * 0.22))
            }
        } else {
            Image(systemName: "square.dashed").resizable().scaledToFit().foregroundStyle(foreground).frame(width: size, height: size)
        }
    }
}
