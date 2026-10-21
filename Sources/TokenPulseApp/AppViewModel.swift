import AppKit
import Combine
import Foundation
import TokenPulseCore

public enum MenuBarDisplayMode: String, CaseIterable, Identifiable {
    case iconOnly = "icon_only"
    case tokensAndCost = "tokens_cost"
    case upDownStream = "up_down"
    case costOnly = "cost_only"

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .iconOnly: return "仅图标（适合刘海屏）"
        case .tokensAndCost: return "总 Token + 价值"
        case .upDownStream: return "上下行 Token"
        case .costOnly: return "仅显示 API 价值"
        }
    }
}

protocol DashboardCollecting: Sendable {
    func refreshLogCache(now: Date)
    func snapshotFromCache(window: TimeWindow, now: Date) -> DashboardSnapshot
    func refreshOllamaModels() async -> [LocalRunningModel]
    var lastScanAt: Date? { get }
    var sourceWarnings: [String] { get }
    var ollamaStatus: String { get }
}

extension UsageCollector: DashboardCollecting { }

@MainActor
public final class AppViewModel: ObservableObject {
    @Published public var selectedWindow: TimeWindow {
        didSet {
            guard selectedWindow != oldValue else { return }
            defaults.set(selectedWindow.rawValue, forKey: "selectedWindow")
            renderFromCache()
        }
    }
    @Published public var displayMode: MenuBarDisplayMode {
        didSet { defaults.set(displayMode.rawValue, forKey: "displayMode") }
    }
    @Published public var snapshot: DashboardSnapshot
    @Published public var isRefreshing = false
    @Published public var copiedToast = false
    @Published public private(set) var lastScanAt: Date?
    @Published public private(set) var sourceWarnings: [String] = []
    @Published public private(set) var ollamaStatus = "尚未查询 Ollama"

    private let collector: any DashboardCollecting
    private let defaults: UserDefaults
    private let clock: @Sendable () -> Date
    private var timer: Timer?
    private var renderRevision = 0
    private var renderTask: Task<Void, Never>?
    private var refreshTask: Task<Void, Never>?

    public convenience init() {
        self.init(collector: UsageCollector(), defaults: .standard)
    }

    init(collector: any DashboardCollecting, defaults: UserDefaults, startsAutomatically: Bool = true,
         clock: @escaping @Sendable () -> Date = { Date() }) {
        self.collector = collector
        self.defaults = defaults
        self.clock = clock
        let window = defaults.string(forKey: "selectedWindow").flatMap(TimeWindow.init(rawValue:)) ?? .today
        selectedWindow = window
        displayMode = defaults.string(forKey: "displayMode").flatMap(MenuBarDisplayMode.init(rawValue:)) ?? .iconOnly
        snapshot = UsageCollector.buildSnapshot(allEvents: [], rateLimits: [:], runningLocalModels: [], window: window, now: clock())
        if startsAutomatically {
            timer = Timer.scheduledTimer(withTimeInterval: 4.0, repeats: true) { [weak self] _ in
                Task { @MainActor in self?.refreshNow() }
            }
            refreshNow()
        }
    }

    public func dataStatus(at now: Date) -> String {
        guard let lastScanAt else { return "读取中" }
        if now.timeIntervalSince(lastScanAt) > 30 { return "待刷新" }
        if !sourceWarnings.isEmpty || snapshot.unknownTimestampEventCount > 0 { return "需检查" }
        return "LIVE"
    }

    public var statusHelp: String {
        var lines = ["只读本地日志 · 费用为估算值", ollamaStatus]
        if let lastScanAt { lines.append("日志更新于 " + lastScanAt.formatted(date: .numeric, time: .standard)) }
        lines.append(contentsOf: sourceWarnings)
        if snapshot.unknownTimestampEventCount > 0 {
            lines.append("\(snapshot.unknownTimestampEventCount) 条记录时间未知，仅计入全部用量，不参与趋势和高消耗提醒。")
        }
        return lines.joined(separator: "\n")
    }

    private func renderFromCache() {
        renderRevision += 1
        let revision = renderRevision
        let window = selectedWindow
        let collector = collector
        let now = clock()
        renderTask?.cancel()
        renderTask = Task { [weak self] in
            let result = await Task.detached(priority: .utility) {
                (collector.snapshotFromCache(window: window, now: now), collector.lastScanAt, collector.sourceWarnings, collector.ollamaStatus)
            }.value
            guard let self, !Task.isCancelled, revision == renderRevision, window == selectedWindow else { return }
            snapshot = result.0
            lastScanAt = result.1
            sourceWarnings = result.2
            ollamaStatus = result.3
        }
    }

    public var menuBarText: String {
        let s = snapshot
        switch displayMode {
        case .iconOnly:
            return ""
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
        let collector = collector
        let now = clock()
        refreshTask = Task { [weak self] in
            // Run both sources concurrently; a slow Ollama query must not hold back log totals.
            async let models = collector.refreshOllamaModels()
            await Task.detached(priority: .utility) { collector.refreshLogCache(now: now) }.value
            self?.renderFromCache()
            _ = await models
            self?.renderFromCache()
            self?.isRefreshing = false
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
