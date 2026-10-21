import Foundation
import Testing
@testable import TokenPulseCore

private final class EmptyAdapter: ProviderAdapter, @unchecked Sendable {
    let provider = AIProvider.codex
    private let lock = NSLock()
    private var calls = 0
    var count: Int { lock.withLock { calls } }
    func scan() -> AdapterScanResult {
        lock.withLock { calls += 1 }
        return AdapterScanResult(provider: provider, events: [], rateLimit: RateLimitSnapshot(provider: provider,
            planType: nil, primaryUsedPercent: 55, primaryWindowMinutes: 300, primaryResetsAt: nil))
    }
}

private final class MockOllamaProtocol: URLProtocol, @unchecked Sendable {
    final class Responses: @unchecked Sendable {
        let lock = NSLock()
        var entries: [(Int, String)] = []
        func set(_ values: [(Int, String)]) { lock.withLock { entries = values } }
        func next() -> (Int, String) { lock.withLock { entries.isEmpty ? (500, "{}") : entries.removeFirst() } }
    }
    static let responses = Responses()
    override class func canInit(with request: URLRequest) -> Bool { request.url?.host == "127.0.0.1" }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let (status, body) = Self.responses.next()
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() { }
}

@Suite("Mac correctness regressions")
struct MacRegressionTests {
    @Test
    func undatedHistoryCannotBecomeRecentAfterAppendCacheOrRestart() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("undated.jsonl")
        let content = "{\"message\":{\"usage\":{\"input_tokens\":300000}}}\n"
        let now = Date()
        try content.write(to: file, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-86400)], ofItemAtPath: file.path)
        func collector() -> UsageCollector {
            UsageCollector(adapters: [ClaudeCodeAdapter(baseDirectory: directory)], customEventsURL: directory.appendingPathComponent("none"))
        }
        func verify(_ collector: UsageCollector) {
            let today = collector.collectSnapshot(window: .today, now: now)
            #expect(today.grandTotalTokens == 0)
            #expect(today.unknownTimestampEventCount == 1)
            for period in TimeWindow.allCases {
                let snapshot = collector.snapshotFromCache(window: period, now: now)
                #expect(snapshot.grandTotalTokens == (period == .allTime ? 300000 : 0))
                #expect(snapshot.recentFiveMinTokens == 0)
                #expect(snapshot.dailyTrend.allSatisfy { $0.totalTokens == 0 })
                #expect(!snapshot.isHighBurnRate)
                if period == .allTime { #expect(snapshot.providers.first?.lastActiveAt == nil) }
            }
        }
        let initial = collector()
        verify(initial)
        try (content + "{}\n").write(to: file, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: file.path)
        verify(initial); verify(initial); verify(collector())
    }

    @Test
    func nativeTimestampFlagsAndCustomJSONContract() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("sample.jsonl")
        try "{\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10}}}}".write(to: file, atomically: true, encoding: .utf8)
        #expect(CodexAdapter.parseRolloutFile(at: file).events.first?.hasKnownTimestamp == false)
        try "{\"created_at\":\"invalid\",\"input_tokens\":10}".write(to: file, atomically: true, encoding: .utf8)
        #expect(AntigravityAdapter.parseTranscriptFile(at: file, sessionId: "test").first?.hasKnownTimestamp == false)
        let event = TokenUsageEvent(id: "custom", provider: .custom, model: "test", timestamp: Date(), sessionId: "test", inputTokens: 1, outputTokens: 0)
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(event)
        #expect(!String(decoding: data, as: UTF8.self).contains("hasKnownTimestamp"))
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        #expect(try decoder.decode(TokenUsageEvent.self, from: data).hasKnownTimestamp)
    }

    @Test
    func quotaUsesNewestTimestampWithinFile() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".jsonl")
        defer { try? FileManager.default.removeItem(at: file) }
        let log = """
        {"timestamp":"2026-10-21T08:00:00Z","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":60,"resets_at":1792570000}}}}
        {"timestamp":"2026-10-21T07:00:00Z","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":20,"resets_at":1792560000}}}}
        """
        try log.write(to: file, atomically: true, encoding: .utf8)
        let result = CodexAdapter.parseRolloutFile(at: file)
        #expect(result.rateLimit?.primaryUsedPercent == 60)
        #expect(result.rateLimit?.primaryResetsAt == Date(timeIntervalSince1970: 1792570000))
    }

    @Test
    func malformedOllamaResponsesClearPreviouslyCachedModels() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MockOllamaProtocol.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let missing = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let collector = UsageCollector(adapters: [], customEventsURL: missing, ollamaSession: session)
        let success = "{\"models\":[{\"name\":\"test-model\",\"size\":100,\"size_vram\":50}]}"
        for invalid in [(200, "{}"), (200, "{\"models\":{}}"), (200, "broken"), (503, success)] {
            MockOllamaProtocol.responses.set([(200, success), invalid])
            #expect(await collector.refreshOllamaModels().count == 1)
            #expect(collector.snapshotFromCache(window: .today).runningLocalModels.count == 1)
            #expect(await collector.refreshOllamaModels().isEmpty)
            #expect(collector.snapshotFromCache(window: .today).runningLocalModels.isEmpty)
        }
    }

    @Test
    func emptyAndQuotaOnlyCachesDoNotTriggerNewScans() {
        let adapter = EmptyAdapter()
        let missing = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let collector = UsageCollector(adapters: [adapter], customEventsURL: missing)
        #expect(collector.snapshotFromCache(window: .today).providers.isEmpty)
        #expect(adapter.count == 0)
        _ = collector.collectSnapshot()
        #expect(collector.snapshotFromCache(window: .today).providers.first?.rateLimit?.primaryUsedPercent == 55)
        #expect(adapter.count == 1)
    }

    @Test
    func countdownBoundariesAndMissingTime() throws {
        let now = try #require(ISO8601DateFormatter().date(from: "2026-10-21T04:00:00Z"))
        let zone = try #require(TimeZone(secondsFromGMT: 8 * 3600))
        #expect(TokenFormatters.quotaResetCountdown(now.addingTimeInterval(4980), now: now, timeZone: zone) == "约 1 小时 23 分后 · 13:23")
        #expect(TokenFormatters.quotaResetCountdown(now.addingTimeInterval(1), now: now, timeZone: zone) == "不到 1 分钟后 · 12:00")
        #expect(TokenFormatters.quotaResetCountdown(now, now: now, timeZone: zone).contains("待更新"))
        #expect(TokenFormatters.quotaResetCountdown(nil, now: now, timeZone: zone) == "日志未提供重置时间")
        #expect(TokenFormatters.quotaResetCountdown(now.addingTimeInterval(172800), now: now, timeZone: zone) == "约 2 天后 · 10/23 12:00")
        #expect(TokenFormatters.quotaResetCountdown(now.addingTimeInterval(3600), now: now, timeZone: zone) == "约 1 小时后 · 13:00")
    }

    @Test
    func sourceWarningDistinguishesMissingDirectoryFromWrongPathType() throws {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        #expect(ClaudeCodeAdapter(baseDirectory: path).accessWarning == nil)
        try "not a directory".write(to: path, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: path) }
        #expect(ClaudeCodeAdapter(baseDirectory: path).accessWarning != nil)
    }
}
