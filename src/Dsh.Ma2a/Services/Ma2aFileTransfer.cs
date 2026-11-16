using Blake3;
using Dsh.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Ma2a.Services;

public readonly record struct Ma2aTransferProgress(ulong Completed, ulong Total);
public readonly record struct Ma2aTransferResult(ulong Bytes, ulong Size, string Blake3);

/// <summary>Bounded, verified file transfers with durable download identity and explicit overwrite.</summary>
public static class Ma2aFileTransfer
{
    public const string PartialFileSuffix = ".dsh-ma2a-part";
    private sealed record DownloadIdentity(string Endpoint, string Path, ulong Size, ulong ModifiedNanoseconds);

    public static async Task<Ma2aTransferResult> UploadAsync(Ma2aClient client, string endpoint,
        string remotePath, string localPath, string requestId, bool replace = false,
        IProgress<Ma2aTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        DemandTransferFile(localPath, allowMissing: false);
        await using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            Ma2aServiceSession.MaximumDataLength, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = checked((ulong)source.Length);
        using var hasher = Hasher.New();
        await using var session = await Ma2aServiceSession.OpenAsync(client, endpoint, "transfer", requestId,
            Ma2aServiceRequests.Upload(remotePath, size, replace), cancellationToken);
        var first = await session.ReadAsync(cancellationToken);
        if (first.Kind == Ma2aFrameKind.Outcome)
        {
            await HashPrefixAsync(source, hasher, cancellationToken);
            var replayed = DecodeResult(first);
            if (replayed.Size != size || replayed.Bytes != size || replayed.Blake3 != hasher.Finalize().ToString())
                throw new Ma2aServiceException(32, false, "Upload replay does not match the selected source content.");
            return replayed;
        }
        var accepted = first.DecodeCbor() as JsonArray;
        if (accepted is not { Count: 1 } || accepted[0]!.GetValue<ulong>() != 1)
            throw new InvalidDataException("Expected upload admission.");
        await SendUploadAsync(session, source, size, hasher, progress, cancellationToken);
        await session.EndAsync(cancellationToken: cancellationToken);
        var expectedHash = hasher.Finalize().ToString();
        await session.ControlAsync(new JsonArray(0, new JsonObject { ["$bytes"] = expectedHash }), cancellationToken);
        var terminal = await session.ReadAsync(cancellationToken);
        var result = DecodeResult(terminal);
        if (result.Bytes != size || result.Size != size || result.Blake3 != expectedHash)
            throw new Ma2aServiceException(32, false, "Uploaded content failed its integrity check.");
        return result;
    }

    private static async Task SendUploadAsync(Ma2aServiceSession session, FileStream source, ulong size,
        Hasher hasher, IProgress<Ma2aTransferProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[Ma2aServiceSession.MaximumDataLength];
        ulong sent = 0;
        while (sent < size)
        {
            var length = (int)Math.Min((ulong)buffer.Length, size - sent);
            var count = await source.ReadAsync(buffer.AsMemory(0, length), cancellationToken);
            if (count == 0) throw new EndOfStreamException("Upload source became shorter.");
            hasher.Update(buffer.AsSpan(0, count));
            await session.SendDataAsync(0, buffer.AsMemory(0, count), cancellationToken);
            sent += (ulong)count;
            progress?.Report(new Ma2aTransferProgress(sent, size));
        }
        if (source.Length != (long)size) throw new IOException("Upload source changed size.");
    }

    public static Task<Ma2aTransferResult> DownloadAsync(Ma2aClient client, string endpoint,
        string remotePath, string localPath, string requestId, bool replace = false, bool resume = true,
        IProgress<Ma2aTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => DownloadCoreAsync(client, endpoint, "transfer", remotePath, localPath, requestId, replace, resume,
            (offset, identity) => Ma2aServiceRequests.Download(remotePath, offset, identity), progress, cancellationToken);

    public static Task<Ma2aTransferResult> DownloadArtifactAsync(Ma2aClient client, string endpoint,
        string agent, string task, string name, string localPath, string requestId, bool replace = false, bool resume = true,
        IProgress<Ma2aTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => DownloadCoreAsync(client, endpoint, "agent", $"artifact:{agent}/{task}/{name}", localPath, requestId, replace, resume,
            (offset, identity) => Ma2aServiceRequests.AgentArtifact(agent, task, name, offset, identity), progress, cancellationToken);

    private static async Task<Ma2aTransferResult> DownloadCoreAsync(Ma2aClient client, string endpoint,
        string service, string sourceKey, string localPath, string requestId, bool replace, bool resume,
        Func<ulong, JsonArray?, JsonNode> request, IProgress<Ma2aTransferProgress>? progress, CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(localPath);
        DemandTransferFile(destination, allowMissing: true);
        if (!replace && File.Exists(destination)) throw new IOException("Download destination already exists.");
        var part = $"{destination}{PartialFileSuffix}";
        var metadata = $"{part}.meta";
        foreach (var path in new[] { part, metadata, $"{metadata}.new", $"{part}.lock" })
            DemandTransferFile(path, allowMissing: true);
        await using var lease = new FileStream($"{part}.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var identity = await ReadIdentityAsync(metadata, endpoint, sourceKey, resume, cancellationToken);
        await using var sink = new FileStream(part, identity is null ? FileMode.OpenOrCreate : FileMode.Open,
            FileAccess.ReadWrite, FileShare.None, Ma2aServiceSession.MaximumDataLength, FileOptions.Asynchronous);
        if (identity is null && sink.Length != 0) throw new IOException("Partial download has no source identity.");
        var offset = checked((ulong)sink.Length);
        if (identity is not null && offset > identity.Size) throw new InvalidDataException("Partial download exceeds its source identity.");
        using var hasher = Hasher.New();
        await HashPrefixAsync(sink, hasher, cancellationToken);
        var sourceIdentity = identity is null ? null : new JsonArray(identity.Size, identity.ModifiedNanoseconds);
        await using var session = await Ma2aServiceSession.OpenAsync(client, endpoint, service, requestId,
            request(offset, sourceIdentity), cancellationToken);
        var first = await session.ReadAsync(cancellationToken);
        var acceptedIdentity = DecodeIdentity(first, endpoint, sourceKey);
        if (identity is not null && acceptedIdentity != identity)
            throw new InvalidDataException("Download admission changed the resumed source identity.");
        identity = acceptedIdentity;
        await PersistIdentityAsync(metadata, identity, cancellationToken);
        var result = await ReceiveDownloadAsync(session, sink, hasher, offset, identity.Size, progress, cancellationToken);
        await sink.FlushAsync(cancellationToken);
        sink.Flush(flushToDisk: true);
        await sink.DisposeAsync();
        DemandTransferFile(part, allowMissing: false);
        DemandTransferFile(destination, allowMissing: true);
        File.Move(part, destination, replace);
        File.Delete(metadata);
        return result;
    }

    private static void DemandTransferFile(string path, bool allowMissing)
    {
        FilePathAccess.DemandOrdinaryFile(path, allowMissing);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("Transfer files and sidecars cannot be symbolic links or reparse points.");
        }
        catch (FileNotFoundException) when (allowMissing) { }
        catch (DirectoryNotFoundException) when (allowMissing) { }
    }

    private static async Task<DownloadIdentity?> ReadIdentityAsync(string metadata, string endpoint,
        string path, bool resume, CancellationToken cancellationToken)
    {
        if (!File.Exists(metadata)) return null;
        DemandTransferFile(metadata, allowMissing: false);
        if (!resume) throw new IOException("A previous download exists; choose another destination or resume it.");
        await using var source = File.OpenRead(metadata);
        var identity = await JsonSerializer.DeserializeAsync<DownloadIdentity>(source, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Invalid partial download identity.");
        if (identity.Endpoint != endpoint || identity.Path != path)
            throw new InvalidDataException("The partial download belongs to another source.");
        return identity;
    }

    private static async Task HashPrefixAsync(FileStream source, Hasher hasher, CancellationToken cancellationToken)
    {
        var buffer = new byte[Ma2aServiceSession.MaximumDataLength];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0) hasher.Update(buffer.AsSpan(0, read));
    }

    private static DownloadIdentity DecodeIdentity(Ma2aServiceFrame frame, string endpoint, string path)
    {
        if (frame.Kind != Ma2aFrameKind.Accept || frame.DecodeCbor() is not JsonArray { Count: 2 } accept
            || accept[0]!.GetValue<ulong>() != 0 || accept[1] is not JsonArray { Count: 2 } identity)
            throw new InvalidDataException("Expected download admission and content identity.");
        return new DownloadIdentity(endpoint, path, identity[0]!.GetValue<ulong>(), identity[1]!.GetValue<ulong>());
    }

    private static async Task PersistIdentityAsync(string path, DownloadIdentity identity, CancellationToken cancellationToken)
    {
        DemandTransferFile(path, allowMissing: true);
        DemandTransferFile($"{path}.new", allowMissing: true);
        await using (var file = new FileStream($"{path}.new", FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(file, identity, cancellationToken: cancellationToken);
            await file.FlushAsync(cancellationToken);
            file.Flush(flushToDisk: true);
        }
        File.Move($"{path}.new", path, overwrite: true);
    }

    private static async Task<Ma2aTransferResult> ReceiveDownloadAsync(Ma2aServiceSession session, FileStream sink,
        Hasher hasher, ulong offset, ulong size, IProgress<Ma2aTransferProgress>? progress, CancellationToken cancellationToken)
    {
        ulong received = 0;
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken);
            if (frame.Kind == Ma2aFrameKind.Outcome)
            {
                var result = DecodeResult(frame);
                if (result.Bytes != received || result.Size != size || offset + received != size || result.Blake3 != hasher.Finalize().ToString())
                    throw new Ma2aServiceException(32, false, "Downloaded content failed its integrity check.");
                return result;
            }
            if (frame.Kind != Ma2aFrameKind.Data || frame.Channel != 1 || (ulong)frame.Data.Length > size - offset - received)
                throw new InvalidDataException("Unexpected download content frame.");
            await sink.WriteAsync(frame.Data, cancellationToken);
            hasher.Update(frame.Data.Span);
            received += (ulong)frame.Data.Length;
            progress?.Report(new Ma2aTransferProgress(offset + received, size));
        }
    }

    public static Ma2aTransferResult DecodeResult(Ma2aServiceFrame frame)
    {
        if (frame.Kind != Ma2aFrameKind.Outcome || frame.DecodeCbor() is not JsonObject envelope
            || envelope["0"]!.GetValue<ulong>() != 0 || envelope["1"] is not JsonArray { Count: 3 } result)
            throw new InvalidDataException("Expected a successful transfer outcome.");
        var hash = result[2]?["$bytes"]?.GetValue<string>() ?? throw new InvalidDataException("Missing transfer hash.");
        if (hash.Length != 64) throw new InvalidDataException("Invalid transfer hash length.");
        return new Ma2aTransferResult(result[0]!.GetValue<ulong>(), result[1]!.GetValue<ulong>(), hash);
    }
}
