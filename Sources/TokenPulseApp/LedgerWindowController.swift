import AppKit
import SwiftUI

/// Created only on demand; adding the ledger never opens a window during login startup.
@MainActor
final class LedgerWindowController: NSWindowController {
    init(model: LedgerViewModel, refresh: @escaping () -> Void) {
        let host = NSHostingController(rootView: LedgerView(model: model, refresh: refresh))
        let window = NSWindow(contentViewController: host)
        window.title = "TokenPulse 配额账本"
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.setContentSize(NSSize(width: 960, height: 760))
        window.minSize = NSSize(width: 760, height: 600)
        window.isReleasedWhenClosed = false
        window.center()
        super.init(window: window)
    }
    required init?(coder: NSCoder) { nil }
    func showLedger() {
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
    }
}
