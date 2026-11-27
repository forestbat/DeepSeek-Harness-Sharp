using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Text.Json.Nodes;

namespace Dsh.Ma2a.Services;

public enum Ma2aFrameKind : byte { Open = 1, Accept, Reject, Data, End, Control, Outcome, Cancel }

public readonly record struct Ma2aServiceFrame(Ma2aFrameKind Kind, ReadOnlyMemory<byte> Payload)
{
    public JsonNode? DecodeCbor() => Ma2aCbor.Decode(Payload);
    public byte Channel => Kind is Ma2aFrameKind.Data or Ma2aFrameKind.End
        ? Payload.Span[0] : throw new InvalidOperationException("Frame has no channel.");
    public ReadOnlyMemory<byte> Data => Kind is Ma2aFrameKind.Data
        ? Payload[1..] : throw new InvalidOperationException("Frame has no channel data.");
}

/// <summary>Distinguishes a refusal before admission from a failed or indeterminate operation.</summary>
public sealed class Ma2aServiceException(byte status, bool rejected, string message, Exception? inner = null)
    : IOException($"{message} (status: {status}, rejected: {rejected}).", inner)
{
    public byte Status { get; } = status;
    public bool Rejected { get; } = rejected;
    public bool OutcomeUnknown => Status == 10;
}

/// <summary>One native operation, with independent serialized reads and writes and no semantic retry.</summary>
public sealed class Ma2aServiceSession : IAsyncDisposable
{
    public const int MaximumDataLength = 65_536;
    private const int HeaderLength = 5;
    private const int MaximumHeaderPayload = 16_384;
    private const byte InvalidInputStatus = 2;
    private const byte OutcomeUnknownStatus = 10;
    private readonly Stream _stream;
    private readonly bool _opaqueControls;
    private readonly Ma2aServiceResponseState _response;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly byte[] _writeHeader = new byte[HeaderLength + 1];
    private readonly HashSet<byte> _endedInputs = [];
    private readonly HashSet<byte> _endedOutputs = [];
    private bool _accepted;
    private volatile bool _terminal;
    private bool _disposed;

    internal Ma2aServiceSession(Stream stream, string service, JsonNode body)
        : this(stream, service, new Ma2aServiceResponseState(service, body)) { }

    private Ma2aServiceSession(Stream stream, string service, Ma2aServiceResponseState response)
    {
        _stream = stream;
        Service = service;
        _opaqueControls = service == "service";
        _response = response;
    }

    public string Service { get; }
    public bool IsTerminal => _terminal;

    public static async Task<Ma2aServiceSession> OpenAsync(Ma2aClient client, string targetEndpointId,
        string service, string requestId, JsonNode body, CancellationToken cancellationToken = default)
    {
        var payload = EncodeOpen(targetEndpointId, service, requestId, body);
        var response = new Ma2aServiceResponseState(service, body);
        var stream = await client.OpenServiceStreamAsync(targetEndpointId, service, requestId, cancellationToken);
        var session = new Ma2aServiceSession(stream, service, response);
        try
        {
            await session.WriteAsync(Ma2aFrameKind.Open, payload, cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public static byte[] EncodeOpen(string targetEndpointId, string service, string requestId, JsonNode body)
    {
        var endpoint = DecodeIdentifier(targetEndpointId, 32);
        var request = DecodeIdentifier(requestId, 16);
        var code = service switch
        {
            "files" => 2, "transfer" => 3, "exec" => 4, "shell" => 5,
            "proxy" => 6, "process" => 7, "agent" => 8, "service" => 9,
            _ => throw new ArgumentException("Unknown MA2A service family.", nameof(service))
        };
        var header = new JsonObject
        {
            ["0"] = new JsonArray(1, 0), ["1"] = Ma2aCbor.Bytes(request),
            ["2"] = Ma2aCbor.Bytes(endpoint), ["3"] = code, ["4"] = body.DeepClone()
        };
        var payload = Ma2aCbor.Encode(header);
        ValidateLength(Ma2aFrameKind.Open, payload.Length);
        return payload;
    }

    public static byte[] DecodeIdentifier(string value, int byteLength)
    {
        if (value.Length != byteLength * 2 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("MA2A identifiers require canonical lowercase hexadecimal.", nameof(value));
        return Convert.FromHexString(value);
    }

    public async Task<Ma2aServiceFrame> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken);
        try
        {
            if (_terminal) throw new InvalidOperationException("The service operation has ended.");
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await ReadIncomingAsync(cancellationToken);
        }
        finally { _readGate.Release(); }
    }

    private async Task<Ma2aServiceFrame> ReadIncomingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var frame = await Ma2aServiceFraming.ReadAsync(_stream, cancellationToken);
            ValidateIncoming(frame);
            return frame;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or CborContentException
            or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            await DisposeAsync();
            if (error is OperationCanceledException or Ma2aServiceException) throw;
            var malformed = error is not IOException || error is InvalidDataException;
            throw new Ma2aServiceException(malformed ? InvalidInputStatus : OutcomeUnknownStatus, false,
                malformed ? "The service sent an invalid response." : "Service stream ended without a terminal outcome.", error);
        }
    }

    private void ValidateIncoming(Ma2aServiceFrame frame)
    {
        if (!_accepted && frame.Kind is not (Ma2aFrameKind.Accept or Ma2aFrameKind.Reject or Ma2aFrameKind.Outcome))
            throw new InvalidDataException("Expected service admission or terminal response.");
        switch (frame.Kind)
        {
            case Ma2aFrameKind.Accept:
                if (_accepted) throw new InvalidDataException("Repeated service admission.");
                _response.ValidateAccept(frame.DecodeCbor());
                _accepted = true;
                break;
            case Ma2aFrameKind.Reject:
                if (_accepted) throw new InvalidDataException("A service cannot reject after admission.");
                ValidateStatus(frame.Payload.Span[0]);
                _terminal = true;
                throw new Ma2aServiceException(frame.Payload.Span[0], true, "Target refused the service operation.");
            case Ma2aFrameKind.Outcome: ValidateOutcome(frame); break;
            case Ma2aFrameKind.Data or Ma2aFrameKind.End:
                _response.ValidateFrame(frame);
                if (frame.Channel > 2 || _endedOutputs.Contains(frame.Channel))
                    throw new InvalidDataException("Unknown or ended service channel.");
                if (frame.Kind == Ma2aFrameKind.End) _endedOutputs.Add(frame.Channel);
                break;
            case Ma2aFrameKind.Control:
                _response.ValidateFrame(frame);
                if (!_opaqueControls) frame.DecodeCbor();
                break;
            default: throw new InvalidDataException("Invalid server service frame.");
        }
    }

    private void ValidateOutcome(Ma2aServiceFrame frame)
    {
        var envelope = frame.DecodeCbor() as JsonObject ?? throw new InvalidDataException("Invalid service outcome.");
        if (envelope.Count != 2 || !envelope.ContainsKey("1")) throw new InvalidDataException("Invalid service outcome envelope.");
        var status = envelope["0"]?.GetValue<ulong>() ?? throw new InvalidDataException("Missing service status.");
        if (status > byte.MaxValue) throw new InvalidDataException("Invalid service status.");
        if (status != 0)
        {
            ValidateStatus((byte)status);
            if (envelope["1"] is not null) throw new InvalidDataException("Failed outcome must have a null result.");
            _terminal = true;
            throw new Ma2aServiceException((byte)status, false, "Service operation failed.");
        }
        _response.ValidateSuccess(envelope["1"]);
        _terminal = true;
    }

    private static void ValidateStatus(byte status)
    {
        if (status is not (>= 1 and <= 10 or >= 16 and <= 26 or >= 32 and <= 34 or 48 or >= 64 and <= 66
            or >= 80 and <= 84 or >= 96 and <= 101))
            throw new InvalidDataException("Unknown service status.");
    }

    /// <summary>Sends data from the supplied memory, which must remain unchanged until the operation completes.</summary>
    public async Task SendDataAsync(byte channel, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (channel > 2 || _endedInputs.Contains(channel)) throw new InvalidOperationException("Channel is unavailable for input.");
            if (_response.IsHostedCall && channel != 0) throw new InvalidOperationException("Hosted input must use channel 0.");
            if (!_accepted) throw new InvalidOperationException("Read service admission before sending data.");
            while (!bytes.IsEmpty)
            {
                var count = Math.Min(bytes.Length, MaximumDataLength);
                await WriteFrameAsync(Ma2aFrameKind.Data, bytes[..count], cancellationToken, dataChannel: channel);
                bytes = bytes[count..];
            }
        }
        finally { _writeGate.Release(); }
    }

    public async Task EndAsync(byte channel = 0, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (_response.IsHostedCall && channel != 0) throw new InvalidOperationException("Hosted input must use channel 0.");
            if (!_accepted || channel > 2 || !_endedInputs.Add(channel)) throw new InvalidOperationException("Channel cannot be ended.");
            await WriteFrameAsync(Ma2aFrameKind.End, new byte[] { channel }, cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    public Task ControlAsync(JsonNode control, CancellationToken cancellationToken = default)
    {
        if (!_accepted) throw new InvalidOperationException("Read service admission before sending control.");
        return WriteAsync(Ma2aFrameKind.Control, Ma2aCbor.Encode(control), cancellationToken);
    }

    /// <summary>Sends control bytes from memory that must remain unchanged until the operation completes.</summary>
    public Task ControlBytesAsync(ReadOnlyMemory<byte> control, CancellationToken cancellationToken = default)
    {
        if (!_accepted || !_response.IsHostedCall)
            throw new InvalidOperationException("Opaque controls require an admitted hosted service call.");
        return WriteAsync(Ma2aFrameKind.Control, control, cancellationToken);
    }

    public Task CancelAsync(CancellationToken cancellationToken = default)
        => WriteAsync(Ma2aFrameKind.Cancel, ReadOnlyMemory<byte>.Empty, cancellationToken);

    private async Task WriteAsync(Ma2aFrameKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try { await WriteFrameAsync(kind, payload, cancellationToken); }
        finally { _writeGate.Release(); }
    }

    private async Task WriteFrameAsync(Ma2aFrameKind kind, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken, byte? dataChannel = null)
    {
        var channelLength = dataChannel.HasValue ? 1 : 0;
        var payloadLength = payload.Length + channelLength;
        ValidateLength(kind, payloadLength);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_terminal) throw new InvalidOperationException("The service operation has ended.");
            _writeHeader[0] = (byte)kind;
            BinaryPrimitives.WriteUInt32BigEndian(_writeHeader.AsSpan(1), (uint)payloadLength);
            if (dataChannel is { } channel) _writeHeader[HeaderLength] = channel;
            await _stream.WriteAsync(_writeHeader.AsMemory(0, HeaderLength + channelLength), cancellationToken);
            await _stream.WriteAsync(payload, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            await DisposeAsync();
            if (error is OperationCanceledException) throw;
            throw new Ma2aServiceException(OutcomeUnknownStatus, false, "Service write failed; operation outcome is unknown.", error);
        }
    }

    public static void ValidateLength(Ma2aFrameKind kind, long length)
    {
        var valid = kind switch
        {
            Ma2aFrameKind.Open or Ma2aFrameKind.Accept or Ma2aFrameKind.Control => length is >= 1 and <= MaximumHeaderPayload,
            Ma2aFrameKind.Outcome => length is >= 1 and <= MaximumDataLength,
            Ma2aFrameKind.Data => length is >= 2 and <= MaximumDataLength + 1,
            Ma2aFrameKind.End or Ma2aFrameKind.Reject => length == 1,
            Ma2aFrameKind.Cancel => length == 0,
            _ => false
        };
        if (!valid) throw new InvalidDataException("Invalid service frame kind or payload length.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stream.DisposeAsync();
    }
}
