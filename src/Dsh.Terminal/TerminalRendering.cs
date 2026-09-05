using System.Text;

namespace Dsh.Terminal;

public static class TerminalRendering
{
    private const string Truncated = "\n[output truncated]";

    public static string BoundTerminalText(string text, int maxBytes)
    {
        if (ByteLength(text) <= maxBytes)
            return text;
        var markerBytes = ByteLength(Truncated);
        if (markerBytes >= maxBytes)
            return TerminalText.RetainTail(Truncated, maxBytes);
        return $"{TerminalText.RetainHead(text, maxBytes - markerBytes)}{Truncated}";
    }

    public static string RenderSpawn(TerminalSpawnResult result, int maxBytes)
    {
        var label = result.Name is null ? result.SessionId.Value : $"{result.SessionId.Value} ({result.Name})";
        var prefix = $"started terminal session {label} [type: {result.Type}]\n";
        var motd = string.IsNullOrEmpty(result.Motd) ? "(no startup output)" : result.Motd;
        var complete = $"{prefix}{motd}";
        return ByteLength(complete) <= maxBytes ? complete : FitWithPrefix(prefix, motd, maxBytes);
    }

    public static string RenderSend(TerminalSendResult result, int maxBytes)
    {
        var output = string.IsNullOrEmpty(result.Viewport) ? "(no new output)" : result.Viewport;
        var status = result.SessionStatus.Kind == "running"
            ? "running"
            : $"exited code={result.SessionStatus.ExitCode?.ToString() ?? "null"} signal={result.SessionStatus.Signal ?? "null"}";
        return BoundBodyWithSuffix(
            output,
            $"\n[wait: {WaitReasonText(result.WaitReason)}]\n[session: {status}]",
            result.Truncated,
            maxBytes);
    }

    public static string RenderSendRead(TerminalSendRead read)
    {
        var separator = read.Delta.Length == 0 || read.Delta.EndsWith('\n') ? "" : "\n";
        return read.Truncated ? $"{read.Delta}{separator}[output truncated]" : read.Delta;
    }

    public static string RenderRead(TerminalReadResult result, int maxBytes)
    {
        var output = string.IsNullOrEmpty(result.Text) ? "(no retained output)" : result.Text;
        return BoundBodyWithSuffix(
            output,
            $"\n[lines: {result.LineBegin}-{result.LineEnd} of {result.TotalLines}]",
            result.Truncated,
            maxBytes);
    }

    public static string RenderList(IReadOnlyList<TerminalSessionSnapshot> sessions, int maxBytes)
    {
        if (sessions.Count == 0)
            return "(no terminal sessions)";
        var text = string.Join('\n', sessions.Select(SessionLine));
        return BoundBodyWithSuffix(text, "", false, maxBytes);
    }

    private static string SessionLine(TerminalSessionSnapshot session)
    {
        var name = session.Name is null ? "" : $" ({session.Name})";
        var pid = session.Pid is null ? "" : $" pid={session.Pid}";
        var status = session.Status.Kind == "running"
            ? "running"
            : $"exited code={session.Status.ExitCode?.ToString() ?? "null"} signal={session.Status.Signal ?? "null"}";
        return $"{session.SessionId.Value}{name} [{session.Type}] {status}{pid}";
    }

    private static string WaitReasonText(TerminalWaitReason reason) => reason switch
    {
        TerminalWaitReason.StdinRead => "stdin_read",
        TerminalWaitReason.InferredIdle => "inferred_idle",
        TerminalWaitReason.Timeout => "timeout",
        TerminalWaitReason.SessionExit => "session_exit",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    private static int ByteLength(string text) => Encoding.UTF8.GetByteCount(text);

    private static string FitWithSuffix(string content, string suffix, int maxBytes)
    {
        var fixedBytes = ByteLength(suffix);
        if (fixedBytes >= maxBytes)
            return TerminalText.RetainTail(suffix, maxBytes);
        return $"{TerminalText.RetainTail(content, maxBytes - fixedBytes)}{suffix}";
    }

    private static string FitWithPrefix(string prefix, string content, int maxBytes)
    {
        var fixedPart = $"{prefix}{Truncated}";
        var fixedBytes = ByteLength(fixedPart);
        if (fixedBytes >= maxBytes)
            return TerminalText.RetainHead(fixedPart, maxBytes);
        return $"{prefix}{TerminalText.RetainTail(content, maxBytes - fixedBytes)}{Truncated}";
    }

    private static string BoundBodyWithSuffix(string content, string metadata, bool upstreamTruncated, int maxBytes)
    {
        var suffix = $"{metadata}{(upstreamTruncated ? Truncated : "")}";
        var complete = $"{content}{suffix}";
        if (ByteLength(complete) <= maxBytes)
            return complete;
        return FitWithSuffix(content, $"{metadata}{Truncated}", maxBytes);
    }
}
