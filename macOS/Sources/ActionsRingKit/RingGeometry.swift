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
        let r = CGFloat(radius)
        return CGRect(x: center.x - r, y: center.y - r, width: r * 2, height: r * 2)
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
        let safeScale: Double = requestedScale.isFinite ? min(1.8, max(0.65, requestedScale)) : 1
        let fit: Double = min(1, max(0.25, min(Double(bounds.width), Double(bounds.height)) / 530))
        let scale: Double = safeScale * fit
        let radius: Double = 28 * scale
        let distance: Double = 87 * scale
        let margin = CGFloat(distance + radius + 16)
        let center = clamped(cursor, to: bounds.insetBy(dx: margin, dy: margin))
        let count = max(1, min(8, slotCount))
        let centerX = Double(center.x)
        let centerY = Double(center.y)
        var bubbles: [RingBubbleLayout] = []
        for index in 0..<count {
            let fraction: Double = Double(index) / Double(count)
            let angle: Double = fraction * Double.pi * 2 - Double.pi / 2
            let x: Double = centerX + cos(angle) * distance
            let y: Double = centerY + sin(angle) * distance
            bubbles.append(RingBubbleLayout(index: index, center: point(x, y), radius: radius))
        }
        return RootRingLayout(center: center, scale: scale, bubbles: bubbles)
    }

    /// Chooses an outward fan first, then rotates it to fit around screen edges and existing bubbles.
    public static func submenu(count requestedCount: Int, parent: CGPoint, origin: CGPoint,
                               bounds: CGRect, scale: Double, obstacles: [RingBubbleLayout]) -> [RingBubbleLayout] {
        let count = max(1, min(9, requestedCount))
        let radius: Double = 25 * scale
        let spacing: Double = radius * 2 + 12 * scale
        let arc: Double = min(2.8, max(0.6, Double(count - 1) * 0.44))
        let distance: Double
        if count == 1 { distance = 76 * scale }
        else {
            let halfStep: Double = arc / Double(count - 1) / 2
            distance = max(78 * scale, spacing / (2 * sin(halfStep)))
        }
        let parentX = Double(parent.x)
        let parentY = Double(parent.y)
        let outward: Double = atan2(parentY - Double(origin.y), parentX - Double(origin.x))
        let margin = CGFloat(radius + 12)
        let safe = bounds.insetBy(dx: margin, dy: margin)
        var best: [RingBubbleLayout] = []
        var bestScore: Double = .infinity
        var bestPenalty: Double = .infinity
        let pi = Double.pi
        let offsets: [Double] = [0, pi / 6, -pi / 6, pi / 3, -pi / 3, pi / 2, -pi / 2,
                                 pi * 2 / 3, -pi * 2 / 3, pi * 5 / 6, -pi * 5 / 6, pi]
        for offset in offsets {
            var nodes: [RingBubbleLayout] = []
            for index in 0..<count {
                let fraction: Double = count == 1 ? 0.5 : Double(index) / Double(count - 1)
                let fanOffset: Double = (fraction - 0.5) * arc
                let angle: Double = outward + offset + fanOffset
                let x: Double = parentX + cos(angle) * distance
                let y: Double = parentY + sin(angle) * distance
                nodes.append(RingBubbleLayout(index: index, center: point(x, y), radius: radius))
            }
            let penalty = layoutScore(nodes, bounds: safe, obstacles: obstacles)
            let score: Double = penalty + abs(offset) * 0.5
            if isBetterLayout(penalty: penalty, score: score, bestPenalty: bestPenalty, bestScore: bestScore) {
                best = nodes
                bestScore = score
                bestPenalty = penalty
            }
            if score < 0.01 { break }
        }
        if bestPenalty > 0.0001 {
            // Search positions beside the parent and tangent to nearby obstacles, not a coarse
            // screen-wide grid: at a corner, the latter can strand children far from their parent.
            // Only candidate generation is bounded; collision checks still include every ancestor.
            let nearby = Array(obstacles.sorted { lhs, rhs in
                let leftDistance = hypot(Double(lhs.center.x) - parentX, Double(lhs.center.y) - parentY)
                let rightDistance = hypot(Double(rhs.center.x) - parentX, Double(rhs.center.y) - parentY)
                return leftDistance < rightDistance
            }.prefix(12))
            for columns in 1...min(3, count) {
                let rows = Int(ceil(Double(count) / Double(columns)))
                let width: Double = Double(columns - 1) * spacing
                let height: Double = Double(rows - 1) * spacing
                guard width <= Double(safe.width), height <= Double(safe.height) else { continue }
                let horizontal = gridAxisCandidates(parent: parentX, span: width, spacing: spacing, cells: columns,
                                                    lower: Double(safe.minX), upper: Double(safe.maxX),
                                                    radius: radius, obstacles: nearby, horizontal: true)
                let vertical = gridAxisCandidates(parent: parentY, span: height, spacing: spacing, cells: rows,
                                                  lower: Double(safe.minY), upper: Double(safe.maxY),
                                                  radius: radius, obstacles: nearby, horizontal: false)
                for top in vertical {
                    for left in horizontal {
                        var nodes: [RingBubbleLayout] = []
                        var nearest: Double = .infinity
                        var totalDistance: Double = 0
                        for index in 0..<count {
                            let x: Double = left + Double(index % columns) * spacing
                            let y: Double = top + Double(index / columns) * spacing
                            nodes.append(RingBubbleLayout(index: index, center: point(x, y), radius: radius))
                            let distance = hypot(x - parentX, y - parentY)
                            nearest = min(nearest, distance)
                            totalDistance += distance
                        }
                        let penalty = layoutScore(nodes, bounds: safe, obstacles: obstacles)
                        let proximity: Double = nearest * 0.01 + totalDistance / Double(count) * 0.02
                        let score: Double = penalty + 4 + proximity
                        if isBetterLayout(penalty: penalty, score: score, bestPenalty: bestPenalty, bestScore: bestScore) {
                            // The first droplet emerges into the nearest free position even when the
                            // compact fallback has to be placed above or to the left of its parent.
                            nodes.sort { lhs, rhs in
                                let leftDistance = hypot(Double(lhs.center.x) - parentX, Double(lhs.center.y) - parentY)
                                let rightDistance = hypot(Double(rhs.center.x) - parentX, Double(rhs.center.y) - parentY)
                                return leftDistance == rightDistance ? lhs.index < rhs.index : leftDistance < rightDistance
                            }
                            for index in nodes.indices { nodes[index].index = index }
                            best = nodes
                            bestScore = score
                            bestPenalty = penalty
                        }
                    }
                }
            }
        }
        return best
    }

    public static func tooltip(size: CGSize, anchor: CGRect, bounds: CGRect) -> CGRect {
        let width: CGFloat = min(max(1, size.width), max(1, bounds.width - 20))
        let height: CGFloat = min(max(1, size.height), max(1, bounds.height - 20))
        var origin = CGPoint(x: anchor.midX - width / 2, y: anchor.minY - height - 12)
        if origin.y < bounds.minY + 10 { origin.y = anchor.maxY + 12 }
        origin.x = max(bounds.minX + 10, min(bounds.maxX - 10 - width, origin.x))
        origin.y = max(bounds.minY + 10, min(bounds.maxY - 10 - height, origin.y))
        return CGRect(origin: origin, size: CGSize(width: width, height: height))
    }

    private static func clamped(_ point: CGPoint, to rect: CGRect) -> CGPoint {
        let x: CGFloat = rect.width < 0 ? rect.midX : max(rect.minX, min(rect.maxX, point.x))
        let y: CGFloat = rect.height < 0 ? rect.midY : max(rect.minY, min(rect.maxY, point.y))
        return CGPoint(x: x, y: y)
    }

    private static func point(_ x: Double, _ y: Double) -> CGPoint {
        CGPoint(x: CGFloat(x), y: CGFloat(y))
    }

    private static func isBetterLayout(penalty: Double, score: Double, bestPenalty: Double, bestScore: Double) -> Bool {
        let valid = penalty <= 0.0001
        let bestValid = bestPenalty <= 0.0001
        if valid != bestValid { return valid }
        return score < bestScore
    }

    private static func gridAxisCandidates(parent: Double, span: Double, spacing: Double, cells: Int,
                                           lower: Double, upper: Double, radius: Double,
                                           obstacles: [RingBubbleLayout], horizontal: Bool) -> [Double] {
        let maximum = upper - span
        var candidates: Set<Double> = [lower, maximum]
        for cell in 0..<cells {
            let shift = Double(cell) * spacing
            candidates.insert(max(lower, min(maximum, parent - shift)))
            for obstacle in obstacles {
                let coordinate = Double(horizontal ? obstacle.center.x : obstacle.center.y)
                let clearance = radius + obstacle.radius + 8
                candidates.insert(max(lower, min(maximum, coordinate - clearance - shift)))
                candidates.insert(max(lower, min(maximum, coordinate + clearance - shift)))
            }
        }
        return candidates.sorted()
    }

    private static func layoutScore(_ nodes: [RingBubbleLayout], bounds: CGRect, obstacles: [RingBubbleLayout]) -> Double {
        var score: Double = 0
        let left = Double(bounds.minX)
        let right = Double(bounds.maxX)
        let top = Double(bounds.minY)
        let bottom = Double(bounds.maxY)
        for node in nodes {
            let x = Double(node.center.x)
            let y = Double(node.center.y)
            let dx: Double = max(0, left - x) + max(0, x - right)
            let dy: Double = max(0, top - y) + max(0, y - bottom)
            score += (dx * dx + dy * dy) * 100
            for obstacle in obstacles {
                let distance: Double = hypot(x - Double(obstacle.center.x), y - Double(obstacle.center.y))
                let intrusion: Double = max(0, node.radius + obstacle.radius + 8 - distance)
                score += intrusion * intrusion * 20
            }
        }
        return score
    }
}
