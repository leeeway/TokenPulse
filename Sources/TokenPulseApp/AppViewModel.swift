import AppKit
import Combine
import Foundation
import TokenPulseCore
import UserNotifications

public enum MenuBarDisplayMode: String, CaseIterable, Identifiable {
    case quotaOnly = "quota_only"
    case iconOnly = "icon_only"
    case tokensAndCost = "tokens_cost"
    case upDownStream = "up_down"
    case costOnly = "cost_only"

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .quotaOnly: return "最低剩余配额（紧凑）"
        case .iconOnly: return "仅图标（最省空间）"
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
    var logWatchDirectories: [URL] { get }
    func refreshOllamaModelsIfDue(now: Date, force: Bool) async -> [LocalRunningModel]
    func ledgerInputForApp() -> CodexLedgerInput?
}

extension DashboardCollecting {
    var logWatchDirectories: [URL] { [] }
    func refreshOllamaModelsIfDue(now: Date, force: Bool) async -> [LocalRunningModel] { await refreshOllamaModels() }
    func ledgerInputForApp() -> CodexLedgerInput? { nil }
}

extension UsageCollector: DashboardCollecting {
    func ledgerInputForApp() -> CodexLedgerInput? { ledgerInputFromCache() }
}

private final class QuotaNotificationDelegate: NSObject, UNUserNotificationCenterDelegate {
    func userNotificationCenter(_ center: UNUserNotificationCenter,
                                willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }
}

@MainActor
public final class AppViewModel: ObservableObject {
    let ledger: LedgerViewModel
    private var ledgerWindow: LedgerWindowController?
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

    @Published private(set) var codexQuotas: [QuotaObservation] = []
    @Published private(set) var quotaNow: Date
    @Published private(set) var quotaNotificationsEnabled: Bool
    @Published private(set) var quotaNotificationStatus: String?
    private var quotaAlerts: QuotaAlerts
    private var notificationPreferenceRevision = 0
    private let quotaNotificationDelegate = QuotaNotificationDelegate()
    private let notificationSink: (@MainActor ([QuotaObservation], Date) -> Void)?

    private let collector: any DashboardCollecting
    private let defaults: UserDefaults
    private let clock: @Sendable () -> Date
    private var driver: BackgroundRefreshDriver?
    private var dashboardVisible = false
    private var eventTask: Task<Void, Never>?
    private var pendingRefresh = false
    private var pendingForce = false
    private var lastRefreshStartedAt = Date.distantPast
    private let eventMinimumInterval: TimeInterval
    private var renderRevision = 0
    private var renderTask: Task<Void, Never>?
    private var refreshTask: Task<Void, Never>?

    public convenience init() {
        self.init(collector: UsageCollector(), defaults: .standard)
    }

    init(collector: any DashboardCollecting, defaults: UserDefaults, startsAutomatically: Bool = true,
         clock: @escaping @Sendable () -> Date = { Date() }, eventMinimumInterval: TimeInterval = 4,
         ledgerDirectory: URL? = nil, notificationSink: (@MainActor ([QuotaObservation], Date) -> Void)? = nil) {
        self.collector = collector
        self.defaults = defaults
        self.clock = clock
        ledger = LedgerViewModel(directory: ledgerDirectory, clock: clock)
        self.notificationSink = notificationSink
        quotaNow = clock()
        quotaNotificationsEnabled = defaults.bool(forKey: "quotaNotificationsEnabled")
        quotaAlerts = defaults.data(forKey: "quotaAlertCycles").flatMap {
            try? JSONDecoder().decode(QuotaAlerts.self, from: $0)
        } ?? QuotaAlerts()
        self.eventMinimumInterval = eventMinimumInterval
        let window = defaults.string(forKey: "selectedWindow").flatMap(TimeWindow.init(rawValue:)) ?? .today
        selectedWindow = window
        displayMode = defaults.string(forKey: "displayMode").flatMap(MenuBarDisplayMode.init(rawValue:)) ?? .quotaOnly
        snapshot = UsageCollector.buildSnapshot(allEvents: [], rateLimits: [:], runningLocalModels: [], window: window, now: clock())
        if startsAutomatically {
            driver = BackgroundRefreshDriver(directories: collector.logWatchDirectories, refresh: { [weak self] trigger in
                Task { @MainActor [weak self] in
                    switch trigger {
                    case .logs: self?.logsDidChange()
                    case .fallback, .wake: self?.refreshAutomatically()
                    case let .quotaReset(deadline): self?.quotaDeadlineReached(at: deadline)
                    }
                }
            }, render: { [weak self] in
                Task { @MainActor [weak self] in self?.renderFromCache() }
            })
            refreshAutomatically()
        }
    }

    public func dataStatus(at now: Date) -> String {
        guard let lastScanAt else { return "读取中" }
        if now.timeIntervalSince(lastScanAt) > 75 { return "待刷新" }
        if !sourceWarnings.isEmpty || snapshot.unknownTimestampEventCount > 0 { return "需检查" }
        return "LIVE"
    }

    public var statusHelp: String {
        var lines = ["只读本地日志 · 费用为估算值", ollamaStatus]
        if let lastScanAt { lines.append("日志更新于 " + lastScanAt.formatted(date: .numeric, time: .standard)) }
        if let quotaNotificationStatus { lines.append(quotaNotificationStatus) }
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
        case .quotaOnly:
            guard let lowestQuota else { return "" }
            return QuotaIndicator(usedPercent: lowestQuota.usedPercent, expired: false).percentText ?? ""
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

    var lowestQuota: QuotaObservation? {
        codexQuotas.filter { !$0.isExpired(at: quotaNow) }.min { $0.remainingPercent < $1.remainingPercent }
    }

    var quotaHelp: String? {
        if let lowestQuota { return QuotaIndicator.summary(of: lowestQuota, at: quotaNow) }
        return codexQuotas.isEmpty ? nil : "Codex 配额待更新"
    }

    func setQuotaNotifications(_ enabled: Bool) {
        notificationPreferenceRevision += 1
        let revision = notificationPreferenceRevision
        quotaNotificationStatus = nil
        if !enabled {
            quotaNotificationsEnabled = false
            defaults.set(false, forKey: "quotaNotificationsEnabled")
            return
        }
        Task { [weak self] in
            let allowed = (try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])) == true
            guard let self, revision == notificationPreferenceRevision else { return }
            quotaNotificationsEnabled = allowed
            defaults.set(allowed, forKey: "quotaNotificationsEnabled")
            quotaNotificationStatus = allowed ? nil : "如需配额通知，请在系统设置中允许 TokenPulse 通知。"
            if allowed { refreshAutomatically() }
        }
    }

    private func advanceQuotaClock(to now: Date) {
        if quotaNow != now { quotaNow = now }
        ledger.advanceQuotaClock(to: now)
        driver?.scheduleReset(at: codexQuotas.map(\.resetsAt).filter { $0 > now }.min())
    }

    func quotaDeadlineReached(at deadline: Date) {
        let now = clock()
        advanceQuotaClock(to: now)
        if deadline <= now && lastRefreshStartedAt < deadline { refreshAutomatically() }
    }

    private func receiveQuotas(_ input: CodexLedgerInput, now: Date) {
        let verified = input.latestQuotas(at: now)
        let readings = input.partialRead ? QuotaObservation.latestByWindow(codexQuotas + verified, at: now) : verified
        if codexQuotas != readings { codexQuotas = readings }
        advanceQuotaClock(to: now)
        ledger.receive(input, at: now)
        guard quotaNotificationsEnabled else { return }
        let alerts = quotaAlerts.claim(verified.filter { readings.contains($0) }, at: now)
        guard !alerts.isEmpty else { return }
        defaults.set(try? JSONEncoder().encode(quotaAlerts), forKey: "quotaAlertCycles")
        if let notificationSink {
            notificationSink(alerts, now)
            return
        }
        let content = UNMutableNotificationContent()
        content.title = "Codex 配额提醒"
        content.body = alerts.map { QuotaIndicator.summary(of: $0, at: now) }.joined(separator: " · ")
        content.sound = .default
        let center = UNUserNotificationCenter.current()
        center.delegate = quotaNotificationDelegate
        Task { [weak self] in
            guard self?.quotaNotificationsEnabled == true else { return }
            do {
                try await center.add(UNNotificationRequest(
                    identifier: UUID().uuidString, content: content, trigger: nil))
            } catch {
                self?.quotaNotificationStatus = "配额通知未送出，请检查系统通知设置。"
            }
        }
    }

    /// User actions bypass Ollama backoff; background events respect it.
    public func refreshNow() { refresh(forceOllama: true) }
    func refreshAutomatically() { refresh(forceOllama: false) }

    func dashboardVisibilityChanged(_ visible: Bool) {
        guard visible != dashboardVisible else { return }
        dashboardVisible = visible
        driver?.setVisible(visible)
        if visible { refreshNow() }
    }

    /// Leading-edge coalescing prevents starvation during a continuous stream of
    /// writes. New changes during a scan queue one follow-up instead of being lost.
    func logsDidChange() {
        guard eventTask == nil else { return }
        let wait = max(0.5, eventMinimumInterval - clock().timeIntervalSince(lastRefreshStartedAt))
        eventTask = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(wait)) } catch { return }
            guard let self else { return }
            eventTask = nil
            refreshAutomatically()
        }
    }

    private func refresh(forceOllama: Bool) {
        let now = clock()
        advanceQuotaClock(to: now)
        guard !isRefreshing else {
            pendingRefresh = true
            pendingForce = pendingForce || forceOllama
            return
        }
        driver?.recoverWatchRoots()
        eventTask?.cancel()
        eventTask = nil
        isRefreshing = true
        let collector = collector
        lastRefreshStartedAt = now
        refreshTask = Task { [weak self] in
            async let models = collector.refreshOllamaModelsIfDue(now: now, force: forceOllama)
            let ledgerInput = await Task.detached(priority: .utility) {
                collector.refreshLogCache(now: now)
                return collector.ledgerInputForApp()
            }.value
            self?.renderFromCache()
            if let self, let ledgerInput { receiveQuotas(ledgerInput, now: clock()) }
            let refreshedModels = await models
            guard let self else { return }
            if refreshedModels != snapshot.runningLocalModels || collector.ollamaStatus != ollamaStatus {
                renderFromCache()
            }
            isRefreshing = false
            if pendingRefresh {
                let force = pendingForce
                pendingRefresh = false
                pendingForce = false
                if force { refreshNow() } else { logsDidChange() }
            }
        }
    }

    func openLedger() {
        if ledgerWindow == nil {
            ledgerWindow = LedgerWindowController(model: ledger, refresh: { [weak self] in self?.refreshNow() })
        }
        refreshAutomatically()
        ledgerWindow?.showLedger()
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
