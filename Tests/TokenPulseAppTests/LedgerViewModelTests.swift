import Foundation
import Testing
import TokenPulseCore
@testable import TokenPulseApp

private func ledgerDate(_ text: String) -> Date { ISO8601DateFormatter().date(from: text)! }

@MainActor
private func waitForLedger(_ condition: () -> Bool) async throws {
    for _ in 0..<1500 {
        if condition() { return }
        try await Task.sleep(for: .milliseconds(2))
    }
    throw CocoaError(.coderReadCorrupt)
}

@Suite("Mac ledger presentation")
struct LedgerViewModelTests {
    private let now = ledgerDate("2026-10-21T15:41:03Z")
    private var input: CodexLedgerInput {
        CodexLedgerInput(events: [
            TokenUsageEvent(id: "a", provider: .codex, model: "gpt-6.1-sol", timestamp: ledgerDate("2026-10-20T08:00:00Z"),
                sessionId: "private-a", inputTokens: 1000, cacheReadTokens: 9000, outputTokens: 100, reasoningTokens: 20),
            TokenUsageEvent(id: "b", provider: .codex, model: "gpt-6.1-sol", timestamp: ledgerDate("2026-10-21T10:00:00Z"),
                sessionId: "private-b", inputTokens: 2000, cacheWriteTokens: 500, cacheReadTokens: 4000, outputTokens: 200, reasoningTokens: 30)
        ], observations: [QuotaObservation(observedAt: ledgerDate("2026-10-21T09:00:00Z"), windowRole: "secondary",
            windowMinutes: 10080, resetsAt: ledgerDate("2026-10-27T07:07:27Z"), usedPercent: 100)],
            scannedAt: now, sourceAvailable: true)
    }

    @Test @MainActor
    func ledgerRefreshAndCustomRangeUseIndependentFrozenSnapshots() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let clock = now
        let model = LedgerViewModel(directory: directory, clock: { clock })
        model.receive(input)
        try await waitForLedger { !model.isLoading }
        #expect(model.state.current?.totals.totalTokens == 16800)
        #expect(abs((model.state.current?.totals.estimatedUSD ?? 0) - 0.0159) < 1e-10)
        let start = ledgerDate("2026-10-20T07:07:27Z")
        model.generateCustom(start: start, end: ledgerDate("2026-10-21T10:00:00Z"))
        try await waitForLedger { model.customReport != nil }
        #expect(model.customReport?.totals.totalTokens == 10100)
        #expect(model.customReport?.quotaUsedPercent == nil)
        #expect(model.state.current?.totals.totalTokens == 16800)
        model.generateCustom(start: now, end: start)
        try await waitForLedger { model.message != nil }
        #expect(model.customReport == nil)
        #expect(model.state.current?.totals.totalTokens == 16800)
    }

    @Test @MainActor
    func laterUnavailableSourceCannotBeReplacedByAnEarlierRefresh() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let clock = now
        let model = LedgerViewModel(directory: directory, clock: { clock })
        model.receive(input)
        model.receive(.empty(scannedAt: now.addingTimeInterval(1)))
        try await waitForLedger { !model.isLoading }
        #expect(!model.state.sourceAvailable)
        #expect(model.state.current == nil)
        #expect(model.state.warning != nil)
    }
}
