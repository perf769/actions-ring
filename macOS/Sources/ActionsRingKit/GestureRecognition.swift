import Foundation

public struct ScrollGestureDecision: Equatable, Sendable {
    public var shouldSuppress: Bool
    public var triggered: Bool
    public init(shouldSuppress: Bool = false, triggered: Bool = false) {
        self.shouldSuppress = shouldSuppress
        self.triggered = triggered
    }
}

/// Recognizes a deliberate modifier + directional scroll without treating inertia as a new gesture.
/// The caller supplies monotonic seconds and native event deltas: positive Y is up, positive X is left.
public struct ScrollGestureRecognizer: Sendable {
    private var accumulatedX: Double = 0
    private var accumulatedY: Double = 0
    private var lastTimestamp: Double?
    private var lastTriggerTimestamp: Double?
    private var latched = false
    private var previousBinding: TriggerBinding?
    public var cooldown: Double
    public var sessionGap: Double

    public init(cooldown: Double = 0.7, sessionGap: Double = 0.35) {
        self.cooldown = max(0.1, cooldown)
        self.sessionGap = max(0.1, sessionGap)
    }

    public mutating func reset() {
        accumulatedX = 0
        accumulatedY = 0
        lastTimestamp = nil
        lastTriggerTimestamp = nil
        latched = false
        previousBinding = nil
    }

    public mutating func update(deltaX: Double, deltaY: Double, modifiers: [KeyModifier],
                                timestamp: Double, isMomentum: Bool = false,
                                binding: TriggerBinding) -> ScrollGestureDecision {
        guard deltaX.isFinite, deltaY.isFinite, timestamp.isFinite else { reset(); return .init() }
        if previousBinding != binding { reset(); previousBinding = binding }
        guard binding.device == .scroll, !binding.modifiers.isEmpty,
              binding.scrollThreshold.isFinite, binding.scrollThreshold >= 10,
              Set(modifiers) == Set(binding.modifiers) else {
            accumulatedX = 0
            accumulatedY = 0
            lastTimestamp = nil
            latched = false
            return .init()
        }
        if let lastTimestamp, timestamp < lastTimestamp { reset(); previousBinding = binding }
        if !isMomentum, lastTimestamp == nil || timestamp - (lastTimestamp ?? timestamp) > sessionGap {
            accumulatedX = 0
            accumulatedY = 0
            latched = false
        }
        lastTimestamp = timestamp
        guard !isMomentum else { return .init(shouldSuppress: true) }
        guard !latched else { return .init(shouldSuppress: true) }
        accumulatedX = min(100_000, max(-100_000, accumulatedX + deltaX))
        accumulatedY = min(100_000, max(-100_000, accumulatedY + deltaY))
        let primary: Double
        let cross: Double
        switch binding.scrollDirection {
        case .up: primary = accumulatedY; cross = abs(accumulatedX)
        case .down: primary = -accumulatedY; cross = abs(accumulatedX)
        case .left: primary = accumulatedX; cross = abs(accumulatedY)
        case .right: primary = -accumulatedX; cross = abs(accumulatedY)
        }
        let elapsed = lastTriggerTimestamp.map { timestamp - $0 } ?? .infinity
        guard primary >= binding.scrollThreshold, primary >= cross * 1.25, elapsed >= cooldown else {
            return .init(shouldSuppress: true)
        }
        latched = true
        lastTriggerTimestamp = timestamp
        return .init(shouldSuppress: true, triggered: true)
    }
}

/// Pure keyboard/mouse lifetime logic. Call reset on permission loss, capture, or binding changes.
public struct TriggerActivationState: Sendable {
    public enum Transition: Equatable, Sendable { case none, toggle, open, close }
    public private(set) var isPressed = false
    public init() {}
    public mutating func reset() { isPressed = false }
    public mutating func press(mode: ActivationMode, isRepeat: Bool = false) -> Transition {
        guard !isPressed, !isRepeat else { return .none }
        isPressed = true
        return mode == .hold ? .open : .toggle
    }
    public mutating func release(mode: ActivationMode) -> Transition {
        guard isPressed else { return .none }
        isPressed = false
        return mode == .hold ? .close : .none
    }
}
