using System.Globalization;
using System.Net;
using System.Text;

namespace TokenPulse.Core;

/// <summary>Exports a frozen, numeric-only Codex report as an offline HTML/CSV pair.</summary>
public static class QuotaReportExport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Creates a unique report directory without replacing any existing export. Both files
    /// are prepared in a sibling staging directory, then exposed together by one rename.
    /// </summary>
    public static string Export(QuotaReport report, string parentDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        Validate(report);
        var parent = Path.GetFullPath(parentDirectory);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("请选择已存在的导出目录。");
        var id = Guid.NewGuid().ToString("N");
        var destination = Path.Combine(parent, "TokenPulse-Codex-" + report.GeneratedAtUtc.ToUniversalTime().ToString("yyyyMMdd-HHmmss", Invariant) + "-" + id[..8]);
        var staging = Path.Combine(parent, ".tokenpulse-report-" + id + ".tmp");
        Directory.CreateDirectory(staging);
        try
        {
            using (var html = NewWriter(Path.Combine(staging, "report.html"), bom: false)) WriteHtml(html, report);
            using (var csv = NewWriter(Path.Combine(staging, "report.csv"), bom: true)) WriteCsv(csv, report);
            // Directory.Move fails if the destination already exists. There is no overwrite path.
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static StreamWriter NewWriter(string path, bool bom) => new(
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(bom))
        { NewLine = "\r\n" };

    private static void Validate(QuotaReport report)
    {
        if (report.StartUtc >= report.EndUtc || report.StatisticsEndUtc < report.StartUtc || report.StatisticsEndUtc > report.EndUtc)
            throw new ArgumentException("报告时间范围无效，请重新生成报告。");
        if (report.Quota is { } quota && (!double.IsFinite(quota.UsedPercent) || quota.UsedPercent < 0 || quota.WindowMinutes <= 0))
            throw new ArgumentException("报告中的配额观察无效，请重新生成报告。");
        foreach (var total in new[] { report.Totals }.Concat(report.Models.Select(m => m.Totals)).Concat(report.Days.Select(d => d.Totals)))
        {
            if (total.Input < 0 || total.CacheWrite < 0 || total.CacheRead < 0 || total.Output < 0 || total.Reasoning < 0
                || total.Sessions < 0 || total.Events < 0 || !double.IsFinite(total.EstimatedUSD) || total.EstimatedUSD < 0
                || !double.IsFinite(total.SavedUSD) || total.SavedUSD < 0)
                throw new ArgumentException("报告中存在无效用量，请重新生成报告。");
        }
    }

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string N(long value) => value.ToString("N0", Invariant);
    private static string U(double value) => "$" + value.ToString("F2", Invariant);
    private static string D(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", Invariant);
    private static string O(DateTimeOffset value) => value.ToUniversalTime().ToString("O", Invariant);
    private static string Boundary(string frozen, DateTimeOffset utc) => string.IsNullOrWhiteSpace(frozen) ? D(utc) : frozen;
    private static string R(double value) => value.ToString("R", Invariant);
    private static string Mode(string value) => value.ToLowerInvariant() switch
    {
        "current" => "当前周配额周期", "history" or "historical" => "历史周配额周期", "cycle" => "周配额周期", "custom" => "自定义时间范围", _ => value
    };

    private static void WriteHtml(TextWriter writer, QuotaReport report)
    {
        writer.Write("""
<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src 'none'; base-uri 'none'; form-action 'none'">
<title>Codex 配额账本周报</title>
<style>
:root{color-scheme:light}*{box-sizing:border-box}body{margin:0;background:#f4f6fa;color:#152238;font:15px/1.65 "Segoe UI","Microsoft YaHei",system-ui,sans-serif}main{max-width:1020px;margin:32px auto;padding:38px 42px;background:#fff;border:1px solid #dfe5ee;border-radius:18px}h1{font-size:29px;line-height:1.3;margin:0 0 10px}h2{font-size:18px;margin:28px 0 12px}p{margin:10px 0}.muted{color:#59697e}.lead{font-size:17px}dl{display:grid;grid-template-columns:minmax(120px,180px) 1fr;gap:6px 18px;margin:22px 0}dt{color:#59697e}dd{margin:0;overflow-wrap:anywhere}.scroll{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:14px}th,td{padding:10px 12px;border:1px solid #dfe5ee;text-align:right;font-variant-numeric:tabular-nums;vertical-align:middle}th{background:#f0f4fa;color:#253d5a;font-weight:600}td:first-child,th:first-child{text-align:left;overflow-wrap:anywhere}tbody tr:nth-child(even){background:#fafbfd}.note{font-size:13px;color:#59697e}.total{font-weight:650}strong{font-weight:650}@media(max-width:640px){body{font-size:14px}main{margin:0;padding:24px 18px;border:0;border-radius:0}h1{font-size:24px}dl{grid-template-columns:1fr;gap:2px}dd{margin-bottom:10px}th,td{padding:8px;white-space:nowrap}}@media print{body{background:#fff;font-size:11pt}main{max-width:none;border:0;border-radius:0;margin:0;padding:0}h1{font-size:20pt}h2{break-after:avoid}.scroll{overflow:visible}table{font-size:9pt}tr{break-inside:avoid}thead{display:table-header-group}p,dl{orphans:2;widows:2}th,td{padding:6pt}main>section{break-inside:auto}}
</style></head><body><main>
<h1>Codex 配额账本周报</h1>
""");
        writer.Write("<p class=\"muted\">" + H(Mode(report.Mode)) + " · 冻结快照 " + H(D(report.GeneratedAtUtc)) + "</p>");
        writer.Write("<p class=\"lead\">本机本范围累计 <strong>" + N(report.Totals.Total) + " Token</strong>，兼容估算等值 <strong>" + U(report.Totals.EstimatedUSD) + " USD</strong>。</p>");
        writer.Write("<dl><dt>冻结时区</dt><dd>" + H(report.TimeZoneId) + " · 快照 UTC 偏移分钟 " + report.UtcOffsetMinutes.ToString(Invariant) + "</dd><dt>范围起点</dt><dd>" + H(Boundary(report.StartLocal, report.StartUtc)) + "</dd><dt>范围终点</dt><dd>" + H(Boundary(report.EndLocal, report.EndUtc)) + "</dd><dt>实际统计截止</dt><dd>" + H(Boundary(report.StatisticsEndLocal, report.StatisticsEndUtc)) + "，不包含截止时刻</dd>");
        if (report.Quota is { } quota)
        {
            writer.Write("<dt>配额观察</dt><dd>" + quota.UsedPercent.ToString("0.##", Invariant) + "% · " + H(D(quota.RecordedAtUtc)) + "</dd>");
            writer.Write("<dt>窗口与下次重置</dt><dd>" + quota.WindowMinutes.ToString(Invariant) + " 分钟 · " + H(D(quota.ResetsAtUtc)) + "</dd>");
            writer.Write("<dt>窗口角色与套餐</dt><dd>" + H(quota.WindowRole) + " · " + H(string.IsNullOrWhiteSpace(quota.PlanType) ? "未知套餐" : quota.PlanType) + "</dd>");
        }
        else writer.Write("<dt>周配额观察</dt><dd>本报告没有可确认的配额百分比或重置记录，不能据此判断额度余量。</dd>");
        writer.Write("<dt>费用依据</dt><dd>" + H(report.PricingVersion) + " · " + H(report.PricingBasis) + "</dd><dt>覆盖范围</dt><dd>" + H(report.Coverage) + "</dd></dl>");
        writer.Write("<p class=\"note\">统计边界以 UTC 半开区间 [起点, 实际统计截止) 比较；上方本地时刻和每日分组使用报告生成时冻结的时区。周周期起点由日志重置时刻减窗口长度推定；自定义范围以用户选择为准。配额到期但没有新记录时，不自行推算额度归零或生成新周期。</p>");
        writer.Write("<h2>Token 用量与等值美元</h2><div class=\"scroll\"><table><thead><tr><th>分类</th><th>Token 数量</th></tr></thead><tbody>");
        HtmlTokenRow(writer, "普通输入", report.Totals.Input);
        HtmlTokenRow(writer, "缓存写入", report.Totals.CacheWrite);
        HtmlTokenRow(writer, "缓存读取", report.Totals.CacheRead);
        HtmlTokenRow(writer, "输出", report.Totals.Output);
        HtmlTokenRow(writer, "合计", report.Totals.Total);
        writer.Write("</tbody></table></div><p>等值美元 <strong>" + U(report.Totals.EstimatedUSD) + " USD</strong>；缓存节省估算 " + U(report.Totals.SavedUSD) + " USD；缓存命中率 " + report.Totals.CacheHitRatio.ToString("P2", Invariant) + "。</p>");
        writer.Write("<p class=\"note\">推理 Token " + N(report.Totals.Reasoning) + " 已包含在输出中，不重复累计。" + report.Totals.Sessions.ToString(Invariant) + " 个会话，" + report.Totals.Events.ToString(Invariant) + " 条增量用量记录。</p>");
        writer.Write("<h2>模型明细</h2><div class=\"scroll\"><table><thead><tr><th>模型标识</th><th>Token 合计</th><th>等值美元 USD</th><th>缓存节省 USD</th><th>会话</th><th>记录</th></tr></thead><tbody>");
        foreach (var model in report.Models)
            writer.Write("<tr><td>" + H(model.Model) + "</td>" + HtmlTotals(model.Totals) + "</tr>");
        if (report.Models.Count == 0) writer.Write("<tr><td colspan=\"6\">本范围没有可确认时间的原生 Codex 用量记录。</td></tr>");
        writer.Write("</tbody></table></div><h2>每日明细</h2><div class=\"scroll\"><table><thead><tr><th>冻结时区本地日期</th><th>Token 合计</th><th>等值美元 USD</th><th>缓存节省 USD</th><th>会话</th><th>记录</th></tr></thead><tbody>");
        foreach (var day in report.Days)
            writer.Write("<tr><td>" + day.Date.ToString("yyyy-MM-dd", Invariant) + "</td>" + HtmlTotals(day.Totals) + "</tr>");
        if (report.Days.Count == 0) writer.Write("<tr><td colspan=\"6\">本范围没有已知时间记录。</td></tr>");
        writer.Write("</tbody></table></div><h2>口径与限制</h2>");
        writer.Write("<p class=\"note\">本报告仅包含本机原生 Codex 日志；不混入自定义 JSONL 中标记为 Codex 的记录，不包含其他设备或未落入本机日志的用量。未知时间的事件不计入本范围。服务端百分比与本机等值美元仅按时间对齐，不能据此认定套餐有固定美元额度。观察到 100% 后的真实用量仍计入，100% 观察并非精确停用时刻。</p>");
        writer.Write("<p class=\"note\">HTML 金额显示两位小数，分项舍入后可能与合计不同。CSV 保留未舍入数值。模型或每日会话数分别去重，不能跨行相加当作总会话数；CSV 的 total、model、day 是不同聚合视图，不能跨类型相加。</p>");
        writer.Write("<p class=\"note\">此文件可离线阅读和打印，不加载网络资源，不包含对话正文、本机日志路径或会话 ID。</p></main></body></html>");
    }

    private static void HtmlTokenRow(TextWriter writer, string label, long value) => writer.Write("<tr><td>" + H(label) + "</td><td>" + N(value) + "</td></tr>");
    private static string HtmlTotals(QuotaTotals totals) => "<td>" + N(totals.Total) + "</td><td>" + U(totals.EstimatedUSD) + "</td><td>" + U(totals.SavedUSD)
        + "</td><td>" + totals.Sessions.ToString(Invariant) + "</td><td>" + totals.Events.ToString(Invariant) + "</td>";

    private static void WriteCsv(TextWriter writer, QuotaReport report)
    {
        writer.WriteLine("RecordType,Mode,GeneratedAtUtc,PeriodStartUtc,PeriodEndUtc,StatisticsEndUtc,TimeZoneId,UtcOffsetMinutes,PeriodStartLocal,PeriodEndLocal,StatisticsEndLocal,QuotaRecordedAtUtc,QuotaUsedPercent,QuotaWindowRole,QuotaWindowMinutes,QuotaResetsAtUtc,PlanType,PricingVersion,PricingBasis,Coverage,Model,LocalDate,InputTokens,CacheWriteTokens,CacheReadTokens,OutputTokens,ReasoningTokensIncludedInOutput,TotalTokens,CacheHitRatio,EstimatedUSD,CacheSavedUSD,Sessions,Events");
        CsvRow(writer, report, "total", "", "", report.Totals);
        foreach (var model in report.Models) CsvRow(writer, report, "model", model.Model, "", model.Totals);
        foreach (var day in report.Days) CsvRow(writer, report, "day", "", day.Date.ToString("yyyy-MM-dd", Invariant), day.Totals);
    }

    private static void CsvRow(TextWriter writer, QuotaReport report, string kind, string model, string date, QuotaTotals t)
    {
        var q = report.Quota;
        writer.WriteLine(string.Join(",", new[]
        {
            Cell(kind), Cell(report.Mode), Cell(O(report.GeneratedAtUtc)), Cell(O(report.StartUtc)), Cell(O(report.EndUtc)), Cell(O(report.StatisticsEndUtc)),
            Cell(report.TimeZoneId), report.UtcOffsetMinutes.ToString(Invariant), Cell(report.StartLocal), Cell(report.EndLocal), Cell(report.StatisticsEndLocal),
            Cell(q is null ? "" : O(q.RecordedAtUtc)), q is null ? "" : R(q.UsedPercent), Cell(q?.WindowRole ?? ""), q?.WindowMinutes.ToString(Invariant) ?? "",
            Cell(q is null ? "" : O(q.ResetsAtUtc)), Cell(q?.PlanType ?? ""), Cell(report.PricingVersion), Cell(report.PricingBasis), Cell(report.Coverage), Cell(model), Cell(date),
            t.Input.ToString(Invariant), t.CacheWrite.ToString(Invariant), t.CacheRead.ToString(Invariant), t.Output.ToString(Invariant), t.Reasoning.ToString(Invariant),
            t.Total.ToString(Invariant), R(t.CacheHitRatio), R(t.EstimatedUSD), R(t.SavedUSD), t.Sessions.ToString(Invariant), t.Events.ToString(Invariant)
        }));
    }

    private static string Cell(string value)
    {
        var trimmed = value.TrimStart();
        if ((trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@') || value.Any(c => c is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
