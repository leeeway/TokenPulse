import Foundation

/// A quota observation whose fields were explicitly present and valid in a native Codex log.
public struct QuotaObservation: Codable, Sendable, Equatable {
    public let observedAt: Date
    public let windowRole: String
    public let windowMinutes: Int
    public let resetsAt: Date
    public let usedPercent: Double
    public let planType: String?
    public init(observedAt: Date, windowRole: String, windowMinutes: Int, resetsAt: Date, usedPercent: Double, planType: String? = nil) {
        self.observedAt = observedAt; self.windowRole = windowRole; self.windowMinutes = windowMinutes
        self.resetsAt = resetsAt; self.usedPercent = usedPercent; self.planType = planType
    }
    public var inferredStart: Date { resetsAt.addingTimeInterval(-Double(windowMinutes) * 60) }
    public var isValidWeeklyObservation: Bool {
        windowMinutes == 10080 && usedPercent.isFinite && (0...100).contains(usedPercent)
            && observedAt.timeIntervalSince1970.isFinite && resetsAt.timeIntervalSince1970.isFinite
            && resetsAt.timeIntervalSince1970 > 0 && observedAt >= inferredStart && observedAt < resetsAt
    }
}

/// Captured before custom events are mixed into the normal dashboard totals.
public struct CodexLedgerInput: Sendable {
    public let events: [TokenUsageEvent]
    public let observations: [QuotaObservation]
    public let scannedAt: Date?
    public let sourceAvailable: Bool
    public let warnings: [String]
    public init(events: [TokenUsageEvent] = [], observations: [QuotaObservation] = [], scannedAt: Date? = nil,
                sourceAvailable: Bool = false, warnings: [String] = []) {
        self.events = events; self.observations = observations; self.scannedAt = scannedAt
        self.sourceAvailable = sourceAvailable; self.warnings = warnings
    }
    public static func empty(scannedAt: Date? = nil) -> Self { Self(scannedAt: scannedAt) }
}

public struct LedgerUsageTotals: Codable, Sendable, Equatable {
    public var inputTokens: Int = 0
    public var cacheWriteTokens: Int = 0
    public var cacheReadTokens: Int = 0
    public var outputTokens: Int = 0
    public var reasoningTokens: Int = 0
    public var inputCostUSD: Double = 0
    public var cacheWriteCostUSD: Double = 0
    public var cacheReadCostUSD: Double = 0
    public var outputCostUSD: Double = 0
    public var estimatedUSD: Double = 0
    public var cacheSavedUSD: Double = 0
    public var sessions: Int = 0
    public var events: Int = 0
    public var totalTokens: Int { inputTokens + cacheWriteTokens + cacheReadTokens + outputTokens }
    public var upstreamTokens: Int { inputTokens + cacheWriteTokens + cacheReadTokens }
    public var cacheHitRatio: Double { upstreamTokens > 0 ? Double(cacheReadTokens) / Double(upstreamTokens) : 0 }
    public init() { }
    static func build(_ items: [TokenUsageEvent]) -> Self {
        var result = Self()
        var sessions = Set<String>()
        for event in items {
            result.inputTokens += event.inputTokens; result.cacheWriteTokens += event.cacheWriteTokens
            result.cacheReadTokens += event.cacheReadTokens; result.outputTokens += event.outputTokens
            result.reasoningTokens += event.reasoningTokens; result.estimatedUSD += event.costUSD
            let rate = PricingEngine.rate(for: .codex, model: event.model)
            result.inputCostUSD += Double(event.inputTokens) * rate.inputPerMillion / 1e6
            result.cacheWriteCostUSD += Double(event.cacheWriteTokens) * rate.cacheWritePerMillion / 1e6
            result.cacheReadCostUSD += Double(event.cacheReadTokens) * rate.cacheReadPerMillion / 1e6
            result.outputCostUSD += Double(event.outputTokens) * rate.outputPerMillion / 1e6
            result.cacheSavedUSD += event.savedByCacheUSD; sessions.insert(event.sessionId)
        }
        result.sessions = sessions.count; result.events = items.count
        return result
    }
}

public struct LedgerModelSummary: Codable, Sendable, Equatable, Identifiable {
    public var id: String { model }
    public let model: String
    public let totals: LedgerUsageTotals
    public let pricingBasis: String
}
public struct LedgerDailySummary: Codable, Sendable, Equatable, Identifiable {
    public var id: String { dateLabel }
    public let dateLabel: String
    public let totals: LedgerUsageTotals
}
public enum UsageReportKind: String, Codable, Sendable { case weekly, custom }

/// Numbers only. No event IDs, session IDs, raw log paths or message text are serialised.
public struct UsageReportSnapshot: Codable, Sendable, Equatable, Identifiable {
    public let id: String
    public let kind: UsageReportKind
    public let startAt: Date
    public let endAt: Date
    public let cutoffAt: Date
    public let generatedAt: Date
    public let timeZoneIdentifier: String
    public let timeZoneOffsetSeconds: Int
    public let totals: LedgerUsageTotals
    public let models: [LedgerModelSummary]
    public let days: [LedgerDailySummary]
    public let quotaUsedPercent: Double?
    public let quotaRecordedAt: Date?
    public let nextResetAt: Date?
    public let planType: String?
    public let cycleStartInferred: Bool
    public let pricingVersion: String
    public let unknownTimestampEvents: Int
    public var warnings: [String]
    public var archived: Bool
    public var isEnded: Bool { archived || endAt <= generatedAt }
    public var quotaStatus: String {
        guard kind == .weekly else { return "自定义时间范围，配额未与此区间对应" }
        if isEnded { return "周期已结束，百分比为末次观测，等待新周期日志" }
        return quotaUsedPercent == nil ? "配额信息不足" : "日志中的周配额记录"
    }
}

public enum LedgerReportError: Error, LocalizedError {
    case invalidRange, sourceUnavailable
    public var errorDescription: String? {
        switch self {
        case .invalidRange: return "开始时间必须早于结束时间，且不能晚于统计时刻。"
        case .sourceUnavailable: return "Codex 原生日志暂不可用，请先检查数据源；不能将不可读日志当作零用量。"
        }
    }
}

public enum CodexLedgerEngine {
    public static func currentReport(input: CodexLedgerInput, now: Date = Date(), timeZone: TimeZone = .current) -> UsageReportSnapshot? {
        reports(input: input, now: now, timeZone: timeZone)
            .filter { $0.startAt <= now && $0.endAt > now }
            .max { ($0.quotaRecordedAt ?? .distantPast, $0.endAt) < ($1.quotaRecordedAt ?? .distantPast, $1.endAt) }
    }

    public static func reports(input: CodexLedgerInput, now: Date = Date(), timeZone: TimeZone = .current) -> [UsageReportSnapshot] {
        guard input.sourceAvailable else { return [] }
        let observations = input.observations.filter { $0.isValidWeeklyObservation && $0.observedAt <= now && $0.resetsAt > now.addingTimeInterval(-90 * 86400) }
        let groups = Dictionary(grouping: observations, by: { $0.resetsAt })
        let ends = groups.keys.sorted()
        return ends.compactMap { end in
            guard let group = groups[end], let latest = group.sorted(by: observationOrder).last else { return nil }
            let start = latest.inferredStart
            guard start < now else { return nil }
            var warnings = input.warnings
            if Set(group.compactMap { $0.planType }.filter { !$0.isEmpty }).count > 1 {
                warnings.append("同一周期观测到多个套餐标识，不能据此推断账号归属。")
            }
            if ends.contains(where: { $0 != end && $0 > start && $0.addingTimeInterval(-604800) < end }) {
                warnings.append("存在重叠或冲突的周周期；按本条重置记录推定，不合并候选周期或跨周期金额。")
            }
            return build(input: input, start: start, end: end, now: now, timeZone: timeZone, kind: .weekly,
                         observation: latest, warnings: warnings)
        }.sorted { $0.endAt > $1.endAt }
    }

    public static func customReport(input: CodexLedgerInput, start: Date, end: Date, now: Date = Date(), timeZone: TimeZone = .current) throws -> UsageReportSnapshot {
        guard input.sourceAvailable else { throw LedgerReportError.sourceUnavailable }
        guard start.timeIntervalSince1970.isFinite, end.timeIntervalSince1970.isFinite,
              start < end, start < now else { throw LedgerReportError.invalidRange }
        return build(input: input, start: start, end: end, now: now, timeZone: timeZone, kind: .custom,
                     observation: nil, warnings: input.warnings)
    }

    private static func observationOrder(_ lhs: QuotaObservation, _ rhs: QuotaObservation) -> Bool {
        if lhs.observedAt != rhs.observedAt { return lhs.observedAt < rhs.observedAt }
        if lhs.usedPercent != rhs.usedPercent { return lhs.usedPercent < rhs.usedPercent }
        if lhs.planType != rhs.planType { return (lhs.planType ?? "") < (rhs.planType ?? "") }
        return lhs.windowRole < rhs.windowRole
    }

    private static func build(input: CodexLedgerInput, start: Date, end: Date, now: Date, timeZone: TimeZone,
                              kind: UsageReportKind, observation: QuotaObservation?, warnings: [String]) -> UsageReportSnapshot {
        let cutoff = min(end, now)
        // Native-source provenance is ensured by the collector, never inferred from provider alone.
        let events = input.events.filter { $0.hasKnownTimestamp && $0.timestamp >= start && $0.timestamp < cutoff }
            .sorted { $0.timestamp == $1.timestamp ? $0.id < $1.id : $0.timestamp < $1.timestamp }
        let groupedModels: [String: [TokenUsageEvent]] = Dictionary(grouping: events, by: { $0.model })
        var models: [LedgerModelSummary] = []
        for (model, items) in groupedModels {
            models.append(LedgerModelSummary(model: model, totals: LedgerUsageTotals.build(items),
                pricingBasis: PricingEngine.basis(provider: .codex, model: model)))
        }
        models.sort {
            if $0.totals.totalTokens != $1.totals.totalTokens { return $0.totals.totalTokens > $1.totals.totalTokens }
            return $0.model < $1.model
        }
        let dateFormatter = DateFormatter(); dateFormatter.locale = Locale(identifier: "en_US_POSIX")
        dateFormatter.timeZone = timeZone; dateFormatter.dateFormat = "yyyy-MM-dd"
        let days = Dictionary(grouping: events, by: { dateFormatter.string(from: $0.timestamp) }).map { date, items in
            LedgerDailySummary(dateLabel: date, totals: .build(items))
        }.sorted { $0.dateLabel < $1.dateLabel }
        let unknown = input.events.filter { !$0.hasKnownTimestamp }.count
        var notes = warnings
        if unknown > 0 { notes.append("\(unknown) 条记录时间未知，已排除，不使用文件修改时间确定周期归属。") }
        notes.append("仅覆盖本机可读取的原生 Codex 日志，包含自动审批模型；不能视为账户完整账单或套餐固定美元额度。")
        return UsageReportSnapshot(id: kind == .weekly ? "codex-weekly-\(Int(end.timeIntervalSince1970))" : "codex-custom-\(Int(start.timeIntervalSince1970))-\(Int(end.timeIntervalSince1970))",
            kind: kind, startAt: start, endAt: end, cutoffAt: cutoff, generatedAt: now,
            timeZoneIdentifier: timeZone.identifier, timeZoneOffsetSeconds: timeZone.secondsFromGMT(for: now),
            totals: .build(events), models: models, days: days, quotaUsedPercent: observation?.usedPercent,
            quotaRecordedAt: observation?.observedAt, nextResetAt: observation?.resetsAt, planType: observation?.planType,
            cycleStartInferred: kind == .weekly, pricingVersion: PricingEngine.catalogVersion,
            unknownTimestampEvents: unknown, warnings: Array(Set(notes)).sorted(), archived: false)
    }
}

public struct LedgerViewState: Sendable {
    public var current: UsageReportSnapshot?
    public var history: [UsageReportSnapshot]
    public var warning: String?
    public var sourceAvailable: Bool
    public init(current: UsageReportSnapshot? = nil, history: [UsageReportSnapshot] = [], warning: String? = nil, sourceAvailable: Bool = false) {
        self.current = current; self.history = history; self.warning = warning; self.sourceAvailable = sourceAvailable
    }
}
