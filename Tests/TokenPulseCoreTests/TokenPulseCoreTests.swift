import Foundation
import Testing
@testable import TokenPulseCore

@Suite("TokenPulseCore Adapters & Aggregation Tests")
struct TokenPulseCoreTests {

    @Test("ClaudeCodeAdapter parses JSONL usage lines accurately")
    func testClaudeCodeAdapterParsing() throws {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("claude-\(UUID().uuidString).jsonl")
        defer { try? FileManager.default.removeItem(at: tmp) }

        let sample = """
        {"timestamp":"2026-10-18T10:00:00.000Z","uuid":"u1","message":{"id":"m1","model":"claude-opus-4-7","usage":{"input_tokens":100,"cache_creation_input_tokens":2000,"cache_read_input_tokens":8000,"output_tokens":300}}}
        {"timestamp":"2026-10-18T10:01:00.000Z","uuid":"u2","message":{"id":"m2","model":"claude-sonnet-4-5","usage":{"input_tokens":50,"cache_creation_input_tokens":0,"cache_read_input_tokens":10000,"output_tokens":200}}}
        """
        try sample.write(to: tmp, atomically: true, encoding: .utf8)

        let events = ClaudeCodeAdapter.parseJSONLFile(at: tmp)
        #expect(events.count == 2)
        #expect(events[0].upstreamTokens == 10_100)
        #expect(events[0].downstreamTokens == 300)
        #expect(events[0].cacheReadTokens == 8_000)
        #expect(events[0].costUSD > 0)
        #expect(events[0].savedByCacheUSD > 0)
    }

    @Test("CodexAdapter computes cumulative deltas and extracts rate limits")
    func testCodexAdapterParsing() throws {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("rollout-\(UUID().uuidString).jsonl")
        defer { try? FileManager.default.removeItem(at: tmp) }

        let sample = """
        {"timestamp":"2026-10-18T05:28:30.052Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5-codex"}}}
        {"timestamp":"2026-10-18T05:28:34.297Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10000,"cached_input_tokens":6000,"cache_write_input_tokens":0,"output_tokens":500,"reasoning_output_tokens":100,"total_tokens":10500}},"rate_limits":{"plan_type":"team","primary":{"used_percent":12.5,"window_minutes":300,"resets_at":1792319147},"secondary":{"used_percent":4.0,"window_minutes":10080,"resets_at":1792905947}}}}
        {"timestamp":"2026-10-18T05:29:00.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":25000,"cached_input_tokens":18000,"cache_write_input_tokens":0,"output_tokens":1200,"reasoning_output_tokens":300,"total_tokens":26200}}}}
        """
        try sample.write(to: tmp, atomically: true, encoding: .utf8)

        let parsed = CodexAdapter.parseRolloutFile(at: tmp)
        #expect(parsed.events.count == 2)
        // Step 1: 4000 fresh + 6000 cached = 10000 upstream, 500 output
        #expect(parsed.events[0].inputTokens == 4000)
        #expect(parsed.events[0].cacheReadTokens == 6000)
        #expect(parsed.events[0].outputTokens == 500)
        // Step 2 delta: (25000-10000)=15000 raw input, (18000-6000)=12000 cached -> 3000 fresh input, 700 output
        #expect(parsed.events[1].inputTokens == 3000)
        #expect(parsed.events[1].cacheReadTokens == 12000)
        #expect(parsed.events[1].outputTokens == 700)
        // Total across both events equals cumulative 26,200
        let sumTokens = parsed.events.reduce(0) { $0 + $1.totalTokens }
        #expect(sumTokens == 26_200)
        #expect(parsed.rateLimit?.primaryUsedPercent == 12.5)
        #expect(parsed.rateLimit?.secondaryUsedPercent == 4.0)
        #expect(parsed.rateLimit?.planType == "team")
    }

    @Test("AntigravityAdapter parses transcript.jsonl steps")
    func testAntigravityAdapterParsing() throws {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("transcript-\(UUID().uuidString).jsonl")
        defer { try? FileManager.default.removeItem(at: tmp) }

        let sample = """
        {"step_index":1,"type":"PLANNER_RESPONSE","created_at":"2026-10-18T06:22:51Z","input_tokens":12000,"cache_read_tokens":48000,"output_tokens":400}
        """
        try sample.write(to: tmp, atomically: true, encoding: .utf8)

        let events = AntigravityAdapter.parseTranscriptFile(at: tmp, sessionId: "s1")
        #expect(events.count == 1)
        #expect(events[0].upstreamTokens == 60_000)
        #expect(events[0].downstreamTokens == 400)
        #expect(events[0].totalTokens == 60_400)
    }
}
