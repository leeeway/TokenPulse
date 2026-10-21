import Foundation
import Testing
@testable import TokenPulseCore

@Suite("Review fixes")
struct ReviewFixTests {
    @Test func cliUsesRecordedWindowsAndUnknownPlan() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let quota = RateLimitSnapshot(provider: .codex, planType: " ", primaryUsedPercent: 42,
            primaryWindowMinutes: 120, primaryResetsAt: now.addingTimeInterval(600),
            secondaryUsedPercent: 70, secondaryWindowMinutes: 1440, secondaryResetsAt: now.addingTimeInterval(-60))
        let text = TokenFormatters.quotaSummary(quota, now: now, timeZone: TimeZone(secondsFromGMT: 0)!)
        #expect(text.contains("2h 配额 42%"))
        #expect(text.contains("1d 配额 70%"))
        #expect(text.contains("10 分钟后"))
        #expect(text.contains("待更新"))
        #expect(text.contains("套餐：未知"))
        #expect(!text.contains("pro"))
        let missing = RateLimitSnapshot(provider: .codex, planType: "team", primaryUsedPercent: 2,
            primaryWindowMinutes: 0, primaryResetsAt: nil)
        #expect(TokenFormatters.quotaSummary(missing).contains("日志未提供重置时间"))
        #expect(TokenFormatters.quotaSummary(missing).contains("套餐：team"))
    }

    @Test func unknownModelsAreExplicitCompatibilityEstimates() {
        #expect(PricingEngine.basis(provider: .codex, model: "gpt-6.1-sol").contains("未精确匹配"))
        #expect(PricingEngine.basis(provider: .codex, model: "gpt-5").contains("内置模型条目"))
        #expect(PricingEngine.basis(provider: .custom, model: "anything").contains("记录金额优先"))
        #expect(PricingEngine.basis(provider: .ollama, model: "local").contains("不包含硬件或电费"))
        let rate = PricingEngine.rate(for: .codex, model: "gpt-6.1-sol")
        #expect(rate.inputPerMillion == 2)
        #expect(rate.outputPerMillion == 8)
    }
}
