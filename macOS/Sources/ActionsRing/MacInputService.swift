import AppKit
import ApplicationServices
import Carbon
import CoreGraphics
import ActionsRingKit

enum MacSyntheticEvent {
    static let tag: Int64 = 0x41524354494F4E53
}

/// Canonical, layout-independent physical keys used by the shortcut editor and Quartz.
enum MacKeyboardMap {
    static let codes: [String: CGKeyCode] = [
        "A": 0, "S": 1, "D": 2, "F": 3, "H": 4, "G": 5, "Z": 6, "X": 7, "C": 8, "V": 9,
        "B": 11, "Q": 12, "W": 13, "E": 14, "R": 15, "Y": 16, "T": 17,
        "1": 18, "2": 19, "3": 20, "4": 21, "6": 22, "5": 23, "Equal": 24,
        "9": 25, "7": 26, "Minus": 27, "8": 28, "0": 29, "RightBracket": 30,
        "O": 31, "U": 32, "LeftBracket": 33, "I": 34, "P": 35, "Return": 36,
        "L": 37, "J": 38, "Quote": 39, "K": 40, "Semicolon": 41, "Backslash": 42,
        "Comma": 43, "Slash": 44, "N": 45, "M": 46, "Period": 47, "Tab": 48,
        "Space": 49, "Grave": 50, "Delete": 51, "Escape": 53,
        "F17": 64, "F18": 79, "F19": 80, "F20": 90, "F5": 96, "F6": 97,
        "F7": 98, "F3": 99, "F8": 100, "F9": 101, "F11": 103, "F13": 105,
        "F16": 106, "F14": 107, "F10": 109, "F12": 111, "F15": 113,
        "Home": 115, "PageUp": 116, "ForwardDelete": 117, "F4": 118,
        "End": 119, "F2": 120, "PageDown": 121, "F1": 122,
        "Left": 123, "Right": 124, "Down": 125, "Up": 126,
    ]
    static let names = Dictionary(uniqueKeysWithValues: codes.map { ($0.value, $0.key) })

    static func code(for key: String) -> CGKeyCode? {
        codes.first { $0.key.caseInsensitiveCompare(key) == .orderedSame }?.value
    }
    static func modifiers(in flags: CGEventFlags) -> [KeyModifier] {
        KeyModifier.allCases.filter { modifier in
            switch modifier {
            case .command: return flags.contains(.maskCommand)
            case .control: return flags.contains(.maskControl)
            case .option: return flags.contains(.maskAlternate)
            case .shift: return flags.contains(.maskShift)
            }
        }
    }
    static func flags(for modifiers: [KeyModifier]) -> CGEventFlags {
        var flags: CGEventFlags = []
        for modifier in modifiers {
            switch modifier {
            case .command: flags.insert(.maskCommand)
            case .control: flags.insert(.maskControl)
            case .option: flags.insert(.maskAlternate)
            case .shift: flags.insert(.maskShift)
            }
        }
        return flags
    }
}

private let actionsRingEventCallback: CGEventTapCallBack = { _, type, event, context in
    guard let context else { return Unmanaged.passUnretained(event) }
    // The source is installed exclusively on CFRunLoopGetMain(). No dispatch wait
    // is used here: a synchronous decision is required to balance suppressed input.
    return MainActor.assumeIsolated {
        Unmanaged<MacInputService>.fromOpaque(context).takeUnretainedValue().handle(type, event)
    }
}

@MainActor
final class MacInputService {
    var onTrigger: (() -> Void)?
    var onRelease: (() -> Void)?
    var onError: ((String) -> Void)?
    var shouldCancelOnEscape: (() -> Bool)?
    var onCancel: (() -> Void)?

    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    private var permissionTimer: Timer?
    private var binding: TriggerBinding?
    private var activation = TriggerActivationState()
    private var scroll = ScrollGestureRecognizer()
    private var pressedKey: CGKeyCode?
    private var pressedMouse: Int?
    private var scrollHeld = false
    private var suppressedKeys: Set<CGKeyCode> = []
    private var suppressedButtons: Set<Int> = []
    private var captureCompletion: ((String, [KeyModifier]) -> Void)?
    private var captured: (code: CGKeyCode, key: String, modifiers: [KeyModifier])?

    deinit {
        permissionTimer?.invalidate()
        if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        if let tap { CFMachPortInvalidate(tap) }
    }

    static var accessibilityGranted: Bool { AXIsProcessTrusted() }
    static var inputMonitoringGranted: Bool { CGPreflightListenEventAccess() }

    /// Only call from the user's explicit permission button, never from startup.
    static func requestPermissions() {
        guard !ProcessInfo.processInfo.arguments.contains("--smoke-test") else { return }
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
        _ = CGRequestListenEventAccess()
    }

    @discardableResult
    func start(binding: TriggerBinding) -> Bool {
        stop()
        guard !ProcessInfo.processInfo.arguments.contains("--smoke-test") else { return false }
        guard binding.device != .keyboard || MacKeyboardMap.code(for: binding.key) != nil,
              binding.device != .mouse || (0...31).contains(binding.mouseButton),
              binding.device != .scroll || (!binding.modifiers.isEmpty && binding.scrollThreshold.isFinite
                  && (10...500).contains(binding.scrollThreshold)) else {
            onError?("Проверьте сочетание для вызова кольца.")
            return false
        }
        self.binding = binding
        if installTap() { return true }
        self.binding = nil
        return false
    }

    func stop() {
        permissionTimer?.invalidate()
        permissionTimer = nil
        if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
        if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        if let tap { CFMachPortInvalidate(tap) }
        source = nil
        tap = nil
        binding = nil
        captureCompletion = nil
        captured = nil
        resetInputState()
    }

    func captureShortcut(completion: @escaping (String, [KeyModifier]) -> Void) {
        guard !ProcessInfo.processInfo.arguments.contains("--smoke-test") else { return }
        cancelCapture()
        activation.reset()
        scroll.reset()
        pressedKey = nil
        pressedMouse = nil
        scrollHeld = false
        guard tap != nil || installTap() else { return }
        captureCompletion = completion
    }

    func cancelCapture() {
        captureCompletion = nil
        captured = nil
        // The captured down may already have been filtered. Keep its key in the
        // suppression set until the physical up arrives, even after cancellation.
    }

    private func installTap() -> Bool {
        guard Self.accessibilityGranted, Self.inputMonitoringGranted else {
            onError?("Разрешите Actions Ring универсальный доступ и мониторинг ввода в настройках macOS.")
            return false
        }
        let types: [CGEventType] = [.keyDown, .keyUp, .flagsChanged, .scrollWheel,
            .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp, .otherMouseDown, .otherMouseUp]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
        guard let newTap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap,
            options: .defaultTap, eventsOfInterest: mask, callback: actionsRingEventCallback,
            userInfo: Unmanaged.passUnretained(self).toOpaque()) else {
            onError?("macOS не разрешила подключить ввод. Проверьте разрешения и запустите Actions Ring снова.")
            return false
        }
        guard let newSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, newTap, 0) else {
            CFMachPortInvalidate(newTap)
            onError?("Не удалось подключить ввод. Запустите Actions Ring снова.")
            return false
        }
        tap = newTap
        source = newSource
        CFRunLoopAddSource(CFRunLoopGetMain(), newSource, .commonModes)
        CGEvent.tapEnable(tap: newTap, enable: true)
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.tap != nil else { return }
                if !Self.accessibilityGranted || !Self.inputMonitoringGranted {
                    self.fail("Разрешение на ввод отозвано. Вызов кольца приостановлен.")
                }
            }
        }
        return true
    }

    fileprivate func handle(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if Self.accessibilityGranted, Self.inputMonitoringGranted, let tap,
               type == .tapDisabledByTimeout {
                CGEvent.tapEnable(tap: tap, enable: true)
            } else {
                // Defer teardown until Quartz has returned from its callback.
                DispatchQueue.main.async { [weak self] in
                    self?.fail("macOS остановила мониторинг ввода. Проверьте разрешения Actions Ring.")
                }
            }
            return Unmanaged.passUnretained(event)
        }
        if event.getIntegerValueField(.eventSourceUserData) == MacSyntheticEvent.tag {
            return Unmanaged.passUnretained(event)
        }
        let modifiers = MacKeyboardMap.modifiers(in: event.flags)
        let code = CGKeyCode(truncatingIfNeeded: event.getIntegerValueField(.keyboardEventKeycode))

        if type == .keyUp, suppressedKeys.remove(code) != nil {
            if let captured, captured.code == code, let completion = captureCompletion {
                self.captured = nil
                captureCompletion = nil
                completion(captured.key, captured.modifiers)
            } else if pressedKey == code {
                pressedKey = nil
                finishPress()
            }
            return nil
        }
        if type == .keyDown, suppressedKeys.contains(code) { return nil }

        let button = mouseButton(for: type, event: event)
        if isMouseUp(type), let button, suppressedButtons.remove(button) != nil {
            if pressedMouse == button {
                pressedMouse = nil
                finishPress()
            }
            return nil
        }

        if captureCompletion != nil {
            if type == .keyDown, captured == nil, event.getIntegerValueField(.keyboardEventAutorepeat) == 0,
               let name = MacKeyboardMap.names[code] {
                captured = (code, name, modifiers)
                suppressedKeys.insert(code)
                return nil
            }
            return Unmanaged.passUnretained(event)
        }

        // Shortcut capture takes precedence, so Escape remains assignable. A fresh
        // Escape cancels only an already visible ring; its repeat and matching up
        // are filtered by the same balanced suppression path as a trigger key.
        if type == .keyDown, code == 53,
           event.getIntegerValueField(.keyboardEventAutorepeat) == 0,
           shouldCancelOnEscape?() == true {
            suppressedKeys.insert(code)
            activation.reset()
            scrollHeld = false
            onCancel?()
            return nil
        }

        guard let binding else { return Unmanaged.passUnretained(event) }
        if type == .flagsChanged {
            if binding.device == .scroll, !Set(binding.modifiers).isSubset(of: Set(modifiers)) {
                scroll.reset()
                if scrollHeld {
                    scrollHeld = false
                    onRelease?()
                }
            }
            return Unmanaged.passUnretained(event)
        }

        switch binding.device {
        case .keyboard:
            if type == .keyDown, code == MacKeyboardMap.code(for: binding.key),
               Set(modifiers) == Set(binding.modifiers), event.getIntegerValueField(.keyboardEventAutorepeat) == 0 {
                pressedKey = code
                suppressedKeys.insert(code)
                beginPress()
                return nil
            }
        case .mouse:
            if isMouseDown(type), button == binding.mouseButton, Set(modifiers) == Set(binding.modifiers) {
                pressedMouse = button
                suppressedButtons.insert(binding.mouseButton)
                beginPress()
                return nil
            }
        case .scroll:
            if type == .scrollWheel {
                let continuous = event.getIntegerValueField(.scrollWheelEventIsContinuous) != 0
                let deltaX = continuous ? event.getDoubleValueField(.scrollWheelEventPointDeltaAxis2)
                    : event.getDoubleValueField(.scrollWheelEventDeltaAxis2) * 12
                let deltaY = continuous ? event.getDoubleValueField(.scrollWheelEventPointDeltaAxis1)
                    : event.getDoubleValueField(.scrollWheelEventDeltaAxis1) * 12
                let decision = scroll.update(deltaX: deltaX, deltaY: deltaY, modifiers: modifiers,
                    timestamp: Double(event.timestamp) / 1_000_000_000,
                    isMomentum: event.getIntegerValueField(.scrollWheelEventMomentumPhase) != 0, binding: binding)
                if decision.triggered && !scrollHeld {
                    scrollHeld = binding.mode == .hold
                    onTrigger?()
                }
                if decision.shouldSuppress { return nil }
            }
        }
        return Unmanaged.passUnretained(event)
    }

    private func beginPress() {
        guard let binding else { return }
        switch activation.press(mode: binding.mode) {
        case .open, .toggle: onTrigger?()
        case .none, .close: break
        }
    }
    private func finishPress() {
        guard let binding else { return }
        if activation.release(mode: binding.mode) == .close { onRelease?() }
    }
    private func resetInputState() {
        activation.reset()
        scroll.reset()
        scrollHeld = false
        pressedKey = nil
        pressedMouse = nil
        suppressedKeys.removeAll()
        suppressedButtons.removeAll()
    }
    private func fail(_ message: String) {
        stop()
        onError?(message)
    }
    private func isMouseDown(_ type: CGEventType) -> Bool {
        type == .leftMouseDown || type == .rightMouseDown || type == .otherMouseDown
    }
    private func isMouseUp(_ type: CGEventType) -> Bool {
        type == .leftMouseUp || type == .rightMouseUp || type == .otherMouseUp
    }
    private func mouseButton(for type: CGEventType, event: CGEvent) -> Int? {
        switch type {
        case .leftMouseDown, .leftMouseUp: return 0
        case .rightMouseDown, .rightMouseUp: return 1
        case .otherMouseDown, .otherMouseUp: return Int(event.getIntegerValueField(.mouseEventButtonNumber))
        default: return nil
        }
    }
}
