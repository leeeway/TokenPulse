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
