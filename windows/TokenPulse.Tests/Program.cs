using System.Net;
using System.Text.Json;
using TokenPulse.Core;

// Dependency-free test executable: every failure returns a nonzero exit code to CI.
var failures = 0;
var passed = 0;
var now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Equal<T>(T expected, T actual) where T : notnull => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}");
void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < 1e-10, $"Expected {expected}; got {actual}");
string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".jsonl"));
ParsedLog Parse(string provider, string text) => LogParser.Parse(new StringReader(text), provider, "test-session", now);
async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Task Sync(Action test) { test(); return Task.CompletedTask; }

await Test("Claude Swift fixture: counters, costs and cache savings", () => Sync(() =>
{
    var result = Parse(Providers.Claude, Fixture("claude"));
    Equal(2, result.Events.Count);
    Equal(10_100L, result.Events[0].UpstreamTokens);
    Equal(300L, result.Events[0].OutputTokens);
    Equal(8_000L, result.Events[0].CacheReadTokens);
    Near(.0735, result.Events[0].CostUSD); Near(.108, result.Events[0].SavedByCacheUSD);
    Near(.07965, result.Events.Sum(e => e.CostUSD));
}));
await Test("Codex Swift fixture: cumulative deltas, reasoning and quota", () => Sync(() =>
{
    var result = Parse(Providers.Codex, Fixture("codex"));
    Equal(2, result.Events.Count);
    Equal(4000L, result.Events[0].InputTokens); Equal(6000L, result.Events[0].CacheReadTokens);
    Equal(3000L, result.Events[1].InputTokens); Equal(12000L, result.Events[1].CacheReadTokens);
    Equal(700L, result.Events[1].OutputTokens); Equal(200L, result.Events[1].ReasoningTokens);
    Equal(26200L, result.Events.Sum(e => e.TotalTokens));
    Near(.0326, result.Events.Sum(e => e.CostUSD)); Near(.027, result.Events.Sum(e => e.SavedByCacheUSD));
    Near(12.5, result.RateLimit!.Primary.UsedPercent); Near(4, result.RateLimit.Secondary!.UsedPercent);
    Equal("team", result.RateLimit.PlanType!); Equal(300, result.RateLimit.Primary.WindowMinutes!.Value);
    Equal(1792319147L, result.RateLimit.Primary.ResetsAt!.Value.ToUnixTimeSeconds());
}));
await Test("Codex duplicate and decreasing cumulative counters do not add usage", () => Sync(() =>
{
    var fixture = Fixture("codex");
    var result = Parse(Providers.Codex, fixture + fixture);
    Equal(26200L, result.Events.Sum(e => e.TotalTokens));
    Equal(2, result.Events.Count);
}));
await Test("Codex cache writes are separated from fresh input", () => Sync(() =>
{
    var log = """{"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":30,"cache_write_input_tokens":20,"output_tokens":10,"reasoning_output_tokens":5}}}}""";
    var e = Parse(Providers.Codex, log).Events.Single();
    Equal(50L, e.InputTokens); Equal(20L, e.CacheWriteTokens); Equal(110L, e.TotalTokens);
}));
await Test("Antigravity Swift fixture", () => Sync(() =>
{
    var e = Parse(Providers.Antigravity, Fixture("antigravity")).Events.Single();
    Equal(60000L, e.UpstreamTokens); Equal(400L, e.OutputTokens); Equal(60400L, e.TotalTokens);
    Near(.03388, e.CostUSD); Near(.04512, e.SavedByCacheUSD);
}));
await Test("Custom Swift camelCase contract preserves explicit costs", () => Sync(() =>
{
    var e = Parse(Providers.Custom, Fixture("custom")).Events.Single();
    Equal(250L, e.TotalTokens); Equal(10L, e.ReasoningTokens); Near(.123, e.CostUSD); Near(.045, e.SavedByCacheUSD);
    Equal(0, Parse(Providers.Custom, Fixture("custom").Replace("\"custom\"", "\"unsupported\"")).Events.Count);
    Equal(0, Parse(Providers.Custom, "{}").Events.Count);
}));
await Test("Malformed lines, non-objects and partial trailing lines are skipped", () => Sync(() =>
{
    var result = Parse(Providers.Claude, "broken\n[]\nnull\n{\"message\":1}\n" + Fixture("claude") + "{\"message\":");
    Equal(2, result.Events.Count);
}));
await Test("Missing dates use file timestamp and quota-only records remain visible", () => Sync(() =>
{
    var noDate = """{"message":{"usage":{"input_tokens":1}}}""";
    Equal(now, Parse(Providers.Claude, noDate).Events.Single().Timestamp);
    var log = """{"payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":95}}}}""";
    var result = Parse(Providers.Codex, log);
    var snapshot = Aggregation.Build(new([], [result.RateLimit!], []), TimeWindow.Today, now, TimeZoneInfo.Utc);
    Equal(1, snapshot.Providers.Count); Equal(0L, snapshot.Totals.Total);
    Equal(300, snapshot.Providers[0].RateLimit!.Primary.WindowMinutes!.Value);
}));
await Test("All compatibility pricing branches", () => Sync(() =>
{
    (string Provider, string Model, double Cost)[] cases =
    [ ("ollama", "opus", 0), ("custom", "haiku", 5.88), (Providers.Claude, "unknown", 22.05),
      (Providers.Codex, "unknown", 12.5), ("custom", "flash", 3.175), (Providers.Antigravity, "unknown", 12.81),
      ("custom", "unknown", 15.5) ];
    foreach (var c in cases) Near(c.Cost, Pricing.Estimate(c.Provider, c.Model, 1_000_000, 1_000_000, 1_000_000, 1_000_000).Cost);
}));
await Test("Calendar windows, independent trend and provider-scoped sessions", () => Sync(() =>
{
    var zone = TimeZoneInfo.CreateCustomTimeZone("Test +08", TimeSpan.FromHours(8), "Test +08", "Test +08");
    UsageEvent Event(string provider, string time) => UsageEvent.Create(time, provider, "x", DateTimeOffset.Parse(time), "same", 100, 0, 100, 10);
    var events = new[] { Event(Providers.Claude, "2026-10-17T15:59:59Z"), Event(Providers.Claude, "2026-10-17T16:00:00Z"),
        Event(Providers.Codex, "2026-10-18T01:00:00Z"), Event(Providers.Claude, "2026-10-11T16:00:00Z"),
        Event(Providers.Claude, "2026-09-18T16:00:00Z"), Event(Providers.Claude, "2026-09-18T15:59:59Z") };
    var scan = new ScanResult(events, [], []);
    var today = Aggregation.Build(scan, TimeWindow.Today, now, zone);
    Equal(2, today.Totals.Events); Equal(2, today.Totals.Sessions); Near(.5, today.Totals.CacheHitRatio);
    Equal(4, Aggregation.Build(scan, TimeWindow.SevenDays, now, zone).Totals.Events);
    Equal(5, Aggregation.Build(scan, TimeWindow.ThirtyDays, now, zone).Totals.Events);
    Equal(6, Aggregation.Build(scan, TimeWindow.All, now, zone).Totals.Events);
    Equal(7, today.Trend.Count); Equal(420L, today.Trend.Last().Total);
}));
await Test("DST calendar day and unordered high-burn events", () => Sync(() =>
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    var time = DateTimeOffset.Parse("2026-11-01T07:00:00Z");
    var first = UsageEvent.Create("a", Providers.Codex, "x", DateTimeOffset.Parse("2026-11-01T04:00:00Z"), "a", 1, 0, 0, 0);
    var recent = UsageEvent.Create("b", Providers.Codex, "x", time.AddMinutes(-1), "b", 250000, 0, 0, 0);
    var future = UsageEvent.Create("c", Providers.Codex, "x", time.AddMinutes(2), "c", 999999, 0, 0, 0);
    var snapshot = Aggregation.Build(new([first, recent, future], [], []), TimeWindow.Today, time, zone);
    Equal(3, snapshot.Totals.Events); Equal(250000L, snapshot.Recent.Total); Check(snapshot.IsHighBurnRate, "burn warning missing");
}));
await Test("Scanner append, half-line repair, truncate, delete, Unicode paths and repeated scans", () => Sync(() =>
{
    using var folder = new TestDirectory();
    var settings = folder.Settings;
    Directory.CreateDirectory(settings.ClaudeDirectory);
    var file = Path.Combine(settings.ClaudeDirectory, "中文 会话.jsonl");
    var lines = Fixture("claude").TrimEnd().Split('\n');
    File.WriteAllText(file, lines[0] + "\n");
    var scanner = new LogScanner();
    Equal(1, scanner.Scan(settings).Events.Count); Equal(1, scanner.Scan(settings).Events.Count);
    var split = lines[1].Length / 2;
    File.AppendAllText(file, lines[1][..split]); Equal(1, scanner.Scan(settings).Events.Count);
    File.AppendAllText(file, lines[1][split..] + "\n"); Equal(2, scanner.Scan(settings).Events.Count);
    File.WriteAllText(file, lines[1] + "\n"); Equal(1, scanner.Scan(settings).Events.Count);
    File.Delete(file); Equal(0, scanner.Scan(settings).Events.Count);
    Equal(SourceState.Missing, scanner.Scan(settings).Statuses.Single(s => s.Provider == Providers.Codex).State);
}));
await Test("Scanner distinguishes inaccessible/wrong paths from missing", () => Sync(() =>
{
    using var folder = new TestDirectory();
    Directory.CreateDirectory(folder.Settings.ClaudeDirectory);
    var file = Path.Combine(folder.Settings.ClaudeDirectory, "locked.jsonl");
    File.WriteAllText(file, Fixture("claude"));
    using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var scan = new LogScanner().Scan(folder.Settings);
    Equal(SourceState.Error, scan.Statuses.Single(s => s.Provider == Providers.Claude).State);
    Equal(0, scan.Events.Count);
    var wrong = folder.Settings with { CodexDirectory = file };
    Equal(SourceState.Error, new LogScanner().Scan(wrong).Statuses.Single(s => s.Provider == Providers.Codex).State);
}));
await Test("Scanner selects newest quota across files, recovers from removed sources", () => Sync(() =>
{
    using var folder = new TestDirectory();
    Directory.CreateDirectory(folder.Settings.CodexDirectory);
    File.WriteAllText(Path.Combine(folder.Settings.CodexDirectory, "one.jsonl"), Fixture("codex"));
    File.WriteAllText(Path.Combine(folder.Settings.CodexDirectory, "two.jsonl"), """{"timestamp":"2026-10-19T00:00:00Z","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":70}}}}""");
    var scanner = new LogScanner();
    Near(70, scanner.Scan(folder.Settings).RateLimits.Single().Primary.UsedPercent);
    Directory.Delete(folder.Settings.CodexDirectory, true);
    Equal(0, scanner.Scan(folder.Settings).RateLimits.Count);
    Directory.CreateDirectory(folder.Settings.CodexDirectory);
    File.WriteAllText(Path.Combine(folder.Settings.CodexDirectory, "one.jsonl"), Fixture("codex"));
    Near(12.5, scanner.Scan(folder.Settings).RateLimits.Single().Primary.UsedPercent);
}));
await Test("Antigravity hidden system directory and custom source discovery", () => Sync(() =>
{
    using var folder = new TestDirectory();
    var logs = Path.Combine(folder.Settings.AntigravityDirectory, "session-1", ".system_generated", "logs");
    Directory.CreateDirectory(logs);
    File.WriteAllText(Path.Combine(logs, "transcript.jsonl"), Fixture("antigravity"));
    File.WriteAllText(folder.Settings.CustomEventsFile, Fixture("custom"));
    var scan = new LogScanner().Scan(folder.Settings);
    Equal(2, scan.Events.Count); Equal("session-1", scan.Events.Single(e => e.Provider == Providers.Antigravity).SessionId);
}));
await Test("Settings atomic roundtrip and corrupt-file fallback without overwriting", () => Sync(() =>
{
    using var folder = new TestDirectory();
    var store = new SettingsStore(Path.Combine(folder.Path, "settings", "settings.json"));
    Check(!store.Load().Settings.StartWithWindows, "startup must default off");
    store.Save(folder.Settings); Equal(folder.Settings, store.Load().Settings);
    Check(!File.Exists(store.FilePath + ".tmp"), "temporary file left behind");
    File.WriteAllText(store.FilePath, "broken");
    Check(store.Load().Warning is not null, "corruption should be visible"); Equal("broken", File.ReadAllText(store.FilePath));
    var invalid = JsonSerializer.Serialize(folder.Settings).Replace(folder.Settings.ClaudeDirectory.Replace("\\", "\\\\"), "relative");
    File.WriteAllText(store.FilePath, invalid);
    Check(store.Load().Warning is not null, "relative paths should be rejected");
}));
await Test("Cancellation is propagated", () => Sync(() =>
{
    using var folder = new TestDirectory();
    using var source = new CancellationTokenSource(); source.Cancel();
    try { new LogScanner().Scan(folder.Settings, source.Token); throw new Exception("scan ignored cancellation"); }
    catch (OperationCanceledException) { }
    try { Aggregation.Build(new([], [], []), TimeWindow.All, now, cancellationToken: source.Token); throw new Exception("aggregation ignored cancellation"); }
    catch (OperationCanceledException) { }
}));
await Test("Ollama success, malformed response and disconnect clear models", async () =>
{
    var handler = new StubHandler((request, _) =>
    {
        Equal("http://127.0.0.1:11434/api/ps", request.RequestUri!.AbsoluteUri);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"models":[{"name":"local-model","size":2048,"size_vram":1024}]}""") });
    });
    using var client = new OllamaClient(handler);
    var result = await client.ReadAsync(); Equal(1, result.Models.Count); Equal(1024L, result.Models[0].VramBytes);
    handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("broken") });
    Equal(0, (await client.ReadAsync()).Models.Count);
    handler.Respond = (_, _) => throw new HttpRequestException("offline");
    Equal(0, (await client.ReadAsync()).Models.Count);
});
await Test("Ollama timeout and caller cancellation", async () =>
{
    using var client = new OllamaClient(new StubHandler(async (_, cancellation) =>
    {
        await Task.Delay(Timeout.Infinite, cancellation); return new HttpResponseMessage(HttpStatusCode.OK);
    }));
    Check((await client.ReadAsync()).Status.Contains("超时"), "timeout was not reported");
    using var source = new CancellationTokenSource(); source.Cancel();
    try { await client.ReadAsync(source.Token); throw new Exception("client ignored cancellation"); }
    catch (OperationCanceledException) { }
});

await Test("Single-pass aggregation matches reference for mixed histories and DST", () => Sync(() =>
{
    var random = new Random(42);
    var time = DateTimeOffset.Parse("2026-11-02T12:00:00Z");
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    var events = Enumerable.Range(0, 12000).Select(i => UsageEvent.Create(i.ToString(), i % 2 == 0 ? Providers.Claude : Providers.Codex,
        "model-" + i % 5, time.AddMinutes(-random.Next(-120, 60000)), "session-" + i % 19,
        random.Next(1000), random.Next(100), random.Next(500), random.Next(200), random.Next(30))).ToArray();
    UsageTotals Reference(IEnumerable<UsageEvent> source)
    {
        var rows = source.ToArray();
        return new(rows.Sum(e => e.InputTokens), rows.Sum(e => e.CacheWriteTokens), rows.Sum(e => e.CacheReadTokens),
            rows.Sum(e => e.OutputTokens), rows.Sum(e => e.ReasoningTokens), rows.Sum(e => e.CostUSD), rows.Sum(e => e.SavedByCacheUSD),
            rows.Select(e => (e.Provider, e.SessionId)).Distinct().Count(), rows.Length);
    }
    var today = TimeZoneInfo.ConvertTime(time, zone).Date;
    foreach (var period in Enum.GetValues<TimeWindow>())
    {
        var start = today.AddDays(period == TimeWindow.SevenDays ? -6 : period == TimeWindow.ThirtyDays ? -29 : 0);
        var selected = events.Where(e => period == TimeWindow.All || TimeZoneInfo.ConvertTime(e.Timestamp, zone).Date >= start).ToArray();
        var snapshot = Aggregation.Build(new(events, [], []), period, time, zone);
        Equal(Reference(selected), snapshot.Totals);
        Equal(Reference(events.Where(e => e.Timestamp >= time.AddMinutes(-5) && e.Timestamp <= time.AddSeconds(60))), snapshot.Recent);
        foreach (var day in snapshot.Trend)
        {
            var expected = Reference(events.Where(e => TimeZoneInfo.ConvertTime(e.Timestamp, zone).Date == day.Date));
            Equal(expected.Total, day.Total); Near(expected.Cost, day.Cost);
        }
        foreach (var provider in snapshot.Providers)
        {
            Equal(Reference(selected.Where(e => e.Provider == provider.Provider)), provider.Totals);
            foreach (var model in provider.Models)
                Equal(Reference(selected.Where(e => e.Provider == model.Provider && e.Model == model.Model)), model.Totals);
            Equal(provider.Totals.Total, provider.Models.Sum(m => m.Totals.Total));
        }
    }
}));
await Test("Models share sessions without double-counting provider totals; quota expiry boundary", () => Sync(() =>
{
    var rows = new[] { UsageEvent.Create("a", Providers.Codex, "a", now, "shared", 100, 10, 50, 20),
        UsageEvent.Create("b", Providers.Codex, "b", now, "shared", 200, 0, 0, 30),
        UsageEvent.Create("c", Providers.Codex, "old", now.AddDays(-1), "yesterday", 999, 0, 0, 0) };
    var provider = Aggregation.Build(new(rows, [], []), TimeWindow.Today, now, TimeZoneInfo.Utc).Providers.Single();
    Equal(2, provider.Models.Count); Equal(1, provider.Totals.Sessions);
    Equal("b", provider.Models[0].Model); Equal(2, provider.Models.Sum(m => m.Totals.Sessions));
    Check(new QuotaWindow(95, 300, now).IsExpired(now), "reset boundary must expire");
    Check(!new QuotaWindow(95, 300, now.AddSeconds(1)).IsExpired(now), "future reset expired early");
    Check(!new QuotaWindow(95, 300, null).IsExpired(now), "missing reset must not invent expiry");
}));
await Test("CSV uses invariant numbers, escaped model names and literal spreadsheet cells", () => Sync(() =>
{
    var oldCulture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new("fr-FR");
        var names = new[] { "中文,模型\"测试", "=1+1", "  @SUM(A1)", "line\nmodel", "-2+3", "+1+1", "normal" };
        var rows = names.Select((name, i) => new UsageEvent(i.ToString(), Providers.Custom, name, now, "private-session", 10, 20, 30, 40, 5, .123, .045)).ToArray();
        var snapshot = Aggregation.Build(new(rows, [], []), TimeWindow.Today, now, TimeZoneInfo.Utc);
        using var writer = new StringWriter(); UsageCsv.Write(writer, snapshot, TimeWindow.Today);
        var csv = writer.ToString();
        Check(csv.Contains("\"中文,模型\"\"测试\""), "CSV did not escape commas/quotes");
        foreach (var name in names.Where(n => n != names[0] && n != "normal"))
            Check(csv.Contains("\"'" + name + "\""), "unsafe spreadsheet cell: " + name);
        Check(csv.Contains(",10,20,30,40,5,100,0.5,0.123,0.045,1,1"), "numeric output changed with culture or reasoning was double-counted");
        Check(!csv.Contains("private-session"), "session ID leaked into summary");
        using var empty = new StringWriter(); UsageCsv.Write(empty, Aggregation.Build(new([], [], []), TimeWindow.All, now), TimeWindow.All);
        Equal(1, empty.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = oldCulture; }
}));

await Test("All source enable combinations select events and quotas independently", () => Sync(() =>
{
    using var folder = new TestDirectory();
    Directory.CreateDirectory(folder.Settings.ClaudeDirectory);
    Directory.CreateDirectory(folder.Settings.CodexDirectory);
    var logs = Path.Combine(folder.Settings.AntigravityDirectory, "session", ".system_generated", "logs");
    Directory.CreateDirectory(logs);
    File.WriteAllText(Path.Combine(folder.Settings.ClaudeDirectory, "test.jsonl"), Fixture("claude"));
    File.WriteAllText(Path.Combine(folder.Settings.CodexDirectory, "test.jsonl"), Fixture("codex"));
    File.WriteAllText(Path.Combine(logs, "transcript.jsonl"), Fixture("antigravity"));
    File.WriteAllText(folder.Settings.CustomEventsFile, Fixture("custom"));
    var scanner = new LogScanner();
    for (var mask = 0; mask < 16; mask++)
    {
        var settings = folder.Settings with { EnableClaude = (mask & 1) != 0, EnableCodex = (mask & 2) != 0,
            EnableAntigravity = (mask & 4) != 0, EnableCustom = (mask & 8) != 0 };
        var scan = scanner.Scan(settings);
        Equal((settings.EnableClaude ? 2 : 0) + (settings.EnableCodex ? 2 : 0) + (settings.EnableAntigravity ? 1 : 0) + (settings.EnableCustom ? 1 : 0), scan.Events.Count);
        Equal(settings.EnableCodex ? 1 : 0, scan.RateLimits.Count);
        foreach (var source in settings.LogSources())
            Equal(source.Enabled ? SourceState.Ready : SourceState.Disabled, scan.Statuses.Single(s => s.Provider == source.Provider).State);
    }
}));
await Test("Disabled sources skip invalid paths and evict cached content before re-enabling", () => Sync(() =>
{
    using var folder = new TestDirectory();
    Directory.CreateDirectory(folder.Settings.ClaudeDirectory);
    var path = Path.Combine(folder.Settings.ClaudeDirectory, "test.jsonl");
    var first = """{"message":{"usage":{"input_tokens":1}}}""";
    File.WriteAllText(path, first);
    var timestamp = File.GetLastWriteTimeUtc(path);
    var scanner = new LogScanner();
    Equal(1L, scanner.Scan(folder.Settings).Events.Single().InputTokens);
    var off = folder.Settings with { EnableClaude = false, EnableCodex = false, EnableAntigravity = false, EnableCustom = false,
        ClaudeDirectory = "\0", CodexDirectory = "", AntigravityDirectory = "relative", CustomEventsFile = null! };
    off.Validate();
    var scan = scanner.Scan(off);
    Equal(0, scan.Events.Count); Check(scan.Statuses.All(s => s.State == SourceState.Disabled), "disabled path was accessed");
    File.WriteAllText(path, first.Replace(":1", ":9")); File.SetLastWriteTimeUtc(path, timestamp);
    Equal(9L, scanner.Scan(folder.Settings).Events.Single().InputTokens);
    try { (off with { EnableCodex = true }).Validate(); throw new Exception("enabled empty path accepted"); }
    catch (ArgumentException) { }
}));
await Test("Burn thresholds honor independent disabling, exact boundaries and finite validation", () => Sync(() =>
{
    UsageTotals Totals(long tokens, double usd) => new(tokens, 0, 0, 0, 0, usd, 0, 1, 1);
    var defaults = new BurnRateThresholds();
    Check(!defaults.IsExceeded(Totals(249999, 1.49)), "warned below both thresholds");
    Check(defaults.IsExceeded(Totals(250000, 0)), "token boundary missed");
    Check(defaults.IsExceeded(Totals(0, 1.5)), "cost boundary missed");
    Check(!new BurnRateThresholds(0, 0).IsExceeded(Totals(long.MaxValue, double.MaxValue)), "zero did not disable alerts");
    Check(!new BurnRateThresholds(0, 2).IsExceeded(Totals(500000, 1.99)), "disabled token threshold still active");
    Check(!new BurnRateThresholds(500000, 0).IsExceeded(Totals(499999, 100)), "disabled cost threshold still active");
    foreach (var value in new[] { new BurnRateThresholds(-1, 0), new(0, -1), new(0, double.NaN), new(0, double.PositiveInfinity) })
    {
        try { value.Validate(); throw new Exception("invalid threshold accepted"); }
        catch (ArgumentException) { }
    }
    var snapshot = Aggregation.Build(new([UsageEvent.Create("a", Providers.Codex, "x", now, "a", 250000, 0, 0, 0)], [], []), TimeWindow.All, now);
    Check(snapshot.IsHighBurnRate && !(snapshot with { AlertThresholds = new(0, 0) }).IsHighBurnRate, "snapshot ignored configured thresholds");
}));
await Test("Legacy settings retain defaults and new preferences persist together", () => Sync(() =>
{
    using var folder = new TestDirectory();
    var store = new SettingsStore(Path.Combine(folder.Path, "settings.json"));
    var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(folder.Settings))!.AsObject();
    foreach (var key in new[] { "EnableClaude", "EnableCodex", "EnableAntigravity", "EnableCustom", "BurnRateTokens", "BurnRateUSD" }) legacy.Remove(key);
    File.WriteAllText(store.FilePath, legacy.ToJsonString());
    var (loaded, warning) = store.Load();
    Check(warning is null && loaded.LogSources().All(s => s.Enabled), "legacy source defaults changed");
    Equal(250000L, loaded.BurnRateTokens); Near(1.5, loaded.BurnRateUSD);
    var changed = loaded with { EnableClaude = false, EnableAntigravity = false, BurnRateTokens = 500000, BurnRateUSD = 0 };
    store.Save(changed); Equal(changed, store.Load().Settings);
    File.WriteAllText(store.FilePath, JsonSerializer.Serialize(changed with { BurnRateTokens = -1 }));
    Check(store.Load().Warning is not null, "corrupt thresholds should be visible");
}));

await Test("Unknown timestamps never move into calendar totals or burn alerts after append or restart", () => Sync(() =>
{
    using var folder = new TestDirectory();
    Directory.CreateDirectory(folder.Settings.ClaudeDirectory);
    var path = Path.Combine(folder.Settings.ClaudeDirectory, "undated.jsonl");
    File.WriteAllText(path, "{\"message\":{\"usage\":{\"input_tokens\":300000}}}\n");
    File.SetLastWriteTimeUtc(path, now.AddDays(-1).UtcDateTime);
    var scanner = new LogScanner();
    void Verify(ScanResult scan)
    {
        Check(!scan.Events.Single().HasKnownTimestamp, "unknown timestamp marked reliable");
        Equal(SourceState.Warning, scan.Statuses.Single(s => s.Provider == Providers.Claude).State);
        foreach (var period in Enum.GetValues<TimeWindow>())
        {
            var snapshot = Aggregation.Build(scan, period, now, TimeZoneInfo.Utc);
            Equal(period == TimeWindow.All ? 300000L : 0L, snapshot.Totals.Total);
            Equal(1, snapshot.UnknownTimestampEvents); Equal(0L, snapshot.Recent.Total);
            Equal(0L, snapshot.Trend.Sum(p => p.Total)); Check(!snapshot.IsHighBurnRate, "unknown date triggered burn alert");
        }
    }
    Verify(scanner.Scan(folder.Settings));
    File.AppendAllText(path, "{}\n"); File.SetLastWriteTimeUtc(path, now.UtcDateTime);
    Verify(scanner.Scan(folder.Settings)); Verify(scanner.Scan(folder.Settings)); Verify(new LogScanner().Scan(folder.Settings));
}));
await Test("All native parsers mark missing and invalid dates without changing the custom contract", () => Sync(() =>
{
    (string Provider, string Line)[] cases =
    [ (Providers.Claude, """{"timestamp":"invalid","message":{"usage":{"input_tokens":3}}}"""),
      (Providers.Codex, """{"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":3}}}}"""),
      (Providers.Antigravity, """{"created_at":"invalid","input_tokens":3}""") ];
    foreach (var (provider, line) in cases)
    {
        var e = Parse(provider, line).Events.Single(); Check(!e.HasKnownTimestamp, provider + " missed unknown date");
        Equal(0L, Aggregation.Build(new([e], [], []), TimeWindow.Today, now).Totals.Total);
        Equal(3L, Aggregation.Build(new([e], [], []), TimeWindow.All, now).Totals.Total);
    }
    var custom = Parse(Providers.Custom, Fixture("custom")).Events.Single();
    Check(custom.HasKnownTimestamp, "custom timestamp lost");
    Check(!JsonSerializer.Serialize(custom).Contains("HasKnownTimestamp"), "internal timestamp metadata leaked into serialized contract");
    Check(Parse(Providers.Claude, Fixture("claude")).Events.All(e => e.HasKnownTimestamp), "dated fixture changed");
}));
await Test("JSON diagnostics distinguish damaged lines, incomplete EOF objects and unrelated valid JSON", () => Sync(() =>
{
    var parsed = Parse(Providers.Claude, "broken\n{invalid}\n{}\n[]\n\n{\"message\":");
    Equal(2, parsed.MalformedLines); Equal(1, parsed.PendingLines); Equal(0, parsed.Events.Count);
    foreach (var tail in new[] { "{", "{\"message\"", "{\"message\":{\"usage\":", "{\"value\":\"unfinished" })
    { var p = Parse(Providers.Claude, tail); Equal(1, p.PendingLines); Equal(0, p.MalformedLines); }
    foreach (var tail in new[] { "broken", "{invalid}", "{}{}", "{\"a\":!}" })
    { var p = Parse(Providers.Claude, tail); Equal(0, p.PendingLines); Equal(1, p.MalformedLines); }
    var middle = Parse(Providers.Claude, "{\"message\":\n{}\n");
    Equal(1, middle.MalformedLines); Equal(0, middle.PendingLines);
}));
await Test("Source diagnostics survive cache reuse and clear after completion or truncation", () => Sync(() =>
{
    using var folder = new TestDirectory(); Directory.CreateDirectory(folder.Settings.ClaudeDirectory);
    var path = Path.Combine(folder.Settings.ClaudeDirectory, "diagnostics.jsonl");
    File.WriteAllText(path, "broken\n{\"message\":");
    var scanner = new LogScanner();
    SourceStatus Status() => scanner.Scan(folder.Settings).Statuses.Single(s => s.Provider == Providers.Claude);
    var first = Status(); Equal(SourceState.Warning, first.State);
    Check(first.Message.Contains("0 条用量记录") && first.Message.Contains("1 行 JSON 损坏") && first.Message.Contains("1 个末行"), "missing diagnostic counts");
    Equal(first, Status());
    File.AppendAllText(path, "{}}\n");
    Check(!Status().Message.Contains("末行"), "completed object still pending");
    File.WriteAllText(path, Fixture("claude"));
    var recovered = Status(); Equal(SourceState.Ready, recovered.State);
    Check(recovered.Message.Contains("2 条用量记录") && !recovered.Message.Contains("损坏"), "truncated/recovered source retained warnings");
}));

Console.WriteLine($"{passed} passed, {failures} failed.");
return failures == 0 ? 0 : 1;

sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tokenpulse-test-" + Guid.NewGuid());
    public Settings Settings { get; }
    public TestDirectory()
    {
        Directory.CreateDirectory(Path);
        Settings = new Settings { ClaudeDirectory = System.IO.Path.Combine(Path, "claude"), CodexDirectory = System.IO.Path.Combine(Path, "codex"),
            AntigravityDirectory = System.IO.Path.Combine(Path, "antigravity"), CustomEventsFile = System.IO.Path.Combine(Path, "custom.jsonl"), EnableOllama = false };
    }
    public void Dispose() => Directory.Delete(Path, true);
}
sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = respond;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request, cancellationToken);
}
