import AppKit
import SwiftUI

/// MenuBarExtra may keep its content alive after ordering its window out.
/// Observe the native window as well as the SwiftUI view's lifetime.
struct DashboardVisibilityObserver: NSViewRepresentable {
    let changed: @MainActor (Bool) -> Void
    func makeNSView(context: Context) -> DashboardVisibilityView { DashboardVisibilityView(changed: changed) }
    func updateNSView(_ nsView: DashboardVisibilityView, context: Context) { }
    static func dismantleNSView(_ nsView: DashboardVisibilityView, coordinator: ()) { nsView.stopObserving() }
}

final class DashboardVisibilityView: NSView {
    private let changed: @MainActor (Bool) -> Void
    private var lastVisible = false
    init(changed: @escaping @MainActor (Bool) -> Void) {
        self.changed = changed
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { nil }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        NotificationCenter.default.removeObserver(self)
        if let window {
            for name in [NSWindow.didChangeOcclusionStateNotification, NSWindow.willCloseNotification,
                         NSWindow.didMiniaturizeNotification, NSWindow.didDeminiaturizeNotification] {
                NotificationCenter.default.addObserver(self, selector: #selector(windowChanged(_:)), name: name, object: window)
            }
        }
        scheduleReport()
    }
    @objc private func windowChanged(_ notification: Notification) {
        scheduleReport(forceHidden: notification.name == NSWindow.willCloseNotification)
    }
    private func scheduleReport(forceHidden: Bool = false) {
        Task { @MainActor [weak self] in
            guard let self else { return }
            let visible = !forceHidden && window?.isVisible == true && window?.occlusionState.contains(.visible) == true
            if visible != lastVisible {
                lastVisible = visible
                changed(visible)
            }
        }
    }
    func stopObserving() {
        NotificationCenter.default.removeObserver(self)
        scheduleReport(forceHidden: true)
    }
}
