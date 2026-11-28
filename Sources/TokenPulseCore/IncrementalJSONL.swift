import Foundation
import CryptoKit

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
        var digest: SHA256.Digest
    }
    private let lock = NSLock()
    private var entries: [String: Entry] = [:]
    private var generation: UInt64 = 0
    private(set) var maximumReadSize = 0
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
                    maximumReadSize = max(maximumReadSize, data.count)
                    return data
                }
                let chunkSize = 256 * 1024
                var previous: Entry?
                var digest = SHA256()
                if let old = entries[key], stamp.sameFile(old.stamp), stamp.size > old.stamp.size {
                    // A larger file can also have been rewritten in place. Hash the
                    // existing prefix before reusing its cumulative parser state.
                    var checked: UInt64 = 0
                    while checked < old.stamp.size {
                        let count = Int(min(UInt64(chunkSize), old.stamp.size - checked))
                        try autoreleasepool {
                            let chunk = try read(checked, count)
                            guard chunk.count == count else { throw CocoaError(.fileReadUnknown) }
                            digest.update(data: chunk)
                        }
                        checked += UInt64(count)
                    }
                    if digest.finalize() == old.digest { previous = old }
                    else { digest = SHA256() }
                }
                let offset = previous?.stamp.size ?? 0
                var state = previous?.committed ?? initial
                var lines = previous?.lines ?? 0
                var pending = previous?.tail ?? Data()
                let boundarySize = Int(min(128, stamp.size))
                let suffixOffset = stamp.size - UInt64(boundarySize)
                let prefix = try read(0, boundarySize)
                let suffix = try read(suffixOffset, boundarySize)
                guard prefix.count == boundarySize, suffix.count == boundarySize else { throw CocoaError(.fileReadUnknown) }
                var position = offset
                // Bound temporary allocations during the initial scan too. Reading a
                // whole large file through FileHandle leaves a large reusable malloc
                // buffer in a long-lived menu bar process even after it is freed.
                while position < stamp.size {
                    let count = Int(min(UInt64(chunkSize), stamp.size - position))
                    try autoreleasepool {
                        let chunk = try read(position, count)
                        guard chunk.count == count else { throw CocoaError(.fileReadUnknown) }
                        digest.update(data: chunk)
                        pending.append(chunk)
                        if let newline = chunk.lastIndex(of: 0x0A) {
                            let trailingBytes = chunk.distance(from: chunk.index(after: newline), to: chunk.endIndex)
                            let completeEnd = pending.index(pending.endIndex, offsetBy: -trailingBytes)
                            let complete = pending[..<completeEnd]
                            for line in complete.split(separator: 0x0A, omittingEmptySubsequences: false).dropLast() {
                                lines += 1
                                consume(&state, Data(line), lines, stamp.modified)
                            }
                            // subdata makes an independent tail rather than a slice
                            // retaining the allocation for the already-consumed chunk.
                            pending = pending.subdata(in: completeEnd..<pending.endIndex)
                        }
                    }
                    position += UInt64(count)
                }
                let tail = pending
                var preview = state
                autoreleasepool {
                    if !tail.isEmpty { consume(&preview, tail, lines + 1, stamp.modified) }
                }
                let finalStamp = try Stamp(url)
                if finalStamp != stamp {
                    // An active writer can append while we parse the original
                    // length. Keep that prefix and leave the new bytes for the
                    // next scan, provided the identity and boundaries agree.
                    guard finalStamp.sameFile(stamp), finalStamp.size > stamp.size,
                          try read(0, boundarySize) == prefix, try read(suffixOffset, boundarySize) == suffix
                    else { throw CocoaError(.fileReadUnknown) }
                }
                entries[key] = Entry(stamp: stamp, committed: state, preview: preview, tail: tail,
                    lines: lines, digest: digest.finalize())
                generation &+= 1
                return Result(state: preview, readSucceeded: true)
            } catch {
                if entries.removeValue(forKey: key) != nil { generation &+= 1 }
                return Result(state: initial, readSucceeded: false)
            }
        }
    }
}
