using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenPulse.Core;

public sealed record QuotaObservation(DateTimeOffset RecordedAtUtc, string WindowRole, int WindowMinutes,
    DateTimeOffset ResetsAtUtc, double UsedPercent, string? PlanType)
{
    [JsonIgnore] public DateTimeOffset StartUtc => ResetsAtUtc.AddMinutes(-WindowMinutes);
    [JsonIgnore] public bool IsWeekly => WindowMinutes == 10080;
    [JsonIgnore] public string Key => ResetsAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "/10080";
}
public sealed record QuotaTotals(long Input, long CacheWrite, long CacheRead, long Output, long Reasoning,
    double EstimatedUSD, double SavedUSD, int Sessions, int Events)
{
    [JsonIgnore] public long Total => Input + CacheWrite + CacheRead + Output;
    [JsonIgnore] public double CacheHitRatio => Input + CacheWrite + CacheRead > 0 ? (double)CacheRead / (Input + CacheWrite + CacheRead) : 0;
    public static QuotaTotals From(IEnumerable<UsageEvent> values)
    {
        var total = UsageTotals.From(values);
        return new(total.Input, total.CacheWrite, total.CacheRead, total.Output, total.Reasoning, total.Cost, total.Saved, total.Sessions, total.Events);
    }
}
public sealed record QuotaModelTotal(string Model, QuotaTotals Totals);
public sealed record QuotaDayTotal(DateTime Date, QuotaTotals Totals);
public sealed record QuotaReport(string Mode, DateTimeOffset GeneratedAtUtc, DateTimeOffset StartUtc,
    DateTimeOffset EndUtc, DateTimeOffset StatisticsEndUtc, QuotaObservation? Quota, QuotaTotals Totals,
    IReadOnlyList<QuotaModelTotal> Models, IReadOnlyList<QuotaDayTotal> Days, string Coverage, string PricingVersion = "compat-v1")
{
    public DateTimeOffset? FirstObserved100AtUtc { get; init; }
    public string PricingBasis { get; init; } = "内置兼容估算；不是当前官方报价或实际账单";
    public string TimeZoneId { get; init; } = "UTC";
    public int UtcOffsetMinutes { get; init; }
    public string StartLocal { get; init; } = "";
    public string EndLocal { get; init; } = "";
    public string StatisticsEndLocal { get; init; } = "";
}
public sealed record QuotaCycle(string Key, QuotaReport Report, bool Archived);
public sealed record QuotaLedgerDocument(int SchemaVersion, string PricingVersion, DateTimeOffset? ClearedEndedBeforeUtc,
    DateTimeOffset? LastBuiltAtUtc, IReadOnlyList<QuotaCycle> Cycles)
{
    public static QuotaLedgerDocument Empty => new(1, Pricing.CatalogVersion, null, null, []);
}
public sealed record QuotaLedgerView(IReadOnlyList<QuotaCycle> Cycles, QuotaReport? Current, bool NativeSourceAvailable,
    string Status, bool StoreCorrupt, DateTimeOffset? ClearedEndedBeforeUtc);

/// <summary>Native-only UTC aggregation. Reports are immutable snapshots and never contain raw identifiers.</summary>
public static class QuotaReports
{
    public static QuotaReport Build(IReadOnlyList<UsageEvent> nativeEvents, DateTimeOffset start, DateTimeOffset end,
        DateTimeOffset snapshot, string mode, QuotaObservation? quota, string coverage,
        IReadOnlyList<QuotaObservation>? observations = null, TimeZoneInfo? timeZone = null)
    {
        start = start.ToUniversalTime(); end = end.ToUniversalTime(); snapshot = snapshot.ToUniversalTime();
        if (start >= end) throw new ArgumentException("开始时间必须早于结束时间");
        var stop = end < snapshot ? end : snapshot;
        if (stop < start) throw new ArgumentException("统计区间不能完全位于未来");
        var zone = timeZone ?? TimeZoneInfo.Utc;
        var events = nativeEvents.Where(e => e.HasKnownTimestamp && e.Provider == Providers.Codex
            && e.Timestamp >= start && e.Timestamp < stop).ToArray();
        return new(mode, snapshot, start, end, stop, quota, QuotaTotals.From(events),
            events.GroupBy(e => e.Model, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new QuotaModelTotal(g.Key, QuotaTotals.From(g))).ToArray(),
            events.GroupBy(e => TimeZoneInfo.ConvertTime(e.Timestamp, zone).Date).OrderBy(g => g.Key)
                .Select(g => new QuotaDayTotal(g.Key, QuotaTotals.From(g))).ToArray(), coverage)
        {
            TimeZoneId = zone.Id,
            UtcOffsetMinutes = (int)zone.GetUtcOffset(snapshot).TotalMinutes,
            StartLocal = TimeZoneInfo.ConvertTime(start, zone).ToString("O", CultureInfo.InvariantCulture),
            EndLocal = TimeZoneInfo.ConvertTime(end, zone).ToString("O", CultureInfo.InvariantCulture),
            StatisticsEndLocal = TimeZoneInfo.ConvertTime(stop, zone).ToString("O", CultureInfo.InvariantCulture),
            PricingBasis = Pricing.EstimateNotice + "\n" + string.Join("\n\n", events.Select(e => e.Model).Distinct(StringComparer.Ordinal)
                .OrderBy(m => m, StringComparer.Ordinal).Select(m => Pricing.Basis(Providers.Codex, m))),
            FirstObserved100AtUtc = observations?.Where(o => o.IsWeekly && o.RecordedAtUtc >= start && o.RecordedAtUtc < stop
                && o.ResetsAtUtc == end && o.UsedPercent == 100).Select(o => (DateTimeOffset?)o.RecordedAtUtc).Min()
        };
    }
}

/// <summary>Versioned, numeric-only ledger. A corrupt file is never overwritten by automatic refresh.</summary>
public sealed class QuotaLedgerStore(string? path = null)
{
    public string FilePath { get; } = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenPulse", "ledger", "ledger.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public (QuotaLedgerDocument Document, string? Error) Load()
    {
        try
        {
            var document = JsonSerializer.Deserialize<QuotaLedgerDocument>(File.ReadAllText(FilePath), Json) ?? throw new JsonException("账本为空");
            Validate(document);
            return (document, null);
        }
        catch (FileNotFoundException) { return (QuotaLedgerDocument.Empty, null); }
        catch (DirectoryNotFoundException) { return (QuotaLedgerDocument.Empty, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException)
        { return (QuotaLedgerDocument.Empty, "账本读取失败，原文件已保留；请手动重建：" + ex.Message); }
    }
    private static void Validate(QuotaLedgerDocument d)
    {
        if (d.SchemaVersion != 1 || string.IsNullOrWhiteSpace(d.PricingVersion) || d.Cycles is null || d.Cycles.Count > 200
            || d.Cycles.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != d.Cycles.Count) throw new JsonException("不支持的账本格式");
        foreach (var c in d.Cycles)
        {
            var r = c.Report;
            if (r is null || r.Quota is not { } q || !q.IsWeekly || q.WindowRole is not ("primary" or "secondary")
                || !double.IsFinite(q.UsedPercent) || q.UsedPercent is < 0 or > 100 || c.Key != q.Key
                || r.StartUtc != q.StartUtc || r.EndUtc != q.ResetsAtUtc || r.StartUtc >= r.EndUtc
                || r.StatisticsEndUtc < r.StartUtc || r.StatisticsEndUtc > r.EndUtc || r.StatisticsEndUtc > r.GeneratedAtUtc
                || r.Models is null || r.Days is null || r.Coverage is null || string.IsNullOrWhiteSpace(r.PricingVersion) || r.PricingBasis is null || r.TimeZoneId is null)
                throw new JsonException("账本周期无效");
            ValidateTotals(r.Totals);
            foreach (var model in r.Models) { if (model.Model is null) throw new JsonException("模型名称缺失"); ValidateTotals(model.Totals); }
            foreach (var day in r.Days) ValidateTotals(day.Totals);
        }
    }
    private static void ValidateTotals(QuotaTotals t)
    {
        if (t is null || t.Input < 0 || t.CacheWrite < 0 || t.CacheRead < 0 || t.Output < 0 || t.Reasoning < 0
            || t.Sessions < 0 || t.Events < 0 || !double.IsFinite(t.EstimatedUSD) || t.EstimatedUSD < 0
            || !double.IsFinite(t.SavedUSD) || t.SavedUSD < 0) throw new JsonException("账本数值无效");
        checked { _ = t.Input + t.CacheWrite + t.CacheRead + t.Output; }
    }
    public void Save(QuotaLedgerDocument document, bool explicitRecovery = false)
    {
        Validate(document);
        if (!explicitRecovery && Load().Error is not null) throw new IOException("原账本损坏，自动保存已暂停；请手动重建");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));
            if (explicitRecovery && File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".backup-" + Guid.NewGuid().ToString("N"));
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Serial session state, with no additive merge of usage across rescans.</summary>
public sealed class QuotaLedgerSession
{
    private readonly object gate = new();
    private readonly QuotaLedgerStore store;
    private QuotaLedgerDocument document;
    private ScanResult? lastScan;
    private bool enabled;
    private DateTimeOffset lastSnapshot;
    private string? warning;
    private bool corrupt;
    private long generation;
    public long Generation { get { lock (gate) return generation; } }
    public void Suspend(bool sourceEnabled)
    {
        lock (gate) { generation++; enabled = sourceEnabled; lastScan = null; }
    }
    public QuotaLedgerSession(QuotaLedgerStore? storage = null)
    {
        store = storage ?? new();
        var loaded = store.Load(); document = loaded.Document; warning = loaded.Error; corrupt = warning is not null;
    }
    private bool SourceAvailable => enabled && lastScan?.Statuses.Any(s => s.Provider == Providers.Codex && s.State is SourceState.Ready or SourceState.Warning) == true;
    public void Update(ScanResult scan, bool sourceEnabled, DateTimeOffset snapshot, bool rebuild = false, long? expectedGeneration = null)
    {
        lock (gate)
        {
            if (expectedGeneration is { } expected && expected != generation) return;
            lastScan = scan; enabled = sourceEnabled; lastSnapshot = snapshot.ToUniversalTime();
            var retained = (rebuild ? Array.Empty<QuotaCycle>() : document.Cycles)
                .Where(c => c.Report.EndUtc > lastSnapshot.AddDays(-90)).ToDictionary(c => c.Key, StringComparer.Ordinal);
            if (SourceAvailable)
            {
                var groups = scan.QuotaObservations.Where(o => o.IsWeekly && o.RecordedAtUtc <= lastSnapshot)
                    .GroupBy(o => o.Key).OrderBy(g => g.Key, StringComparer.Ordinal);
                foreach (var group in groups)
                {
                    var all = group.OrderBy(o => o.RecordedAtUtc).ThenBy(o => o.WindowRole, StringComparer.Ordinal).ToArray();
                    var first = all[0]; var stop = first.ResetsAtUtc < lastSnapshot ? first.ResetsAtUtc : lastSnapshot;
                    var observations = all.Where(o => o.RecordedAtUtc >= first.StartUtc && o.RecordedAtUtc < stop).ToArray();
                    if (observations.Length == 0 || first.ResetsAtUtc <= lastSnapshot.AddDays(-90)) continue;
                    if (retained.TryGetValue(group.Key, out var old) && old.Archived && !rebuild) continue;
                    if (!retained.ContainsKey(group.Key) && !rebuild && document.ClearedEndedBeforeUtc is { } cleared && first.ResetsAtUtc <= cleared) continue;
                    var latest = observations[^1];
                    var coverage = "仅本机现存原生 Codex 日志；起点由重置时间减窗口长度推定；日志删除、截断或其他设备使用可能使记录不完整。";
                    if (scan.Statuses.Any(s => s.Provider == Providers.Codex && s.State == SourceState.Warning)) coverage += " 当前来源含解析警告。";
                    if (retained.TryGetValue(group.Key, out old) && scan.NativeCodexEvents.Count < old.Report.Totals.Events)
                        coverage += " 现存记录减少，本轮已重新计算，未累加旧摘要。";
                    var report = QuotaReports.Build(scan.NativeCodexEvents, first.StartUtc, first.ResetsAtUtc, lastSnapshot, "cycle", latest, coverage, observations, TimeZoneInfo.Local);
                    retained[group.Key] = new(group.Key, report, first.ResetsAtUtc <= lastSnapshot);
                }
            }
            // Ended entries lacking surviving logs are still retained as frozen numeric archives.
            foreach (var pair in retained.ToArray())
                if (!pair.Value.Archived && pair.Value.Report.EndUtc <= lastSnapshot)
                    retained[pair.Key] = pair.Value with { Archived = true };
            var candidate = new QuotaLedgerDocument(1, Pricing.CatalogVersion, rebuild ? null : document.ClearedEndedBeforeUtc,
                lastSnapshot, retained.Values.OrderByDescending(c => c.Report.EndUtc).ToArray());
            if (corrupt && !rebuild) return;
            try { store.Save(candidate, rebuild); document = candidate; corrupt = false; warning = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { warning = "账本保存失败，旧文件已保留：" + ex.Message; }
        }
    }
    public QuotaLedgerView View()
    {
        lock (gate)
        {
            var current = SourceAvailable && lastScan is { } scan ? Current(scan, lastSnapshot) : null;
            var status = warning ?? (!enabled ? "原生 Codex 来源已关闭，历史归档仍保留。"
                : !SourceAvailable ? "原生 Codex 来源读取失败或尚未扫描，当前周期不可确认。"
                : current is null ? "日志未提供可确认的当前周配额周期；不会推算新周期或归零。" : "仅本机原生 Codex 日志；账本保留最近 90 天。");
            return new(document.Cycles, current, SourceAvailable, status, corrupt, document.ClearedEndedBeforeUtc);
        }
    }
    private QuotaReport? Current(ScanResult scan, DateTimeOffset snapshot)
    {
        var observation = scan.QuotaObservations.Where(o => o.IsWeekly && o.RecordedAtUtc <= snapshot
            && o.RecordedAtUtc >= o.StartUtc && o.ResetsAtUtc > snapshot).OrderByDescending(o => o.RecordedAtUtc)
            .ThenBy(o => o.Key, StringComparer.Ordinal).FirstOrDefault();
        if (observation is null) return null;
        var conflict = scan.QuotaObservations.Any(o => o.IsWeekly && o.Key != observation.Key
            && o.StartUtc < observation.ResetsAtUtc && o.ResetsAtUtc > observation.StartUtc && o.RecordedAtUtc <= snapshot);
        var report = QuotaReports.Build(scan.NativeCodexEvents, observation.StartUtc, observation.ResetsAtUtc, snapshot,
            "current", observation, "仅本机现存原生 Codex 日志；周期起点为推定，金额为兼容估算。"
            + (conflict ? " 观察到不同重置时间的重叠周期，请勿跨周期相加。" : ""), scan.QuotaObservations, TimeZoneInfo.Local);
        return report;
    }
    public QuotaReport Custom(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        lock (gate)
        {
            if (!SourceAvailable || lastScan is null) throw new InvalidOperationException("原生 Codex 来源当前不可用");
            return QuotaReports.Build(lastScan.NativeCodexEvents, startUtc, endUtc, lastSnapshot, "custom", null,
                "自定义区间，仅本机现存原生 Codex 日志；不对应固定套餐美元额度。", timeZone: TimeZoneInfo.Local);
        }
    }
    public void ClearEndedHistory()
    {
        lock (gate)
        {
            if (corrupt) throw new IOException("原账本损坏，请先手动重建");
            var candidate = document with { ClearedEndedBeforeUtc = lastSnapshot,
                Cycles = document.Cycles.Where(c => c.Report.EndUtc > lastSnapshot).ToArray() };
            store.Save(candidate); document = candidate;
        }
    }
    public void Rebuild()
    {
        lock (gate)
        {
            if (!SourceAvailable || lastScan is null) throw new InvalidOperationException("重建前请先启用并成功扫描原生 Codex 来源");
            Update(lastScan, enabled, lastSnapshot, true);
        }
    }
}
