using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace TokenPulse.Core;

/// <summary>Reuses parsed UTF-8 lines only after verifying the complete previous byte range.</summary>
internal sealed class IncrementalLog
{
    private static readonly Encoding TailEncoding = new UTF8Encoding(false);
    private readonly long size, lastNewline, previousNewline, committedEnd;
    private readonly byte[] fingerprint;
    private readonly LogParser.Cursor cursor;
    private readonly List<UsageEvent> events;
    private readonly List<QuotaObservation> observations;
    public bool UsedFallback => cursor.UsedFallback;

    private IncrementalLog(long size, long lastNewline, long previousNewline, long committedEnd,
        byte[] fingerprint, LogParser.Cursor cursor, List<UsageEvent> events, List<QuotaObservation> observations)
    {
        this.size = size;
        this.lastNewline = lastNewline;
        this.previousNewline = previousNewline;
        this.committedEnd = committedEnd;
        this.fingerprint = fingerprint;
        this.cursor = cursor;
        this.events = events;
        this.observations = observations;
    }

    public static (ParsedLog Log, IncrementalLog? State) Read(Stream stream, long size, string provider,
        string session, DateTimeOffset fallback, IncrementalLog? previous, CancellationToken token)
    {
        stream.Position = 0;
        Span<byte> header = stackalloc byte[(int)Math.Min(4, size)];
        stream.ReadExactly(header);
        stream.Position = 0;
        // Preserve StreamReader's UTF-16 and UTF-32 BOM handling. Those files
        // cannot use a byte-aligned UTF-8 line checkpoint.
        if (header.StartsWith(new byte[] { 0xff, 0xfe }) || header.StartsWith(new byte[] { 0xfe, 0xff })
            || header.SequenceEqual(new byte[] { 0, 0, 0xfe, 0xff }))
            return (ReadRange(new(), stream, 0, size, provider, session, fallback, token), null);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        long last = 0, penultimate = 0;
        try
        {
            if (previous is not null && previous.size < size)
            {
                HashTo(previous.size, trackNewlines: false);
                if (!hash.GetCurrentHash().AsSpan().SequenceEqual(previous.fingerprint))
                {
                    _ = hash.GetHashAndReset();
                    stream.Position = 0;
                    previous = null;
                }
                else
                {
                    last = previous.lastNewline;
                    penultimate = previous.previousNewline;
                }
            }
            else previous = null;
            HashTo(size, trackNewlines: true);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        // Leave the final line provisional even if it ends in LF. Diagnostics
        // distinguish an incomplete final record from a damaged interior line.
        var end = last == size ? penultimate : last;
        var start = previous?.committedEnd ?? 0;
        var cursor = previous?.cursor.Copy() ?? new LogParser.Cursor();
        var committed = ReadRange(cursor, stream, start, end, provider, session, fallback, token, allowPending: false);
        var preview = ReadRange(cursor.Copy(), stream, end, size, provider, session, fallback, token);

        var events = previous?.events ?? [];
        var observations = previous?.observations ?? [];
        events.AddRange(committed.Events);
        observations.AddRange(committed.QuotaObservations);
        var log = preview with {
            Events = preview.Events.Count == 0 ? events : [.. events, .. preview.Events],
            QuotaObservations = preview.QuotaObservations.Count == 0 ? observations : [.. observations, .. preview.QuotaObservations]
        };
        return (log, new(size, last, penultimate, end, hash.GetHashAndReset(), cursor, events, observations));

        void HashTo(long limit, bool trackNewlines)
        {
            while (stream.Position < limit)
            {
                token.ThrowIfCancellationRequested();
                var offset = stream.Position;
                var bytes = buffer.AsSpan(0, (int)Math.Min(buffer.Length, limit - offset));
                stream.ReadExactly(bytes);
                hash.AppendData(bytes);
                if (!trackNewlines) continue;
                var rightmost = bytes.LastIndexOf((byte)'\n');
                if (rightmost < 0) continue;
                var second = bytes[..rightmost].LastIndexOf((byte)'\n');
                penultimate = second < 0 ? last : offset + second + 1;
                last = offset + rightmost + 1;
            }
        }
    }

    private static ParsedLog ReadRange(LogParser.Cursor cursor, Stream stream, long start, long end, string provider,
        string session, DateTimeOffset fallback, CancellationToken token, bool allowPending = true)
    {
        stream.Position = start;
        using var bounded = new LogSnapshotStream(stream, end - start);
        using var reader = new StreamReader(bounded, TailEncoding, detectEncodingFromByteOrderMarks: start == 0);
        var result = cursor.Read(reader, provider, session, fallback, token, allowPending);
        if (bounded.Remaining != 0) throw new EndOfStreamException("日志在读取期间被截断");
        return result;
    }
}
