import CoreServices
import Foundation

/// FSEvents owns a separate callback box, avoiding a stream/owner retain cycle.
final class LogChangeMonitor {
    private final class Callback: @unchecked Sendable {
        let roots: [String]
        let changed: @Sendable () -> Void
        init(roots: [String], changed: @escaping @Sendable () -> Void) { self.roots = roots; self.changed = changed }
        func receive(paths: [String], flags: UnsafePointer<FSEventStreamEventFlags>, count: Int) {
            for index in 0..<count {
                if flags[index] & FSEventStreamEventFlags(kFSEventStreamEventFlagHistoryDone) != 0 { continue }
                let mustScan = flags[index] & FSEventStreamEventFlags(kFSEventStreamEventFlagMustScanSubDirs | kFSEventStreamEventFlagRootChanged) != 0
                if mustScan || roots.contains(where: { Self.overlaps(paths[index], $0) }) {
                    changed()
                    return
                }
            }
        }
        static func overlaps(_ path: String, _ root: String) -> Bool {
            path == "/" || root == "/" || path == root || path.hasPrefix(root + "/") || root.hasPrefix(path + "/")
        }
    }
    private var stream: FSEventStreamRef?
    private let queue = DispatchQueue(label: "TokenPulse.LogChanges", qos: .utility)
    var isRunning: Bool { stream != nil }
    private var watchedAncestors: [String] = []
    private var watchedIdentities: [String] = []
    private let directories: [URL]
    private let changed: @Sendable () -> Void
    init(directories: [URL], changed: @escaping @Sendable () -> Void) {
        self.directories = directories
        self.changed = changed
        restartIfNeeded()
    }
    /// Missing source directories are watched through their nearest existing parent.
    /// Reevaluate after fallback scans, so newly created/replaced roots recover.
    func restartIfNeeded() {
        let ancestors = Self.existingAncestors(directories)
        let identities = ancestors.map { path -> String in
            let attributes = try? FileManager.default.attributesOfItem(atPath: path)
            let device = (attributes?[.systemNumber] as? NSNumber)?.uint64Value ?? 0
            let inode = (attributes?[.systemFileNumber] as? NSNumber)?.uint64Value ?? 0
            return "\(device):\(inode)"
        }
        guard ancestors != watchedAncestors || identities != watchedIdentities || stream == nil else { return }
        stop()
        guard !ancestors.isEmpty else { return }
        let callback = Callback(roots: directories.map { $0.standardizedFileURL.resolvingSymlinksInPath().path }, changed: changed)
        var context = FSEventStreamContext(version: 0,
            info: Unmanaged.passUnretained(callback).toOpaque(),
            retain: { pointer in
                guard let pointer else { return nil }
                _ = Unmanaged<Callback>.fromOpaque(pointer).retain()
                return pointer
            }, release: { pointer in
                if let pointer { Unmanaged<Callback>.fromOpaque(pointer).release() }
            }, copyDescription: nil)
        let flags = FSEventStreamCreateFlags(kFSEventStreamCreateFlagUseCFTypes | kFSEventStreamCreateFlagFileEvents | kFSEventStreamCreateFlagWatchRoot)
        // Capture a concrete cursor before registering. SinceNow is evaluated by
        // the daemon later and can miss writes during stream startup/replacement.
        let since = FSEventsGetCurrentEventId()
        stream = FSEventStreamCreate(nil, { _, info, count, paths, flags, _ in
            guard let info else { return }
            let callback = Unmanaged<Callback>.fromOpaque(info).takeUnretainedValue()
            let values = Unmanaged<NSArray>.fromOpaque(paths).takeUnretainedValue() as? [String] ?? []
            guard values.count >= count else { return }
            callback.receive(paths: values, flags: flags, count: count)
        }, &context, ancestors as CFArray, since, 1.0, flags)
        guard let stream else { return }
        FSEventStreamSetDispatchQueue(stream, queue)
        if FSEventStreamStart(stream) { watchedAncestors = ancestors; watchedIdentities = identities }
        else { stop() }
    }
    private func stop() {
        if let stream {
            FSEventStreamStop(stream)
            FSEventStreamInvalidate(stream)
            FSEventStreamRelease(stream)
        }
        stream = nil
        watchedAncestors = []
        watchedIdentities = []
    }
    deinit { stop() }

    static func existingAncestors(_ directories: [URL]) -> [String] {
        var candidates = Set<String>()
        for directory in directories {
            var parent = directory.standardizedFileURL.resolvingSymlinksInPath()
            var isDirectory: ObjCBool = false
            while !FileManager.default.fileExists(atPath: parent.path, isDirectory: &isDirectory) || !isDirectory.boolValue {
                let next = parent.deletingLastPathComponent()
                if next.path == parent.path { break }
                parent = next
            }
            candidates.insert(parent.path)
        }
        return candidates.sorted().filter { candidate in
            !candidates.contains { $0 != candidate && (candidate.hasPrefix($0 + "/") || $0 == "/") }
        }
    }
}
