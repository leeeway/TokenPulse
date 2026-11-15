import AppKit
import Foundation

/// All timers and callbacks run on the main queue. Owning this object controls
/// their lifetime; no repeating timer retains the view model.
final class BackgroundRefreshDriver: @unchecked Sendable {
    enum Trigger: Sendable { case logs, fallback, wake }
    private var fallbackTimer: Timer?
    private var displayTimer: Timer?
    private var monitor: LogChangeMonitor?
    private let center: NotificationCenter
    private var wakeObserver: NSObjectProtocol?
    private let refresh: @Sendable (Trigger) -> Void
    private let render: @Sendable () -> Void

    @MainActor
    init(directories: [URL], refresh: @escaping @Sendable (Trigger) -> Void, render: @escaping @Sendable () -> Void) {
        self.refresh = refresh
        self.render = render
        center = NSWorkspace.shared.notificationCenter
        monitor = LogChangeMonitor(directories: directories) { refresh(.logs) }
        fallbackTimer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            self?.monitor?.restartIfNeeded()
            refresh(.fallback)
        }
        fallbackTimer?.tolerance = 3
        wakeObserver = center.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            self?.monitor?.restartIfNeeded()
            refresh(.wake)
        }
    }

    @MainActor
    func recoverWatchRoots() { monitor?.restartIfNeeded() }

    @MainActor
    func setVisible(_ visible: Bool) {
        displayTimer?.invalidate()
        displayTimer = nil
        if visible {
            displayTimer = Timer.scheduledTimer(withTimeInterval: 4, repeats: true) { [render] _ in render() }
            displayTimer?.tolerance = 1
        }
    }
    deinit {
        fallbackTimer?.invalidate()
        displayTimer?.invalidate()
        if let wakeObserver { center.removeObserver(wakeObserver) }
    }
}
