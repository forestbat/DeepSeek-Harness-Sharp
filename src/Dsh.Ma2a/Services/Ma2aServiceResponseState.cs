using System.Globalization;
using System.Text.Json.Nodes;

namespace Dsh.Ma2a.Services;

/// <summary>Tracks response contracts whose bounds depend on the operation being requested.</summary>
internal sealed class Ma2aServiceResponseState
{
    private const int MaximumHostedAcceptBytes = 8_192;
    private const byte IntegrityMismatchStatus = 32;
    private const byte TooLargeStatus = 25;
    private readonly ulong? _readLength;
    private readonly bool _describe;
    private ulong _readBytes;

    public Ma2aServiceResponseState(string service, JsonNode body)
    {
        if (service is not ("files" or "service") || body is not JsonArray request || request.Count == 0) return;
        var operation = request[0]?.ToJsonString();
        IsHostedCall = service == "service" && operation == "1";
        _describe = service == "service" && operation == "0";
        if (service == "files" && operation == "3")
            _readLength = Math.Min(ulong.Parse(request[3]!.ToJsonString(), CultureInfo.InvariantCulture), (ulong)Ma2aServiceSession.MaximumDataLength);
    }

    public bool IsHostedCall { get; }

    public void ValidateAccept(JsonNode? value)
    {
        if (_describe) throw new InvalidDataException("Service discovery cannot accept a stream.");
        if (IsHostedCall) ValidateBytes(value, MaximumHostedAcceptBytes);
        if (_readLength is not null && value is not JsonArray { Count: 0 })
            throw new InvalidDataException("Invalid Files Read admission.");
    }

    public void ValidateFrame(Ma2aServiceFrame frame)
    {
        if (_describe) throw new InvalidDataException("Service discovery requires a terminal response.");
        if (IsHostedCall && frame.Kind is Ma2aFrameKind.Data or Ma2aFrameKind.End && frame.Channel is not (1 or 2))
            throw new InvalidDataException("Hosted output must use channel 1 or 2.");
        if (_readLength is null) return;
        if (frame.Kind != Ma2aFrameKind.Data || frame.Channel != 1)
            throw new InvalidDataException("Files Read requires output data followed by its outcome.");
        if ((ulong)frame.Data.Length > (ulong)Ma2aServiceSession.MaximumDataLength - _readBytes)
            throw new Ma2aServiceException(TooLargeStatus, false, "Files Read exceeds its content bound.");
        _readBytes += (ulong)frame.Data.Length;
    }

    public void ValidateSuccess(JsonNode? result)
    {
        if (IsHostedCall) ValidateBytes(result, Ma2aServiceRequests.MaximumHostedCallResultBytes);
        if (_readLength is not { } requested) return;
        if (result is not JsonArray { Count: 3 } read || read[0]?.GetValue<ulong>() != 2)
            throw new InvalidDataException("Expected a Files Read result.");
        var declared = read[1]?.GetValue<ulong>() ?? throw new InvalidDataException("Missing Files Read byte count.");
        if (declared > uint.MaxValue) throw new InvalidDataException("Files Read byte count exceeds its native integer type.");
        _ = read[2]?.GetValue<bool>() ?? throw new InvalidDataException("Missing Files Read EOF indicator.");
        if (declared != _readBytes || declared > requested)
            throw new Ma2aServiceException(IntegrityMismatchStatus, false, "Files Read result does not match its received bytes.");
    }

    private static void ValidateBytes(JsonNode? value, int maximum)
    {
        if (value is not JsonObject { Count: 1 } bytes || bytes["$bytes"] is not { } content
            || content.GetValue<string>().Length > maximum * 2)
            throw new InvalidDataException("Hosted admission and successful results require bounded opaque byte strings.");
    }
}
