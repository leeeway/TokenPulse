namespace TokenPulse.Core;

public static class Aggregation
{
    public static DashboardSnapshot Build(ScanResult scan, TimeWindow window, DateTimeOffset now, TimeZoneInfo? timeZone = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var zone = timeZone ?? TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var start = window switch { TimeWindow.SevenDays => today.AddDays(-6), TimeWindow.ThirtyDays => today.AddDays(-29), _ => today };
        var totals = new UsageAccumulator();
        var recent = new UsageAccumulator();
        var daily = Enumerable.Range(0, 7).Select(_ => new UsageAccumulator()).ToArray();
        var providers = new Dictionary<string, UsageAccumulator>();
        var models = new Dictionary<(string Provider, string Model), UsageAccumulator>();
        var trendStart = today.AddDays(-6);
        var recentStart = now.AddMinutes(-5);
        var recentEnd = now.AddSeconds(60);
        var unknownTimestampEvents = 0;
        // Convert each timestamp once. Preserve local calendar semantics across DST,
        // including the existing handling of future-dated log entries.
        foreach (var e in scan.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!e.HasKnownTimestamp)
            {
                unknownTimestampEvents++;
                if (window != TimeWindow.All) continue;
            }
            else
            {
                var date = TimeZoneInfo.ConvertTime(e.Timestamp, zone).Date;
                var dayIndex = (date - trendStart).Days;
                if (dayIndex is >= 0 and < 7) daily[dayIndex].Add(e);
                if (e.Timestamp >= recentStart && e.Timestamp <= recentEnd) recent.Add(e);
                if (window != TimeWindow.All && date < start) continue;
            }
            totals.Add(e);
            if (!providers.TryGetValue(e.Provider, out var provider)) providers[e.Provider] = provider = new();
            provider.Add(e);
            var key = (e.Provider, e.Model);
            if (!models.TryGetValue(key, out var model)) models[key] = model = new();
            model.Add(e);
        }
        var limits = scan.RateLimits.GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.MaxBy(r => r.UpdatedAt)!);
        var byProvider = models.Select(pair => new ModelSummary(pair.Key.Provider, pair.Key.Model, pair.Value.Build()))
            .GroupBy(m => m.Provider).ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Totals.Total).ThenBy(m => m.Model, StringComparer.Ordinal).ToArray());
        var summaries = providers.Keys.Concat(limits.Keys).Distinct().Select(p =>
        {
            var detail = byProvider.GetValueOrDefault(p) ?? [];
            return new ProviderSummary(p, providers.GetValueOrDefault(p)?.Build() ?? new UsageAccumulator().Build(),
                detail.Take(3).Select(m => m.Model).ToArray(), limits.GetValueOrDefault(p)) { Models = detail };
        }).OrderByDescending(p => p.Totals.Total).ThenBy(p => Providers.Name(p.Provider)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var trend = daily.Select((accumulator, index) =>
        {
            var total = accumulator.Build();
            return new DailyPoint(today.AddDays(index - 6), total.Upstream, total.Output, total.Cost);
        }).ToArray();
        return new(now, totals.Build(), summaries, trend, recent.Build()) { UnknownTimestampEvents = unknownTimestampEvents };
    }
}
