import Foundation
import Testing
@testable import TokenPulseCore

private struct TailDirectory {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("TokenPulse-tail-" + UUID().uuidString)
    init() throws { try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true) }
    func remove() { try? FileManager.default.removeItem(at: url) }
}
private func append(_ data: Data, to url: URL) throws {
    let handle = try FileHandle(forWritingTo: url)
    defer { try? handle.close() }
    try handle.seekToEnd(); try handle.write(contentsOf: data)
}

@Suite("Incremental native and custom logs")
struct IncrementalJSONLTests {
    @Test func readsOnlyAppendedBytesAndPreviewsTheTailWithoutDoubleCounting() throws {
        let directory = try TailDirectory(); defer { directory.remove() }
        let file = directory.url.appendingPathComponent("尾部.jsonl")
        var data = Data(String(repeating: "x", count: 2_000_000).utf8)
        data.append(Data("\n{\"n\":1}".utf8)); try data.write(to: file)
        let cache = IncrementalJSONL<[Int]>()
        func read() -> [Int] {
            cache.read(at: file, initial: []) { state, line, _, _ in
                if let json = try? JSONSerialization.jsonObject(with: line) as? [String: Int], let n = json["n"] { state.append(n) }
            }.state
        }
        #expect(read() == [1])
        #expect(cache.maximumReadSize < 1_000_000)
        let initialBytes = cache.bytesRead, revision = cache.revision
        #expect(read() == [1] && cache.bytesRead == initialBytes && cache.revision == revision)
        let extra = Data("\n{\"n\":2}\n{\"n\":".utf8)
        try append(extra, to: file)
        #expect(read() == [1, 2])
        #expect(cache.bytesRead - initialBytes <= UInt64(extra.count + 512))
        try append(Data("3}".utf8), to: file)
        #expect(read() == [1, 2, 3])
        try append(Data("\n".utf8), to: file)
        #expect(read() == [1, 2, 3])
    }

    @Test func truncateReplaceRewriteAndDeleteInvalidateTheTailState() throws {
        let directory = try TailDirectory(); defer { directory.remove() }
        let file = directory.url.appendingPathComponent("rotate.jsonl")
        let cache = IncrementalJSONL<[String]>()
        func read() -> IncrementalJSONL<[String]>.Result {
            cache.read(at: file, initial: []) { state, data, _, _ in state.append(String(decoding: data, as: UTF8.self)) }
        }
        try Data("old\n".utf8).write(to: file)
        #expect(read().state == ["old"])
        let handle = try FileHandle(forWritingTo: file)
        try handle.truncate(atOffset: 0); try handle.write(contentsOf: Data("new and longer\n".utf8)); try handle.close()
        #expect(read().state == ["new and longer"])
        try Data("replacement\n".utf8).write(to: file, options: .atomic)
        #expect(read().state == ["replacement"])
        try Data("x\n".utf8).write(to: file)
        #expect(read().state == ["x"])
        try FileManager.default.removeItem(at: file)
        #expect(!read().readSucceeded && read().state.isEmpty)
        try Data("recreated\n".utf8).write(to: file)
        #expect(read().state == ["recreated"])
        let beforeEviction = cache.revision
        cache.retainOnly([])
        #expect(cache.revision > beforeEviction)
        #expect(read().state == ["recreated"])
    }

    @Test func symbolicLinksFollowTheirTargetAndRetargetingRebuildsTheCache() throws {
        let directory = try TailDirectory(); defer { directory.remove() }
        let first = directory.url.appendingPathComponent("first"), second = directory.url.appendingPathComponent("second")
        let link = directory.url.appendingPathComponent("custom.jsonl")
        try Data("first\n".utf8).write(to: first)
        try Data("second\n".utf8).write(to: second)
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: first)
        let cache = IncrementalJSONL<[String]>()
        func read() -> [String] {
            cache.read(at: link, initial: []) { state, data, _, _ in state.append(String(decoding: data, as: UTF8.self)) }.state
        }
        #expect(read() == ["first"])
        try append(Data("append\n".utf8), to: first)
        #expect(read() == ["first", "append"])
        try FileManager.default.removeItem(at: link)
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: second)
        #expect(read() == ["second"])
    }

    @Test func cumulativeCodexTailMatchesFullParsingAcrossModelQuotaAndUTF8Boundaries() throws {
        let directory = try TailDirectory(); defer { directory.remove() }
        let file = directory.url.appendingPathComponent("codex.jsonl")
        let stamp = "2026-11-15T10:00:00Z"
        func row(_ payload: [String: Any]) throws -> Data {
            try JSONSerialization.data(withJSONObject: ["timestamp": stamp, "payload": payload])
        }
        func usage(_ total: Int) throws -> Data { try row(["type": "token_count", "info": ["total_token_usage": ["input_tokens": total, "output_tokens": total / 10]]]) }
        var initial = try row(["type": "thread_settings_applied", "thread_settings": ["model": "gpt-5"]])
        initial.append(10); initial.append(try usage(100)); try initial.write(to: file)
        let adapter = CodexAdapter(baseDirectory: directory.url)
        func verify() throws {
            let incremental = adapter.scan()
            let full = CodexAdapter.parseDetailedRolloutFile(at: file)
            #expect(incremental.events.map(\.id) == full.events.map(\.id))
            #expect(incremental.events.map(\.totalTokens) == full.events.map(\.totalTokens))
            #expect(incremental.events.map(\.model) == full.events.map(\.model))
            #expect(incremental.events.map(\.hasKnownTimestamp) == full.events.map(\.hasKnownTimestamp))
            #expect(incremental.rateLimit == full.rateLimit)
            #expect(incremental.ledgerInput?.observations == full.observations)
        }
        try verify(); try verify()
        var extra = Data("\n".utf8)
        extra.append(try row(["type": "token_count", "rate_limits": ["primary": ["window_minutes": 10080, "used_percent": 30, "resets_at": 1795341600]]]))
        extra.append(10)
        extra.append(try row(["type": "thread_settings_applied", "thread_settings": ["model": "中文模型"]]))
        extra.append(10)
        let last = try usage(200)
        extra.append(last.prefix(last.count / 2)); try append(extra, to: file); try verify()
        try append(Data(last.dropFirst(last.count / 2)), to: file); try verify()
        try append(Data("\n{broken}\n".utf8), to: file); try verify()
        try usage(10).write(to: file); try verify()
        try usage(300).write(to: file, options: .atomic); try verify()
        try append(Data("\n".utf8) + usage(400), to: file); try verify()
        try FileManager.default.removeItem(at: file)
        #expect(adapter.scan().events.isEmpty)
    }

    @Test func claudeAntigravityAndCustomAppendsRemainEquivalentAndIsolated() throws {
        let directory = try TailDirectory(); defer { directory.remove() }
        let claude = directory.url.appendingPathComponent("claude")
        let brain = directory.url.appendingPathComponent("brain")
        let transcript = brain.appendingPathComponent("session/.system_generated/logs/transcript.jsonl")
        try FileManager.default.createDirectory(at: claude, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: transcript.deletingLastPathComponent(), withIntermediateDirectories: true)
        let claudeFile = claude.appendingPathComponent("session.jsonl")
        let c = Data("{\"timestamp\":\"2026-11-15T10:00:00Z\",\"message\":{\"usage\":{\"input_tokens\":10}}}".utf8)
        let a = Data("{\"created_at\":\"2026-11-15T10:00:00Z\",\"input_tokens\":20}".utf8)
        try c.write(to: claudeFile); try a.write(to: transcript)
        let claudeAdapter = ClaudeCodeAdapter(baseDirectory: claude)
        let agy = AntigravityAdapter(baseDirectory: brain)
        #expect(claudeAdapter.scan().events.count == 1 && agy.scan().events.count == 1)
        try append(Data("\n".utf8) + c, to: claudeFile)
        try append(Data("\n".utf8) + a, to: transcript)
        #expect(claudeAdapter.scan().events.map(\.id) == ClaudeCodeAdapter.parseJSONLFile(at: claudeFile).map(\.id))
        #expect(agy.scan().events.map(\.id) == AntigravityAdapter.parseTranscriptFile(at: transcript, sessionId: "session").map(\.id))
        let custom = directory.url.appendingPathComponent("custom.jsonl")
        let event = TokenUsageEvent(id: "custom", provider: .codex, model: "custom", timestamp: Date(), sessionId: "custom", inputTokens: 99, outputTokens: 0)
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
        let encoded = try encoder.encode(event)
        try encoded.write(to: custom)
        let collector = UsageCollector(adapters: [claudeAdapter, agy], customEventsURL: custom)
        #expect(collector.collectSnapshot(window: .allTime).grandTotalTokens == 159)
        #expect(collector.collectSnapshot(window: .allTime).grandTotalTokens == 159)
        try append(Data("\n".utf8) + encoded, to: custom)
        #expect(collector.collectSnapshot(window: .allTime).grandTotalTokens == 258)
        #expect(collector.ledgerInputFromCache().events.isEmpty)
        try FileManager.default.removeItem(at: custom)
        #expect(collector.collectSnapshot(window: .allTime).grandTotalTokens == 60)
    }
}
