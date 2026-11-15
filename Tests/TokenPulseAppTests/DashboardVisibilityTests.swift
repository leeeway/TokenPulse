import AppKit
import Testing
@testable import TokenPulseApp

@Suite("Native dashboard visibility")
struct DashboardVisibilityTests {
    @Test @MainActor
    func orderedOutAndReopenedWindowsReportVisibility() async throws {
        _ = NSApplication.shared
        var states: [Bool] = []
        let probe = DashboardVisibilityView { states.append($0) }
        let window = NSWindow(contentRect: NSRect(x: 40, y: 40, width: 100, height: 40),
            styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.level = .floating
        window.contentView = probe
        defer { window.close() }
        func waitFor(_ visible: Bool) async throws {
            for _ in 0..<100 {
                if states.last == visible { return }
                try await Task.sleep(for: .milliseconds(20))
            }
            throw CocoaError(.coderReadCorrupt)
        }
        window.orderFrontRegardless()
        try await waitFor(true)
        window.orderOut(nil)
        try await waitFor(false)
        window.orderFrontRegardless()
        try await waitFor(true)
        probe.stopObserving()
        try await waitFor(false)
        #expect(states == [true, false, true, false])
    }
}
