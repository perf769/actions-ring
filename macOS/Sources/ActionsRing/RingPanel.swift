import AppKit
import ActionsRingKit

@MainActor
final class RingPanelController {
    var onAction: ((RingAction) -> Void)?
    var onClose: (() -> Void)?
    private(set) var panel: NSPanel?
    private(set) var isVisible = false
    private var content: RingCanvasView?
    private var fadeTimer: Timer?
    private var generation = 0
    private var preferences = AppPreferences()

    func show(profile: RingProfile, preferences: AppPreferences) {
        guard let screen = NSScreen.screens.first(where: { $0.frame.contains(NSEvent.mouseLocation) }) ?? NSScreen.main else { return }
        generation += 1
        fadeTimer?.invalidate()
        fadeTimer = nil
        self.preferences = preferences
        if panel == nil {
            let window = RingOverlayPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel],
                                          backing: .buffered, defer: false)
            window.backgroundColor = .clear
            window.isOpaque = false
            window.hasShadow = false
            window.hidesOnDeactivate = false
            window.isFloatingPanel = true
            window.level = .popUpMenu
            window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
            window.acceptsMouseMovedEvents = true
            window.isReleasedWhenClosed = false
            window.title = "Кольцо действий"
            let view = RingCanvasView(frame: CGRect(origin: .zero, size: screen.frame.size))
            view.onCommit = { [weak self] action in self?.finish(action: action) }
            view.onDismiss = { [weak self] in self?.hide() }
            window.contentView = view
            panel = window
            content = view
        }
        guard let panel, let content else { return }
        // Relocate while fully hidden so an interrupted close cannot flash the previous position.
        panel.orderOut(nil)
        panel.alphaValue = 1
        panel.setFrame(screen.frame, display: false)
        content.frame = CGRect(origin: .zero, size: screen.frame.size)
        let mouse = NSEvent.mouseLocation
        let cursor = CGPoint(x: mouse.x - screen.frame.minX, y: screen.frame.maxY - mouse.y)
        let usable = CGRect(x: screen.visibleFrame.minX - screen.frame.minX,
                            y: screen.frame.maxY - screen.visibleFrame.maxY,
                            width: screen.visibleFrame.width, height: screen.visibleFrame.height)
        content.configure(profile: profile, preferences: preferences, cursor: cursor, usableBounds: usable)
        isVisible = true
        panel.orderFrontRegardless()
        content.startAnimating()
    }

    func hide() {
        guard isVisible else { return }
        isVisible = false
        generation += 1
        let closingGeneration = generation
        content?.stopAnimating()
        onClose?()
        guard preferences.animations, !preferences.reducedMotion, let panel else {
            panel?.orderOut(nil)
            return
        }
        let started = ProcessInfo.processInfo.systemUptime
        fadeTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { [weak self] timer in
            Task { @MainActor in
                guard let self, self.generation == closingGeneration, !self.isVisible else { timer.invalidate(); return }
                let amount = min(1, (ProcessInfo.processInfo.systemUptime - started) / 0.13)
                panel.alphaValue = 1 - amount
                if amount >= 1 {
                    timer.invalidate()
                    self.fadeTimer = nil
                    panel.orderOut(nil)
                    panel.alphaValue = 1
                }
            }
        }
        if let fadeTimer { RunLoop.main.add(fadeTimer, forMode: .common) }
    }

    func commit(holdRelease: Bool) {
        guard isVisible else { return }
        content?.refreshPointer()
        content?.commit(holdRelease: holdRelease)
    }

    /// Used by the app's render-only smoke mode; this never injects or executes input.
    func previewSubmenu(at index: Int) { content?.previewSubmenu(at: index) }

    private func finish(action: RingAction?) {
        guard isVisible else { return }
        guard let action else { hide(); return }
        // Actions such as screenshots must run after every pixel of the ring has disappeared.
        generation += 1
        fadeTimer?.invalidate()
        fadeTimer = nil
        isVisible = false
        content?.stopAnimating()
        panel?.orderOut(nil)
        onClose?()
        onAction?(action)
    }
}

private final class RingOverlayPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@MainActor
private final class RingCanvasView: NSView {
    struct Node {
        var path: [Int]
        var slot: RingSlot
        var layout: RingBubbleLayout
        var anchor: CGPoint
        var parentPath: [Int]?
        var appeared: TimeInterval
    }
    var onCommit: ((RingAction?) -> Void)?
    var onDismiss: (() -> Void)?
    override var isFlipped: Bool { true }
    private var profile = RingProfile()
    private var preferences = AppPreferences()
    private var usableBounds = CGRect.zero
    private var rootLayout = RingGeometry.root(slotCount: 8, cursor: .zero, bounds: .zero, scale: 1)
    private var nodes: [Node] = []
    private var displayedFrames: [[Int]: CGRect] = [:]
    private var openedPaths: [[Int]] = []
    private var submenuTimes: [[Int]: TimeInterval] = [:]
    private var hoveredPath: [Int]?
    private var hoveredAt: TimeInterval = 0
    private var closeHovered = false
    private var startedAt: TimeInterval = 0
    private var timer: Timer?
    private var tracking: NSTrackingArea?
    private var imageCache: [String: NSImage] = [:]
    private var brightnessCache: [SlotIcon: Bool] = [:]

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = false
        setAccessibilityElement(false)
        setAccessibilityRole(.group)
        setAccessibilityLabel("Кольцо действий")
    }
    required init?(coder: NSCoder) { return nil }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    func configure(profile: RingProfile, preferences: AppPreferences, cursor: CGPoint, usableBounds: CGRect) {
        stopAnimating()
        self.profile = profile
        self.preferences = preferences
        self.usableBounds = usableBounds
        rootLayout = RingGeometry.root(slotCount: profile.slots.count, cursor: cursor,
                                       bounds: usableBounds, scale: preferences.ringScale)
        openedPaths = []
        submenuTimes = [:]
        imageCache = [:]
        brightnessCache = [:]
        hoveredPath = nil
        closeHovered = false
        displayedFrames = [:]
        startedAt = ProcessInfo.processInfo.systemUptime
        rebuildNodes()
        needsDisplay = true
    }

    func startAnimating() {
        timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.refreshPointer()
                self?.needsDisplay = true
            }
        }
        if let timer { RunLoop.main.add(timer, forMode: .common) }
    }
    func stopAnimating() { timer?.invalidate(); timer = nil }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: .zero, options: [.activeAlways, .inVisibleRect, .mouseMoved, .mouseEnteredAndExited, .enabledDuringMouseDrag], owner: self, userInfo: nil)
        addTrackingArea(area)
        tracking = area
    }

    override func mouseMoved(with event: NSEvent) { updateHover(convert(event.locationInWindow, from: nil)) }
    override func mouseDragged(with event: NSEvent) { updateHover(convert(event.locationInWindow, from: nil)) }
    override func rightMouseDragged(with event: NSEvent) { updateHover(convert(event.locationInWindow, from: nil)) }
    override func otherMouseDragged(with event: NSEvent) { updateHover(convert(event.locationInWindow, from: nil)) }
    override func mouseEntered(with event: NSEvent) { updateHover(convert(event.locationInWindow, from: nil)) }
    override func mouseExited(with event: NSEvent) { hoveredPath = nil; closeHovered = false; needsDisplay = true }
    override func mouseDown(with event: NSEvent) {
        updateHover(convert(event.locationInWindow, from: nil))
        commit(holdRelease: false)
    }
    override func rightMouseDown(with event: NSEvent) { onDismiss?() }

    func refreshPointer() {
        guard let window, window.isVisible else { return }
        // A held trigger's down event is suppressed globally. The originating app can therefore
        // retain drag routing; polling the public cursor position keeps hover correct in that case.
        updateHover(convert(window.convertPoint(fromScreen: NSEvent.mouseLocation), from: nil))
    }

    func previewSubmenu(at index: Int) {
        guard profile.slots.indices.contains(index), profile.slots[index].hasSubmenu else { return }
        let path = [index]
        hoveredPath = path
        hoveredAt = ProcessInfo.processInfo.systemUptime - 1
        openedPaths = [path]
        submenuTimes[path] = ProcessInfo.processInfo.systemUptime - 1
        rebuildNodes()
    }

    func commit(holdRelease: Bool) {
        guard !closeHovered, let path = hoveredPath, let node = nodes.first(where: { $0.path == path }) else {
            onDismiss?()
            return
        }
        if let action = node.slot.action {
            onCommit?(action)
        } else if node.slot.hasSubmenu, !holdRelease {
            expand(path)
        } else {
            // A released hold never leaves a pure folder, empty bubble, or gap stranded on screen.
            onDismiss?()
        }
    }

    private func updateHover(_ point: CGPoint) {
        let closeDistance = hypot(point.x - rootLayout.center.x, point.y - rootLayout.center.y)
        closeHovered = closeDistance <= rootLayout.closeRadius + 3
        let path = closeHovered ? nil : nodes.reversed().first(where: { node in
            let frame = displayedFrames[node.path] ?? node.layout.frame
            return hypot(point.x - frame.midX, point.y - frame.midY) <= frame.width / 2 + 4
        })?.path
        guard path != hoveredPath else { needsDisplay = true; return }
        hoveredPath = path
        hoveredAt = ProcessInfo.processInfo.systemUptime
        guard let path, let node = nodes.first(where: { $0.path == path }) else { needsDisplay = true; return }
        let ancestors = openedPaths.filter { candidate in candidate.count < path.count && Array(path.prefix(candidate.count)) == candidate }
        if node.slot.hasSubmenu {
            openedPaths = ancestors + [path]
            if submenuTimes[path] == nil { submenuTimes[path] = hoveredAt }
        } else { openedPaths = ancestors }
        submenuTimes = submenuTimes.filter { openedPaths.contains($0.key) }
        rebuildNodes()
    }

    private func expand(_ path: [Int]) {
        openedPaths = openedPaths.filter { $0.count < path.count && Array(path.prefix($0.count)) == $0 } + [path]
        submenuTimes[path] = submenuTimes[path] ?? ProcessInfo.processInfo.systemUptime
        rebuildNodes()
    }

    private func rebuildNodes() {
        nodes = rootLayout.bubbles.compactMap { layout in
            guard profile.slots.indices.contains(layout.index) else { return nil }
            return Node(path: [layout.index], slot: profile.slots[layout.index], layout: layout,
                        anchor: rootLayout.center, parentPath: nil, appeared: startedAt)
        }
        for path in openedPaths.sorted(by: { $0.count < $1.count }) {
            guard let parent = nodes.first(where: { $0.path == path }), let children = parent.slot.submenu else { continue }
            let obstacles = nodes.map(\.layout) + [RingBubbleLayout(index: -1, center: rootLayout.center, radius: rootLayout.closeRadius)]
            let layout = RingGeometry.submenu(count: children.count, parent: parent.layout.center, origin: parent.anchor,
                                              bounds: usableBounds, scale: rootLayout.scale, obstacles: obstacles)
            for bubble in layout where children.indices.contains(bubble.index) {
                nodes.append(Node(path: path + [bubble.index], slot: children[bubble.index], layout: bubble,
                                  anchor: parent.layout.center, parentPath: path,
                                  appeared: submenuTimes[path] ?? ProcessInfo.processInfo.systemUptime))
            }
        }
        updateAccessibility()
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        context.clear(bounds)
        let now = ProcessInfo.processInfo.systemUptime
        let rootProgress = progress(since: startedAt, now: now, delay: 0)
        displayedFrames = [:]
        let activeRoot = openedPaths.first?.first
        for node in nodes {
            let levelDelay = node.parentPath == nil ? Double(node.layout.index) * 0.008 : Double(node.layout.index) * 0.013
            let amount = progress(since: node.appeared, now: now, delay: levelDelay)
            let eased = ease(amount)
            let point = CGPoint(x: node.anchor.x + (node.layout.center.x - node.anchor.x) * eased,
                                y: node.anchor.y + (node.layout.center.y - node.anchor.y) * eased)
            let radius = node.layout.radius * (0.2 + 0.8 * eased)
            let frame = CGRect(x: point.x - radius, y: point.y - radius, width: radius * 2, height: radius * 2)
            if amount > 0.35 { displayedFrames[node.path] = frame }
            let selected = hoveredPath == node.path || openedPaths.contains(node.path)
            let palette = node.slot.colors?.resolving(profile.effectivePalette) ?? profile.effectivePalette
            let fill = NSColor.ringHex(selected ? palette.hover : palette.bubble)
            let ink = NSColor.ringHex(selected ? palette.hoverIcon : palette.icon)
            let dim = activeRoot != nil && node.path.first != activeRoot ? 0.25 : 1.0
            context.saveGState()
            context.setAlpha(min(1, amount * 2) * dim)
            if node.parentPath != nil, node.layout.index == 0, amount > 0.03, amount < 0.8 {
                drawBridge(from: node.anchor, to: point, radius: radius, amount: amount, color: fill)
            }
            let direction = atan2(point.y - node.anchor.y, point.x - node.anchor.x)
            drawBubble(frame: frame, submenu: node.slot.hasSubmenu, direction: direction, fill: fill)
            drawIcon(node.slot.effectiveIcon, frame: frame.insetBy(dx: radius * 0.47, dy: radius * 0.47), ink: ink)
            if node.slot.hasSubmenu { drawChevron(center: point, radius: radius, direction: direction, ink: ink) }
            context.restoreGState()
        }
        drawClose(progress: rootProgress)
        if now - hoveredAt > 0.32, let path = hoveredPath,
           let node = nodes.first(where: { $0.path == path }), let frame = displayedFrames[path] {
            drawTooltip(node.slot.label, anchor: frame)
        }
    }

    private func progress(since time: TimeInterval, now: TimeInterval, delay: Double) -> Double {
        guard preferences.animations else { return 1 }
        return min(1, max(0, (now - time - (preferences.reducedMotion ? 0 : delay)) / (preferences.reducedMotion ? 0.09 : 0.28)))
    }
    private func ease(_ amount: Double) -> Double { preferences.reducedMotion ? 1 : 1 - pow(1 - amount, 3) }

    private func drawBubble(frame: CGRect, submenu: Bool, direction: Double, fill: NSColor) {
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.18)
        shadow.shadowBlurRadius = 9 * rootLayout.scale
        shadow.shadowOffset = NSSize(width: 0, height: 2)
        shadow.set()
        let shape = NSBezierPath(ovalIn: frame)
        if submenu {
            let nubRadius = frame.width * 0.14
            let distance = frame.width * 0.45
            let nub = CGPoint(x: frame.midX + cos(direction) * distance, y: frame.midY + sin(direction) * distance)
            shape.appendOval(in: CGRect(x: nub.x - nubRadius, y: nub.y - nubRadius,
                                       width: nubRadius * 2, height: nubRadius * 2))
        }
        fill.setFill()
        shape.fill()
        NSGraphicsContext.restoreGraphicsState()
    }

    private func drawChevron(center: CGPoint, radius: Double, direction: Double, ink: NSColor) {
        let point = CGPoint(x: center.x + cos(direction) * radius * 1.03,
                            y: center.y + sin(direction) * radius * 1.03)
        let dx = cos(direction), dy = sin(direction)
        let size = 2.4 * rootLayout.scale
        let path = NSBezierPath()
        path.move(to: CGPoint(x: point.x - dx * size - dy * size, y: point.y - dy * size + dx * size))
        path.line(to: point)
        path.line(to: CGPoint(x: point.x - dx * size + dy * size, y: point.y - dy * size - dx * size))
        path.lineWidth = 1.1 * rootLayout.scale
        path.lineCapStyle = .round
        path.lineJoinStyle = .round
        ink.setStroke()
        path.stroke()
    }

    private func drawBridge(from start: CGPoint, to end: CGPoint, radius: Double, amount: Double, color: NSColor) {
        let distance = hypot(end.x - start.x, end.y - start.y)
        guard distance > 1 else { return }
        let nx = -(end.y - start.y) / distance
        let ny = (end.x - start.x) / distance
        let startRadius = 23 * rootLayout.scale
        let neck = radius * max(0, 1 - amount / 0.8) * 0.6
        let mid = CGPoint(x: start.x + (end.x - start.x) * 0.56, y: start.y + (end.y - start.y) * 0.56)
        let path = NSBezierPath()
        path.move(to: CGPoint(x: start.x + nx * startRadius, y: start.y + ny * startRadius))
        path.curve(to: CGPoint(x: end.x + nx * radius * 0.75, y: end.y + ny * radius * 0.75),
                   controlPoint1: CGPoint(x: mid.x + nx * neck, y: mid.y + ny * neck),
                   controlPoint2: CGPoint(x: mid.x + nx * neck, y: mid.y + ny * neck))
        path.line(to: CGPoint(x: end.x - nx * radius * 0.75, y: end.y - ny * radius * 0.75))
        path.curve(to: CGPoint(x: start.x - nx * startRadius, y: start.y - ny * startRadius),
                   controlPoint1: CGPoint(x: mid.x - nx * neck, y: mid.y - ny * neck),
                   controlPoint2: CGPoint(x: mid.x - nx * neck, y: mid.y - ny * neck))
        path.close()
        color.setFill()
        path.fill()
    }

    private func drawIcon(_ reference: SlotIcon, frame: CGRect, ink: NSColor) {
        let image: NSImage
        let sourceKey = "\(reference.kind.rawValue):\(reference.value)"
        if let cached = imageCache[sourceKey] { image = cached }
        else {
            guard let loaded = MacIconLoader.image(for: reference)
                ?? NSImage(systemSymbolName: "questionmark", accessibilityDescription: nil) else { return }
            image = loaded
            imageCache[sourceKey] = loaded
        }
        if reference.kind == .symbol || image.isTemplate {
            let key = "\(sourceKey):\(ink.description):\(Int(frame.width.rounded()))"
            let tinted: NSImage
            if let cached = imageCache[key] { tinted = cached }
            else {
                let size = CGSize(width: max(1, frame.width), height: max(1, frame.height))
                tinted = NSImage(size: size)
                tinted.lockFocusFlipped(true)
                let rect = CGRect(origin: .zero, size: size)
                image.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
                ink.setFill()
                NSRectFillUsingOperation(rect, .sourceAtop)
                tinted.unlockFocus()
                imageCache[key] = tinted
            }
            tinted.draw(in: frame, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        } else {
            let bright: Bool
            if let cached = brightnessCache[reference] { bright = cached }
            else {
                bright = isBright(image)
                brightnessCache[reference] = bright
            }
            (bright ? NSColor(white: 0.16, alpha: 0.95) : NSColor(white: 1, alpha: 0.94)).setFill()
            NSBezierPath(roundedRect: frame.insetBy(dx: -3, dy: -3), xRadius: 7, yRadius: 7).fill()
            image.draw(in: frame, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        }
    }

    private func isBright(_ image: NSImage) -> Bool {
        guard let data = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: data) else { return false }
        var total = 0.0, weight = 0.0
        for y in stride(from: 0, to: bitmap.pixelsHigh, by: max(1, bitmap.pixelsHigh / 12)) {
            for x in stride(from: 0, to: bitmap.pixelsWide, by: max(1, bitmap.pixelsWide / 12)) {
                guard let color = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB), color.alphaComponent > 0.1 else { continue }
                total += (color.redComponent * 0.2126 + color.greenComponent * 0.7152 + color.blueComponent * 0.0722) * color.alphaComponent
                weight += color.alphaComponent
            }
        }
        return weight > 0 && total / weight > 0.7
    }

    private func drawClose(progress: Double) {
        let amount = ease(progress)
        let radius = rootLayout.closeRadius * (0.3 + amount * 0.7)
        let frame = CGRect(x: rootLayout.center.x - radius, y: rootLayout.center.y - radius, width: radius * 2, height: radius * 2)
        let palette = profile.effectivePalette
        NSGraphicsContext.current?.cgContext.saveGState()
        NSGraphicsContext.current?.cgContext.setAlpha(min(1, progress * 2))
        drawBubble(frame: frame, submenu: false, direction: 0,
                   fill: .ringHex(closeHovered ? palette.hover : palette.bubble))
        let cross = NSBezierPath()
        let half = 3.7 * rootLayout.scale
        cross.move(to: CGPoint(x: rootLayout.center.x - half, y: rootLayout.center.y - half))
        cross.line(to: CGPoint(x: rootLayout.center.x + half, y: rootLayout.center.y + half))
        cross.move(to: CGPoint(x: rootLayout.center.x + half, y: rootLayout.center.y - half))
        cross.line(to: CGPoint(x: rootLayout.center.x - half, y: rootLayout.center.y + half))
        cross.lineWidth = 1.5 * rootLayout.scale
        cross.lineCapStyle = .round
        NSColor.ringHex(closeHovered ? palette.hoverIcon : palette.icon).setStroke()
        cross.stroke()
        NSGraphicsContext.current?.cgContext.restoreGState()
    }

    private func drawTooltip(_ label: String, anchor: CGRect) {
        let dark = preferences.appearance == .dark || (preferences.appearance == .system && effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua)
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        paragraph.lineBreakMode = .byWordWrapping
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 12, weight: .medium),
            .foregroundColor: dark ? NSColor.white : NSColor.black,
            .paragraphStyle: paragraph,
        ]
        let text = NSAttributedString(string: label, attributes: attributes)
        let size = text.boundingRect(with: CGSize(width: 240, height: 150), options: [.usesLineFragmentOrigin, .usesFontLeading]).size
        let frame = RingGeometry.tooltip(size: CGSize(width: ceil(size.width) + 24, height: ceil(size.height) + 16),
                                         anchor: anchor, bounds: usableBounds)
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.18)
        shadow.shadowBlurRadius = 10
        shadow.shadowOffset = CGSize(width: 0, height: 2)
        shadow.set()
        (dark ? NSColor(white: 0.16, alpha: 0.98) : NSColor(white: 1, alpha: 0.98)).setFill()
        NSBezierPath(roundedRect: frame, xRadius: 7, yRadius: 7).fill()
        NSGraphicsContext.restoreGraphicsState()
        text.draw(in: frame.insetBy(dx: 12, dy: 8))
    }

    private func updateAccessibility() {
        guard let window else { return }
        var children: [NSAccessibilityElement] = nodes.map { node in
            let element = RingAccessibleButton()
            element.setAccessibilityRole(.button)
            element.setAccessibilityLabel(node.slot.label)
            element.setAccessibilityParent(self)
            element.setAccessibilityFrame(window.convertToScreen(convert(node.layout.frame, to: nil)))
            element.pressHandler = { [weak self] in
                guard let self else { return }
                self.hoveredPath = node.path
                self.closeHovered = false
                self.commit(holdRelease: false)
            }
            return element
        }
        let close = RingAccessibleButton()
        close.setAccessibilityRole(.button)
        close.setAccessibilityLabel("Закрыть кольцо")
        close.setAccessibilityParent(self)
        let radius = rootLayout.closeRadius
        let frame = CGRect(x: rootLayout.center.x - radius, y: rootLayout.center.y - radius, width: radius * 2, height: radius * 2)
        close.setAccessibilityFrame(window.convertToScreen(convert(frame, to: nil)))
        close.pressHandler = { [weak self] in self?.onDismiss?() }
        children.append(close)
        setAccessibilityChildren(children)
    }
}

private final class RingAccessibleButton: NSAccessibilityElement {
    var pressHandler: (() -> Void)?
    override func accessibilityPerformPress() -> Bool { pressHandler?(); return true }
}

private extension NSColor {
    static func ringHex(_ value: String) -> NSColor {
        guard value.count == 7, let integer = UInt32(value.dropFirst(), radix: 16) else { return .labelColor }
        return NSColor(srgbRed: CGFloat((integer >> 16) & 255) / 255,
                       green: CGFloat((integer >> 8) & 255) / 255,
                       blue: CGFloat(integer & 255) / 255, alpha: 1)
    }
}
