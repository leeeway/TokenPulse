import Foundation

/// Calculates equivalent API cost in USD and prompt-cache savings.
public enum PricingEngine {
    public struct ModelRate: Sendable {
        /// USD per 1,000,000 fresh input tokens
        public let inputPerMillion: Double
        /// USD per 1,000,000 cache-write tokens
        public let cacheWritePerMillion: Double
        /// USD per 1,000,000 cache-read tokens
        public let cacheReadPerMillion: Double
        /// USD per 1,000,000 output tokens
        public let outputPerMillion: Double

        public init(
            inputPerMillion: Double,
            cacheWritePerMillion: Double,
            cacheReadPerMillion: Double,
            outputPerMillion: Double
        ) {
            self.inputPerMillion = inputPerMillion
            self.cacheWritePerMillion = cacheWritePerMillion
            self.cacheReadPerMillion = cacheReadPerMillion
            self.outputPerMillion = outputPerMillion
        }
    }

    public struct CostResult: Sendable {
        public let costUSD: Double
        public let savedByCacheUSD: Double
    }

    public static let catalogVersion = "compat-v1"
    public static let estimateNotice = "内置兼容估算 compat-v1，非实时官方报价或实际账单。自定义记录可自带金额。"

    /// Exact catalog keys describe the legacy table, not verified current market prices.
    private static let catalogModels: Set<String> = [
        "claude-opus", "claude-haiku", "claude-sonnet", "o3", "gpt-5",
        "gpt-5-codex", "gemini-flash", "gemini-pro"
    ]

    public static func basis(provider: AIProvider, model: String) -> String {
        if provider == .custom { return "自定义记录金额优先；未提供时使用兼容估算 · " + catalogVersion }
        if provider == .ollama { return "本地模型 API 费用按 0 估算，不包含硬件或电费" }
        let lower = model.lowercased()
        let matchingProvider = (provider == .claudeCode && lower.hasPrefix("claude-"))
            || (provider == .codex && (lower.hasPrefix("gpt-") || lower == "o3"))
            || (provider == .antigravity && lower.hasPrefix("gemini-"))
        let match = matchingProvider && catalogModels.contains(lower) ? "内置模型条目" : "未精确匹配模型，使用兼容回退"
        let r = rate(for: provider, model: model)
        return "\(model)：\(match) · \(catalogVersion)\n每百万 Token（USD）：输入 \(r.inputPerMillion)，缓存写入 \(r.cacheWritePerMillion)，缓存读取 \(r.cacheReadPerMillion)，输出 \(r.outputPerMillion)\n非实时官方报价或实际账单"
    }

    public static func rate(for provider: AIProvider, model: String) -> ModelRate {
        let lower = model.lowercased()

        if provider == .ollama {
            return ModelRate(inputPerMillion: 0, cacheWritePerMillion: 0, cacheReadPerMillion: 0, outputPerMillion: 0)
        }

        // Claude models
        if lower.contains("opus") {
            return ModelRate(inputPerMillion: 15.0, cacheWritePerMillion: 18.75, cacheReadPerMillion: 1.50, outputPerMillion: 75.0)
        }
        if lower.contains("haiku") {
            return ModelRate(inputPerMillion: 0.80, cacheWritePerMillion: 1.00, cacheReadPerMillion: 0.08, outputPerMillion: 4.00)
        }
        if lower.contains("sonnet") || provider == .claudeCode {
            return ModelRate(inputPerMillion: 3.00, cacheWritePerMillion: 3.75, cacheReadPerMillion: 0.30, outputPerMillion: 15.00)
        }

        // OpenAI / Codex models
        if lower.contains("o3") || lower.contains("gpt-5") || lower.contains("codex") || provider == .codex {
            return ModelRate(inputPerMillion: 2.00, cacheWritePerMillion: 2.00, cacheReadPerMillion: 0.50, outputPerMillion: 8.00)
        }

        // Gemini / Antigravity models
        if lower.contains("flash") {
            return ModelRate(inputPerMillion: 0.30, cacheWritePerMillion: 0.30, cacheReadPerMillion: 0.075, outputPerMillion: 2.50)
        }
        if lower.contains("pro") || lower.contains("gemini") || provider == .antigravity {
            return ModelRate(inputPerMillion: 1.25, cacheWritePerMillion: 1.25, cacheReadPerMillion: 0.31, outputPerMillion: 10.00)
        }

        // Default fallback (Sonnet / GPT-4o class)
        return ModelRate(inputPerMillion: 2.50, cacheWritePerMillion: 2.50, cacheReadPerMillion: 0.50, outputPerMillion: 10.00)
    }

    public static func estimateCost(
        provider: AIProvider,
        model: String,
        inputTokens: Int,
        cacheWriteTokens: Int,
        cacheReadTokens: Int,
        outputTokens: Int
    ) -> CostResult {
        let r = rate(for: provider, model: model)
        let m = 1_000_000.0

        let inputCost = (Double(inputTokens) / m) * r.inputPerMillion
        let cacheWriteCost = (Double(cacheWriteTokens) / m) * r.cacheWritePerMillion
        let cacheReadCost = (Double(cacheReadTokens) / m) * r.cacheReadPerMillion
        let outputCost = (Double(outputTokens) / m) * r.outputPerMillion

        let totalCost = inputCost + cacheWriteCost + cacheReadCost + outputCost

        // Without prompt caching, cacheReadTokens would have been billed at full inputPerMillion rate
        let uncachedReadCost = (Double(cacheReadTokens) / m) * r.inputPerMillion
        let saved = max(0.0, uncachedReadCost - cacheReadCost)

        return CostResult(costUSD: totalCost, savedByCacheUSD: saved)
    }
}

/// Human-friendly formatters for token counts, costs, and percentages.
public enum TokenFormatters {
    public static func quotaSummary(_ quota: RateLimitSnapshot, now: Date = Date(), timeZone: TimeZone = .current) -> String {
        func window(_ minutes: Int?, _ used: Double, _ reset: Date?) -> String {
            "\(quotaWindow(minutes)) 配额 \(String(format: "%.0f%%", used)) · \(quotaResetCountdown(reset, now: now, timeZone: timeZone))"
        }
        var parts = [window(quota.primaryWindowMinutes, quota.primaryUsedPercent, quota.primaryResetsAt)]
        if let used = quota.secondaryUsedPercent {
            parts.append(window(quota.secondaryWindowMinutes, used, quota.secondaryResetsAt))
        }
        let plan = quota.planType?.trimmingCharacters(in: .whitespacesAndNewlines)
        parts.append("套餐：" + ((plan?.isEmpty == false) ? plan! : "未知"))
        return parts.joined(separator: " | ")
    }

    public static func quotaWindow(_ minutes: Int?) -> String {
        guard let minutes, minutes > 0 else { return "窗口" }
        if minutes % 1440 == 0 { return "\(minutes / 1440)d" }
        if minutes % 60 == 0 { return "\(minutes / 60)h" }
        return "\(minutes)m"
    }

    public static func quotaReset(_ reset: Date?, now: Date = Date(), timeZone: TimeZone = .current) -> String {
        guard let reset else { return "日志未提供重置时间" }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = calendar
        formatter.timeZone = timeZone
        formatter.dateFormat = calendar.component(.year, from: reset) == calendar.component(.year, from: now)
            ? "MM/dd HH:mm" : "yyyy/MM/dd HH:mm"
        let date = formatter.string(from: reset)
        return reset <= now ? "上次重置 \(date) · 待更新" : "重置 \(date)"
    }

    public static func quotaResetCountdown(_ reset: Date?, now: Date = Date(), timeZone: TimeZone = .current) -> String {
        guard let reset, reset > now else { return quotaReset(reset, now: now, timeZone: timeZone) }
        let minutes = Int(ceil(reset.timeIntervalSince(now) / 60))
        let duration: String
        if minutes >= 1440 {
            let hours = minutes % 1440 / 60
            duration = "约 \(minutes / 1440) 天" + (hours == 0 ? "" : " \(hours) 小时")
        }
        else if minutes >= 60 {
            let remainder = minutes % 60
            duration = "约 \(minutes / 60) 小时" + (remainder == 0 ? "" : " \(remainder) 分")
        }
        else { duration = minutes <= 1 ? "不到 1 分钟" : "约 \(minutes) 分钟" }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = calendar
        formatter.timeZone = timeZone
        formatter.dateFormat = calendar.isDate(reset, inSameDayAs: now) ? "HH:mm"
            : calendar.component(.year, from: reset) == calendar.component(.year, from: now) ? "MM/dd HH:mm" : "yyyy/MM/dd HH:mm"
        return "\(duration)后 · \(formatter.string(from: reset))"
    }

    public static func compactTokens(_ count: Int) -> String {
        let absVal = abs(Double(count))
        let sign = count < 0 ? "-" : ""
        if absVal >= 1_000_000_000 {
            return String(format: "%@%.2fB", sign, absVal / 1_000_000_000)
        } else if absVal >= 1_000_000 {
            return String(format: "%@%.2fM", sign, absVal / 1_000_000)
        } else if absVal >= 1_000 {
            return String(format: "%@%.1fK", sign, absVal / 1_000)
        } else {
            return "\(count)"
        }
    }

    public static func usd(_ value: Double) -> String {
        if value >= 100 {
            return String(format: "$%.1f", value)
        } else if value >= 0.01 || value == 0 {
            return String(format: "$%.2f", value)
        } else {
            return String(format: "$%.4f", value)
        }
    }

    public static func percent(_ ratio: Double) -> String {
        return String(format: "%.1f%%", ratio * 100.0)
    }
}
