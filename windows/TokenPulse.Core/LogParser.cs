using System.Globalization;
using System.Text.Json;

namespace TokenPulse.Core;

public static class LogParser
{
    internal static JsonElement Field(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;
    internal static string? Text(JsonElement node, string name) => Field(node, name) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long Number(JsonElement node, string name) => Field(node, name) is var value && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? Math.Max(0, number) : 0;
    private static double? Real(JsonElement node, string name) => Field(node, name) is var value && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static DateTimeOffset? Date(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    private static int? KnownMinutes(JsonElement node) => Field(node, "window_minutes") is var value
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var minutes) && minutes > 0 ? minutes : null;
    private static DateTimeOffset? Epoch(JsonElement node)
    {
        var seconds = Real(node, "resets_at");
        return seconds is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds.Value) : null;
    }

    public static ParsedLog Parse(TextReader reader, string provider, string sessionId, DateTimeOffset fallbackDate, CancellationToken cancellationToken = default) =>
        new Cursor().Read(reader, provider, sessionId, fallbackDate, cancellationToken);

    // Only scalar state belongs in the cursor so previewing the last line can
    // use a copy without writing to the cumulative parser state.
    internal sealed class Cursor
    {
        private RateLimit? latest;
        private string model = "gpt-5-codex";
        private long previousInput, previousWrite, previousRead, previousOutput, previousReasoning;
        private int index;
        private int malformedLines;
        public bool UsedFallback { get; private set; }

        public Cursor Copy() => (Cursor)MemberwiseClone();

        public ParsedLog Read(TextReader reader, string provider, string sessionId, DateTimeOffset fallbackDate,
            CancellationToken cancellationToken, bool allowPending = true)
        {
            List<UsageEvent> events = [];
            List<QuotaObservation> observations = [];
            var pendingLines = 0;
            DateTimeOffset Timestamp(string? stamp)
            {
                if (Date(stamp) is { } date) return date;
                UsedFallback = true;
                return fallbackDate;
            }
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    UsageEvent? item = null;
                    if (provider == Providers.Claude)
                    {
                        var message = Field(root, "message");
                        var usage = Field(message, "usage");
                        if (usage.ValueKind != JsonValueKind.Object) continue;
                        var id = Text(message, "id") ?? Text(root, "uuid") ?? $"{sessionId}-{index}";
                        item = UsageEvent.Create($"claude-{sessionId}-{id}-{index}", provider, Text(message, "model") ?? "claude-sonnet",
                            Timestamp(Text(root, "timestamp")), sessionId, Number(usage, "input_tokens"),
                            Number(usage, "cache_creation_input_tokens"), Number(usage, "cache_read_input_tokens"), Number(usage, "output_tokens"))
                            with { HasKnownTimestamp = Date(Text(root, "timestamp")) is not null };
                    }
                    else if (provider == Providers.Codex)
                    {
                        var payload = Field(root, "payload");
                        var type = Text(payload, "type");
                        if (type == "thread_settings_applied")
                        {
                            var newModel = Text(Field(payload, "thread_settings"), "model");
                            if (!string.IsNullOrEmpty(newModel)) model = newModel;
                            continue;
                        }
                        if (type != "token_count") continue;
                        var timestamp = Timestamp(Text(root, "timestamp"));
                        var limits = Field(payload, "rate_limits");
                        // The ledger never promotes mtime, missing percentages or default windows to evidence.
                        var rawTime = Text(root, "timestamp");
                        if (rawTime is not null && rawTime.Contains('T') && (rawTime.EndsWith('Z') || rawTime.Length >= 6 && rawTime[^6] is '+' or '-') && Date(rawTime) is { } recorded)
                        {
                            foreach (var (role, node) in new[] { ("primary", Field(limits, "primary")), ("secondary", Field(limits, "secondary")) })
                            {
                                if (Real(node, "used_percent") is not { } percent || percent is < 0 or > 100
                                    || KnownMinutes(node) is not { } minutes
                                    || Epoch(node) is not { } reset) continue;
                                var observation = new QuotaObservation(recorded.ToUniversalTime(), role, minutes, reset.ToUniversalTime(), percent, Text(limits, "plan_type"));
                                if (observation.IsValid) observations.Add(observation);
                            }
                        }
                        var primary = Field(limits, "primary");
                        if (primary.ValueKind == JsonValueKind.Object)
                        {
                            var secondary = Field(limits, "secondary");
                            int? Minutes(JsonElement value, int? defaultValue) => Field(value, "window_minutes").ValueKind == JsonValueKind.Number
                                && Field(value, "window_minutes").TryGetInt32(out var minutes) ? minutes : defaultValue;
                            QuotaWindow Window(JsonElement value, int? defaultMinutes)
                            {
                                var used = Real(value, "used_percent");
                                return new(used ?? 0, Minutes(value, defaultMinutes), Epoch(value)) { UsageReported = used.HasValue };
                            }
                            var limit = new RateLimit(provider, Text(limits, "plan_type"),
                                Window(primary, 300),
                                secondary.ValueKind == JsonValueKind.Object ? Window(secondary, null) : null,
                                timestamp);
                            if (latest is null || timestamp >= latest.UpdatedAt) latest = limit;
                        }
                        var usage = Field(Field(payload, "info"), "total_token_usage");
                        if (usage.ValueKind != JsonValueKind.Object) continue;
                        long Delta(string key, ref long previous)
                        {
                            var current = Number(usage, key);
                            var delta = Math.Max(0, current - previous);
                            previous = Math.Max(previous, current);
                            return delta;
                        }
                        var raw = Delta("input_tokens", ref previousInput);
                        var read = Delta("cached_input_tokens", ref previousRead);
                        var write = Delta("cache_write_input_tokens", ref previousWrite);
                        var output = Delta("output_tokens", ref previousOutput);
                        var reasoning = Delta("reasoning_output_tokens", ref previousReasoning);
                        item = UsageEvent.Create($"codex-{sessionId}-{index}", provider, model, timestamp, sessionId,
                            Math.Max(0, raw - read - write), write, read, output, reasoning)
                            with { HasKnownTimestamp = Date(Text(root, "timestamp")) is not null };
                    }
                    else if (provider == Providers.Antigravity)
                    {
                        var step = Field(root, "step_index").ValueKind == JsonValueKind.Number ? Number(root, "step_index") : index;
                        item = UsageEvent.Create($"agy-{sessionId}-{step}", provider, "gemini-antigravity",
                            Timestamp(Text(root, "created_at")), sessionId, Number(root, "input_tokens"), 0,
                            Number(root, "cache_read_tokens"), Number(root, "output_tokens"))
                            with { HasKnownTimestamp = Date(Text(root, "created_at")) is not null };
                    }
                    else if (provider == Providers.Custom)
                    {
                        // Swift's Codable contract uses camelCase and requires all stored fields.
                        var customProvider = Text(root, "provider");
                        if (customProvider is null || !Providers.IsKnown(customProvider) || Date(Text(root, "timestamp")) is not { } timestamp
                            || Text(root, "id") is not { } id || Text(root, "model") is not { } customModel
                            || Text(root, "sessionId") is not { } customSession) continue;
                        string[] counters = ["inputTokens", "cacheWriteTokens", "cacheReadTokens", "outputTokens", "reasoningTokens"];
                        if (counters.Any(key => Field(root, key).ValueKind != JsonValueKind.Number || !Field(root, key).TryGetInt64(out var n) || n < 0)
                            || Real(root, "costUSD") is not { } cost || cost < 0 || Real(root, "savedByCacheUSD") is not { } saved || saved < 0) continue;
                        item = new(id, customProvider, customModel, timestamp, customSession, Number(root, "inputTokens"),
                            Number(root, "cacheWriteTokens"), Number(root, "cacheReadTokens"), Number(root, "outputTokens"),
                            Number(root, "reasoningTokens"), cost, saved);
                    }
                    if (item is not null && (provider == Providers.Custom || item.TotalTokens > 0)) events.Add(item);
                }
                catch (JsonException)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Only a syntactically valid, incomplete object at EOF may still be being written.
                    if (allowPending && reader.Peek() == -1 && IsIncompleteObject(line)) pendingLines++;
                    else malformedLines++;
                }
            }
            return new(events, latest) { MalformedLines = malformedLines, PendingLines = pendingLines, QuotaObservations = observations };
        }
    }

    private static bool IsIncompleteObject(string line)
    {
        if (!line.AsSpan().TrimStart().StartsWith("{")) return false;
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
            var rootComplete = false;
            while (reader.Read())
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) rootComplete = true;
            return !rootComplete;
        }
        catch (JsonException) { return false; }
    }
}
