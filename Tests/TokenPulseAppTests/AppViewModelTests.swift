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

private final class QuotaTestClock: @unchecked Sendable {
    private var value: Date
    private let lock = NSLock()
    init(_ value: Date) { self.value = value }
    var now: Date { lock.withLock { value } }
    func set(_ newValue: Date) { lock.withLock { value = newValue } }
}

private final class ResetRecorder: @unchecked Sendable {
    private var values: [Date] = []
    private let lock = NSLock()
    var deadlines: [Date] { lock.withLock { values } }
    func record(_ time: Date) { lock.withLock { values.append(time) } }
}

private final class ControlledCollector: DashboardCollecting, @unchecked Sendable {
    let now = Date(timeIntervalSince1970: 1792540800)
    let lock = NSLock()
    let scanGate: DispatchSemaphore?
    let sevenDayGate: DispatchSemaphore?
    let modelGate: ModelGate?
    let sourceWarnings: [String]
    private var scans = 0
    private var input: CodexLedgerInput?
    func setLedgerInput(_ readings: CodexLedgerInput) { lock.withLock { input = readings } }
    func ledgerInputForApp() -> CodexLedgerInput? { lock.withLock { input } }
    private var pollForces: [Bool] = []
    var scanCount: Int { lock.withLock { scans } }
    var requestedPollForces: [Bool] { lock.withLock { pollForces } }
    private var scanned = false
    private var renders = 0
    var renderCount: Int { lock.withLock { renders } }
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
        lock.withLock { scans += 1 }
        _ = scanGate?.wait(timeout: .now() + 5)
        lock.withLock { scanned = true }
    }
    func snapshotFromCache(window: TimeWindow, now: Date) -> DashboardSnapshot {
        lock.withLock { renders += 1 }
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
    func refreshOllamaModelsIfDue(now: Date, force: Bool) async -> [LocalRunningModel] {
        lock.withLock { pollForces.append(force) }
        return await refreshOllamaModels()
    }
}

@Suite("Mac dashboard refresh and preferences")
struct AppViewModelTests {
    private struct SlowSource: ProviderAdapter {
        let provider = AIProvider.claudeCode
        let started: DispatchSemaphore
        let resume: DispatchSemaphore
        func scan() -> AdapterScanResult {
            started.signal()
            _ = resume.wait(timeout: .now() + 5)
            return AdapterScanResult(provider: provider, events: [])
        }
    }

    @Test @MainActor
    func menuBarQuotaPublishesBeforeAnotherProviderFinishesScanning() async throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let logs = root.appendingPathComponent("codex")
        try FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let now = Date(timeIntervalSince1970: 1792540800)
        let stamp = ISO8601DateFormatter().string(from: now.addingTimeInterval(-1))
        let reset = Int(now.addingTimeInterval(3600).timeIntervalSince1970)
        let row = #"{"timestamp":"\#(stamp)","payload":{"type":"token_count","rate_limits":{"primary":{"window_minutes":300,"resets_at":\#(reset),"used_percent":25}}}}"#
        try (row + "\n").write(to: logs.appendingPathComponent("quota.jsonl"), atomically: true, encoding: .utf8)
        let started = DispatchSemaphore(value: 0)
        let resume = DispatchSemaphore(value: 0)
        defer { resume.signal() }
        let collector = UsageCollector(adapters: [SlowSource(started: started, resume: resume), CodexAdapter(baseDirectory: logs)],
                                       customEventsURL: root.appendingPathComponent("custom.jsonl"))
        let model = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false,
                                 clock: { now }, ledgerDirectory: root.appendingPathComponent("ledger"))
        model.refreshAutomatically()
        try await eventually { started.wait(timeout: .now()) == .success }
        try await eventually { model.lowestQuota?.remainingPercent == 75 }
        #expect(model.isRefreshing && model.lastScanAt == nil)
        resume.signal()
        try await eventually { !model.isRefreshing && model.lastScanAt != nil }
    }

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
            #expect(model.dataStatus(at: collector.now.addingTimeInterval(76)) == "待刷新")
            #expect(model.statusHelp.contains("测试目录不可读取"))
        } catch { await gate.release(); throw error }
        await gate.release()
        try await eventually { !model.isRefreshing }
    }

    @Test @MainActor
    func unchangedOllamaDoesNotRepeatTheDashboardAggregation() async throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let gate = ModelGate()
        let collector = ControlledCollector(modelGate: gate)
        let model = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false, clock: { collector.now })
        model.refreshNow()
        do {
            try await eventually { model.lastScanAt != nil && model.snapshot.grandTotalTokens == 110 }
            #expect(collector.renderCount == 1)
        } catch { await gate.release(); throw error }
        await gate.release()
        try await eventually { !model.isRefreshing }
        #expect(collector.renderCount == 1)
    }

    @Test @MainActor
    func refreshDuringScanQueuesOneFollowUpAndManualActionsBypassBackoff() async throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let gate = DispatchSemaphore(value: 0)
        defer { gate.signal(); gate.signal() }
        let collector = ControlledCollector(scanGate: gate)
        let model = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false,
            clock: { collector.now }, eventMinimumInterval: 0)
        model.refreshAutomatically()
        try await eventually { collector.scanCount == 1 }
        for _ in 0..<20 { model.refreshAutomatically() }
        model.refreshNow()
        gate.signal()
        try await eventually { collector.scanCount == 2 }
        gate.signal()
        try await eventually { !model.isRefreshing }
        #expect(collector.scanCount == 2)
        #expect(collector.requestedPollForces == [false, true])
        model.dashboardVisibilityChanged(false)
        #expect(collector.scanCount == 2)
        model.dashboardVisibilityChanged(true)
        try await eventually { collector.scanCount == 3 }
        gate.signal()
        try await eventually { !model.isRefreshing }
        #expect(collector.requestedPollForces.last == true)
    }

    @Test @MainActor
    func fileEventBurstProducesOneRefresh() async throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let collector = ControlledCollector()
        let model = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false,
            clock: { collector.now }, eventMinimumInterval: 0)
        for _ in 0..<30 { model.logsDidChange() }
        try await eventually { collector.scanCount == 1 && !model.isRefreshing }
        #expect(collector.requestedPollForces == [false])
    }

    @Test @MainActor
    func resetTimerFiresTheLatestDeadlineOnlyOnce() async throws {
        let recorder = ResetRecorder()
        let driver = BackgroundRefreshDriver(directories: [], refresh: { trigger in
            if case let .quotaReset(deadline) = trigger { recorder.record(deadline) }
        }, render: {})
        let now = Date()
        let deadline = now.addingTimeInterval(0.08)
        driver.scheduleReset(at: now.addingTimeInterval(0.03))
        driver.scheduleReset(at: deadline)
        try await eventually { !recorder.deadlines.isEmpty }
        try await Task.sleep(for: .milliseconds(100))
        #expect(recorder.deadlines == [deadline])
        withExtendedLifetime(driver) {}
    }

    @Test @MainActor
    func quotaDeadlineHidesStaleBalanceBeforeScanAndNotificationsDeduplicateAcrossRestart() async throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let collector = ControlledCollector()
        let clock = QuotaTestClock(collector.now)
        let end = clock.now.addingTimeInterval(60)
        let secondEnd = end.addingTimeInterval(604800)
        var delivered: [Double] = []
        func model() -> AppViewModel {
            AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false,
                clock: { clock.now }, ledgerDirectory: directory,
                notificationSink: { observations, _ in delivered += observations.map(\.usedPercent) })
        }
        func use(_ percent: Double, until deadline: Date) {
            let now = clock.now
            let observation = QuotaObservation(observedAt: now, windowRole: "secondary", windowMinutes: 10080,
                resetsAt: deadline, usedPercent: percent)
            collector.setLedgerInput(CodexLedgerInput(observations: [observation], scannedAt: now, sourceAvailable: true))
        }
        use(80, until: end)
        let silent = model()
        #expect(!silent.quotaNotificationsEnabled)
        silent.refreshAutomatically()
        try await eventually { silent.ledger.currentReport?.quotaUsedPercent == 80 && !silent.isRefreshing }
        #expect(delivered.isEmpty)

        defaults.set(true, forKey: "quotaNotificationsEnabled")
        let active = model()
        active.refreshAutomatically()
        try await eventually { active.ledger.currentReport?.quotaUsedPercent == 80 && !active.isRefreshing }
        #expect(delivered == [80])
        #expect(active.lowestQuota?.isLow == true)

        clock.set(clock.now.addingTimeInterval(1))
        use(100, until: end)
        let reopened = model()
        reopened.refreshAutomatically()
        try await eventually { reopened.ledger.currentReport?.quotaUsedPercent == 100 && !reopened.isRefreshing }
        #expect(delivered == [80])

        let scans = collector.scanCount
        clock.set(end)
        reopened.quotaDeadlineReached(at: end)
        reopened.quotaDeadlineReached(at: end)
        #expect(reopened.lowestQuota == nil)
        #expect(reopened.ledger.currentReport == nil)
        #expect(reopened.quotaHelp == "Codex 配额待更新")
        try await eventually { !reopened.isRefreshing }
        #expect(collector.scanCount == scans + 1)
        #expect(reopened.ledger.currentReport == nil)
        #expect(delivered == [80])

        clock.set(end.addingTimeInterval(1))
        use(5, until: secondEnd)
        reopened.refreshAutomatically()
        try await eventually { reopened.ledger.currentReport?.quotaUsedPercent == 5 && !reopened.isRefreshing }
        #expect(reopened.lowestQuota?.remainingPercent == 95)

        clock.set(end.addingTimeInterval(2))
        use(95, until: secondEnd)
        reopened.refreshAutomatically()
        try await eventually { reopened.ledger.currentReport?.quotaUsedPercent == 95 && !reopened.isRefreshing }
        #expect(delivered == [80, 95])
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
        #expect(fallback.displayMode == .quotaOnly)
    }

    @Test @MainActor
    func compactMenuBarShowsLowestActiveQuotaAndKeepsOtherModesAvailable() async throws {
        let suite = "TokenPulse.Tests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let collector = ControlledCollector()
        let clock = QuotaTestClock(collector.now)
        let model = AppViewModel(collector: collector, defaults: defaults, startsAutomatically: false,
                                 clock: { clock.now }, ledgerDirectory: directory)
        #expect(model.displayMode == .quotaOnly)
        #expect(model.menuBarText.isEmpty)
        let now = clock.now
        let shortReset = now.addingTimeInterval(60)
        let weeklyReset = now.addingTimeInterval(120)
        collector.setLedgerInput(CodexLedgerInput(observations: [
            QuotaObservation(observedAt: now, windowRole: "primary", windowMinutes: 300,
                             resetsAt: shortReset, usedPercent: 80),
            QuotaObservation(observedAt: now, windowRole: "secondary", windowMinutes: 10080,
                             resetsAt: weeklyReset, usedPercent: 55)
        ], scannedAt: now, sourceAvailable: true))
        model.refreshAutomatically()
        try await eventually { model.codexQuotas.count == 2 && !model.isRefreshing }
        #expect(model.menuBarText == QuotaIndicator(usedPercent: 80, expired: false).percentText)
        #expect(model.lowestQuota?.isLow == true)
        clock.set(shortReset)
        model.quotaDeadlineReached(at: shortReset)
        #expect(model.menuBarText == QuotaIndicator(usedPercent: 55, expired: false).percentText)
        #expect(model.lowestQuota?.isLow == false)
        try await eventually { !model.isRefreshing }
        clock.set(weeklyReset)
        model.quotaDeadlineReached(at: weeklyReset)
        #expect(model.menuBarText.isEmpty)
        #expect(model.quotaHelp == "Codex 配额待更新")
        try await eventually { !model.isRefreshing }
        model.displayMode = .tokensAndCost
        #expect(!model.menuBarText.isEmpty)
        model.displayMode = .iconOnly
        let restored = AppViewModel(collector: ControlledCollector(), defaults: defaults, startsAutomatically: false)
        #expect(restored.displayMode == .iconOnly)
        #expect(restored.menuBarText.isEmpty)
    }
}
