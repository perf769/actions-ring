import CoreGraphics
import Foundation
import XCTest
@testable import ActionsRingKit

final class RingGeometryTests: XCTestCase {
    func testRootKeepsClockwiseOrderingAndScreenBounds() {
        let bounds = CGRect(x: 0, y: 0, width: 1280, height: 800)
        for cursor in [CGPoint.zero, CGPoint(x: 1280, y: 0), CGPoint(x: 1280, y: 800), CGPoint(x: 640, y: 400)] {
            for scale in [0.65, 1.0, 1.8] {
                let layout = RingGeometry.root(slotCount: 8, cursor: cursor, bounds: bounds, scale: scale)
                XCTAssertEqual(layout.bubbles.count, 8)
                XCTAssertLessThan(layout.bubbles[0].center.y, layout.center.y)
                XCTAssertGreaterThan(layout.bubbles[2].center.x, layout.center.x)
                for bubble in layout.bubbles { XCTAssertTrue(bounds.contains(bubble.frame)) }
            }
        }
    }

    func testSubmenusFitAtEdgesWithoutChildCollisions() {
        let bounds = CGRect(x: 0, y: 0, width: 1280, height: 800)
        for cursor in [CGPoint.zero, CGPoint(x: 1280, y: 800), CGPoint(x: 640, y: 400)] {
            let root = RingGeometry.root(slotCount: 8, cursor: cursor, bounds: bounds, scale: 1)
            let obstacles = root.bubbles + [RingBubbleLayout(index: -1, center: root.center, radius: root.closeRadius)]
            for parent in root.bubbles {
                for count in [1, 5, 9] {
                    let nodes = RingGeometry.submenu(count: count, parent: parent.center, origin: root.center,
                                                    bounds: bounds, scale: root.scale, obstacles: obstacles)
                    XCTAssertEqual(nodes.count, count)
                    for node in nodes {
                        XCTAssertTrue(bounds.contains(node.frame), "\(cursor), \(parent.index), \(count): \(node)")
                        for obstacle in obstacles {
                            let distance = Double(hypot(node.center.x - obstacle.center.x, node.center.y - obstacle.center.y))
                            XCTAssertGreaterThan(distance, node.radius + obstacle.radius)
                        }
                    }
                    for (index, node) in nodes.enumerated() {
                        for other in nodes.dropFirst(index + 1) {
                            let distance = Double(hypot(node.center.x - other.center.x, node.center.y - other.center.y))
                            XCTAssertGreaterThan(distance, node.radius + other.radius)
                        }
                    }
                }
            }
        }
    }

    func testTooltipClampsLongLabelsInsideAllEdges() {
        let bounds = CGRect(x: 0, y: 0, width: 800, height: 600)
        for point in [CGPoint.zero, CGPoint(x: 800, y: 0), CGPoint(x: 0, y: 600), CGPoint(x: 800, y: 600)] {
            let tooltip = RingGeometry.tooltip(size: CGSize(width: 1000, height: 55),
                                                anchor: CGRect(origin: point, size: CGSize(width: 56, height: 56)), bounds: bounds)
            XCTAssertTrue(bounds.contains(tooltip))
        }
    }

    func testCornerSubmenuStaysNearParentInsteadOfJumpingDownScreen() {
        let bounds = CGRect(x: 0, y: 29, width: 1024, height: 739)
        let root = RingGeometry.root(slotCount: 8, cursor: .zero, bounds: bounds, scale: 1)
        let parent = root.bubbles[6]
        let obstacles = root.bubbles + [RingBubbleLayout(index: -1, center: root.center, radius: root.closeRadius)]
        let children = RingGeometry.submenu(count: 5, parent: parent.center, origin: root.center,
                                            bounds: bounds, scale: root.scale, obstacles: obstacles)
        XCTAssertEqual(children.count, 5)
        let firstDistance = Double(hypot(children[0].center.x - parent.center.x, children[0].center.y - parent.center.y))
        XCTAssertLessThan(firstDistance, 160, "The first submenu bubble must remain connected to its corner parent.")
        for child in children {
            XCTAssertTrue(bounds.contains(child.frame))
            for obstacle in obstacles {
                let distance = Double(hypot(child.center.x - obstacle.center.x, child.center.y - obstacle.center.y))
                XCTAssertGreaterThanOrEqual(distance + 0.001, child.radius + obstacle.radius + 8)
            }
        }
    }
}
