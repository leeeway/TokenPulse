import Foundation

/// Stores only frozen aggregate snapshots. Source logs are never copied or removed.
public final class CodexLedgerStore: @unchecked Sendable {
    private struct Envelope: Codable {
        var schemaVersion = 1
        var clearedAt: Date?
        var reports: [UsageReportSnapshot] = []
    }
    public let directory: URL
    public var fileURL: URL { directory.appendingPathComponent("ledger.json") }
    private let lock = NSLock()
    private var envelope = Envelope()
    private var loadError: String?
    private var lastSignature: Data?
    private var lastInput = CodexLedgerInput.empty()
    private var lastState = LedgerViewState()

    public init(directory: URL? = nil) {
        self.directory = directory ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("TokenPulse/ledger", isDirectory: true)
        if FileManager.default.fileExists(atPath: fileURL.path) {
            do {
                let data = try Data(contentsOf: fileURL)
                let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
                envelope = try decoder.decode(Envelope.self, from: data)
                guard envelope.schemaVersion == 1, envelope.reports.allSatisfy(Self.validStoredReport),
                      Set(envelope.reports.map(\.id)).count == envelope.reports.count else {
                    throw CocoaError(.fileReadCorruptFile)
                }
                lastSignature = try Self.signature(envelope)
            } catch {
                envelope = Envelope()
                loadError = "本地账本损坏或版本不支持，原文件已保留，自动写入暂停。可从现存日志显式重建并保留损坏文件备份。"
            }
        }
    }

    public func update(input: CodexLedgerInput, now: Date = Date(), timeZone: TimeZone = .current) -> LedgerViewState {
        lock.withLock {
            if let old = lastInput.scannedAt, let new = input.scannedAt, new < old { return lastState }
            lastInput = input
            return updateLocked(input: input, now: now, timeZone: timeZone)
        }
    }

    public func clearHistory(now: Date = Date(), timeZone: TimeZone = .current) -> LedgerViewState {
        lock.withLock {
            guard loadError == nil else { return stateLocked(input: lastInput, now: now, timeZone: timeZone, warning: loadError) }
            envelope.clearedAt = now
            envelope.reports.removeAll { $0.endAt <= now }
            return updateLocked(input: lastInput, now: now, timeZone: timeZone)
        }
    }

    public func rebuild(input: CodexLedgerInput, now: Date = Date(), timeZone: TimeZone = .current) -> LedgerViewState {
        lock.withLock {
            guard input.sourceAvailable, input.observations.contains(where: { $0.isValidWeeklyObservation && $0.observedAt <= now }) else {
                return stateLocked(input: input, now: now, timeZone: timeZone, warning: "现存日志没有可重建的可信周配额记录，未替换已有账本。")
            }
            if loadError != nil {
                do {
                    let backup = directory.appendingPathComponent("ledger.json.corrupt-" + UUID().uuidString)
                    try FileManager.default.copyItem(at: fileURL, to: backup)
                } catch {
                    return stateLocked(input: input, now: now, timeZone: timeZone, warning: "无法备份损坏账本，未执行重建：\(error.localizedDescription)")
                }
            }
            loadError = nil; envelope = Envelope(); lastSignature = nil; lastInput = input
            return updateLocked(input: input, now: now, timeZone: timeZone)
        }
    }

    private func updateLocked(input: CodexLedgerInput, now: Date, timeZone: TimeZone) -> LedgerViewState {
        guard loadError == nil else { return stateLocked(input: input, now: now, timeZone: timeZone, warning: loadError) }
        let retainAfter = now.addingTimeInterval(-90 * 86400)
        var entries = Dictionary(uniqueKeysWithValues: envelope.reports.filter { $0.endAt > retainAfter }.map { ($0.id, $0) })
        // Closed summaries are immutable until an explicit rebuild. Late log removal cannot erase them.
        let candidates = CodexLedgerEngine.reports(input: input, now: now, timeZone: timeZone)
        for var report in candidates where report.endAt > retainAfter {
            if report.endAt <= (envelope.clearedAt ?? .distantPast) { continue }
            if let saved = entries[report.id], saved.archived { continue }
            report.archived = report.endAt <= now
            entries[report.id] = report
        }
        for (key, var report) in entries where report.endAt <= now && !report.archived {
            report.archived = true
            report.warnings = Array(Set(report.warnings + ["周期已结束，保留最近一次可读取的本机数字摘要；不保证覆盖整个账号或全部周期。"])).sorted()
            entries[key] = report
        }
        envelope.reports = entries.values.sorted { $0.id < $1.id }
        var warning: String?
        do { try persistIfChanged() }
        catch { warning = "本地账本保存失败，当前统计仍可查看：\(error.localizedDescription)" }
        return stateLocked(input: input, now: now, timeZone: timeZone, warning: warning)
    }

    private func stateLocked(input: CodexLedgerInput, now: Date, timeZone: TimeZone, warning: String?) -> LedgerViewState {
        var warnings = input.warnings
        if let warning { warnings.append(warning) }
        if !input.sourceAvailable { warnings.append("Codex 原生日志不可用，本轮统计未更新；历史本地归档继续保留。") }
        let current = CodexLedgerEngine.currentReport(input: input, now: now, timeZone: timeZone)
        let history = envelope.reports.filter { $0.endAt <= now && $0.endAt > now.addingTimeInterval(-90 * 86400) }
            .sorted { $0.endAt > $1.endAt }
        lastState = LedgerViewState(current: current, history: history,
            warning: warnings.isEmpty ? nil : Array(Set(warnings)).sorted().joined(separator: "\n"), sourceAvailable: input.sourceAvailable)
        return lastState
    }

    private func persistIfChanged() throws {
        let signature = try Self.signature(envelope)
        guard signature != lastSignature else { return }
        let fm = FileManager.default
        try fm.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601; encoder.outputFormatting = [.sortedKeys, .prettyPrinted]
        let data = try encoder.encode(envelope)
        try data.write(to: fileURL, options: .atomic)
        try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: fileURL.path)
        lastSignature = signature
    }

    private static func signature(_ value: Envelope) throws -> Data {
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(value)
        var object = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        var reports = object["reports"] as? [[String: Any]] ?? []
        for index in reports.indices {
            reports[index].removeValue(forKey: "generatedAt")
            reports[index].removeValue(forKey: "cutoffAt")
        }
        object["reports"] = reports
        return try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
    }

    private static func validStoredReport(_ value: UsageReportSnapshot) -> Bool {
        value.kind == .weekly && value.startAt < value.endAt && value.cutoffAt >= value.startAt && value.cutoffAt <= value.endAt
            && value.totals.inputTokens >= 0 && value.totals.cacheWriteTokens >= 0 && value.totals.cacheReadTokens >= 0
            && value.totals.outputTokens >= 0 && value.totals.reasoningTokens >= 0 && value.totals.sessions >= 0 && value.totals.events >= 0
            && value.totals.estimatedUSD.isFinite && value.totals.estimatedUSD >= 0
            && value.totals.cacheSavedUSD.isFinite && value.totals.cacheSavedUSD >= 0
    }
}
