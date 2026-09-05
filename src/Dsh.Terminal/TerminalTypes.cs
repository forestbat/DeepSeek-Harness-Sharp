using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Terminal;

[JsonConverter(typeof(BrandJsonConverter<TerminalSessionId>))]
public readonly record struct TerminalSessionId(string Value) : IBrand<TerminalSessionId>
{
    public static TerminalSessionId Create(string value) => new(value);
    public override string ToString() => Value;
}

public sealed class TerminalBackendCleanupError : Exception
{
    public TerminalBackendCleanupError(Exception spawnError, Exception cleanupError)
        : base("PTY backend startup and cleanup both failed", new AggregateException(spawnError, cleanupError))
    {
        SpawnError = spawnError;
        CleanupError = cleanupError;
    }

    public Exception SpawnError { get; }
    public Exception CleanupError { get; }
}

public enum TerminalWaitReason
{
    StdinRead,
    InferredIdle,
    Timeout,
    SessionExit,
}

public enum TerminalSignal
{
    SIGINT,
    SIGTERM,
    SIGKILL,
    SIGTSTP,
    SIGHUP,
}

public static class TerminalSignalNames
{
    public static string Of(TerminalSignal signal) => signal switch
    {
        TerminalSignal.SIGINT => "SIGINT",
        TerminalSignal.SIGTERM => "SIGTERM",
        TerminalSignal.SIGKILL => "SIGKILL",
        TerminalSignal.SIGTSTP => "SIGTSTP",
        TerminalSignal.SIGHUP => "SIGHUP",
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null),
    };
}

[JsonConverter(typeof(TerminalSessionStatusConverter))]
public sealed record TerminalSessionStatus
{
    public string Kind { get; init; } = "running";
    public int? ExitCode { get; init; }
    public string? Signal { get; init; }

    public static TerminalSessionStatus Running() => new() { Kind = "running" };

    public static TerminalSessionStatus Exited(int? exitCode, string? signal)
        => new() { Kind = "exited", ExitCode = exitCode, Signal = signal };
}

public sealed class TerminalSessionStatusConverter : JsonConverter<TerminalSessionStatus>
{
    public override TerminalSessionStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var kind = root.GetProperty("kind").GetString() ?? "";
        if (kind == "running")
            return TerminalSessionStatus.Running();
        return TerminalSessionStatus.Exited(
            root.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number ? exitCode.GetInt32() : null,
            root.TryGetProperty("signal", out var signal) && signal.ValueKind == JsonValueKind.String ? signal.GetString() : null);
    }

    public override void Write(Utf8JsonWriter writer, TerminalSessionStatus value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        if (value.Kind == "exited")
        {
            writer.WritePropertyName("exitCode");
            JsonSerializer.Serialize(writer, value.ExitCode, options);
            writer.WritePropertyName("signal");
            JsonSerializer.Serialize(writer, value.Signal, options);
        }
        writer.WriteEndObject();
    }
}

public sealed record TerminalSpawnRequest(string Type, string? Name = null, string? Cwd = null);

public sealed record TerminalBackendSpawnSpec(
    TerminalSessionId SessionId,
    IAgent Owner,
    string Type,
    string? Name = null,
    string? Cwd = null,
    CancellationToken Signal = default);

public sealed record TerminalSendRequest(string Text, bool Submit, CancellationToken Signal = default);

public sealed record TerminalSendRead(string Delta, bool Truncated);

public sealed record TerminalSendResult(string Viewport, TerminalWaitReason WaitReason, TerminalSessionStatus SessionStatus, bool Truncated);

public sealed record TerminalReadRequest(int? Offset = null, int? Count = null);

public sealed record TerminalReadResult(string Text, int TotalLines, int LineBegin, int LineEnd, bool Truncated);

public sealed record TerminalSignalResult(bool Delivered, int TargetPgid);

public sealed record TerminalSessionSnapshot(
    TerminalSessionId SessionId,
    string? Name,
    string Type,
    int? Pid,
    TerminalSessionStatus Status);

public sealed record TerminalSpawnResult(
    TerminalSessionId SessionId,
    string? Name,
    string Type,
    int? Pid,
    TerminalSessionStatus Status,
    string Motd);

public interface TerminalSendOperation
{
    Task<TerminalSendResult> Done { get; }
    TerminalSendRead ReadOutput();
    bool Cancel();
}

public interface TerminalBackendSession
{
    string Motd { get; }
    int? Pid { get; }
    TerminalSendOperation StartSend(TerminalSendRequest request);
    TerminalReadResult Read(TerminalReadRequest request);
    Task<TerminalSignalResult> Signal(TerminalSignal signal);
    TerminalSessionStatus Status();
    Task Close(string reason);
}

public interface TerminalBackend
{
    string Type { get; }
    Task<TerminalBackendSession> Spawn(TerminalBackendSpawnSpec spec);
}

public static class TerminalErrorCodes
{
    public const string DuplicateBackend = "DUPLICATE_BACKEND";
    public const string DuplicateName = "DUPLICATE_NAME";
    public const string ForeignSession = "FOREIGN_SESSION";
    public const string NoBackend = "NO_BACKEND";
    public const string NoSession = "NO_SESSION";
    public const string OwnerNotLive = "OWNER_NOT_LIVE";
    public const string SendActive = "SEND_ACTIVE";
    public const string ServiceDisposing = "SERVICE_DISPOSING";
}

public sealed class TerminalError : Exception
{
    public TerminalError(string message, string code) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
