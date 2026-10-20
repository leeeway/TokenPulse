namespace TokenPulse.Core;

public static class Aggregation
{
    public static DashboardSnapshot Build(ScanResult scan, TimeWindow window, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var start = window switch { TimeWindow.SevenDays => today.AddDays(-6), TimeWindow.ThirtyDays => today.AddDays(-29), _ => today };
        // Compare local calendar dates, preserving Swift's start-of-day / calendar-day semantics across DST.
        DateTime LocalDate(UsageEvent e) => TimeZoneInfo.ConvertTime(e.Timestamp, zone).Date;
        var filtered = scan.Events.Where(e => window == TimeWindow.All || LocalDate(e) >= start).ToArray();
        var providers = filtered.Select(e => e.Provider).Concat(scan.RateLimits.Select(r => r.Provider)).Distinct()
            .Select(p =>
            {
                var events = filtered.Where(e => e.Provider == p).ToArray();
                return new ProviderSummary(p, UsageTotals.From(events), events.GroupBy(e => e.Model)
                    .OrderByDescending(g => g.Sum(e => e.TotalTokens)).ThenBy(g => g.Key).Take(3).Select(g => g.Key).ToArray(),
                    scan.RateLimits.Where(r => r.Provider == p).MaxBy(r => r.UpdatedAt));
            }).OrderByDescending(p => p.Totals.Total).ThenBy(p => Providers.Name(p.Provider)).ToArray();
        var trend = Enumerable.Range(-6, 7).Select(offset =>
        {
            var day = today.AddDays(offset);
            var totals = UsageTotals.From(scan.Events.Where(e => LocalDate(e) == day));
            return new DailyPoint(day, totals.Upstream, totals.Output, totals.Cost);
        }).ToArray();
        var recent = UsageTotals.From(scan.Events.Where(e => e.Timestamp >= now.AddMinutes(-5) && e.Timestamp <= now.AddSeconds(60)));
        return new(now, UsageTotals.From(filtered), providers, trend, recent);
    }
}
