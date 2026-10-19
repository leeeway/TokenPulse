import Foundation

/// Supported AI tools and providers monitored by TokenPulse.
public enum AIProvider: String, CaseIterable, Codable, Sendable, Identifiable {
    case claudeCode = "claude_code"
    case codex = "codex"
    case antigravity = "antigravity"
    case cursor = "cursor"
    case ollama = "ollama"
    case custom = "custom"

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .claudeCode: return "Claude Code"
        case .codex: return "OpenAI Codex"
        case .antigravity: return "Antigravity"
        case .cursor: return "Cursor"
        case .ollama: return "Ollama (Local)"
        case .custom: return "Custom Tool"
        }
    }

    public var shortName: String {
        switch self {
        case .claudeCode: return "Claude"
        case .codex: return "Codex"
        case .antigravity: return "AGY"
        case .cursor: return "Cursor"
        case .ollama: return "Ollama"
        case .custom: return "Custom"
        }
    }

    public var symbolName: String {
        switch self {
        case .claudeCode: return "sparkles"
        case .codex: return "terminal.fill"
        case .antigravity: return "bolt.horizontal.circle.fill"
        case .cursor: return "cursorarrow.rays"
        case .ollama: return "desktopcomputer"
        case .custom: return "puzzlepiece.extension.fill"
        }
    }

    /// Typical monthly subscription price in USD for ROI calculation.
    public var typicalMonthlySubscriptionUSD: Double {
        switch self {
        case .claudeCode: return 20.0
        case .codex: return 20.0
        case .antigravity: return 20.0
        case .cursor: return 20.0
        case .ollama: return 0.0
        case .custom: return 0.0
        }
    }
}

/// Time window filter for aggregating token usage.
public enum TimeWindow: String, CaseIterable, Codable, Sendable, Identifiable {
    case today = "today"
    case sevenDays = "7d"
    case thirtyDays = "30d"
    case allTime = "all"

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .today: return "今日"
        case .sevenDays: return "近 7 天"
        case .thirtyDays: return "近 30 天"
        case .allTime: return "全部"
        }
    }

    public func startDate(now: Date = Date(), calendar: Calendar = .current) -> Date? {
        switch self {
        case .today:
            return calendar.startOfDay(for: now)
        case .sevenDays:
            return calendar.date(byAdding: .day, value: -6, to: calendar.startOfDay(for: now))
        case .thirtyDays:
            return calendar.date(byAdding: .day, value: -29, to: calendar.startOfDay(for: now))
        case .allTime:
            return nil
        }
    }
}

/// A single normalized token consumption event from any AI tool.
public struct TokenUsageEvent: Codable, Sendable, Identifiable, Equatable {
    public let id: String
    public let provider: AIProvider
    public let model: String
    public let timestamp: Date
    public let sessionId: String
    /// Non-cached fresh input tokens (upstream)
    public let inputTokens: Int
    /// Tokens written into prompt cache (upstream)
    public let cacheWriteTokens: Int
    /// Tokens read from prompt cache (upstream)
    public let cacheReadTokens: Int
    /// Completion / output tokens (downstream)
    public let outputTokens: Int
    /// Reasoning / thinking tokens if reported separately (downstream)
    public let reasoningTokens: Int
    /// Estimated API equivalent cost in USD
    public let costUSD: Double
    /// Estimated savings in USD thanks to prompt cache hits
    public let savedByCacheUSD: Double

    public init(
        id: String,
        provider: AIProvider,
        model: String,
        timestamp: Date,
        sessionId: String,
        inputTokens: Int,
        cacheWriteTokens: Int = 0,
        cacheReadTokens: Int = 0,
        outputTokens: Int,
        reasoningTokens: Int = 0,
        costUSD: Double? = nil,
        savedByCacheUSD: Double? = nil
    ) {
        self.id = id
        self.provider = provider
        self.model = model
        self.timestamp = timestamp
        self.sessionId = sessionId
        self.inputTokens = max(0, inputTokens)
        self.cacheWriteTokens = max(0, cacheWriteTokens)
        self.cacheReadTokens = max(0, cacheReadTokens)
        self.outputTokens = max(0, outputTokens)
        self.reasoningTokens = max(0, reasoningTokens)

        let estimate = PricingEngine.estimateCost(
            provider: provider,
            model: model,
            inputTokens: self.inputTokens,
            cacheWriteTokens: self.cacheWriteTokens,
            cacheReadTokens: self.cacheReadTokens,
            outputTokens: self.outputTokens
        )
        self.costUSD = costUSD ?? estimate.costUSD
        self.savedByCacheUSD = savedByCacheUSD ?? estimate.savedByCacheUSD
    }

    /// Total upstream (prompt) tokens sent or read from context cache.
    public var upstreamTokens: Int {
        inputTokens + cacheWriteTokens + cacheReadTokens
    }

    /// Total downstream (completion) tokens generated by the model.
    public var downstreamTokens: Int {
        outputTokens
    }

    /// Total tokens across upstream and downstream.
    public var totalTokens: Int {
        upstreamTokens + downstreamTokens
    }
}

/// Real-time rate limit quota snapshot (e.g. from Codex 5h & 7d rolling windows).
public struct RateLimitSnapshot: Codable, Sendable, Equatable {
    public let provider: AIProvider
    public let planType: String?
    public let primaryUsedPercent: Double
    public let primaryWindowMinutes: Int
    public let primaryResetsAt: Date?
    public let secondaryUsedPercent: Double?
    public let secondaryWindowMinutes: Int?
    public let secondaryResetsAt: Date?
    public let updatedAt: Date

    public init(
        provider: AIProvider,
        planType: String?,
        primaryUsedPercent: Double,
        primaryWindowMinutes: Int,
        primaryResetsAt: Date?,
        secondaryUsedPercent: Double? = nil,
        secondaryWindowMinutes: Int? = nil,
        secondaryResetsAt: Date? = nil,
        updatedAt: Date = Date()
    ) {
        self.provider = provider
        self.planType = planType
        self.primaryUsedPercent = primaryUsedPercent
        self.primaryWindowMinutes = primaryWindowMinutes
        self.primaryResetsAt = primaryResetsAt
        self.secondaryUsedPercent = secondaryUsedPercent
        self.secondaryWindowMinutes = secondaryWindowMinutes
        self.secondaryResetsAt = secondaryResetsAt
        self.updatedAt = updatedAt
    }
}

/// Active local model running in Ollama / local inference engine.
public struct LocalRunningModel: Codable, Sendable, Identifiable, Equatable {
    public var id: String { name }
    public let name: String
    public let sizeBytes: Int64
    public let vramBytes: Int64
    public let expiresAt: Date?

    public init(name: String, sizeBytes: Int64, vramBytes: Int64, expiresAt: Date? = nil) {
        self.name = name
        self.sizeBytes = sizeBytes
        self.vramBytes = vramBytes
        self.expiresAt = expiresAt
    }
}

/// Per-provider aggregated summary within a selected time window.
public struct ProviderSummary: Codable, Sendable, Identifiable, Equatable {
    public var id: String { provider.rawValue }
    public let provider: AIProvider
    public let eventCount: Int
    public let sessionCount: Int
    public let inputTokens: Int
    public let cacheWriteTokens: Int
    public let cacheReadTokens: Int
    public let outputTokens: Int
    public let reasoningTokens: Int
    public let costUSD: Double
    public let savedByCacheUSD: Double
    public let lastActiveAt: Date?
    public let topModels: [String]
    public let rateLimit: RateLimitSnapshot?

    public var upstreamTokens: Int { inputTokens + cacheWriteTokens + cacheReadTokens }
    public var downstreamTokens: Int { outputTokens }
    public var totalTokens: Int { upstreamTokens + downstreamTokens }

    public var cacheHitRatio: Double {
        guard upstreamTokens > 0 else { return 0 }
        return Double(cacheReadTokens) / Double(upstreamTokens)
    }
}

/// Daily bucket for trend chart visualization.
public struct DailyTokenPoint: Codable, Sendable, Identifiable, Equatable {
    public var id: String { dateLabel }
    public let date: Date
    public let dateLabel: String
    public let upstreamTokens: Int
    public let downstreamTokens: Int
    public let costUSD: Double

    public var totalTokens: Int { upstreamTokens + downstreamTokens }
}

/// Complete snapshot rendered by both the Menu Bar UI and CLI.
public struct DashboardSnapshot: Codable, Sendable, Equatable {
    public let generatedAt: Date
    public let window: TimeWindow
    public let totalInputTokens: Int
    public let totalCacheWriteTokens: Int
    public let totalCacheReadTokens: Int
    public let totalOutputTokens: Int
    public let totalReasoningTokens: Int
    public let totalCostUSD: Double
    public let totalSavedByCacheUSD: Double
    public let totalSessions: Int
    public let totalEvents: Int
    /// Tokens consumed in the last 5 minutes (for live burn rate / speed)
    public let recentFiveMinTokens: Int
    public let recentFiveMinCostUSD: Double
    public let providers: [ProviderSummary]
    public let dailyTrend: [DailyTokenPoint]
    public let recentEvents: [TokenUsageEvent]
    public let runningLocalModels: [LocalRunningModel]

    public var totalUpstreamTokens: Int {
        totalInputTokens + totalCacheWriteTokens + totalCacheReadTokens
    }

    public var totalDownstreamTokens: Int {
        totalOutputTokens
    }

    public var grandTotalTokens: Int {
        totalUpstreamTokens + totalDownstreamTokens
    }

    public var cacheHitRatio: Double {
        guard totalUpstreamTokens > 0 else { return 0 }
        return Double(totalCacheReadTokens) / Double(totalUpstreamTokens)
    }

    public var tokensPerMinute: Int {
        recentFiveMinTokens / 5
    }

    public var isHighBurnRate: Bool {
        recentFiveMinTokens >= 250_000 || recentFiveMinCostUSD >= 1.50
    }
}
