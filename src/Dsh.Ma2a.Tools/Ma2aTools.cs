using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Llm;
using Dsh.Ma2a.Services;
using Dsh.Runtime;
using Dsh.Safety;

namespace Dsh.Ma2a.Tools;

public sealed class Ma2aTools : IDisposable
{
    private const int MaximumSessions = 32;
    private sealed record OwnedSession(Ma2aServiceSession Session, string? Owner);
    private readonly ConcurrentDictionary<string, OwnedSession> _sessions = new();
    private readonly List<IDisposable> _registrations = [];
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Context? _context;
    private int _opening;
    private bool _disposed;
    private static readonly JsonObject Catalog = JsonNode.Parse(Ma2aLocalContract.Contract.GetRawText())!.AsObject();

    public IDisposable Register(Context context)
    {
        _context = context;
        var tools = context.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        _registrations.Add(tools.Register(Define("ma2a_describe", "Inspect native MA2A local command fields and types before calling it.",
            new JsonObject { ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Catalog["commands"]!.AsObject().Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray()) } }.ToJsonString(),
            ["operation"], DescribeAsync)));
        _registrations.Add(tools.Register(Define("ma2a_service_prepare", "Prepare a named native service request for ma2a_stream_open. Fields: files use path ROOT:/path, offset/length/destination; exec/shell/process.start use program,args,cwd,stdin; process uses id; agent uses agent,task,text; service.call uses name,version,operation,hex; proxy uses host,port. A returned body preserves native types and binary identifiers.",
            new JsonObject { ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Ma2aToolRequests.Operations.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) }, ["arguments"] = new JsonObject { ["type"] = "object" } }.ToJsonString(),
            ["operation", "arguments"], (arguments, _) => Task.FromResult<object?>(Ma2aToolRequests.Prepare(arguments.GetProperty("operation").GetString()!, JsonNode.Parse(arguments.GetProperty("arguments").GetRawText())!.AsObject())))));
        _registrations.Add(tools.Register(Define("ma2a_command", "Call a native MA2A local command. Discover fields with ma2a_describe. Supply a stable request_id for mutations; never retry an unknown outcome with a new ID.",
            """{"state_directory":{"type":"string"},"command":{"type":"object"}}""", ["state_directory", "command"], CommandAsync)));
        _registrations.Add(tools.Register(Define("ma2a_stream_open", "Open a native MA2A service session. Body follows the service protocol; encode binary values as {\"$bytes\":\"lowercasehex\"}. Read until the terminal outcome, then close. Cancellation or early close may leave an unknown outcome.",
            """{"state_directory":{"type":"string"},"target":{"type":"string"},"service":{"type":"string","enum":["files","transfer","exec","shell","proxy","process","agent","service"]},"request_id":{"type":"string"},"body":{}}""",
            ["state_directory", "target", "service", "request_id", "body"], OpenAsync)));
        _registrations.Add(tools.Register(Define("ma2a_stream_read", "Wait for the next native service frame. Data frames return channel and data_hex. A terminal outcome is authoritative; events alone do not prove completion.",
            """{"handle":{"type":"string"}}""", ["handle"], ReadAsync)));
        _registrations.Add(tools.Register(Define("ma2a_stream_write", "Send data, end, control or cancel on an open native service session. Data uses hexadecimal bytes, not text conversion.",
            """{"handle":{"type":"string"},"kind":{"type":"string","enum":["data","end","control","cancel"]},"channel":{"type":"integer","minimum":0,"maximum":255},"hex":{"type":"string","description":"Raw data or Hosted Service control bytes in hexadecimal."},"control":{}}""",
            ["handle", "kind"], WriteAsync)));
        _registrations.Add(tools.Register(Define("ma2a_stream_close", "Detach and release a native service session. Closing before its terminal outcome cancels the operation; durable processes and tasks retain their own lifecycle.",
            """{"handle":{"type":"string"}}""", ["handle"], CloseAsync)));
        _registrations.Add(tools.Register(Define("ma2a_transfer", "Upload or download a file through MA2A with BLAKE3 integrity verification. Remote paths use ROOT:/path. Downloads retain partial files for explicit resumption. Reuse request_id when checking an uncertain upload outcome.",
            """{"state_directory":{"type":"string"},"target":{"type":"string"},"direction":{"type":"string","enum":["upload","download"]},"remote_path":{"type":"string"},"local_path":{"type":"string"},"request_id":{"type":"string"},"replace":{"type":"boolean"},"resume":{"type":"boolean"}}""",
            ["state_directory", "target", "direction", "remote_path", "local_path", "request_id"], TransferAsync, transfer: true)));
        return this;
    }

    private static Task<object?> DescribeAsync(JsonElement arguments, ToolRunContext execution)
    {
        var operation = arguments.GetProperty("operation").GetString()!;
        var command = Catalog["commands"]?[operation]
            ?? throw new ArgumentException("Unknown native MA2A command.");
        return Task.FromResult<object?>(new JsonObject
        {
            ["version"] = 1,
            ["operation"] = operation,
            ["command"] = command.DeepClone(),
            ["types"] = Catalog["types"]!.DeepClone(),
            ["request_id_policy"] = Catalog["request_id_policy"]!.DeepClone(),
        });
    }

    private async Task<object?> CommandAsync(JsonElement arguments, ToolRunContext execution)
    {
        var client = new Ma2aClient(arguments.GetProperty("state_directory").GetString()!);
        var command = JsonNode.Parse(arguments.GetProperty("command").GetRawText())!.AsObject();
        if (command["operation"]?.GetValue<string>() == "space_invite" && command["output_path"] is JsonValue path)
            await SafetyFileAccess.AuthorizeAsync(path.GetValue<string>(), "write", execution, execution.Signal);
        return await client.CallAsync(command, execution.Signal);
    }

    private async Task<object?> OpenAsync(JsonElement arguments, ToolRunContext execution)
    {
        var owner = execution.Agent?.Session.Id.Value
            ?? throw new InvalidOperationException("Native service handles require a DSH session.");
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.Count + _opening >= MaximumSessions)
                throw new InvalidOperationException("Close an existing MA2A session before opening another.");
            _opening++;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(execution.Signal, _lifetime.Token);
            var client = new Ma2aClient(arguments.GetProperty("state_directory").GetString()!);
            var session = await Ma2aServiceSession.OpenAsync(client,
                arguments.GetProperty("target").GetString()!, arguments.GetProperty("service").GetString()!,
                arguments.GetProperty("request_id").GetString()!, JsonNode.Parse(arguments.GetProperty("body").GetRawText())!, linked.Token);
            var handle = Guid.NewGuid().ToString("N");
            lock (_lifecycleGate)
                if (!_disposed)
                {
                    _sessions[handle] = new OwnedSession(session, owner);
                    return new { handle };
                }
            await session.DisposeAsync();
            throw new ObjectDisposedException(nameof(Ma2aTools));
        }
        finally { lock (_lifecycleGate) _opening--; }
    }

    private async Task<object?> ReadAsync(JsonElement arguments, ToolRunContext execution)
    {
        try
        {
            var session = Session(arguments, execution);
            var frame = await session.ReadAsync(execution.Signal);
            var result = new
            {
                kind = frame.Kind.ToString(),
                payload_hex = frame.Kind == Ma2aFrameKind.Data ? null : Convert.ToHexStringLower(frame.Payload.Span),
                channel = frame.Kind is Ma2aFrameKind.Data or Ma2aFrameKind.End ? (byte?)frame.Channel : null,
                data_hex = frame.Kind == Ma2aFrameKind.Data ? Convert.ToHexStringLower(frame.Data.Span) : null,
                cbor = frame.Kind is Ma2aFrameKind.Accept or Ma2aFrameKind.Outcome
                    || frame.Kind == Ma2aFrameKind.Control && session.Service != "service" ? frame.DecodeCbor() : null,
            };
            if (frame.Kind == Ma2aFrameKind.Outcome) await CloseAsync(arguments, execution);
            return result;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            await CloseAsync(arguments, execution);
            throw;
        }
    }

    private async Task<object?> WriteAsync(JsonElement arguments, ToolRunContext execution)
    {
        try { return await WriteCoreAsync(arguments, execution); }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            await CloseAsync(arguments, execution);
            throw;
        }
    }

    private async Task<object?> WriteCoreAsync(JsonElement arguments, ToolRunContext execution)
    {
        var session = Session(arguments, execution);
        switch (arguments.GetProperty("kind").GetString())
        {
            case "data":
                await session.SendDataAsync(arguments.GetProperty("channel").GetByte(), Convert.FromHexString(arguments.GetProperty("hex").GetString()!), execution.Signal);
                break;
            case "end":
                await session.EndAsync(arguments.GetProperty("channel").GetByte(), execution.Signal);
                break;
            case "control":
                if (session.Service == "service")
                    await session.ControlBytesAsync(Convert.FromHexString(arguments.GetProperty("hex").GetString()!), execution.Signal);
                else
                    await session.ControlAsync(JsonNode.Parse(arguments.GetProperty("control").GetRawText())!, execution.Signal);
                break;
            case "cancel":
                await session.CancelAsync(execution.Signal);
                break;
            default:
                throw new ArgumentException("Unknown stream write kind.");
        }
        return new { sent = true };
    }

    private async Task<object?> CloseAsync(JsonElement arguments, ToolRunContext execution)
    {
        var handle = arguments.GetProperty("handle").GetString()!;
        if (!_sessions.TryGetValue(handle, out var owned)) return new { closed = true };
        if (owned.Owner != execution.Agent?.Session.Id.Value) throw new ArgumentException("Unknown or closed MA2A service handle.");
        if (_sessions.TryRemove(handle, out var removed)) await removed.Session.DisposeAsync();
        return new { closed = true };
    }

    private Ma2aServiceSession Session(JsonElement arguments, ToolRunContext execution)
        => _sessions.TryGetValue(arguments.GetProperty("handle").GetString()!, out var owned)
            && owned.Owner == execution.Agent?.Session.Id.Value
            ? owned.Session : throw new ArgumentException("Unknown or closed MA2A service handle.");

    private async Task<object?> TransferAsync(JsonElement arguments, ToolRunContext execution)
    {
        var client = new Ma2aClient(arguments.GetProperty("state_directory").GetString()!);
        var endpoint = arguments.GetProperty("target").GetString()!;
        var remote = arguments.GetProperty("remote_path").GetString()!;
        var local = Path.GetFullPath(arguments.GetProperty("local_path").GetString()!, execution.Agent?.Session.WorkingDirectory ?? Environment.CurrentDirectory);
        var requestId = arguments.GetProperty("request_id").GetString()!;
        var replace = arguments.TryGetProperty("replace", out var overwrite) && overwrite.GetBoolean();
        var direction = arguments.GetProperty("direction").GetString();
        var activeSettings = ((execution.Agent?.Ctx ?? _context)?.GetProp("harnessOptions") as HarnessOptions)?.Home.SettingsFile;
        var partial = $"{local}{Ma2aFileTransfer.PartialFileSuffix}";
        string[] writes = [local, partial, $"{partial}.meta", $"{partial}.meta.new", $"{partial}.lock"];
        if (direction == "download" && activeSettings is not null && writes.Any(path => CanonicalFilePath.SameFile(path, activeSettings)))
            throw new InvalidOperationException("Use the settings editor to update the active settings document with a reviewed diff.");
        await SafetyFileAccess.AuthorizeAsync(local, direction == "upload" ? "read" : "write", execution, execution.Signal);
        if (direction == "download")
        {
            foreach (var path in writes.Skip(1))
                await SafetyFileAccess.AuthorizeAsync(path, "write", execution, execution.Signal);
            foreach (var path in new[] { partial, $"{partial}.meta" })
                await SafetyFileAccess.AuthorizeAsync(path, "read", execution, execution.Signal);
        }
        return direction switch
        {
            "upload" => await Ma2aFileTransfer.UploadAsync(client, endpoint, remote, local, requestId, replace, cancellationToken: execution.Signal),
            "download" => await Ma2aFileTransfer.DownloadAsync(client, endpoint, remote, local, requestId, replace,
                !arguments.TryGetProperty("resume", out var resume) || resume.GetBoolean(), cancellationToken: execution.Signal),
            _ => throw new ArgumentException("Unknown transfer direction."),
        };
    }

    private static ToolDefinition Define(string name, string description, string properties, string[] required,
        Func<JsonElement, ToolRunContext, Task<object?>> execute, bool transfer = false) => new()
    {
        Name = name,
        Description = description,
        Parameters = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = JsonNode.Parse(properties),
            ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        },
        Output = new ToolOutputDefinition(new JsonObject { ["type"] = "object" },
            (_, value) => [new TextBlock(value.GetRawText())]),
        Disclosure = new ToolDisclosure("ma2a", "Manage MA2A Runtime resources and call native remote services."),
        MediatesFileAccess = transfer,
        WriteTargets = transfer ? (arguments, execution) => arguments.GetProperty("direction").GetString() == "download"
            ? [Path.GetFullPath(arguments.GetProperty("local_path").GetString()!, execution.Agent?.Session.WorkingDirectory ?? Environment.CurrentDirectory)] : []
            : name == "ma2a_command" ? (arguments, _) => arguments.TryGetProperty("command", out var command)
                && command.TryGetProperty("operation", out var operation) && operation.GetString() == "space_invite"
                && command.TryGetProperty("output_path", out var path) && path.ValueKind == JsonValueKind.String
                    ? [Path.GetFullPath(path.GetString()!)] : [] : null,
        Execute = execute,
    };

    public void Dispose()
    {
        lock (_lifecycleGate) _disposed = true;
        _lifetime.Cancel();
        foreach (var registration in _registrations)
            registration.Dispose();
        foreach (var handle in _sessions.Keys)
            if (_sessions.TryRemove(handle, out var owned))
                owned.Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
