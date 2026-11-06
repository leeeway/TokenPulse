import Foundation

/// Offline documents generated from one immutable report, never from a new scan.
public enum ReportExporter {
    public static func export(report: UsageReportSnapshot, to parentDirectory: URL) throws -> URL {
        let fm = FileManager.default
        var isDirectory: ObjCBool = false
        guard parentDirectory.isFileURL, fm.fileExists(atPath: parentDirectory.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw CocoaError(.fileNoSuchFile)
        }
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0); formatter.dateFormat = "yyyyMMdd-HHmmss"
        let name = "TokenPulse-Codex-" + formatter.string(from: report.generatedAt) + "-" + UUID().uuidString.prefix(8)
        let target = parentDirectory.appendingPathComponent(name, isDirectory: true)
        let staging = parentDirectory.appendingPathComponent(".tokenpulse-export-" + UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        defer { try? fm.removeItem(at: staging) }
        let htmlFile = staging.appendingPathComponent("report.html")
        let csvFile = staging.appendingPathComponent("usage.csv")
        try Data(html(report: report).utf8).write(to: htmlFile, options: .atomic)
        var csvData = Data([0xEF, 0xBB, 0xBF]); csvData.append(Data(csv(report: report).utf8))
        try csvData.write(to: csvFile, options: .atomic)
        try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: htmlFile.path)
        try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: csvFile.path)
        // Move within the chosen parent atomically, refusing an existing destination.
        try fm.moveItem(at: staging, to: target)
        return target
    }

    public static func csv(report: UsageReportSnapshot) -> String {
        let header = "PeriodStart,PeriodEnd,CutoffAt,GeneratedAt,TimeZone,TimeZoneOffsetSeconds,Provider,Model,InputTokens,CacheWriteTokens,CacheReadTokens,OutputTokens,ReasoningTokens,TotalTokens,CacheHitRatio,EstimatedUSD,CacheSavedUSD,Sessions,Events,PricingVersion\r\n"
        let iso = ISO8601DateFormatter(); iso.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let prefix = [iso.string(from: report.startAt), iso.string(from: report.endAt), iso.string(from: report.cutoffAt),
                      iso.string(from: report.generatedAt), report.timeZoneIdentifier]
        let rows = report.models.map { model in
            let t = model.totals
            return (prefix.map(csvCell) + [String(report.timeZoneOffsetSeconds), csvCell("OpenAI Codex"), csvCell(model.model),
                String(t.inputTokens), String(t.cacheWriteTokens), String(t.cacheReadTokens), String(t.outputTokens),
                String(t.reasoningTokens), String(t.totalTokens), String(t.cacheHitRatio), String(t.estimatedUSD),
                String(t.cacheSavedUSD), String(t.sessions), String(t.events), csvCell(report.pricingVersion)]).joined(separator: ",")
        }
        return header + rows.joined(separator: "\r\n") + (rows.isEmpty ? "" : "\r\n")
    }

    private static func csvCell(_ value: String) -> String {
        let first = value.trimmingCharacters(in: .whitespacesAndNewlines).first
        let literal = (first.map { "=+-@".contains($0) } ?? false) || value.contains(where: { $0 == "\t" || $0 == "\r" || $0 == "\n" })
        let text = (literal ? "'" : "") + value
        return "\"" + text.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }

    private static func escape(_ value: String) -> String {
        value.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&#39;")
    }

    public static func html(report r: UsageReportSnapshot) -> String {
        let zone = TimeZone(identifier: r.timeZoneIdentifier) ?? TimeZone(secondsFromGMT: r.timeZoneOffsetSeconds) ?? TimeZone(secondsFromGMT: 0)!
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.timeZone = zone
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss XXX"
        func date(_ value: Date) -> String { escape(formatter.string(from: value)) }
        func number(_ value: Int) -> String { String(value) }
        func money(_ value: Double) -> String { String(format: "$%.2f", locale: Locale(identifier: "en_US_POSIX"), value) }
        func row(_ label: String, _ count: Int, _ cost: Double) -> String {
            "<tr><th scope=\"row\">\(escape(label))</th><td>\(number(count))</td><td>\(money(cost))</td></tr>"
        }
        let t = r.totals
        let modelRows = r.models.map { model in
            "<tr><th scope=\"row\">\(escape(model.model))</th><td>\(model.totals.totalTokens)</td><td>\(money(model.totals.estimatedUSD))</td></tr>"
        }.joined()
        let dayRows = r.days.map { day in
            "<tr><th scope=\"row\">\(escape(day.dateLabel))</th><td>\(day.totals.totalTokens)</td><td>\(money(day.totals.estimatedUSD))</td></tr>"
        }.joined()
        let empty = "<tr><td colspan=\"3\" class=\"empty\">此范围没有可确认时间的本机原生 Codex 用量记录。</td></tr>"
        let quota = r.quotaUsedPercent.map { String(format: "%.1f%%", locale: Locale(identifier: "en_US_POSIX"), $0) } ?? "信息不足"
        let percentLabel = r.archived || r.isEnded ? "末次观测周配额" : "日志周配额"
        let details = r.models.map { "<li><strong>\(escape($0.model))</strong><br>\(escape($0.pricingBasis).replacingOccurrences(of: "\n", with: "<br>"))</li>" }.joined()
        let warnings = (r.warnings + ["金额按完整数值求和后显示舍入，分项四舍五入后可能与合计有微小差异。", "各模型会话数分别去重，不能将模型会话数相加作为总会话数。"])
            .map { "<li>\(escape($0))</li>" }.joined()
        return """
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'"><title>TokenPulse Codex 配额与用量报告</title><style>
        *{box-sizing:border-box}body{margin:0;background:#f4f6fb;color:#172033;font:15px/1.65 -apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}main{max-width:960px;margin:38px auto;padding:42px 48px;background:#fff;border:1px solid #e4e8f0;border-radius:18px}header p{color:#667085;margin:5px 0}h1{font-size:29px;line-height:1.3;margin:0 0 12px}h2{font-size:19px;margin:28px 0 10px}.eyebrow{font-size:12px;letter-spacing:.1em;color:#5667a2}.metrics{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px;margin:22px 0}.metric{padding:16px;background:#f5f7fc;border-radius:12px}.metric span{display:block;font-size:12px;color:#667085}.metric strong{font-size:24px;overflow-wrap:anywhere}table{width:100%;border-collapse:collapse;margin:8px 0 14px;font-variant-numeric:tabular-nums}th,td{padding:11px 12px;border-bottom:1px solid #e4e8f0;text-align:right}th:first-child,td:first-child{text-align:left;overflow-wrap:anywhere}thead{background:#eef2fa}tbody th{font-weight:500}p,li{overflow-wrap:anywhere}.note,.empty{color:#667085;font-size:13px}.empty{text-align:center!important}ul{padding-left:22px}code{font-family:ui-monospace,monospace}footer{margin-top:26px;color:#667085;font-size:12px}@media(max-width:600px){main{margin:0;padding:24px 16px;border:0;border-radius:0}.metrics{grid-template-columns:1fr}h1{font-size:24px}th,td{padding:9px 6px;font-size:13px}}@media print{body{background:#fff}main{max-width:none;margin:0;padding:0;border:0;border-radius:0}h1,h2{break-after:avoid}thead{display:table-header-group}tr{break-inside:avoid}body{font-size:11pt}.metrics{gap:8px}.metric{border:1px solid #d9dfe9;background:#fff}footer{font-size:9pt}}
        </style></head><body><main><header><p class="eyebrow">TOKENPULSE · CODEX · LOCAL REPORT</p><h1>\(r.kind == .weekly ? "Codex 本轮周配额与等值美元报告" : "Codex 自定义时段用量报告")</h1><p>统计截至 \(date(r.cutoffAt)) · 时区 \(escape(r.timeZoneIdentifier))</p><p>生成于 \(date(r.generatedAt)) · \(r.archived ? "本地数字归档" : "本机原生日志快照")</p></header>
        <section class="metrics" aria-label="统计摘要"><div class="metric"><span>\(percentLabel)</span><strong>\(quota)</strong></div><div class="metric"><span>本机 Token 合计</span><strong>\(t.totalTokens)</strong></div><div class="metric"><span>等效 API 金额 USD</span><strong>\(money(t.estimatedUSD))</strong></div></section>
        <p>\(r.cycleStartInferred ? "推定周期" : "查询范围")：\(date(r.startAt)) 至 \(date(r.endAt))</p><p class="note">\(r.cycleStartInferred ? "周期起点由日志重置时刻减去 10080 分钟推算。" : "仅按所选时段汇总用量，不将服务端配额百分比与自定义时段强行对应。") \(escape(r.quotaStatus))</p>
        <p class="note">配额记录：\(r.quotaRecordedAt.map(date) ?? "日志未提供") · 重置时刻：\(r.nextResetAt.map(date) ?? "日志未提供") · 套餐标识：\(escape(r.planType ?? "未知"))</p>
        <h2>Token 构成</h2><table><thead><tr><th>用量分类</th><th>Token 数量</th><th>等值美元 USD</th></tr></thead><tbody>\(row("普通输入", t.inputTokens, t.inputCostUSD))\(row("缓存写入", t.cacheWriteTokens, t.cacheWriteCostUSD))\(row("缓存读取", t.cacheReadTokens, t.cacheReadCostUSD))\(row("输出", t.outputTokens, t.outputCostUSD))\(row("合计", t.totalTokens, t.estimatedUSD))</tbody></table>
        <p class="note">\(t.sessions) 个会话 · \(t.events) 条增量记录。推理 Token \(t.reasoningTokens) 已包含在输出中，不重复累计。未知时间记录 \(r.unknownTimestampEvents) 条已排除。</p>
        <h2>模型明细</h2><table><thead><tr><th>模型标识</th><th>Token 数量</th><th>等值美元 USD</th></tr></thead><tbody>\(modelRows.isEmpty ? empty : modelRows)</tbody></table>
        <h2>每日明细</h2><table><thead><tr><th>本地日期</th><th>Token 数量</th><th>等值美元 USD</th></tr></thead><tbody>\(dayRows.isEmpty ? empty : dayRows)</tbody></table>
        <h2>估算依据</h2><p>费率版本 <code>\(escape(r.pricingVersion))</code>。金额为内置兼容估算，并非实时官方报价、实际账单或套餐固定美元额度。</p><ul class="note">\(details)</ul><h2>覆盖范围与说明</h2><ul class="note">\(warnings)</ul><footer>TokenPulse · 本地只读统计 · HTML 与 CSV 来自同一冻结报告 · 可通过浏览器打印为 PDF</footer></main></body></html>
        """
    }
}
