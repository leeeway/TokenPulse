using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace TokenPulse.Core;

/// <summary>Serial, read-only scans over finite snapshots of log files.</summary>
public sealed class LogScanner
{
    private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);
    private sealed record Cached(FileIdentity Identity, DateTime Mtime, long Size, ParsedLog Log, IncrementalLog? Prefix);
    private readonly Dictionary<(string Provider, string Path), Cached> cache = new();
    private readonly object sync = new();

    public ScanResult Scan(Settings settings, CancellationToken cancellationToken = default) =>
        Scan(settings, cancellationToken, null);

    public ScanResult Scan(Settings settings, CancellationToken cancellationToken, Action<LiveQuotaScan>? onCodex)
    {
        lock (sync)
        {
            List<UsageEvent> events = [];
            List<RateLimit> limits = [];
            List<QuotaObservation> observations = [];
            List<UsageEvent> nativeCodexEvents = [];
            List<SourceStatus> statuses = [];
            var codexScanPartial = false;
            HashSet<(string Provider, string Path)> visited = [];
            var sources = settings.LogSources().ToArray();
            IEnumerable<LogSource> scanOrder = onCodex is null ? sources : sources.OrderBy(s => s.Provider != Providers.Codex);
            foreach (var (provider, root, singleFile, enabled) in scanOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!enabled)
                {
                    statuses.Add(new(provider, root, SourceState.Disabled, "已关闭 · 不扫描"));
                    if (provider == Providers.Codex) onCodex?.Invoke(new([], false, false));
                    continue;
                }
                var errors = new List<string>();
                var count = 0;
                var validEvents = 0;
                var malformedLines = 0;
                var pendingLines = 0;
                var unknownDates = 0;
                var state = SourceState.Ready;
                try
                {
                    // GetAttributes preserves access errors; Directory.Exists would mislabel them as missing.
                    var attributes = File.GetAttributes(root);
                    if (singleFile == attributes.HasFlag(FileAttributes.Directory)) throw new IOException(singleFile ? "需要 JSONL 文件路径" : "需要目录路径");
                    var files = singleFile ? new[] { root } : Walk(root, provider, errors, cancellationToken);
                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var canonical = Path.GetFullPath(file);
                            var key = (provider, OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical);
                            if (!visited.Add(key)) continue;
                            var before = new FileInfo(file);
                            var mtime = before.LastWriteTimeUtc;
                            var size = before.Length;
                            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            var snapshotSize = stream.Length;
                            var identity = GetIdentity(stream.SafeFileHandle);
                            cache.TryGetValue(key, out var entry);
                            ParsedLog parsed;
                            if (identity is { } known && entry?.Identity == known
                                && entry.Mtime == mtime && entry.Size == size && snapshotSize == size) parsed = entry.Log;
                            else
                            {
                                var session = provider == Providers.Antigravity
                                    ? Directory.GetParent(file)?.Parent?.Parent?.Name ?? Path.GetFileNameWithoutExtension(file)
                                    : Path.GetFileNameWithoutExtension(file);
                                var previous = identity is { } same && entry?.Identity == same
                                    && (entry.Mtime == mtime || entry.Prefix?.UsedFallback != true) ? entry.Prefix : null;
                                IncrementalLog? prefix;
                                if (identity.HasValue)
                                {
                                    (parsed, prefix) = IncrementalLog.Read(stream, snapshotSize, provider, session,
                                        new DateTimeOffset(mtime), previous, cancellationToken);
                                }
                                else
                                {
                                    using var reader = new StreamReader(new LogSnapshotStream(stream, snapshotSize));
                                    parsed = LogParser.Parse(reader, provider, session, new DateTimeOffset(mtime), cancellationToken);
                                    prefix = null;
                                }
                                var after = new FileInfo(file);
                                if (identity is { } current && after.Exists
                                    && ((after.Length == snapshotSize && after.LastWriteTimeUtc == mtime)
                                        || (after.Length > snapshotSize && prefix is not null)))
                                    cache[key] = new(current, mtime, snapshotSize, parsed, prefix);
                                else cache.Remove(key);
                            }
                            events.AddRange(parsed.Events);
                            if (provider == Providers.Codex)
                            {
                                nativeCodexEvents.AddRange(parsed.Events);
                                observations.AddRange(parsed.QuotaObservations);
                            }
                            validEvents += parsed.Events.Count;
                            malformedLines += parsed.MalformedLines;
                            pendingLines += parsed.PendingLines;
                            unknownDates += parsed.Events.Count(e => !e.HasKnownTimestamp);
                            if (parsed.RateLimit is { } limit) limits.Add(limit);
                            count++;
                        }
                        catch (Exception ex) when (IsFileError(ex)) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { state = SourceState.Missing; }
                catch (Exception ex) when (IsFileError(ex)) { errors.Add(ex.Message); }
                if (errors.Count > 0) state = SourceState.Error;
                else if (malformedLines > 0 || unknownDates > 0) state = SourceState.Warning;
                if (provider == Providers.Codex && count > 0 && errors.Count > 0) codexScanPartial = true;
                var message = state == SourceState.Missing || count == 0 && errors.Count == 0 ? "未发现日志"
                    : $"已读取 {count} 个文件 · {validEvents} 条用量记录";
                if (malformedLines > 0) message += $" · {malformedLines} 行 JSON 损坏，已跳过";
                if (pendingLines > 0) message += $" · {pendingLines} 个末行疑似未写完，等待追加";
                if (unknownDates > 0) message += $" · {unknownDates} 条时间未知，仅计入全部用量";
                if (errors.Count > 0) message += $" · 访问失败（{errors.Count} 项）：{errors[0]}";
                statuses.Add(new(provider, root, state, message));
                if (provider == Providers.Codex && onCodex is not null)
                {
                    var latest = (codexScanPartial || state is SourceState.Ready or SourceState.Warning)
                        ? QuotaObservation.LatestByWindow(observations, DateTimeOffset.UtcNow) : [];
                    onCodex(new(latest, codexScanPartial, limits.Count > 0));
                }
            }
            foreach (var key in cache.Keys.Where(key => !visited.Contains(key)).ToArray()) cache.Remove(key);
            var orderedStatuses = onCodex is null ? statuses : sources.Select(s => statuses.Single(status => status.Provider == s.Provider)).ToList();
            return new(events, limits.GroupBy(r => r.Provider).Select(g => g.MaxBy(r => r.UpdatedAt)!).ToArray(), orderedStatuses)
            {
                NativeCodexEvents = nativeCodexEvents,
                QuotaObservations = observations.Distinct().OrderBy(o => o.RecordedAtUtc).ToArray(),
                CodexScanPartial = codexScanPartial
            };
        }
    }

    private static FileIdentity? GetIdentity(SafeFileHandle file)
    {
        const int fileIdInfo = 18;
        if (!OperatingSystem.IsWindows()
            || !GetFileInformationByHandleEx(file, fileIdInfo, out var info, (uint)Marshal.SizeOf<NativeFileId>())
            || (info.Low == 0 && info.High == 0)) return null;
        return new(info.Volume, info.Low, info.High);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileId
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out NativeFileId information, uint size);

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static IEnumerable<string> Walk(string root, string provider, List<string> errors, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (IsFileError(ex)) { errors.Add(ex.Message); continue; }
            foreach (var path in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception ex) when (IsFileError(ex)) { errors.Add(ex.Message); continue; }
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // Avoid junction/symlink cycles and duplicate roots.
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(path);
                else if (provider == Providers.Antigravity
                    ? Path.GetFileName(path).Equals("transcript.jsonl", StringComparison.OrdinalIgnoreCase)
                        && Directory.GetParent(path)?.Name == "logs" && Directory.GetParent(path)?.Parent?.Name == ".system_generated"
                    : Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)) yield return path;
            }
        }
    }
}

/// <summary>A non-owning, read-only view of the bytes present when a log scan starts.</summary>
internal sealed class LogSnapshotStream(Stream source, long length) : Stream
{
    private long remaining = length;
    internal long Remaining => remaining;

    public override int Read(Span<byte> buffer)
    {
        if (remaining == 0) return 0;
        var count = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
        remaining -= count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
