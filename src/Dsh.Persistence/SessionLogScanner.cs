using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;

namespace Dsh.Persistence;

internal sealed record SessionLogScan(SessionHeader Meta, long InheritedEventCount, List<SessionEvent> Events, long CommittedBytes);

internal sealed class SessionLogScanner
{
    private const byte Newline = (byte)'\n';
    private readonly List<SessionEvent> _events = [];
    private readonly List<byte> _fragment = [];
    private string? _issue;
    private bool _finished;
    private long _eventLine;

    public SessionLogScanner(ReadOnlySpan<byte> headerRecord)
    {
        var parsed = SessionLogHeader.ParseHeaderRecord(headerRecord);
        Meta = parsed.Meta;
        InheritedEventCount = parsed.InheritedEventCount;
        InputBytes = headerRecord.Length;
        CommittedBytes = headerRecord.Length;
    }

    public SessionHeader Meta { get; }
    public long InheritedEventCount { get; }
    public long InputBytes { get; private set; }
    public long CommittedBytes { get; private set; }

    public void Write(ReadOnlySpan<byte> chunk)
    {
        if (_finished) throw new InvalidOperationException("cannot write to a finished session log scanner");
        var chunkStart = InputBytes;
        InputBytes += chunk.Length;
        var lineStart = 0;
        while (true)
        {
            var newline = chunk[lineStart..].IndexOf(Newline);
            if (newline < 0) break;
            var newlineIndex = lineStart + newline;
            var endByte = chunkStart + newlineIndex + 1;
            if (_fragment.Count == 0)
            {
                ConsumeEventLine(chunk[lineStart..newlineIndex], endByte);
            }
            else
            {
                _fragment.AddRange(chunk[lineStart..newlineIndex]);
                ConsumeEventLine(CollectionsMarshal.AsSpan(_fragment), endByte);
                _fragment.Clear();
            }
            lineStart = newlineIndex + 1;
        }
        if (lineStart < chunk.Length) _fragment.AddRange(chunk[lineStart..]);
    }

    public (long InputBytes, long CommittedBytes, long EventCount) Checkpoint()
        => (InputBytes, CommittedBytes, _events.Count);

    public SessionLogScan Finish()
    {
        _finished = true;
        return new SessionLogScan(Meta, InheritedEventCount, _events, CommittedBytes);
    }

    private void ConsumeEventLine(ReadOnlySpan<byte> line, long endByte)
    {
        _eventLine += 1;
        IReadOnlyList<SessionEvent> decoded;
        try
        {
            decoded = DecodeRecord(line);
        }
        catch (SessionFormatUnsupportedException)
        {
            if (_issue is null) throw;
            return;
        }
        catch (Exception)
        {
            _issue ??= $"corrupt session log: unparsable committed event at line {_eventLine}";
            return;
        }
        if (_issue is not null)
        {
            if (decoded.Any(candidate => candidate.Type == SessionEventTypes.TurnEnd))
                throw new SessionPersistenceCorruptionException(_issue);
            return;
        }
        var rowStart = _events.Count;
        foreach (var sessionEvent in decoded)
        {
            if (sessionEvent.Seq != _events.Count)
            {
                var expected = _events.Count;
                _events.RemoveRange(rowStart, _events.Count - rowStart);
                _issue = $"corrupt session log: seq gap in committed region at line {_eventLine} (expected {expected}, got {sessionEvent.Seq})";
                if (decoded.Any(candidate => candidate.Type == SessionEventTypes.TurnEnd))
                    throw new SessionPersistenceCorruptionException(_issue);
                return;
            }
            _events.Add(sessionEvent);
        }
        CommittedBytes = endByte;
    }

    private static IReadOnlyList<SessionEvent> DecodeRecord(ReadOnlySpan<byte> line)
    {
        var node = JsonNode.Parse(Encoding.UTF8.GetString(line));
        if (node is not JsonObject record) throw new FormatException("stored session records must be objects");
        return ChunkRows.DecodeStorageRecord(record);
    }
}

internal static class SessionLogScannerCompat
{
    public static SessionLogScan ScanLog(byte[] buffer)
    {
        var headerEnd = Array.IndexOf(buffer, (byte)'\n');
        if (headerEnd < 0) throw new FormatException("empty or header-less session log");
        var scanner = new SessionLogScanner(buffer.AsSpan(0, headerEnd + 1));
        scanner.Write(buffer.AsSpan(headerEnd + 1));
        return scanner.Finish();
    }
}
