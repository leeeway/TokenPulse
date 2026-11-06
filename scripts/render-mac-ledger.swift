import AppKit
import Foundation
import SwiftUI
import TokenPulseCore

/// Native SwiftUI visual verification using synthetic counters only.
@main
struct LedgerPreviews {
    @MainActor static func main() async throws {
        _ = NSApplication.shared
        let output = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        let iso = ISO8601DateFormatter()
        let now = iso.date(from: "2026-10-21T15:41:03Z")!
        let reset = iso.date(from: "2026-10-27T07:07:27Z")!
        let input = CodexLedgerInput(events: [
            TokenUsageEvent(id: "synthetic-a", provider: .codex, model: "gpt-6.1-sol", timestamp: now.addingTimeInterval(-7200),
                sessionId: "synthetic-only", inputTokens: 1000, cacheReadTokens: 9000, outputTokens: 100)
        ], observations: [QuotaObservation(observedAt: now.addingTimeInterval(-60), windowRole: "primary",
            windowMinutes: 10080, resetsAt: reset, usedPercent: 100, planType: "team")], scannedAt: now, sourceAvailable: true)
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("TokenPulse-Preview-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let model = LedgerViewModel(directory: directory, clock: { now })
        model.receive(input)
        for _ in 0..<3000 {
            if !model.isLoading { break }
            try await Task.sleep(for: .milliseconds(2))
        }
        guard model.state.current != nil else { throw CocoaError(.coderInvalidValue) }
        try save(LedgerView(model: model, refresh: { }).frame(width: 960, height: 760), name: "ledger-normal", to: output)
        try save(LedgerView(model: model, refresh: { }).frame(width: 760, height: 600), name: "ledger-narrow", to: output)
        model.receive(.empty(scannedAt: now.addingTimeInterval(1)))
        for _ in 0..<3000 {
            if !model.state.sourceAvailable { break }
            try await Task.sleep(for: .milliseconds(2))
        }
        guard !model.state.sourceAvailable else { throw CocoaError(.coderInvalidValue) }
        try save(LedgerView(model: model, refresh: { }).frame(width: 760, height: 600), name: "ledger-unavailable", to: output)
        if let report = CodexLedgerEngine.currentReport(input: input, now: now, timeZone: TimeZone(secondsFromGMT: 0)!) {
            try save(LedgerReportView(report: report).padding(24).frame(width: 900), name: "ledger-full-report", to: output)
            let folder = try ReportExporter.export(report: report, to: output)
            print("Synthetic HTML/CSV: \(folder.path)")
        }
        print("Native ledger previews passed")
    }

    @MainActor private static func save<V: View>(_ view: V, name: String, to directory: URL) throws {
        let host = NSHostingView(rootView: view)
        let size = host.fittingSize
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.titled], backing: .buffered, defer: false)
        window.contentView = host
        window.orderFront(nil)
        host.layoutSubtreeIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.3))
        guard let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { throw CocoaError(.fileWriteUnknown) }
        host.cacheDisplay(in: host.bounds, to: bitmap)
        guard let png = bitmap.representation(using: .png, properties: [:]) else { throw CocoaError(.fileWriteUnknown) }
        window.orderOut(nil)
        try png.write(to: directory.appendingPathComponent(name + ".png"))
    }
}
