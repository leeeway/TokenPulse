using System.Globalization;

namespace TokenPulse.Core;

public static class Providers
{
    public const string Claude = "claude_code", Codex = "codex", Antigravity = "antigravity", Custom = "custom";
    public static bool IsKnown(string value) => value is Claude or Codex or Antigravity or Custom or "cursor" or "ollama";
    public static string Name(string value) => value switch
    {
        Claude => "Claude Code", Codex => "OpenAI Codex", Antigravity => "Antigravity",
        "ollama" => "Ollama (Local)", "cursor" => "Cursor", _ => "Custom Tool"
    };
}

public enum TimeWindow { Today, SevenDays, ThirtyDays, All }

public sealed record UsageEvent(string Id, string Provider, string Model, DateTimeOffset Timestamp,
    string SessionId, long InputTokens, long CacheWriteTokens, long CacheReadTokens,
    long OutputTokens, long ReasoningTokens, double CostUSD, double SavedByCacheUSD)
{
    // File modification time is only a parser fallback, never evidence of when usage occurred.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasKnownTimestamp { get; init; } = true;
    public long UpstreamTokens => InputTokens + CacheWriteTokens + CacheReadTokens;
    public long TotalTokens => UpstreamTokens + OutputTokens;

    public static UsageEvent Create(string id, string provider, string model, DateTimeOffset timestamp,
        string sessionId, long input, long write, long read, long output, long reasoning = 0)
    {
        input = Math.Max(0, input); write = Math.Max(0, write); read = Math.Max(0, read);
        output = Math.Max(0, output); reasoning = Math.Max(0, reasoning);
        var (cost, saved) = Pricing.Estimate(provider, model, input, write, read, output);
        return new(id, provider, model, timestamp, sessionId, input, write, read, output, reasoning, cost, saved);
    }
}

public sealed record QuotaWindow(double UsedPercent, int? WindowMinutes, DateTimeOffset? ResetsAt)
{
    public bool IsExpired(DateTimeOffset now) => ResetsAt is { } reset && reset <= now;
}
public sealed record RateLimit(string Provider, string? PlanType, QuotaWindow Primary,
    QuotaWindow? Secondary, DateTimeOffset UpdatedAt);
public sealed record ParsedLog(IReadOnlyList<UsageEvent> Events, RateLimit? RateLimit = null)
{
    public int MalformedLines { get; init; }
    public int PendingLines { get; init; }
}
public enum SourceState { Ready, Missing, Error, Disabled, Warning }
public sealed record SourceStatus(string Provider, string Path, SourceState State, string Message);
public sealed record ScanResult(IReadOnlyList<UsageEvent> Events, IReadOnlyList<RateLimit> RateLimits,
    IReadOnlyList<SourceStatus> Statuses);
public sealed record LocalModel(string Name, long SizeBytes, long VramBytes);
public sealed record OllamaResult(IReadOnlyList<LocalModel> Models, string Status);

public sealed record UsageTotals(long Input, long CacheWrite, long CacheRead, long Output,
    long Reasoning, double Cost, double Saved, int Sessions, int Events)
{
    public long Upstream => Input + CacheWrite + CacheRead;
    public long Total => Upstream + Output;
    public double CacheHitRatio => Upstream > 0 ? (double)CacheRead / Upstream : 0;
    public static UsageTotals From(IEnumerable<UsageEvent> source)
    {
        var accumulator = new UsageAccumulator();
        foreach (var e in source) accumulator.Add(e);
        return accumulator.Build();
    }
}

internal sealed class UsageAccumulator
{
    private long input, write, read, output, reasoning;
    private double cost, saved;
    private int events;
    private readonly HashSet<(string Provider, string Session)> sessions = [];
    public void Add(UsageEvent e)
    {
        checked
        {
            input += e.InputTokens; write += e.CacheWriteTokens; read += e.CacheReadTokens;
            output += e.OutputTokens; reasoning += e.ReasoningTokens; events++;
        }
        cost += e.CostUSD; saved += e.SavedByCacheUSD; sessions.Add((e.Provider, e.SessionId));
    }
    public UsageTotals Build() => new(input, write, read, output, reasoning, cost, saved, sessions.Count, events);
}
public sealed record ModelSummary(string Provider, string Model, UsageTotals Totals)
{
    public DateTimeOffset? LastActivityAt { get; init; }
}
public sealed record ProviderSummary(string Provider, UsageTotals Totals, string[] TopModels, RateLimit? RateLimit)
{
    public IReadOnlyList<ModelSummary> Models { get; init; } = [];
    public DateTimeOffset? LastActivityAt { get; init; }
}

public sealed record DailyPoint(DateTime Date, long Upstream, long Output, double Cost)
{
    public long Total => Upstream + Output;
}
public sealed record DashboardSnapshot(DateTimeOffset GeneratedAt, UsageTotals Totals,
    IReadOnlyList<ProviderSummary> Providers, IReadOnlyList<DailyPoint> Trend, UsageTotals Recent)
{
    public BurnRateThresholds AlertThresholds { get; init; } = new();
    public int UnknownTimestampEvents { get; init; }
    public bool IsHighBurnRate => AlertThresholds.IsExceeded(Recent);
}

public sealed record BurnRateThresholds(long Tokens = 250_000, double USD = 1.50)
{
    public void Validate()
    {
        if (Tokens < 0 || !double.IsFinite(USD) || USD < 0)
            throw new ArgumentException("提醒阈值必须为非负数；Token 须为整数。填 0 可关闭对应提醒。");
    }
    public bool IsExceeded(UsageTotals usage) => Tokens > 0 && usage.Total >= Tokens || USD > 0 && usage.Cost >= USD;
}

public static class Formatting
{
    public static string Tokens(long count) => count switch
    {
        >= 1_000_000_000 => (count / 1e9).ToString("F2", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (count / 1e6).ToString("F2", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (count / 1e3).ToString("F1", CultureInfo.InvariantCulture) + "K",
        _ => count.ToString(CultureInfo.InvariantCulture)
    };
    public static string Usd(double value) => "$" + value.ToString(value >= 100 ? "F1" : value >= .01 || value == 0 ? "F2" : "F4", CultureInfo.InvariantCulture);
}
