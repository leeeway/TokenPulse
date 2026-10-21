import AppKit
import SwiftUI
import TokenPulseCore

@main
struct TokenPulseMainApp: App {
    @StateObject private var viewModel = AppViewModel()

    init() {
        NSApplication.shared.setActivationPolicy(.accessory)
    }

    var body: some Scene {
        MenuBarExtra {
            DashboardPopoverView(viewModel: viewModel)
                .frame(width: 430)
        } label: {
            HStack(spacing: 4) {
                Image(systemName: viewModel.snapshot.isHighBurnRate ? "flame.fill" : "bolt.fill")
                Text(viewModel.menuBarText)
                    .font(.system(size: 12, weight: .medium, design: .monospaced))
            }
        }
        .menuBarExtraStyle(.window)
    }
}

struct DashboardPopoverView: View {
    @ObservedObject var viewModel: AppViewModel

    private var s: DashboardSnapshot { viewModel.snapshot }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            headerSection
            windowPicker

            if s.isHighBurnRate {
                burnRateBanner
            }

            heroCardsSection
            upstreamDownstreamBar
            providerBreakdownSection
            sevenDayChartSection
            footerSection
        }
        .padding(14)
        .background(Color(NSColor.windowBackgroundColor))
    }

    // MARK: - Header

    private var headerSection: some View {
        HStack {
            HStack(spacing: 7) {
                ZStack {
                    Circle()
                        .fill(Color.accentColor.opacity(0.16))
                        .frame(width: 28, height: 28)
                    Image(systemName: "bolt.horizontal.circle.fill")
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(Color.accentColor)
                }
                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: 5) {
                        Text("TokenPulse")
                            .font(.system(size: 14, weight: .bold))
                        Circle()
                            .fill(Color.green)
                            .frame(width: 6, height: 6)
                        Text("LIVE")
                            .font(.system(size: 9, weight: .bold, design: .monospaced))
                            .foregroundStyle(.green)
                    }
                    Text("macOS 本地 AI 上下行 Token 与配额实时监控")
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            Button {
                viewModel.copyShareSummary()
            } label: {
                Label(viewModel.copiedToast ? "已复制" : "分享卡片", systemImage: viewModel.copiedToast ? "checkmark.circle.fill" : "square.and.arrow.up")
                    .font(.system(size: 11, weight: .medium))
            }
            .buttonStyle(.bordered)
            .controlSize(.small)

            Button {
                viewModel.refreshNow()
            } label: {
                Image(systemName: "arrow.clockwise")
                    .font(.system(size: 12, weight: .semibold))
            }
            .buttonStyle(.borderless)
            .help("立即刷新本地日志")
        }
    }

    // MARK: - Window Picker

    private var windowPicker: some View {
        Picker("统计周期", selection: $viewModel.selectedWindow) {
            ForEach(TimeWindow.allCases) { win in
                Text(win.title).tag(win)
            }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
    }

    // MARK: - High Burn Rate Alert

    private var burnRateBanner: some View {
        HStack(spacing: 8) {
            Image(systemName: "flame.fill")
                .foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: 1) {
                Text("高频 Token 消耗预警")
                    .font(.system(size: 11, weight: .bold))
                Text("过去 5 分钟消耗 \(TokenFormatters.compactTokens(s.recentFiveMinTokens)) (\(TokenFormatters.usd(s.recentFiveMinCostUSD)))")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }
            Spacer()
        }
        .padding(8)
        .background(Color.orange.opacity(0.14))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }

    // MARK: - Hero Metrics

    private var heroCardsSection: some View {
        HStack(spacing: 8) {
            metricTile(
                title: "总 Token 用量",
                value: TokenFormatters.compactTokens(s.grandTotalTokens),
                subtitle: "\(s.totalSessions) 会话 · \(s.totalEvents) 步",
                color: .blue
            )
            metricTile(
                title: "缓存命中率",
                value: TokenFormatters.percent(s.cacheHitRatio),
                subtitle: "已省 \(TokenFormatters.usd(s.totalSavedByCacheUSD))",
                color: .green
            )
            metricTile(
                title: "等效 API 价值",
                value: TokenFormatters.usd(s.totalCostUSD),
                subtitle: "白嫖订阅回本估算",
                color: .orange
            )
        }
    }

    private func metricTile(title: String, value: String, subtitle: String, color: Color) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.system(size: 10, weight: .medium))
                .foregroundStyle(.secondary)
            Text(value)
                .font(.system(size: 16, weight: .bold, design: .rounded))
                .foregroundStyle(color)
                .lineLimit(1)
                .minimumScaleFactor(0.75)
            Text(subtitle)
                .font(.system(size: 9.5))
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(9)
        .background(Color.primary.opacity(0.045))
        .clipShape(RoundedRectangle(cornerRadius: 9))
    }

    // MARK: - Upstream vs Downstream Bar

    private var upstreamDownstreamBar: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Label("↑ 上行 Prompt: \(TokenFormatters.compactTokens(s.totalUpstreamTokens))", systemImage: "arrow.up.circle.fill")
                    .font(.system(size: 10.5, weight: .medium))
                    .foregroundStyle(.cyan)
                Spacer()
                Text("含缓存读取 \(TokenFormatters.compactTokens(s.totalCacheReadTokens))")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                Spacer()
                Label("↓ 下行 Output: \(TokenFormatters.compactTokens(s.totalDownstreamTokens))", systemImage: "arrow.down.circle.fill")
                    .font(.system(size: 10.5, weight: .medium))
                    .foregroundStyle(.purple)
            }

            GeometryReader { geo in
                let total = max(1, s.grandTotalTokens)
                let freshRatio = CGFloat(s.totalInputTokens + s.totalCacheWriteTokens) / CGFloat(total)
                let cacheRatio = CGFloat(s.totalCacheReadTokens) / CGFloat(total)
                let outRatio = CGFloat(s.totalDownstreamTokens) / CGFloat(total)

                HStack(spacing: 1) {
                    Rectangle()
                        .fill(Color.cyan)
                        .frame(width: max(2, geo.size.width * freshRatio))
                    Rectangle()
                        .fill(Color.teal.opacity(0.75))
                        .frame(width: max(0, geo.size.width * cacheRatio))
                    Rectangle()
                        .fill(Color.purple)
                        .frame(width: max(2, geo.size.width * outRatio))
                }
                .clipShape(Capsule())
            }
            .frame(height: 6)
        }
        .padding(9)
        .background(Color.primary.opacity(0.035))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }

    // MARK: - Provider Breakdown

    private var providerBreakdownSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("AI 产品实时明细")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.secondary)
                Spacer()
                if s.providers.count > 3 {
                    Text("最近 3 / \(s.providers.count)")
                        .font(.system(size: 9.5))
                        .foregroundStyle(.secondary)
                }
                if !s.runningLocalModels.isEmpty {
                    Text("Ollama 活跃: \(s.runningLocalModels.count)")
                        .font(.system(size: 10, weight: .medium))
                        .foregroundStyle(.green)
                }
            }

            if s.providers.isEmpty {
                Text("当前时间窗口内暂无 AI Token 记录，可切换至「近 30 天」或「全部」查看")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .padding(.vertical, 12)
                    .frame(maxWidth: .infinity, alignment: .center)
            } else {
                ForEach(s.recentProviders) { p in
                    ProviderRowView(summary: p, grandTotal: max(1, s.grandTotalTokens), now: s.generatedAt)
                }
            }
        }
    }

    // MARK: - 7-Day Trend Chart

    private var sevenDayChartSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("近 7 天 Token 趋势")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.secondary)

            let maxDay = max(1, s.dailyTrend.map(\.totalTokens).max() ?? 1)
            HStack(alignment: .bottom, spacing: 8) {
                ForEach(s.dailyTrend) { pt in
                    VStack(spacing: 3) {
                        Text(pt.totalTokens > 0 ? TokenFormatters.compactTokens(pt.totalTokens) : "-")
                            .font(.system(size: 8.5, design: .monospaced))
                            .foregroundStyle(.secondary)
                        RoundedRectangle(cornerRadius: 3)
                            .fill(pt.totalTokens > 0 ? Color.accentColor.opacity(0.8) : Color.secondary.opacity(0.15))
                            .frame(height: max(4, CGFloat(pt.totalTokens) / CGFloat(maxDay) * 38))
                        Text(pt.dateLabel)
                            .font(.system(size: 9, design: .monospaced))
                            .foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity)
                }
            }
            .padding(8)
            .background(Color.primary.opacity(0.035))
            .clipShape(RoundedRectangle(cornerRadius: 8))
        }
    }

    // MARK: - Footer

    private var footerSection: some View {
        HStack {
            Menu {
                ForEach(MenuBarDisplayMode.allCases) { mode in
                    Button {
                        viewModel.displayMode = mode
                    } label: {
                        HStack {
                            Text(mode.label)
                            if viewModel.displayMode == mode {
                                Image(systemName: "checkmark")
                            }
                        }
                    }
                }
            } label: {
                Label("状态栏样式", systemImage: "menubar.rectangle")
                    .font(.system(size: 10.5))
            }
            .menuStyle(.borderlessButton)
            .fixedSize()

            Spacer()

            Text("100% Local Read-Only · Zero Proxy")
                .font(.system(size: 9.5, design: .monospaced))
                .foregroundStyle(.secondary)

            Spacer()

            Button("退出") {
                NSApplication.shared.terminate(nil)
            }
            .font(.system(size: 10.5))
            .buttonStyle(.borderless)
        }
        .padding(.top, 2)
    }
}

struct ProviderRowView: View {
    let summary: ProviderSummary
    let grandTotal: Int
    let now: Date

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 7) {
                Image(systemName: summary.provider.symbolName)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(Color.accentColor)
                    .frame(width: 18)

                Text(summary.provider.displayName)
                    .font(.system(size: 12, weight: .semibold))

                if let firstModel = summary.topModels.first {
                    Text(firstModel)
                        .font(.system(size: 9.5, design: .monospaced))
                        .padding(.horizontal, 5)
                        .padding(.vertical, 1)
                        .background(Color.primary.opacity(0.07))
                        .clipShape(Capsule())
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }

                Spacer()

                Text(TokenFormatters.compactTokens(summary.totalTokens))
                    .font(.system(size: 12, weight: .bold, design: .monospaced))

                Text(TokenFormatters.usd(summary.costUSD))
                    .font(.system(size: 11, weight: .semibold, design: .monospaced))
                    .foregroundStyle(.orange)
                    .frame(width: 56, alignment: .trailing)
            }

            HStack(spacing: 10) {
                Text("↑上行 \(TokenFormatters.compactTokens(summary.upstreamTokens))")
                Text("↓下行 \(TokenFormatters.compactTokens(summary.downstreamTokens))")
                Text("缓存命中 \(TokenFormatters.percent(summary.cacheHitRatio))")
                Spacer()
                Text("\(summary.sessionCount) 会话")
            }
            .font(.system(size: 9.5, design: .monospaced))
            .foregroundStyle(.secondary)

            if let rl = summary.rateLimit {
                HStack(alignment: .top, spacing: 10) {
                    quotaColumn(used: rl.primaryUsedPercent, minutes: rl.primaryWindowMinutes, resetsAt: rl.primaryResetsAt)
                    if let sec = rl.secondaryUsedPercent {
                        quotaColumn(used: sec, minutes: rl.secondaryWindowMinutes, resetsAt: rl.secondaryResetsAt)
                    }
                }
                .foregroundStyle(.secondary)
                .help("配额记录于 \(rl.updatedAt.formatted(date: .numeric, time: .standard))；以日志为准")
            }
        }
        .padding(8)
        .background(Color.primary.opacity(0.04))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }

    private func quotaColumn(used: Double, minutes: Int?, resetsAt: Date?) -> some View {
        let expired = resetsAt.map { $0 <= now } ?? false
        return VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 4) {
                Text(TokenFormatters.quotaWindow(minutes) + " 配额")
                Spacer(minLength: 4)
                Text((expired ? "上次 " : "") + TokenFormatters.percent(used / 100))
            }
            .font(.system(size: 9.5, weight: .medium, design: .monospaced))
            ProgressView(value: min(100, max(0, used)), total: 100)
                .controlSize(.mini)
                .tint(expired ? .gray : .accentColor)
                .opacity(expired ? 0.4 : 1)
            Text(TokenFormatters.quotaReset(resetsAt, now: now))
                .font(.system(size: 9))
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .help(resetsAt.map { "本地重置时间：\($0.formatted(date: .complete, time: .standard))" } ?? "日志未提供重置时间")
    }
}
