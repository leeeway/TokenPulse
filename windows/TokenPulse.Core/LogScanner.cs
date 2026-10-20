namespace TokenPulse.Core;

/// <summary>Serial, read-only scans. Changed files are reparsed in full to preserve cumulative deltas.</summary>
public sealed class LogScanner
{
    private sealed record Cached(DateTime Mtime, long Size, ParsedLog Log);
    private readonly Dictionary<(string Provider, string Path), Cached> cache = new();
    private readonly object sync = new();

    public ScanResult Scan(Settings settings, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            List<UsageEvent> events = [];
            List<RateLimit> limits = [];
            List<SourceStatus> statuses = [];
            HashSet<(string Provider, string Path)> visited = [];
            foreach (var (provider, root, singleFile) in new[]
            {
                (Providers.Claude, settings.ClaudeDirectory, false), (Providers.Codex, settings.CodexDirectory, false),
                (Providers.Antigravity, settings.AntigravityDirectory, false), (Providers.Custom, settings.CustomEventsFile, true)
            })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var errors = new List<string>();
                var count = 0;
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
                            ParsedLog parsed;
                            if (cache.TryGetValue(key, out var entry) && entry.Mtime == mtime && entry.Size == size) parsed = entry.Log;
                            else
                            {
                                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                using var reader = new StreamReader(stream);
                                var session = provider == Providers.Antigravity
                                    ? Directory.GetParent(file)?.Parent?.Parent?.Name ?? Path.GetFileNameWithoutExtension(file)
                                    : Path.GetFileNameWithoutExtension(file);
                                parsed = LogParser.Parse(reader, provider, session, new DateTimeOffset(mtime), cancellationToken);
                                var after = new FileInfo(file);
                                if (after.Exists && after.LastWriteTimeUtc == mtime && after.Length == size) cache[key] = new(mtime, size, parsed);
                                else cache.Remove(key);
                            }
                            events.AddRange(parsed.Events);
                            if (parsed.RateLimit is { } limit) limits.Add(limit);
                            count++;
                        }
                        catch (Exception ex) when (IsFileError(ex)) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { state = SourceState.Missing; }
                catch (Exception ex) when (IsFileError(ex)) { errors.Add(ex.Message); }
                if (errors.Count > 0) state = SourceState.Error;
                statuses.Add(new(provider, root, state, state == SourceState.Missing ? "未发现日志"
                    : errors.Count > 0 ? $"访问失败（{errors.Count} 项）：{errors[0]}" : count == 0 ? "未发现日志" : $"已读取 {count} 个日志文件"));
            }
            foreach (var key in cache.Keys.Where(key => !visited.Contains(key)).ToArray()) cache.Remove(key);
            return new(events, limits.GroupBy(r => r.Provider).Select(g => g.MaxBy(r => r.UpdatedAt)!).ToArray(), statuses);
        }
    }

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
