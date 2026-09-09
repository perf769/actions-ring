import CoreGraphics
import Foundation

public struct RingBubbleLayout: Equatable, Sendable {
    public var index: Int
    public var center: CGPoint
    public var radius: Double
    public init(index: Int, center: CGPoint, radius: Double) {
        self.index = index
        self.center = center
        self.radius = radius
    }
    public var frame: CGRect {
        CGRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2)
    }
}

public struct RootRingLayout: Equatable, Sendable {
    public var center: CGPoint
    public var scale: Double
    public var bubbles: [RingBubbleLayout]
    public var closeRadius: Double { 18 * scale }
}

public enum RingGeometry {
    public static func root(slotCount: Int, cursor: CGPoint, bounds: CGRect, scale requestedScale: Double) -> RootRingLayout {
        let safeScale = requestedScale.isFinite ? min(1.8, max(0.65, requestedScale)) : 1
        let fit = min(1, max(0.25, min(bounds.width, bounds.height) / 530))
        let scale = safeScale * fit
        let radius = 28 * scale
        let distance = 87 * scale
        let center = clamped(cursor, to: bounds.insetBy(dx: distance + radius + 16, dy: distance + radius + 16))
        let count = max(1, min(8, slotCount))
        let bubbles = (0..<count).map { index -> RingBubbleLayout in
            let angle = Double(index) * .pi * 2 / Double(count) - .pi / 2
            return .init(index: index, center: CGPoint(x: center.x + cos(angle) * distance,
                                                       y: center.y + sin(angle) * distance), radius: radius)
        }
        return RootRingLayout(center: center, scale: scale, bubbles: bubbles)
    }

    /// Chooses an outward fan first, then rotates it to fit around screen edges and existing bubbles.
    public static func submenu(count requestedCount: Int, parent: CGPoint, origin: CGPoint,
                               bounds: CGRect, scale: Double, obstacles: [RingBubbleLayout]) -> [RingBubbleLayout] {
        let count = max(1, min(9, requestedCount))
        let radius = 25 * scale
        let spacing = radius * 2 + 12 * scale
        let arc = min(2.8, max(0.6, Double(count - 1) * 0.44))
        let distance = count == 1 ? 76 * scale : max(78 * scale, spacing / (2 * sin(arc / Double(count - 1) / 2)))
        let outward = atan2(parent.y - origin.y, parent.x - origin.x)
        let safe = bounds.insetBy(dx: radius + 12, dy: radius + 12)
        var best: [RingBubbleLayout] = []
        var bestScore = Double.infinity
        for offset in [0.0, .pi / 6, -.pi / 6, .pi / 3, -.pi / 3, .pi / 2, -.pi / 2,
                       .pi * 2 / 3, -.pi * 2 / 3, .pi * 5 / 6, -.pi * 5 / 6, .pi] {
            let nodes = (0..<count).map { index -> RingBubbleLayout in
                let fraction = count == 1 ? 0.5 : Double(index) / Double(count - 1)
                let angle = outward + offset + (fraction - 0.5) * arc
                return .init(index: index, center: CGPoint(x: parent.x + cos(angle) * distance,
                                                           y: parent.y + sin(angle) * distance), radius: radius)
            }
            let score = layoutScore(nodes, bounds: safe, obstacles: obstacles) + abs(offset) * 0.5
            if score < bestScore { best = nodes; bestScore = score }
            if score < 0.01 { break }
        }
        if bestScore > 0.1 {
            // The compact grid is a deterministic escape hatch for a deeply nested ring near a corner.
            // Unlike individually clamping fan positions, it never stacks several children on one point.
            let columns = min(3, count)
            let rows = Int(ceil(Double(count) / Double(columns)))
            for yIndex in 0..<4 {
                for xIndex in 0..<5 {
                    let width = Double(columns - 1) * spacing
                    let height = Double(rows - 1) * spacing
                    let topLeft = CGPoint(x: safe.minX + max(0, safe.width - width) * Double(xIndex) / 4,
                                          y: safe.minY + max(0, safe.height - height) * Double(yIndex) / 3)
                    let nodes = (0..<count).map { index in
                        RingBubbleLayout(index: index,
                            center: CGPoint(x: topLeft.x + Double(index % columns) * spacing,
                                            y: topLeft.y + Double(index / columns) * spacing), radius: radius)
                    }
                    let proximity = hypot(topLeft.x - parent.x, topLeft.y - parent.y) * 0.003
                    let score = layoutScore(nodes, bounds: safe, obstacles: obstacles) + 4 + proximity
                    if score < bestScore { best = nodes; bestScore = score }
                }
            }
        }
        return best
    }

    public static func tooltip(size: CGSize, anchor: CGRect, bounds: CGRect) -> CGRect {
        let width = min(max(1, size.width), max(1, bounds.width - 20))
        let height = min(max(1, size.height), max(1, bounds.height - 20))
        var origin = CGPoint(x: anchor.midX - width / 2, y: anchor.minY - height - 12)
        if origin.y < bounds.minY + 10 { origin.y = anchor.maxY + 12 }
        origin.x = max(bounds.minX + 10, min(bounds.maxX - 10 - width, origin.x))
        origin.y = max(bounds.minY + 10, min(bounds.maxY - 10 - height, origin.y))
        return CGRect(origin: origin, size: CGSize(width: width, height: height))
    }

    private static func clamped(_ point: CGPoint, to rect: CGRect) -> CGPoint {
        let x = rect.width < 0 ? rect.midX : max(rect.minX, min(rect.maxX, point.x))
        let y = rect.height < 0 ? rect.midY : max(rect.minY, min(rect.maxY, point.y))
        return CGPoint(x: x, y: y)
    }

    private static func layoutScore(_ nodes: [RingBubbleLayout], bounds: CGRect, obstacles: [RingBubbleLayout]) -> Double {
        var score = 0.0
        for node in nodes {
            let dx = max(0, bounds.minX - node.center.x) + max(0, node.center.x - bounds.maxX)
            let dy = max(0, bounds.minY - node.center.y) + max(0, node.center.y - bounds.maxY)
            score += (dx * dx + dy * dy) * 100
            for obstacle in obstacles {
                let distance = hypot(node.center.x - obstacle.center.x, node.center.y - obstacle.center.y)
                let intrusion = max(0, node.radius + obstacle.radius + 8 - distance)
                score += intrusion * intrusion * 20
            }
        }
        return score
    }
}
