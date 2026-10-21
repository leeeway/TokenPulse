import Foundation
import Testing
import TokenPulseCore
@testable import TokenPulseApp

private enum WaitFailure: Error { case timedOut }

@MainActor
private func eventually(_ condition: () -> Bool) async throws {
    for _ in 0..<1500 {
        if condition() { return }
        try await Task.sleep(for: .milliseconds(2))
    }
    throw WaitFailure.timedOut
}

private actor ModelGate {
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    func wait() async {
        if released { return }
        await withCheckedContinuation { continuation = $0 }
    }
    func release() { released = true; continuation?.resume(); continuation = nil }
}

private final class ControlledCollector: DashboardCollecting, @unchecked Sendable {
    let now = Date(timeIntervalSince1970: 1792540800)
    let lock = NSLock()
    let scanGate: DispatchSemaphore?
    let sevenDayGate: DispatchSemaphore?
    let modelGate: ModelGate?
    let sourceWarnings: [String]
    private var scanned = false
    private var sevenStarted = false
    private var sevenFinished = false
    var lastScanAt: Date? { lock.withLock { scanned ? now : nil } }
    var ollamaStatus: String { "Ollama 测试模式" }
    var hasStartedSeven: Bool { lock.withLock { sevenStarted } }
    var hasFinishedSeven: Bool { lock.withLock { sevenFinished } }

    init(scanGate: DispatchSemaphore? = nil, sevenDayGate: DispatchSemaphore? = nil,
         modelGate: ModelGate? = nil, warnings: [String] = []) {
        self.scanGate = scanGate; self.sevenDayGate = sevenDayGate; self.modelGate = modelGate
        sourceWarnings = warnings
    }
    func refreshLogCache(now: Date) {
        _ = scanGate?.wait(timeout: .now() + 5)
        lock.withLock { scanned = true }
    }
    func snapshotFromCache(window: TimeWindow, now: Date) -> DashboardSnapshot {
        if window == .sevenDays, let sevenDayGate {
            lock.withLock { sevenStarted = true }
            _ = sevenDayGate.wait(timeout: .now() + 5)
            lock.withLock { sevenFinished = true }
        }
        return makeSnapshot(window: window, now: now)
    }
    private func makeSnapshot(window: TimeWindow, now: Date) -> DashboardSnapshot {
        let event = TokenUsageEvent(id: "test", provider: .codex, model: "test", timestamp: now,
                                    sessionId: "test", inputTokens: 100, outputTokens: 10)
        return UsageCollector.buildSnapshot(allEvents: [event], rateLimits: [:], runningLocalModels: [], window: window, now: now)
    }
    func refreshOllamaModels() async -> [LocalRunningModel] { await modelGate?.wait(); return [] }
}

@Suite("Mac dashboard refresh and preferences")
struct AppViewModelTests {
    @Test @MainActor
    func latePeriodCompletionCannotReplaceLatestSelection() async throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let gate = DispatchSemaphore(value: 0)
        defer { gate.signal() }
        let collector = ControlledCollector(sevenDayGate: gate)
        let model = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false, clock: { collector.now })
        model.selectedWindow = .sevenDays
        try await eventually { collector.hasStartedSeven }
        model.selectedWindow = .thirtyDays
        model.selectedWindow = .allTime
        try await eventually { model.snapshot.window == .allTime }
        gate.signal()
        try await eventually { collector.hasFinishedSeven }
        try await Task.sleep(for: .milliseconds(30))
        #expect(model.snapshot.window == .allTime)
        #expect(model.selectedWindow == .allTime)
    }

    @Test @MainActor
    func scanFinishingAfterSelectionUsesCurrentPeriod() async throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let gate = DispatchSemaphore(value: 0)
        defer { gate.signal() }
        let collector = ControlledCollector(scanGate: gate)
        let model = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false, clock: { collector.now })
        model.refreshNow()
        model.selectedWindow = .thirtyDays
        gate.signal()
        try await eventually { !model.isRefreshing && model.lastScanAt != nil && model.snapshot.window == .thirtyDays }
        #expect(model.selectedWindow == .thirtyDays)
    }

    @Test @MainActor
    func slowOllamaDoesNotDelayLogsAndStaleStateIsVisible() async throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let gate = ModelGate()
        let collector = ControlledCollector(modelGate: gate, warnings: ["测试目录不可读取"])
        let model = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false, clock: { collector.now })
        #expect(model.dataStatus(at: collector.now) == "读取中")
        model.refreshNow()
        do {
            try await eventually { model.lastScanAt != nil && model.snapshot.grandTotalTokens == 110 }
            #expect(model.isRefreshing)
            #expect(model.dataStatus(at: collector.now) == "需检查")
            #expect(model.dataStatus(at: collector.now.addingTimeInterval(31)) == "待刷新")
            #expect(model.statusHelp.contains("测试目录不可读取"))
        } catch { await gate.release(); throw error }
        await gate.release()
        try await eventually { !model.isRefreshing }
    }

    @Test @MainActor
    func preferencesRestoreAndInvalidStoredValuesFallBack() throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let collector = ControlledCollector()
        let first = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false)
        first.selectedWindow = .allTime
        first.displayMode = .costOnly
        let restored = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false)
        #expect(restored.selectedWindow == .allTime)
        #expect(restored.displayMode == .costOnly)
        defaults.set("invalid", forKey: "selectedWindow")
        defaults.set("invalid", forKey: "displayMode")
        let fallback = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false)
        #expect(fallback.selectedWindow == .today)
        #expect(fallback.displayMode == .iconOnly)
    }

    @Test @MainActor
    func compactMenuBarUsesOnlyAnIconAndKeepsTextModesAvailable() throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let model = AppViewModel(collector: ControlledCollector(), defaults: defaults, startsAutomatically: false)
        #expect(model.displayMode == .iconOnly)
        #expect(model.menuBarText.isEmpty)
        model.displayMode = .tokensAndCost
        #expect(!model.menuBarText.isEmpty)
        model.displayMode = .iconOnly
        let restored = AppViewModel(collector: ControlledCollector(), defaults: defaults, startsAutomatically: false)
        #expect(restored.displayMode == .iconOnly)
        #expect(restored.menuBarText.isEmpty)
    }
}
