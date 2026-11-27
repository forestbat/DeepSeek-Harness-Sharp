using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Ma2a;

public sealed class Ma2aClient
{
    public const int ApiVersion = 1;
    private static long _nextCorrelation;

    public Ma2aClient(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        if (!Path.IsPathFullyQualified(stateDirectory))
            throw new ArgumentException("The MA2A state directory must be absolute.", nameof(stateDirectory));
        StateDirectory = stateDirectory;
    }

    public string StateDirectory { get; }

    public async Task<JsonElement> CallAsync(JsonObject command, CancellationToken cancellationToken = default)
    {
        var request = PrepareCommand(command);
        if (request["operation"]!.GetValue<string>() is "stream_open" or "service_attach" or "service_accept")
            throw new ArgumentException("Use OpenServiceStreamAsync to retain the upgraded connection.", nameof(command));
        if (request["operation"]!.GetValue<string>() != "handshake")
            await HandshakeAsync(cancellationToken);
        await using var stream = await Ma2aConnection.ConnectAsync(StateDirectory, cancellationToken);
        return await ExchangeAsync(stream, request, cancellationToken);
    }

    public Task<JsonElement> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => CallAsync(new JsonObject { ["operation"] = "snapshot_fetch" }, cancellationToken);

    public async Task<Stream> OpenServiceStreamAsync(string targetEndpointId, string service, string requestId, CancellationToken cancellationToken = default)
    {
        var command = new JsonObject
        {
            ["version"] = ApiVersion, ["operation"] = "stream_open", ["request_id"] = requestId,
            ["target_endpoint_id"] = targetEndpointId, ["service"] = service,
        };
        await HandshakeAsync(cancellationToken);
        var stream = await Ma2aConnection.ConnectAsync(StateDirectory, cancellationToken);
        try
        {
            var response = await ExchangeAsync(stream, command, cancellationToken);
            if (response.GetProperty("result").GetProperty("type").GetString() != "stream_opened")
                throw new Ma2aProtocolException("invalid_frame", "The Runtime did not acknowledge the stream upgrade.");
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        await using var stream = await Ma2aConnection.ConnectAsync(StateDirectory, cancellationToken);
        var response = await ExchangeAsync(stream, new JsonObject { ["version"] = ApiVersion, ["operation"] = "handshake" }, cancellationToken);
        if (response.GetProperty("result").GetProperty("type").GetString() != "handshake")
            throw new Ma2aProtocolException("version_mismatch", "The Runtime did not return a compatible handshake.");
    }

    private static JsonObject PrepareCommand(JsonObject command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var copy = (JsonObject)command.DeepClone();
        if (copy["version"] is { } version && version.GetValue<int>() != ApiVersion)
            throw new Ma2aProtocolException("version_mismatch");
        copy["version"] = ApiVersion;
        if (copy["operation"] is not JsonValue operation || !operation.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name))
            throw new ArgumentException("A local API operation is required.", nameof(command));
        return copy;
    }

    private static async Task<JsonElement> ExchangeAsync(Stream stream, JsonObject command, CancellationToken cancellationToken)
    {
        var correlation = unchecked((ulong)Interlocked.Increment(ref _nextCorrelation));
        await Ma2aConnection.WriteAsync(stream, correlation, Encoding.UTF8.GetBytes(command.ToJsonString()), cancellationToken);
        try { return await ReadResponseAsync(stream, correlation, command, cancellationToken); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new Ma2aProtocolException("invalid_frame", "The local response does not match the protocol envelope."); }
    }

    private static async Task<JsonElement> ReadResponseAsync(Stream stream, ulong correlation, JsonObject command, CancellationToken cancellationToken)
    {
        using var reader = new Ma2aConnection.ResponseReader(stream, correlation, cancellationToken);
        var response = Ma2aJson.Parse(await reader.ReadAsync(waitForReply: true));
        if (response.TryGetProperty("type", out var type) && type.GetString() == "snapshot_fragment")
            response = await Ma2aSnapshot.ReadAsync(reader, command["operation"]!.GetValue<string>(), response);
        Ma2aJson.ValidateResponse(response, command);
        if (command["operation"]!.GetValue<string>() is "snapshot_fetch" or "space_list")
            Ma2aSnapshot.ValidateStampedResponse(response, command["operation"]!.GetValue<string>() == "snapshot_fetch" ? "snapshot" : "spaces");
        return response;
    }
}
