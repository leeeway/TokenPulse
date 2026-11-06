import Foundation
import CoreFoundation

/// Result returned by a `ProviderAdapter` scan.
public struct AdapterScanResult: Sendable {
    public let provider: AIProvider
    public let events: [TokenUsageEvent]
    public let rateLimit: RateLimitSnapshot?
    public let ledgerInput: CodexLedgerInput?

    public init(provider: AIProvider, events: [TokenUsageEvent], rateLimit: RateLimitSnapshot? = nil, ledgerInput: CodexLedgerInput? = nil) {
        self.provider = provider
        self.events = events
        self.rateLimit = rateLimit
        self.ledgerInput = ledgerInput
    }
}

/// Open-source extension protocol: implement this to support a new AI tool in TokenPulse.
public protocol ProviderAdapter: Sendable {
    var provider: AIProvider { get }
    var accessWarning: String? { get }
    func scan() -> AdapterScanResult
}

public extension ProviderAdapter {
    var accessWarning: String? { nil }
}

private enum SourceAccess {
    static func warning(provider: AIProvider, at url: URL) -> String? {
        let fm = FileManager.default
        do {
            let attributes = try fm.attributesOfItem(atPath: url.path)
            guard attributes[.type] as? FileAttributeType == .typeDirectory,
                  fm.isReadableFile(atPath: url.path) else {
                return "\(provider.shortName)：日志目录不可读取，请检查路径和权限"
            }
            return nil
        } catch let error as CocoaError where error.code == .fileReadNoSuchFile || error.code == .fileNoSuchFile { return nil }
        catch { return "\(provider.shortName)：无法访问日志目录（\(error.localizedDescription)）" }
    }
}

/// Shared ISO8601 date parser supporting fractional and non-fractional seconds.
public enum LogDateParser {
    public static func parse(_ raw: String?) -> Date? {
        guard let raw, !raw.isEmpty else { return nil }
        let isoFrac = ISO8601DateFormatter()
        isoFrac.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let d = isoFrac.date(from: raw) {
            return d
        }
        let isoBasic = ISO8601DateFormatter()
        isoBasic.formatOptions = [.withInternetDateTime]
        return isoBasic.date(from: raw)
    }
}

// MARK: - 1. Claude Code Adapter (~/.claude/projects/**/*.jsonl)

public final class ClaudeCodeAdapter: ProviderAdapter, @unchecked Sendable {
    public let provider: AIProvider = .claudeCode
    private let baseDirectory: URL
    public var accessWarning: String? { SourceAccess.warning(provider: provider, at: baseDirectory) }
    private let lock = NSLock()
    private var fileCache: [String: (mtime: Date, size: UInt64, events: [TokenUsageEvent])] = [:]

    public init(baseDirectory: URL? = nil) {
        if let baseDirectory {
            self.baseDirectory = baseDirectory
        } else {
            self.baseDirectory = FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent(".claude/projects")
        }
    }

    public func scan() -> AdapterScanResult {
        let fm = FileManager.default
        guard fm.fileExists(atPath: baseDirectory.path) else {
            return AdapterScanResult(provider: provider, events: [])
        }

        var allEvents: [TokenUsageEvent] = []
        guard let enumerator = fm.enumerator(
            at: baseDirectory,
            includingPropertiesForKeys: [.isRegularFileKey, .contentModificationDateKey, .fileSizeKey],
            options: [.skipsHiddenFiles]
        ) else {
            return AdapterScanResult(provider: provider, events: [])
        }

        for case let fileURL as URL in enumerator {
            guard fileURL.pathExtension == "jsonl" else { continue }
            guard let values = try? fileURL.resourceValues(forKeys: [.isRegularFileKey, .contentModificationDateKey, .fileSizeKey]),
                  values.isRegularFile == true else { continue }

            let mtime = values.contentModificationDate ?? .distantPast
            let size = UInt64(values.fileSize ?? 0)
            let key = fileURL.path

            lock.lock()
            if let cached = fileCache[key], cached.mtime == mtime && cached.size == size {
                allEvents.append(contentsOf: cached.events)
                lock.unlock()
                continue
            }
            lock.unlock()

            let parsed = Self.parseJSONLFile(at: fileURL, fallbackDate: mtime)
            lock.lock()
            fileCache[key] = (mtime: mtime, size: size, events: parsed)
            lock.unlock()
            allEvents.append(contentsOf: parsed)
        }

        return AdapterScanResult(provider: provider, events: allEvents)
    }

    public static func parseJSONLFile(at url: URL, fallbackDate: Date = Date()) -> [TokenUsageEvent] {
        guard let content = try? String(contentsOf: url, encoding: .utf8) else { return [] }
        let sessionId = url.deletingPathExtension().lastPathComponent
        var events: [TokenUsageEvent] = []
        var lineIndex = 0

        content.enumerateLines { line, _ in
            lineIndex += 1
            guard line.contains("\"usage\""),
                  let data = line.data(using: .utf8),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let message = json["message"] as? [String: Any],
                  let usage = message["usage"] as? [String: Any] else {
                return
            }

            let input = (usage["input_tokens"] as? Int) ?? 0
            let cacheWrite = (usage["cache_creation_input_tokens"] as? Int) ?? 0
            let cacheRead = (usage["cache_read_input_tokens"] as? Int) ?? 0
            let output = (usage["output_tokens"] as? Int) ?? 0

            guard (input + cacheWrite + cacheRead + output) > 0 else { return }

            let model = (message["model"] as? String) ?? "claude-sonnet"
            let timestamp = LogDateParser.parse(json["timestamp"] as? String) ?? fallbackDate
            let msgId = (message["id"] as? String) ?? (json["uuid"] as? String) ?? "\(sessionId)-\(lineIndex)"

            events.append(
                TokenUsageEvent(
                    id: "claude-\(sessionId)-\(msgId)-\(lineIndex)",
                    provider: .claudeCode,
                    model: model,
                    timestamp: timestamp,
                    sessionId: sessionId,
                    inputTokens: input,
                    cacheWriteTokens: cacheWrite,
                    cacheReadTokens: cacheRead,
                    outputTokens: output,
                    hasKnownTimestamp: LogDateParser.parse(json["timestamp"] as? String) != nil
                )
            )
        }
        return events
    }
}

public struct CodexParsedLog: Sendable {
    public let events: [TokenUsageEvent]
    public let rateLimit: RateLimitSnapshot?
    public let observations: [QuotaObservation]
    public let readSucceeded: Bool
}

// MARK: - 2. OpenAI Codex Adapter (~/.codex/sessions/**/*.jsonl)

public final class CodexAdapter: ProviderAdapter, @unchecked Sendable {
    public let provider: AIProvider = .codex
    private let baseDirectory: URL
    public var accessWarning: String? { SourceAccess.warning(provider: provider, at: baseDirectory) }
    private let lock = NSLock()
    private var fileCache: [String: (mtime: Date, size: UInt64, parsed: CodexParsedLog)] = [:]

    public init(baseDirectory: URL? = nil) {
        if let baseDirectory {
            self.baseDirectory = baseDirectory
        } else {
            self.baseDirectory = FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent(".codex/sessions")
        }
    }

    public func scan() -> AdapterScanResult {
        let fm = FileManager.default
        func unavailable(_ message: String) -> AdapterScanResult {
            AdapterScanResult(provider: provider, events: [], ledgerInput: CodexLedgerInput(warnings: [message]))
        }
        guard fm.fileExists(atPath: baseDirectory.path), fm.isReadableFile(atPath: baseDirectory.path) else {
            lock.withLock { fileCache.removeAll() }
            return unavailable("Codex 原生日志目录不存在或不可读取。")
        }
        var allEvents: [TokenUsageEvent] = []
        var allObservations: [QuotaObservation] = []
        var latestRateLimit: RateLimitSnapshot?
        var errors = false
        var warnings: [String] = []
        var visited = Set<String>()
        guard let enumerator = fm.enumerator(at: baseDirectory,
            includingPropertiesForKeys: [.isRegularFileKey, .contentModificationDateKey, .fileSizeKey],
            options: [.skipsHiddenFiles], errorHandler: { _, _ in errors = true; return true }) else {
            return unavailable("Codex 原生日志目录无法遍历。")
        }
        for case let fileURL as URL in enumerator {
            guard fileURL.pathExtension == "jsonl" else { continue }
            guard let values = try? fileURL.resourceValues(forKeys: [.isRegularFileKey, .contentModificationDateKey, .fileSizeKey]),
                  values.isRegularFile == true else { errors = true; continue }
            let mtime = values.contentModificationDate ?? .distantPast
            let size = UInt64(values.fileSize ?? 0)
            let key = fileURL.path
            visited.insert(key)
            let cached = lock.withLock { fileCache[key] }
            let parsed: CodexParsedLog
            if let cached, cached.mtime == mtime && cached.size == size {
                parsed = cached.parsed
            } else {
                parsed = Self.parseDetailedRolloutFile(at: fileURL, fallbackDate: mtime)
                let after = try? fileURL.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
                lock.withLock {
                    if parsed.readSucceeded && after?.contentModificationDate == mtime && UInt64(after?.fileSize ?? 0) == size {
                        fileCache[key] = (mtime, size, parsed)
                    } else { fileCache.removeValue(forKey: key) }
                }
            }
            if !parsed.readSucceeded { errors = true }
            allEvents.append(contentsOf: parsed.events)
            allObservations.append(contentsOf: parsed.observations)
            if let limit = parsed.rateLimit, latestRateLimit == nil || limit.updatedAt > latestRateLimit!.updatedAt { latestRateLimit = limit }
        }
        lock.withLock { fileCache = fileCache.filter { visited.contains($0.key) } }
        if errors { warnings.append("部分 Codex 原生日志无法读取，本轮账本统计暂停更新，避免将缺失记录当作零用量。") }
        if visited.isEmpty { warnings.append("没有发现 Codex 原生日志文件。") }
        return AdapterScanResult(provider: provider, events: allEvents, rateLimit: latestRateLimit,
            ledgerInput: CodexLedgerInput(events: allEvents, observations: allObservations,
                                         sourceAvailable: !errors, warnings: warnings))
    }

    public static func parseRolloutFile(
        at url: URL,
        fallbackDate: Date = Date()
    ) -> (events: [TokenUsageEvent], rateLimit: RateLimitSnapshot?) {
        let parsed = parseDetailedRolloutFile(at: url, fallbackDate: fallbackDate)
        return (parsed.events, parsed.rateLimit)
    }

    public static func parseDetailedRolloutFile(at url: URL, fallbackDate: Date = Date()) -> CodexParsedLog {
        guard let content = try? String(contentsOf: url, encoding: .utf8) else {
            return CodexParsedLog(events: [], rateLimit: nil, observations: [], readSucceeded: false)
        }

        let sessionId = url.deletingPathExtension().lastPathComponent
        var currentModel = "gpt-5-codex"
        var prevTotalInput = 0
        var prevTotalCacheWrite = 0
        var prevTotalCacheRead = 0
        var prevTotalOutput = 0
        var prevTotalReasoning = 0
        var events: [TokenUsageEvent] = []
        var latestRateLimit: RateLimitSnapshot?
        var lineIndex = 0
        var observations: [QuotaObservation] = []

        content.enumerateLines { line, _ in
            lineIndex += 1
            guard line.contains("\"token_count\"") || line.contains("\"thread_settings_applied\""),
                  let data = line.data(using: .utf8),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let payload = json["payload"] as? [String: Any],
                  let payloadType = payload["type"] as? String else {
                return
            }

            if payloadType == "thread_settings_applied",
               let settings = payload["thread_settings"] as? [String: Any],
               let model = settings["model"] as? String, !model.isEmpty {
                currentModel = model
                return
            }

            guard payloadType == "token_count" else { return }
            let genuineTimestamp = LogDateParser.parse(json["timestamp"] as? String)
            let timestamp = genuineTimestamp ?? fallbackDate
            if let observedAt = genuineTimestamp, let limits = payload["rate_limits"] as? [String: Any] {
                for role in ["primary", "secondary"] {
                    if let window = limits[role] as? [String: Any],
                       let observation = strictWeeklyObservation(window, role: role, observedAt: observedAt, plan: limits["plan_type"] as? String) {
                        observations.append(observation)
                    }
                }
            }

            // Parse rate_limits if present
            if let rl = payload["rate_limits"] as? [String: Any],
               let primary = rl["primary"] as? [String: Any] {
                let primaryPct = (primary["used_percent"] as? Double) ?? Double((primary["used_percent"] as? Int) ?? 0)
                let primaryWin = (primary["window_minutes"] as? Int) ?? 300
                let primaryResetEpoch = (primary["resets_at"] as? Double) ?? Double((primary["resets_at"] as? Int) ?? 0)
                let primaryReset = primaryResetEpoch > 0 ? Date(timeIntervalSince1970: primaryResetEpoch) : nil

                var secPct: Double?
                var secWin: Int?
                var secReset: Date?
                if let secondary = rl["secondary"] as? [String: Any] {
                    secPct = (secondary["used_percent"] as? Double) ?? Double((secondary["used_percent"] as? Int) ?? 0)
                    secWin = (secondary["window_minutes"] as? Int)
                    let secEpoch = (secondary["resets_at"] as? Double) ?? Double((secondary["resets_at"] as? Int) ?? 0)
                    if secEpoch > 0 {
                        secReset = Date(timeIntervalSince1970: secEpoch)
                    }
                }

                let candidate = RateLimitSnapshot(
                    provider: .codex,
                    planType: rl["plan_type"] as? String,
                    primaryUsedPercent: primaryPct,
                    primaryWindowMinutes: primaryWin,
                    primaryResetsAt: primaryReset,
                    secondaryUsedPercent: secPct,
                    secondaryWindowMinutes: secWin,
                    secondaryResetsAt: secReset,
                    updatedAt: timestamp
                )
                if latestRateLimit == nil || candidate.updatedAt >= latestRateLimit!.updatedAt {
                    latestRateLimit = candidate
                }
            }

            guard let info = payload["info"] as? [String: Any],
                  let totalUsage = info["total_token_usage"] as? [String: Any] else {
                return
            }

            let cumInput = (totalUsage["input_tokens"] as? Int) ?? 0
            let cumCached = (totalUsage["cached_input_tokens"] as? Int) ?? 0
            let cumCacheWrite = (totalUsage["cache_write_input_tokens"] as? Int) ?? 0
            let cumOutput = (totalUsage["output_tokens"] as? Int) ?? 0
            let cumReasoning = (totalUsage["reasoning_output_tokens"] as? Int) ?? 0

            let deltaRawInput = max(0, cumInput - prevTotalInput)
            let deltaCached = max(0, cumCached - prevTotalCacheRead)
            let deltaCacheWrite = max(0, cumCacheWrite - prevTotalCacheWrite)
            let deltaOutput = max(0, cumOutput - prevTotalOutput)
            let deltaReasoning = max(0, cumReasoning - prevTotalReasoning)

            prevTotalInput = max(prevTotalInput, cumInput)
            prevTotalCacheRead = max(prevTotalCacheRead, cumCached)
            prevTotalCacheWrite = max(prevTotalCacheWrite, cumCacheWrite)
            prevTotalOutput = max(prevTotalOutput, cumOutput)
            prevTotalReasoning = max(prevTotalReasoning, cumReasoning)

            // In OpenAI/Codex usage, `input_tokens` is total input including `cached_input_tokens`
            let freshInput = max(0, deltaRawInput - deltaCached - deltaCacheWrite)
            guard (freshInput + deltaCacheWrite + deltaCached + deltaOutput) > 0 else { return }

            events.append(
                TokenUsageEvent(
                    id: "codex-\(sessionId)-\(lineIndex)",
                    provider: .codex,
                    model: currentModel,
                    timestamp: timestamp,
                    sessionId: sessionId,
                    inputTokens: freshInput,
                    cacheWriteTokens: deltaCacheWrite,
                    cacheReadTokens: deltaCached,
                    outputTokens: deltaOutput,
                    reasoningTokens: deltaReasoning,
                    hasKnownTimestamp: LogDateParser.parse(json["timestamp"] as? String) != nil
                )
            )
        }

        return CodexParsedLog(events: events, rateLimit: latestRateLimit, observations: observations, readSucceeded: true)
    }
    private static func strictWeeklyObservation(_ window: [String: Any], role: String, observedAt: Date, plan: String?) -> QuotaObservation? {
        func number(_ key: String) -> Double? {
            guard let value = window[key] as? NSNumber, CFGetTypeID(value) != CFBooleanGetTypeID() else { return nil }
            let result = value.doubleValue
            return result.isFinite ? result : nil
        }
        guard number("window_minutes") == 10080, let used = number("used_percent"), (0...100).contains(used),
              let epoch = number("resets_at"), epoch > 0, epoch <= 253402300799 else { return nil }
        let result = QuotaObservation(observedAt: observedAt, windowRole: role, windowMinutes: 10080,
                                     resetsAt: Date(timeIntervalSince1970: epoch), usedPercent: used, planType: plan)
        return result.isValidWeeklyObservation ? result : nil
    }

}

// MARK: - 3. Google Antigravity Adapter (~/.gemini/antigravity/brain/**/.system_generated/logs/transcript.jsonl)

public final class AntigravityAdapter: ProviderAdapter, @unchecked Sendable {
    public let provider: AIProvider = .antigravity
    private let baseDirectory: URL
    public var accessWarning: String? { SourceAccess.warning(provider: provider, at: baseDirectory) }
    private let lock = NSLock()
    private var fileCache: [String: (mtime: Date, size: UInt64, events: [TokenUsageEvent])] = [:]

    public init(baseDirectory: URL? = nil) {
        if let baseDirectory {
            self.baseDirectory = baseDirectory
        } else {
            self.baseDirectory = FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent(".gemini/antigravity/brain")
        }
    }

    public func scan() -> AdapterScanResult {
        let fm = FileManager.default
        guard fm.fileExists(atPath: baseDirectory.path) else {
            return AdapterScanResult(provider: provider, events: [])
        }

        var allEvents: [TokenUsageEvent] = []
        guard let brainDirs = try? fm.contentsOfDirectory(
            at: baseDirectory,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        ) else {
            return AdapterScanResult(provider: provider, events: [])
        }

        for dir in brainDirs {
            let transcriptURL = dir
                .appendingPathComponent(".system_generated/logs/transcript.jsonl")
            guard fm.fileExists(atPath: transcriptURL.path),
                  let values = try? transcriptURL.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey]) else {
                continue
            }

            let mtime = values.contentModificationDate ?? .distantPast
            let size = UInt64(values.fileSize ?? 0)
            let key = transcriptURL.path

            lock.lock()
            if let cached = fileCache[key], cached.mtime == mtime && cached.size == size {
                allEvents.append(contentsOf: cached.events)
                lock.unlock()
                continue
            }
            lock.unlock()

            let sessionId = dir.lastPathComponent
            let parsed = Self.parseTranscriptFile(at: transcriptURL, sessionId: sessionId, fallbackDate: mtime)
            lock.lock()
            fileCache[key] = (mtime: mtime, size: size, events: parsed)
            lock.unlock()
            allEvents.append(contentsOf: parsed)
        }

        return AdapterScanResult(provider: provider, events: allEvents)
    }

    public static func parseTranscriptFile(
        at url: URL,
        sessionId: String,
        fallbackDate: Date = Date()
    ) -> [TokenUsageEvent] {
        guard let content = try? String(contentsOf: url, encoding: .utf8) else { return [] }
        var events: [TokenUsageEvent] = []
        var lineIndex = 0

        content.enumerateLines { line, _ in
            lineIndex += 1
            guard line.contains("\"input_tokens\"") || line.contains("\"output_tokens\""),
                  let data = line.data(using: .utf8),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return
            }

            let input = (json["input_tokens"] as? Int) ?? 0
            let cacheRead = (json["cache_read_tokens"] as? Int) ?? 0
            let output = (json["output_tokens"] as? Int) ?? 0
            guard (input + cacheRead + output) > 0 else { return }

            let stepIndex = (json["step_index"] as? Int) ?? lineIndex
            let timestamp = LogDateParser.parse(json["created_at"] as? String) ?? fallbackDate

            events.append(
                TokenUsageEvent(
                    id: "agy-\(sessionId)-\(stepIndex)",
                    provider: .antigravity,
                    model: "gemini-antigravity",
                    timestamp: timestamp,
                    sessionId: sessionId,
                    inputTokens: input,
                    cacheWriteTokens: 0,
                    cacheReadTokens: cacheRead,
                    outputTokens: output,
                    hasKnownTimestamp: LogDateParser.parse(json["created_at"] as? String) != nil
                )
            )
        }

        return events
    }
}
