import Foundation
import Testing
@testable import TokenPulseApp

private final class ChangeCount: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    var count: Int { lock.withLock { value } }
    func increment() { lock.withLock { value += 1 } }
}

@Suite("Native file change monitor")
struct LogChangeMonitorTests {
    @Test @MainActor
    func missingRootsAndReplacementRecoverThroughExistingParents() async throws {
        let parent = URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent(".build/TokenPulse-watch-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: parent) }
        let root = parent.appendingPathComponent("missing/sessions")
        #expect(LogChangeMonitor.existingAncestors([root]) == [parent.resolvingSymlinksInPath().path])
        let changed = ChangeCount()
        let monitor = LogChangeMonitor(directories: [root]) { changed.increment() }
        try #require(monitor.isRunning)
        try await Task.sleep(for: .milliseconds(200))
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("first\n".utf8).write(to: root.appendingPathComponent("test.jsonl"))
        for _ in 0..<160 {
            if changed.count > 0 { break }
            try await Task.sleep(for: .milliseconds(50))
        }
        #expect(changed.count > 0)
        monitor.restartIfNeeded()
        #expect(LogChangeMonitor.existingAncestors([root]) == [root.resolvingSymlinksInPath().path])
        try FileManager.default.removeItem(at: root)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        monitor.restartIfNeeded()
        let before = changed.count
        try Data("replacement\n".utf8).write(to: root.appendingPathComponent("test.jsonl"))
        for _ in 0..<160 {
            if changed.count > before { break }
            try await Task.sleep(for: .milliseconds(50))
        }
        #expect(changed.count > before)
        withExtendedLifetime(monitor) { }
    }
}
