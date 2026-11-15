import Foundation

/// One cache per source. Commits complete lines and previews an unfinished final
/// line on a copy, so a valid JSON object without LF is visible without duplication.
final class IncrementalJSONL<State: Sendable>: @unchecked Sendable {
    struct Result {
        let state: State
        let readSucceeded: Bool
    }
    private struct Stamp: Equatable {
        let inode: UInt64
        let device: UInt64
        let size: UInt64
        let modified: Date
        init(_ url: URL) throws {
            var a = try FileManager.default.attributesOfItem(atPath: url.path)
            if a[.type] as? FileAttributeType == .typeSymbolicLink {
                a = try FileManager.default.attributesOfItem(atPath: url.resolvingSymlinksInPath().path)
            }
            guard a[.type] as? FileAttributeType == .typeRegular else { throw CocoaError(.fileReadUnknown) }
            inode = (a[.systemFileNumber] as? NSNumber)?.uint64Value ?? 0
            device = (a[.systemNumber] as? NSNumber)?.uint64Value ?? 0
            size = (a[.size] as? NSNumber)?.uint64Value ?? 0
            modified = a[.modificationDate] as? Date ?? .distantPast
        }
        func sameFile(_ other: Self) -> Bool { inode == other.inode && device == other.device }
    }
    private struct Entry {
        var stamp: Stamp
        var committed: State
        var preview: State
        var tail: Data
        var lines: Int
        var prefix: Data
        var suffix: Data
    }
    private let lock = NSLock()
    private var entries: [String: Entry] = [:]
    private var generation: UInt64 = 0
    private(set) var bytesRead: UInt64 = 0 // Internal diagnostics; never contains log content.
    var revision: UInt64 { lock.withLock { generation } }

    func retainOnly(_ paths: Set<String>) {
        lock.withLock {
            let removed = entries.keys.filter { !paths.contains($0) }
            if !removed.isEmpty { generation &+= 1 }
            for path in removed { entries.removeValue(forKey: path) }
        }
    }

    func read(at url: URL, initial: State,
              consume: (inout State, Data, Int, Date) -> Void) -> Result {
        lock.withLock {
            let key = url.path
            do {
                let stamp = try Stamp(url)
                if let old = entries[key], old.stamp == stamp {
                    return Result(state: old.preview, readSucceeded: true)
                }
                let handle = try FileHandle(forReadingFrom: url)
                defer { try? handle.close() }
                func read(_ offset: UInt64, _ count: Int) throws -> Data {
                    try handle.seek(toOffset: offset)
                    let data = try handle.read(upToCount: count) ?? Data()
                    bytesRead += UInt64(data.count)
                    return data
                }
                var previous = entries[key]
                if let old = previous {
                    // Identity + boundary checks detect rotation/replacement and rewrites.
                    // A size decrease or same-size edit always rebuilds the parser state.
                    if !stamp.sameFile(old.stamp) || stamp.size <= old.stamp.size {
                        previous = nil
                    } else {
                        let prefix = try read(0, old.prefix.count)
                        let suffix = try read(old.stamp.size - UInt64(old.suffix.count), old.suffix.count)
                        if prefix != old.prefix || suffix != old.suffix { previous = nil }
                    }
                }
                let offset = previous?.stamp.size ?? 0
                guard stamp.size - offset <= UInt64(Int.max) else { throw CocoaError(.fileReadTooLarge) }
                let appended = try read(offset, Int(stamp.size - offset))
                guard appended.count == Int(stamp.size - offset), try Stamp(url) == stamp else {
                    throw CocoaError(.fileReadUnknown)
                }
                var state = previous?.committed ?? initial
                var lines = previous?.lines ?? 0
                var pending = previous?.tail ?? Data()
                pending.append(appended)
                let completeEnd = pending.lastIndex(of: 0x0A).map { pending.index(after: $0) } ?? pending.startIndex
                if completeEnd > pending.startIndex {
                    // omit the trailing empty slice; empty *physical* lines still count.
                    let complete = pending[..<completeEnd]
                    for line in complete.split(separator: 0x0A, omittingEmptySubsequences: false).dropLast() {
                        lines += 1
                        consume(&state, Data(line), lines, stamp.modified)
                    }
                }
                let tail = Data(pending[completeEnd...])
                var preview = state
                if !tail.isEmpty { consume(&preview, tail, lines + 1, stamp.modified) }
                let prefix: Data
                if let previous, previous.prefix.count == 128 { prefix = previous.prefix }
                else { prefix = try read(0, min(128, Int(stamp.size))) }
                let suffix = try read(stamp.size - min(128, stamp.size), Int(min(128, stamp.size)))
                guard try Stamp(url) == stamp else { throw CocoaError(.fileReadUnknown) }
                entries[key] = Entry(stamp: stamp, committed: state, preview: preview, tail: tail,
                    lines: lines, prefix: prefix, suffix: suffix)
                generation &+= 1
                return Result(state: preview, readSucceeded: true)
            } catch {
                if entries.removeValue(forKey: key) != nil { generation &+= 1 }
                return Result(state: initial, readSucceeded: false)
            }
        }
    }
}
