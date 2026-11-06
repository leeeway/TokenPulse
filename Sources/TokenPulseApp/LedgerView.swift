import AppKit
import SwiftUI
import TokenPulseCore

private enum LedgerPage: String, CaseIterable {
    case current = "本轮", history = "历史", custom = "自定义时段"
}

struct LedgerView: View {
    @ObservedObject var model: LedgerViewModel
    let refresh: () -> Void
    @State private var page = LedgerPage.current
    @State private var historyID: String?
    @State private var start = Calendar.current.date(byAdding: .day, value: -7, to: Date()) ?? Date()
    @State private var end = Date()
    @State private var confirmClear = false
    @State private var confirmRebuild = false

    private var report: UsageReportSnapshot? {
        switch page {
        case .current: return model.state.current
        case .history: return model.state.history.first { $0.id == historyID } ?? model.state.history.first
        case .custom: return model.customReport
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 5) {
                    Text("配额账本与周报").font(.system(size: 25, weight: .bold))
                    Text("Codex · 本机原生日志 · 自动保留 90 天数字摘要")
                        .font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
                Button(action: refresh) { Label("刷新", systemImage: "arrow.clockwise") }
                Menu {
                    Button("从现存日志重建…") { confirmRebuild = true }
                        .disabled(model.isWorking || model.isLoading || !model.state.sourceAvailable)
                    Button("清空历史归档…", role: .destructive) { confirmClear = true }
                        .disabled(model.isWorking || model.isLoading)
                } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.borderlessButton).frame(width: 28)
                .help("账本管理")
            }.padding(24)
            HStack {
                Picker("查看范围", selection: $page) {
                    ForEach(LedgerPage.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                }.pickerStyle(.segmented).frame(maxWidth: 380)
                Spacer()
                Button { if let report { model.export(report) } } label: {
                    Label(model.isExporting ? "正在导出…" : "导出 HTML 与 CSV", systemImage: "square.and.arrow.up")
                }.disabled(report == nil || model.isExporting)
            }.padding(.horizontal, 24).padding(.bottom, 16)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    if let warning = model.state.warning { notice(warning, symbol: "exclamationmark.triangle") }
                    if let message = model.message { notice(message, symbol: "info.circle") }
                    if page == .history { historyPicker }
                    if page == .custom { rangePicker }
                    if let report { LedgerReportView(report: report) }
                    else if model.isLoading { ProgressView("正在读取原生日志…").frame(maxWidth: .infinity, minHeight: 240) }
                    else {
                        ContentUnavailableView(page == .history ? "暂无历史归档" : page == .custom ? "选择统计时段" : "周配额待更新",
                            systemImage: page == .history ? "clock.arrow.circlepath" : "chart.bar.doc.horizontal",
                            description: Text(emptyDescription))
                            .frame(maxWidth: .infinity, minHeight: 240)
                    }
                }.padding(24).frame(maxWidth: 1100)
                    .frame(maxWidth: .infinity)
            }
        }
        .frame(minWidth: 760, minHeight: 560)
        .background(Color(NSColor.windowBackgroundColor))
        .alert("清空历史归档？", isPresented: $confirmClear) {
            Button("取消", role: .cancel) { }
            Button("清空历史", role: .destructive) { model.clearHistory() }
        } message: { Text("仅删除历史数字摘要，保留当前周期、原始日志和导出文件。旧周期不会自动回填，可手动重建。") }
        .alert("从现存日志重建账本？", isPresented: $confirmRebuild) {
            Button("取消", role: .cancel) { }
            Button("重建") { model.rebuild() }
        } message: { Text("将使用当前可读取的 Codex 原生日志重新计算近 90 天账本；此前归档而原始日志已删除的摘要可能无法恢复。手动导出的文件不受影响。") }
    }

    private var emptyDescription: String {
        if page == .custom { return "按本地时间选择开始和结束，再生成本机用量报告；配额百分比不会被映射到任意时段。" }
        if page == .history { return "有可信周配额记录的已结束周期会保留在这里。可从现存日志重建，或先使用自定义时段查询。" }
        return model.state.sourceAvailable
            ? "没有可确认的当前周窗口，或旧记录已到重置时间。等待新的有效日志；不会自动把额度归零。也可使用自定义时段查询。"
            : "Codex 原生日志暂不可用。请检查日志目录和读取权限，历史归档仍可查看。"
    }

    private var historyPicker: some View {
        HStack {
            Text("归档周期").font(.headline)
            Picker("归档周期", selection: Binding(get: { historyID ?? model.state.history.first?.id ?? "" }, set: { historyID = $0 })) {
                ForEach(model.state.history) { item in
                    Text("\(item.startAt.formatted(date: .numeric, time: .omitted)) — \(item.endAt.formatted(date: .numeric, time: .omitted))").tag(item.id)
                }
            }.labelsHidden()
            Spacer()
            Text("\(model.state.history.count) 个周期").foregroundStyle(.secondary)
        }
    }

    private var rangePicker: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                DatePicker("开始", selection: $start, displayedComponents: [.date, .hourAndMinute])
                DatePicker("结束", selection: $end, displayedComponents: [.date, .hourAndMinute])
                Button("生成报告") { model.generateCustom(start: start, end: end) }
            }
            Text("输入时区：\(TimeZone.current.identifier)；统计区间包含开始、不包含结束。未来结束时间截到生成时刻。")
                .font(.caption).foregroundStyle(.secondary)
        }.padding(16).background(.background, in: RoundedRectangle(cornerRadius: 12))
    }
    private func notice(_ text: String, symbol: String) -> some View {
        Label(text, systemImage: symbol).font(.callout).foregroundStyle(.orange)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct LedgerReportView: View {
    let report: UsageReportSnapshot
    private var zone: TimeZone { TimeZone(identifier: report.timeZoneIdentifier) ?? .current }
    private func date(_ value: Date) -> String {
        let formatter = DateFormatter()
        formatter.timeZone = zone
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss XXX"
        return formatter.string(from: value)
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 16) {
                metric(report.isEnded ? "末次观测周配额" : "日志记录周配额",
                       report.quotaUsedPercent.map { String(format: "%.1f%%", $0) } ?? "未知", .blue)
                metric("本机 Token 合计", TokenFormatters.compactTokens(report.totals.totalTokens), .primary)
                metric("本机等值 USD", TokenFormatters.usd(report.totals.estimatedUSD), .orange)
            }
            VStack(alignment: .leading, spacing: 8) {
                Text(report.archived ? "历史数字归档" : report.kind == .weekly ? "本轮周配额" : "自定义时段")
                    .font(.headline)
                Text("\(report.cycleStartInferred ? "周期起点（推定）" : "开始")：\(date(report.startAt))")
                Text("\(report.kind == .weekly ? "周期结束／重置" : "结束")：\(date(report.endAt))")
                Text("统计截至：\(date(report.cutoffAt)) · \(report.timeZoneIdentifier)")
                if let observed = report.quotaRecordedAt { Text("配额记录：\(date(observed)) · 套餐 \(report.planType ?? "未知")") }
                Text(report.quotaStatus).foregroundStyle(.secondary)
            }.font(.callout).frame(maxWidth: .infinity, alignment: .leading).card()
            VStack(alignment: .leading, spacing: 10) {
                Text("Token 构成").font(.headline)
                counter("普通输入", report.totals.inputTokens)
                counter("缓存写入", report.totals.cacheWriteTokens)
                counter("缓存读取", report.totals.cacheReadTokens)
                counter("输出", report.totals.outputTokens)
                Divider()
                Text("推理 \(report.totals.reasoningTokens.formatted()) 已包含于输出，不重复累计。缓存命中 \(TokenFormatters.percent(report.totals.cacheHitRatio))；\(report.totals.sessions) 个会话，\(report.totals.events) 条增量记录。")
                    .font(.caption).foregroundStyle(.secondary)
            }.card()
            VStack(alignment: .leading, spacing: 10) {
                Text("模型明细").font(.headline)
                if report.models.isEmpty { Text("此区间暂无可确认的用量记录。").foregroundStyle(.secondary) }
                ForEach(report.models) { item in
                    HStack {
                        Text(item.model).lineLimit(1).help(item.pricingBasis)
                        Spacer()
                        Text(item.totals.totalTokens.formatted()).monospacedDigit()
                        Text(TokenFormatters.usd(item.totals.estimatedUSD)).monospacedDigit().frame(width: 90, alignment: .trailing)
                    }.font(.callout)
                }
                Text("按模型分别计算会话数，不应跨模型相加。金额仅为 \(report.pricingVersion) 兼容估算，非实际账单或套餐固定美元额度。")
                    .font(.caption).foregroundStyle(.secondary)
            }.card()
            VStack(alignment: .leading, spacing: 10) {
                Text("每日明细").font(.headline)
                ForEach(report.days) { item in
                    HStack {
                        Text(item.dateLabel)
                        Spacer()
                        Text(item.totals.totalTokens.formatted()).monospacedDigit()
                        Text(TokenFormatters.usd(item.totals.estimatedUSD)).monospacedDigit().frame(width: 90, alignment: .trailing)
                    }.font(.callout)
                }
                if report.days.isEmpty { Text("暂无每日用量。").foregroundStyle(.secondary) }
            }.card()
            ForEach(report.warnings, id: \.self) { Text($0).font(.caption).foregroundStyle(.secondary) }
        }
    }
    private func metric(_ title: String, _ value: String, _ color: Color) -> some View {
        VStack(alignment: .leading, spacing: 9) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Text(value).font(.system(size: 26, weight: .semibold, design: .rounded)).foregroundStyle(color)
                .monospacedDigit().lineLimit(1)
        }.frame(maxWidth: .infinity, alignment: .leading).card()
    }
    private func counter(_ title: String, _ count: Int) -> some View {
        HStack { Text(title); Spacer(); Text(count.formatted()).monospacedDigit() }.font(.callout)
    }
}

private extension View {
    func card() -> some View {
        padding(18).background(Color(NSColor.controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
    }
}
