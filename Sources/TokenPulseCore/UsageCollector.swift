import Foundation

/// Aggregates token usage across all registered `ProviderAdapter`s and custom local JSONL events.
public final class UsageCollector: @unchecked Sendable {
    private let adapters: [ProviderAdapter]
    private let customEventsURL: URL
    private let lock = NSLock()
    private var cachedAllEvents: [TokenUsageEvent] = []
    private var cachedRateLimits: [AIProvider: RateLimitSnapshot] = [:]
    private var cachedLocalModels: [LocalRunningModel] = []
    private let ollamaSession: URLSession
    private var scanTime: Date?
    private var modelStatus = "尚未查询 Ollama"

    public var lastScanAt: Date? { lock.withLock { scanTime } }
    public var ollamaStatus: String { lock.withLock { modelStatus } }

    public var sourceWarnings: [String] {
        adapters.compactMap { adapter in adapter.accessWarning }
    }

    public init(adapters: [ProviderAdapter]? = nil, customEventsURL: URL? = nil, ollamaSession: URLSession = .shared) {
        self.ollamaSession = ollamaSession
        self.adapters = adapters ?? [
            ClaudeCodeAdapter(),
            CodexAdapter(),
            AntigravityAdapter()
        ]
        if let customEventsURL {
            self.customEventsURL = customEventsURL
        } else {
            let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".tokenpulse")
            try? FileManager.default.createDirectory(
                at: dir,
                withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700]
            )
            self.customEventsURL = dir.appendingPathComponent("custom_events.jsonl")
        }
    }

    /// Appends a custom telemetry event to `~/.tokenpulse/custom_events.jsonl` and memory.
    public func recordCustomEvent(_ event: TokenUsageEvent) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        if let data = try? encoder.encode(event),
           var line = String(data: data, encoding: .utf8) {
            line.append("\n")
            if let lineData = line.data(using: .utf8) {
                if FileManager.default.fileExists(atPath: customEventsURL.path),
                   let handle = try? FileHandle(forWritingTo: customEventsURL) {
                    handle.seekToEndOfFile()
                    handle.write(lineData)
                    try? handle.close()
                } else {
                    FileManager.default.createFile(
                        atPath: customEventsURL.path,
                        contents: lineData,
                        attributes: [.posixPermissions: 0o600]
                    )
                }
            }
        }
        lock.lock()
        cachedAllEvents.append(event)
        lock.unlock()
    }

    private func loadCustomEvents() -> [TokenUsageEvent] {
        guard let content = try? String(contentsOf: customEventsURL, encoding: .utf8) else { return [] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        var list: [TokenUsageEvent] = []
        content.enumerateLines { line, _ in
            guard let data = line.data(using: .utf8),
                  let ev = try? decoder.decode(TokenUsageEvent.self, from: data) else { return }
            list.append(ev)
        }
        return list
    }

    /// Scans all adapters and returns a fresh `DashboardSnapshot` for the requested time window.
    public func collectSnapshot(window: TimeWindow = .today, now: Date = Date()) -> DashboardSnapshot {
        refreshLogCache(now: now)
        return snapshotFromCache(window: window, now: now)
    }

    /// Refreshes read-only log data without performing an unused period aggregation.
    public func refreshLogCache(now: Date = Date()) {
        var events: [TokenUsageEvent] = []
        var rateLimits: [AIProvider: RateLimitSnapshot] = [:]

        for adapter in adapters {
            let result = adapter.scan()
            events.append(contentsOf: result.events)
            if let rl = result.rateLimit {
                rateLimits[adapter.provider] = rl
            }
        }

        events.append(contentsOf: loadCustomEvents())
        events.sort { $0.timestamp > $1.timestamp }

        lock.lock()
        cachedAllEvents = events
        cachedRateLimits = rateLimits
        scanTime = now
        lock.unlock()
    }

    /// Recomputes snapshot for another `TimeWindow` using the most recently scanned events in memory.
    public func snapshotFromCache(window: TimeWindow, now: Date = Date()) -> DashboardSnapshot {
        lock.lock()
        let events = cachedAllEvents
        let rateLimits = cachedRateLimits
        let localModels = cachedLocalModels
        lock.unlock()

        return Self.buildSnapshot(
            allEvents: events,
            rateLimits: rateLimits,
            runningLocalModels: localModels,
            window: window,
            now: now
        )
    }

    /// Polls local Ollama (`http://127.0.0.1:11434/api/ps`) asynchronously.
    public func refreshOllamaModels() async -> [LocalRunningModel] {
        guard let url = URL(string: "http://127.0.0.1:11434/api/ps") else { return [] }
        var request = URLRequest(url: url)
        request.timeoutInterval = 1.2
        do {
            let (data, response) = try await ollamaSession.data(for: request)
            guard let response = response as? HTTPURLResponse, (200..<300).contains(response.statusCode),
                  let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let models = json["models"] as? [[String: Any]] else {
                lock.withLock { cachedLocalModels = []; modelStatus = "Ollama 返回无效响应" }
                return []
            }
            let parsed: [LocalRunningModel] = models.compactMap { item in
                guard let name = item["name"] as? String else { return nil }
                let size = (item["size"] as? Int64) ?? Int64((item["size"] as? Int) ?? 0)
                let vram = (item["size_vram"] as? Int64) ?? Int64((item["size_vram"] as? Int) ?? 0)
                let expires = LogDateParser.parse(item["expires_at"] as? String)
                return LocalRunningModel(name: name, sizeBytes: size, vramBytes: vram, expiresAt: expires)
            }
            lock.withLock {
                cachedLocalModels = parsed
                modelStatus = parsed.isEmpty ? "Ollama 已连接，暂无运行模型" : "Ollama：\(parsed.count) 个运行模型"
            }
            return parsed
        } catch {
            lock.withLock {
                cachedLocalModels = []
                modelStatus = "Ollama 未连接"
            }
            return []
        }
    }

    public static func buildSnapshot(
        allEvents: [TokenUsageEvent],
        rateLimits: [AIProvider: RateLimitSnapshot],
        runningLocalModels: [LocalRunningModel],
        window: TimeWindow,
        now: Date = Date()
    ) -> DashboardSnapshot {
        let cal = Calendar.current
        let cutoff = window.startDate(now: now, calendar: cal)
        let filtered: [TokenUsageEvent]
        if let cutoff {
            filtered = allEvents.filter { $0.hasKnownTimestamp && $0.timestamp >= cutoff }
        } else {
            filtered = allEvents
        }

        let fiveMinAgo = now.addingTimeInterval(-300)
        var recentFiveMinTokens = 0
        var recentFiveMinCost = 0.0
        for ev in allEvents {
            if ev.hasKnownTimestamp && ev.timestamp >= fiveMinAgo && ev.timestamp <= now.addingTimeInterval(60) {
                recentFiveMinTokens += ev.totalTokens
                recentFiveMinCost += ev.costUSD
            }
        }

        var totalInput = 0
        var totalCacheWrite = 0
        var totalCacheRead = 0
        var totalOutput = 0
        var totalReasoning = 0
        var totalCost = 0.0
        var totalSaved = 0.0
        var allSessions = Set<String>()

        var byProvider: [AIProvider: [TokenUsageEvent]] = [:]
        for ev in filtered {
            totalInput += ev.inputTokens
            totalCacheWrite += ev.cacheWriteTokens
            totalCacheRead += ev.cacheReadTokens
            totalOutput += ev.outputTokens
            totalReasoning += ev.reasoningTokens
            totalCost += ev.costUSD
            totalSaved += ev.savedByCacheUSD
            allSessions.insert("\(ev.provider.rawValue):\(ev.sessionId)")
            byProvider[ev.provider, default: []].append(ev)
        }

        // Include providers with active rateLimits even if 0 events in current window
        let activeProviders = Set(byProvider.keys).union(rateLimits.keys)
        var providerSummaries: [ProviderSummary] = []

        for provider in activeProviders {
            let pEvents = byProvider[provider] ?? []
            var pInput = 0
            var pCacheWrite = 0
            var pCacheRead = 0
            var pOutput = 0
            var pReasoning = 0
            var pCost = 0.0
            var pSaved = 0.0
            var sessions = Set<String>()
            var modelCounts: [String: Int] = [:]
            var lastActive: Date?

            for ev in pEvents {
                pInput += ev.inputTokens
                pCacheWrite += ev.cacheWriteTokens
                pCacheRead += ev.cacheReadTokens
                pOutput += ev.outputTokens
                pReasoning += ev.reasoningTokens
                pCost += ev.costUSD
                pSaved += ev.savedByCacheUSD
                sessions.insert(ev.sessionId)
                modelCounts[ev.model, default: 0] += ev.totalTokens
                if ev.hasKnownTimestamp && (lastActive == nil || ev.timestamp > lastActive!) {
                    lastActive = ev.timestamp
                }
            }

            let topModels = modelCounts
                .sorted { $0.value > $1.value }
                .prefix(3)
                .map { $0.key }

            providerSummaries.append(
                ProviderSummary(
                    provider: provider,
                    eventCount: pEvents.count,
                    sessionCount: sessions.count,
                    inputTokens: pInput,
                    cacheWriteTokens: pCacheWrite,
                    cacheReadTokens: pCacheRead,
                    outputTokens: pOutput,
                    reasoningTokens: pReasoning,
                    costUSD: pCost,
                    savedByCacheUSD: pSaved,
                    lastActiveAt: lastActive,
                    topModels: topModels,
                    rateLimit: rateLimits[provider]
                )
            )
        }

        providerSummaries.sort {
            if $0.totalTokens != $1.totalTokens {
                return $0.totalTokens > $1.totalTokens
            }
            return $0.provider.displayName < $1.provider.displayName
        }

        // Build 7-day daily trend chart
        let formatter = DateFormatter()
        formatter.dateFormat = "MM/dd"
        let startOfToday = cal.startOfDay(for: now)
        var dailyPoints: [DailyTokenPoint] = []

        for offset in stride(from: -6, through: 0, by: 1) {
            guard let dayStart = cal.date(byAdding: .day, value: offset, to: startOfToday),
                  let dayEnd = cal.date(byAdding: .day, value: 1, to: dayStart) else { continue }
            var up = 0
            var down = 0
            var cost = 0.0
            for ev in allEvents {
                if ev.hasKnownTimestamp && ev.timestamp >= dayStart && ev.timestamp < dayEnd {
                    up += ev.upstreamTokens
                    down += ev.downstreamTokens
                    cost += ev.costUSD
                }
            }
            dailyPoints.append(
                DailyTokenPoint(
                    date: dayStart,
                    dateLabel: formatter.string(from: dayStart),
                    upstreamTokens: up,
                    downstreamTokens: down,
                    costUSD: cost
                )
            )
        }

        return DashboardSnapshot(
            generatedAt: now,
            window: window,
            totalInputTokens: totalInput,
            totalCacheWriteTokens: totalCacheWrite,
            totalCacheReadTokens: totalCacheRead,
            totalOutputTokens: totalOutput,
            totalReasoningTokens: totalReasoning,
            totalCostUSD: totalCost,
            totalSavedByCacheUSD: totalSaved,
            totalSessions: allSessions.count,
            totalEvents: filtered.count,
            recentFiveMinTokens: recentFiveMinTokens,
            recentFiveMinCostUSD: recentFiveMinCost,
            providers: providerSummaries,
            dailyTrend: dailyPoints,
            recentEvents: Array(filtered.prefix(25)),
            runningLocalModels: runningLocalModels,
            unknownTimestampEventCount: allEvents.filter { !$0.hasKnownTimestamp }.count
        )
    }
}
