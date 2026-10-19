import AppKit
import Combine
import Foundation
import TokenPulseCore

public enum MenuBarDisplayMode: String, CaseIterable, Identifiable {
    case tokensAndCost = "tokens_cost"
    case upDownStream = "up_down"
    case costOnly = "cost_only"

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .tokensAndCost: return "总 Token + 价值"
        case .upDownStream: return "上下行 Token"
        case .costOnly: return "仅显示 API 价值"
        }
    }
}

@MainActor
public final class AppViewModel: ObservableObject {
    @Published public var selectedWindow: TimeWindow = .today {
        didSet {
            snapshot = collector.snapshotFromCache(window: selectedWindow)
        }
    }
    @Published public var displayMode: MenuBarDisplayMode = .tokensAndCost
    @Published public var snapshot: DashboardSnapshot
    @Published public var isRefreshing: Bool = false
    @Published public var copiedToast: Bool = false

    private let collector = UsageCollector()
    private var timer: Timer?

    public init() {
        let initialToday = collector.collectSnapshot(window: .today)
        if initialToday.grandTotalTokens > 0 {
            self.selectedWindow = .today
            self.snapshot = initialToday
        } else {
            self.selectedWindow = .thirtyDays
            self.snapshot = collector.snapshotFromCache(window: .thirtyDays)
        }

        self.timer = Timer.scheduledTimer(withTimeInterval: 4.0, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.refreshNow()
            }
        }

        Task {
            _ = await collector.refreshOllamaModels()
            self.snapshot = collector.snapshotFromCache(window: self.selectedWindow)
        }
    }

    public var menuBarText: String {
        let s = snapshot
        switch displayMode {
        case .tokensAndCost:
            return "\(TokenFormatters.compactTokens(s.grandTotalTokens)) · \(TokenFormatters.usd(s.totalCostUSD))"
        case .upDownStream:
            return "↑\(TokenFormatters.compactTokens(s.totalUpstreamTokens)) ↓\(TokenFormatters.compactTokens(s.totalDownstreamTokens))"
        case .costOnly:
            return TokenFormatters.usd(s.totalCostUSD)
        }
    }

    public func refreshNow() {
        guard !isRefreshing else { return }
        isRefreshing = true
        let win = selectedWindow
        let collectorRef = collector

        Task.detached(priority: .utility) {
            _ = await collectorRef.refreshOllamaModels()
            let fresh = collectorRef.collectSnapshot(window: win)
            await MainActor.run { [weak self] in
                self?.snapshot = fresh
                self?.isRefreshing = false
            }
        }
    }

    public func copyShareSummary() {
        let s = snapshot
        let lines = [
            "⚡ TokenPulse AI 用量报告 (\(s.window.title))",
            "• 总 Token: \(TokenFormatters.compactTokens(s.grandTotalTokens)) (↑上行 \(TokenFormatters.compactTokens(s.totalUpstreamTokens)) / ↓下行 \(TokenFormatters.compactTokens(s.totalDownstreamTokens)))",
            "• 缓存命中率: \(TokenFormatters.percent(s.cacheHitRatio)) (节省约 \(TokenFormatters.usd(s.totalSavedByCacheUSD)))",
            "• 等效 API 价值: \(TokenFormatters.usd(s.totalCostUSD))",
            "• 活跃工具: " + s.providers.map { "\($0.provider.shortName) \(TokenFormatters.compactTokens($0.totalTokens))" }.joined(separator: " | ")
        ]
        let text = lines.joined(separator: "\n")
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        copiedToast = true
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            self.copiedToast = false
        }
    }
}
