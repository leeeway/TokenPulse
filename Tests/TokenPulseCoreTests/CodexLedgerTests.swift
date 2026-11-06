import Foundation
import Testing
@testable import TokenPulseCore

private func instant(_ value: String) -> Date { ISO8601DateFormatter().date(from: value)! }
private let cycleStart = instant("2026-10-20T07:07:27Z")
private let cycleEnd = instant("2026-10-27T07:07:27Z")
private let reportNow = instant("2026-10-21T15:41:03Z")

private struct LedgerTestDirectory {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("TokenPulse-ledger-" + UUID().uuidString, isDirectory: true)
    init() throws { try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true) }
    func remove() { try? FileManager.default.removeItem(at: url) }
}
private func quota(at: Date = instant("2026-10-21T09:00:00Z"), end: Date = cycleEnd, percent: Double = 100, role: String = "secondary", plan: String? = "team") -> QuotaObservation {
    QuotaObservation(observedAt: at, windowRole: role, windowMinutes: 10080, resetsAt: end, usedPercent: percent, planType: plan)
}
private func fixtureInput(now: Date = reportNow, available: Bool = true) -> CodexLedgerInput {
    let events = [
        TokenUsageEvent(id: "A", provider: .codex, model: "gpt-6.1-sol", timestamp: instant("2026-10-20T08:00:00Z"),
                        sessionId: "private-session-A", inputTokens: 1000, cacheReadTokens: 9000, outputTokens: 100, reasoningTokens: 20),
        TokenUsageEvent(id: "B", provider: .codex, model: "codex-auto-review", timestamp: instant("2026-10-21T10:00:00Z"),
                        sessionId: "private-session-B", inputTokens: 2000, cacheWriteTokens: 500, cacheReadTokens: 4000, outputTokens: 200, reasoningTokens: 30),
        TokenUsageEvent(id: "before", provider: .codex, model: "gpt-6.1-sol", timestamp: cycleStart.addingTimeInterval(-1),
                        sessionId: "outside", inputTokens: 900000, outputTokens: 0),
        TokenUsageEvent(id: "at-end", provider: .codex, model: "gpt-6.1-sol", timestamp: cycleEnd,
                        sessionId: "outside", inputTokens: 900000, outputTokens: 0),
        TokenUsageEvent(id: "undated", provider: .codex, model: "gpt-6.1-sol", timestamp: reportNow,
                        sessionId: "unknown", inputTokens: 900000, outputTokens: 0, hasKnownTimestamp: false)
    ]
    return CodexLedgerInput(events: available ? events : [], observations: available ? [quota()] : [], scannedAt: now, sourceAvailable: available)
}
private func quotaLine(timestamp: String? = "2026-10-21T09:00:00Z", role: String = "secondary", used: Any? = 100,
                       minutes: Any? = 10080, epoch: Any? = cycleEnd.timeIntervalSince1970) throws -> String {
    var window: [String: Any] = [:]
    if let used { window["used_percent"] = used }
    if let minutes { window["window_minutes"] = minutes }
    if let epoch { window["resets_at"] = epoch }
    var root: [String: Any] = ["payload": ["type": "token_count", "rate_limits": [role: window, "plan_type": "team"]]]
    if let timestamp { root["timestamp"] = timestamp }
    return String(data: try JSONSerialization.data(withJSONObject: root), encoding: .utf8)! + "\n"
}

@Suite("Codex quota ledger and reports")
struct CodexLedgerTests {
    @Test func equivalentCrossPlatformFixtureIncludesUsageAfterOneHundredPercent() throws {
        let report = try #require(CodexLedgerEngine.currentReport(input: fixtureInput(), now: reportNow, timeZone: TimeZone(secondsFromGMT: 0)!))
        #expect(report.startAt == cycleStart)
        #expect(report.endAt == cycleEnd)
        #expect(report.cutoffAt == reportNow)
        #expect(report.totals.totalTokens == 16800)
        #expect(report.totals.inputTokens == 3000)
        #expect(report.totals.cacheWriteTokens == 500)
        #expect(report.totals.cacheReadTokens == 13000)
        #expect(report.totals.outputTokens == 300)
        #expect(report.totals.reasoningTokens == 50)
        #expect(report.totals.sessions == 2 && report.totals.events == 2)
        #expect(abs(report.totals.estimatedUSD - 0.0159) < 1e-12)
        #expect(abs(report.totals.cacheSavedUSD - 0.0195) < 1e-12)
        #expect(report.quotaUsedPercent == 100)
        #expect(report.models.count == 2 && report.days.count == 2)
        #expect(report.unknownTimestampEvents == 1)
        #expect(report.cycleStartInferred && report.pricingVersion == "compat-v1")
    }

    @Test func strictQuotaNeedsEveryOriginalFieldAndAcceptsEitherWindowRole() throws {
        let temp = try LedgerTestDirectory(); defer { temp.remove() }
        let file = temp.url.appendingPathComponent("quota.jsonl")
        let lines = try quotaLine(role: "primary") + quotaLine(role: "secondary")
            + quotaLine(timestamp: nil) + quotaLine(timestamp: "broken") + quotaLine(used: nil)
            + quotaLine(used: true) + quotaLine(used: -1) + quotaLine(used: 101)
            + quotaLine(minutes: nil) + quotaLine(minutes: 300) + quotaLine(epoch: nil)
            + quotaLine(epoch: -1) + quotaLine(timestamp: "2026-10-28T09:00:00Z")
        try lines.write(to: file, atomically: true, encoding: .utf8)
        let result = CodexAdapter.parseDetailedRolloutFile(at: file, fallbackDate: reportNow)
        #expect(result.observations.count == 2)
        #expect(Set(result.observations.map(\.windowRole)) == Set(["primary", "secondary"]))
        #expect(result.events.isEmpty)
        let oldAPI = CodexAdapter.parseRolloutFile(at: file)
        #expect(oldAPI.events.isEmpty)
        #expect(oldAPI.rateLimit != nil)
    }

    @Test func currentCycleDoesNotInventResetAndConflictsRemainVisible() throws {
        let ended = CodexLedgerInput(observations: [quota()], sourceAvailable: true)
        #expect(CodexLedgerEngine.currentReport(input: ended, now: cycleEnd.addingTimeInterval(1)) == nil)
        let future = quota(at: reportNow.addingTimeInterval(100))
        #expect(CodexLedgerEngine.currentReport(input: CodexLedgerInput(observations: [future], sourceAvailable: true), now: reportNow) == nil)
        let input = CodexLedgerInput(events: fixtureInput().events,
            observations: [quota(percent: 98, plan: "pro"), quota(), quota(end: cycleEnd.addingTimeInterval(3600), percent: 50)], sourceAvailable: true)
        let reports = CodexLedgerEngine.reports(input: input, now: reportNow)
        #expect(reports.count == 2)
        #expect(reports.allSatisfy { $0.warnings.contains(where: { $0.contains("重叠") }) })
        #expect(reports.contains { $0.warnings.contains(where: { $0.contains("多个套餐") }) })
    }

    @Test func customRangeIsHalfOpenAndUsesFrozenLocalDayAcrossDST() throws {
        let zone = try #require(TimeZone(identifier: "America/New_York"))
        let start = instant("2026-11-01T05:00:00Z")
        let end = instant("2026-11-01T07:00:00Z")
        func event(_ id: String, _ stamp: Date) -> TokenUsageEvent {
            TokenUsageEvent(id: id, provider: .codex, model: "gpt-5", timestamp: stamp, sessionId: id, inputTokens: 1, outputTokens: 0)
        }
        let input = CodexLedgerInput(events: [event("first-fold", start), event("second-fold", start.addingTimeInterval(3600)), event("end", end)], sourceAvailable: true)
        let report = try CodexLedgerEngine.customReport(input: input, start: start, end: end, now: end.addingTimeInterval(1), timeZone: zone)
        #expect(report.totals.totalTokens == 2 && report.days.count == 1)
        #expect(report.days.first?.dateLabel == "2026-11-01")
        #expect(report.quotaUsedPercent == nil && !report.cycleStartInferred)
        #expect(throws: LedgerReportError.self) { try CodexLedgerEngine.customReport(input: input, start: end, end: start) }
        #expect(throws: LedgerReportError.self) { try CodexLedgerEngine.customReport(input: .empty(), start: start, end: end) }
    }

    @Test func collectorSeparatesNativeCodexFromCustomCodexAndReusesDetailedCache() throws {
        let temp = try LedgerTestDirectory(); defer { temp.remove() }
        let native = temp.url.appendingPathComponent("native"); try FileManager.default.createDirectory(at: native, withIntermediateDirectories: true)
        let file = native.appendingPathComponent("test.jsonl")
        let log = try quotaLine() + "{\"timestamp\":\"2026-10-21T10:00:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"output_tokens\":10}}}}\n"
        try log.write(to: file, atomically: true, encoding: .utf8)
        let custom = temp.url.appendingPathComponent("custom.jsonl")
        let extra = TokenUsageEvent(id: "manual", provider: .codex, model: "manual", timestamp: reportNow, sessionId: "custom", inputTokens: 999999, outputTokens: 0)
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
        var data = try encoder.encode(extra); data.append(0x0A); try data.write(to: custom)
        let adapter = CodexAdapter(baseDirectory: native)
        let collector = UsageCollector(adapters: [adapter], customEventsURL: custom)
        _ = collector.collectSnapshot(window: .allTime, now: reportNow)
        let input = collector.ledgerInputFromCache()
        #expect(input.events.reduce(0) { $0 + $1.totalTokens } == 110)
        #expect(input.observations.count == 1)
        #expect(collector.snapshotFromCache(window: .allTime).grandTotalTokens == 1000109)
        let cached = adapter.scan().ledgerInput
        #expect(cached?.observations.count == 1 && cached?.events.count == 1)
        try "".write(to: file, atomically: true, encoding: .utf8)
        #expect(adapter.scan().ledgerInput?.observations.isEmpty == true)
        try FileManager.default.removeItem(at: native)
        _ = collector.collectSnapshot(window: .today, now: reportNow.addingTimeInterval(1))
        #expect(!collector.ledgerInputFromCache().sourceAvailable)
    }

    @Test func historyFreezesNumbersSurvivesDeletedLogsAndDoesNotWriteForClockOnlyChanges() throws {
        let temp = try LedgerTestDirectory(); defer { temp.remove() }
        let store = CodexLedgerStore(directory: temp.url)
        _ = store.update(input: fixtureInput(), now: reportNow)
        let before = try Data(contentsOf: store.fileURL)
        _ = store.update(input: fixtureInput(now: reportNow.addingTimeInterval(10)), now: reportNow.addingTimeInterval(10))
        #expect(try Data(contentsOf: store.fileURL) == before)
        let closedAt = cycleEnd.addingTimeInterval(1)
        let closed = store.update(input: fixtureInput(now: closedAt), now: closedAt)
        #expect(closed.current == nil && closed.history.count == 1)
        #expect(closed.history[0].archived && closed.history[0].cutoffAt == cycleEnd)
        let sourceGone = store.update(input: fixtureInput(now: closedAt.addingTimeInterval(1), available: false), now: closedAt.addingTimeInterval(1))
        #expect(sourceGone.history[0].totals.totalTokens == 16800)
        #expect(sourceGone.warning?.contains("不可用") == true)
        let persisted = try String(contentsOf: store.fileURL, encoding: .utf8)
        #expect(!persisted.contains("private-session") && !persisted.contains("sessionId") && !persisted.contains(temp.url.path))
        let loaded = CodexLedgerStore(directory: temp.url).update(input: .empty(scannedAt: closedAt), now: closedAt)
        #expect(loaded.history.count == 1 && loaded.history[0].totals.totalTokens == 16800)
    }

    @Test func clearHistoryStaysClearedAcrossRestartsUntilExplicitRebuildAndRetentionIsNinetyDays() throws {
        let temp = try LedgerTestDirectory(); defer { temp.remove() }
        let now = cycleEnd.addingTimeInterval(1)
        let input = fixtureInput(now: now)
        let store = CodexLedgerStore(directory: temp.url)
        #expect(store.update(input: input, now: now).history.count == 1)
        #expect(store.clearHistory(now: now).history.isEmpty)
        let reopened = CodexLedgerStore(directory: temp.url)
        #expect(reopened.update(input: input, now: now).history.isEmpty)
        #expect(reopened.rebuild(input: input, now: now).history.count == 1)
        let future = now.addingTimeInterval(91 * 86400)
        #expect(reopened.update(input: .empty(scannedAt: future), now: future).history.isEmpty)
    }

    @Test func corruptArchiveIsNotOverwrittenAndExplicitRebuildKeepsOriginalBackup() throws {
        let temp = try LedgerTestDirectory(); defer { temp.remove() }
        let file = temp.url.appendingPathComponent("ledger.json")
        let corrupt = Data("{broken archive".utf8); try corrupt.write(to: file)
        let store = CodexLedgerStore(directory: temp.url)
        let state = store.update(input: fixtureInput(), now: reportNow)
        #expect(state.warning?.contains("损坏") == true)
        #expect(try Data(contentsOf: file) == corrupt)
        _ = store.clearHistory(now: reportNow)
        #expect(try Data(contentsOf: file) == corrupt)
        let repaired = store.rebuild(input: fixtureInput(), now: reportNow)
        #expect(repaired.current?.totals.totalTokens == 16800)
        #expect(repaired.warning == nil)
        let backup = try #require(FileManager.default.contentsOfDirectory(at: temp.url, includingPropertiesForKeys: nil).first { $0.lastPathComponent.contains(".corrupt-") })
        #expect(try Data(contentsOf: backup) == corrupt)
    }

    @Test func exportEscapesUntrustedModelsAndWritesOneFrozenBundleWithoutOverwrites() throws {
        let temp = try LedgerTestDirectory(); defer { temp.remove() }
        let input = CodexLedgerInput(events: [TokenUsageEvent(id: "private-event", provider: .codex, model: "=SUM(A1) <script>&\"", timestamp: reportNow.addingTimeInterval(-1), sessionId: "private-session", inputTokens: 1000, outputTokens: 20)], sourceAvailable: true)
        let report = try CodexLedgerEngine.customReport(input: input, start: cycleStart, end: reportNow, now: reportNow)
        let html = ReportExporter.html(report: report)
        #expect(html.contains("&lt;script&gt;") && !html.contains("<script>"))
        #expect(html.contains("default-src 'none'") && html.contains("@media print"))
        #expect(!html.contains("private-session") && !html.contains("private-event"))
        let csv = ReportExporter.csv(report: report)
        #expect(csv.contains("\"'=SUM(A1) <script>&\"\"\""))
        #expect(csv.contains("compat-v1") && !csv.contains("private-session"))
        let first = try ReportExporter.export(report: report, to: temp.url)
        let before = try Data(contentsOf: first.appendingPathComponent("report.html"))
        let second = try ReportExporter.export(report: report, to: temp.url)
        let preserved = try Data(contentsOf: first.appendingPathComponent("report.html"))
        #expect(first != second && before == preserved)
        let bom = try Data(contentsOf: first.appendingPathComponent("usage.csv"))
        #expect(Array(bom.prefix(3)) == [0xEF, 0xBB, 0xBF])
        let marker = temp.url.appendingPathComponent("not-a-folder"); try Data("keep".utf8).write(to: marker)
        #expect(throws: (any Error).self) { try ReportExporter.export(report: report, to: marker) }
        #expect(try String(contentsOf: marker, encoding: .utf8) == "keep")
        #expect(try FileManager.default.contentsOfDirectory(atPath: temp.url.path).allSatisfy { !$0.hasPrefix(".tokenpulse-export-") })
    }

    @Test func emptyReportExportsHeadersAndComponentCostsFollowActualModelRates() throws {
        let empty = try CodexLedgerEngine.customReport(input: CodexLedgerInput(sourceAvailable: true), start: cycleStart, end: reportNow, now: reportNow)
        #expect(empty.totals.totalTokens == 0 && ReportExporter.csv(report: empty).split(separator: "\n").count == 1)
        #expect(ReportExporter.html(report: empty).contains("没有可确认时间"))
        let event = TokenUsageEvent(id: "a", provider: .codex, model: "opus", timestamp: reportNow.addingTimeInterval(-1), sessionId: "a", inputTokens: 1_000_000, outputTokens: 0)
        let report = try CodexLedgerEngine.customReport(input: CodexLedgerInput(events: [event], sourceAvailable: true), start: cycleStart, end: reportNow, now: reportNow)
        #expect(report.totals.inputCostUSD == 15 && report.totals.estimatedUSD == 15)
        #expect(ReportExporter.html(report: report).contains("$15.00"))
    }
}
