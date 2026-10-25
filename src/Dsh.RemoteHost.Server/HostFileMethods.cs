using System.Collections.Concurrent;
using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** host.file.* and host.fs.* requests of one connection. */
internal static class HostFileMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(HostFileSystem files, HostUploads uploads, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case FilesProtocol.MethodFileRead:
            {
                var request = Require(message, FilesProtocolJsonContext.Default.RemoteFilePath, "read requires path");
                var bytes = await files.ReadAsync(request.Path, cancellationToken).ConfigureAwait(false);
                return Serialize(new RemoteFileContent(request.Path, Convert.ToBase64String(bytes)), FilesProtocolJsonContext.Default.RemoteFileContent);
            }
            case FilesProtocol.MethodFileWrite:
            {
                var request = Require(message, FilesProtocolJsonContext.Default.RemoteFileContent, "write requires path and content");
                await files.WriteAsync(request.Path, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);
                return True;
            }
            case FilesProtocol.MethodFileList:
                return Serialize(files.List(Require(message, FilesProtocolJsonContext.Default.RemoteFilePath, "list requires path").Path), FilesProtocolJsonContext.Default.RemoteDirectoryListing);
            case FilesProtocol.MethodFsStat:
                return Serialize(files.Stat(Require(message, FilesProtocolJsonContext.Default.RemoteFilePath, "stat requires path").Path), FilesProtocolJsonContext.Default.RemoteFileStat);
            case FilesProtocol.MethodFsMkdir:
                files.CreateDirectory(Require(message, FilesProtocolJsonContext.Default.RemoteFilePath, "mkdir requires path").Path);
                return True;
            case FilesProtocol.MethodFsRename:
                files.Rename(Require(message, FilesProtocolJsonContext.Default.RemoteFsRename, "rename requires from and to"));
                return True;
            case FilesProtocol.MethodFsDelete:
                files.Delete(Require(message, FilesProtocolJsonContext.Default.RemoteFsDelete, "delete requires path"));
                return True;
            case FilesProtocol.MethodFsDownload:
            {
                var request = Require(message, FilesProtocolJsonContext.Default.RemoteDownloadRequest, "download requires path and offset");
                return Serialize(await files.DownloadAsync(request, cancellationToken).ConfigureAwait(false), FilesProtocolJsonContext.Default.RemoteFileChunk);
            }
            default:
                return await HandleUploadAsync(files, uploads, name, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<JsonElement?> HandleUploadAsync(HostFileSystem files, HostUploads uploads, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case FilesProtocol.MethodFsUploadBegin:
            {
                var upload = files.BeginUpload(Require(message, FilesProtocolJsonContext.Default.RemoteUploadBegin, "upload begin requires path"));
                uploads.Add(upload);
                return Serialize(new RemoteUploadRef(upload.Id, HostFileSystem.TransferChunkBytes), FilesProtocolJsonContext.Default.RemoteUploadRef);
            }
            case FilesProtocol.MethodFsUploadChunk:
            {
                var request = Require(message, FilesProtocolJsonContext.Default.RemoteUploadChunk, "upload chunk requires uploadId, offset and content");
                var received = await uploads.Get(request.UploadId).AppendAsync(request.Offset, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);
                return Serialize(received, FilesProtocolJsonContext.Default.Int64);
            }
            case FilesProtocol.MethodFsUploadCommit:
            {
                var request = Require(message, FilesProtocolJsonContext.Default.RemoteUploadCommit, "upload commit requires uploadId");
                await using var upload = uploads.Take(request.UploadId);
                return Serialize(await upload.CommitAsync(request.Size, cancellationToken).ConfigureAwait(false), FilesProtocolJsonContext.Default.RemoteFileStat);
            }
            case FilesProtocol.MethodFsUploadAbort:
            {
                await using var upload = uploads.Take(Require(message, FilesProtocolJsonContext.Default.RemoteUploadRef, "upload abort requires uploadId").UploadId);
                return True;
            }
            default:
                throw MethodNotFound(name);
        }
    }
}

/** Uploads in progress on one connection; whatever is left when the connection ends is discarded with its temporary file. */
internal sealed class HostUploads : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HostUpload> _uploads = new(StringComparer.Ordinal);

    public void Add(HostUpload upload) => _uploads[upload.Id] = upload;

    public HostUpload Get(string id)
        => _uploads.TryGetValue(id, out var upload) ? upload : throw new JsonRpcException(InvalidParams, $"unknown upload: {id}");

    public HostUpload Take(string id)
        => _uploads.TryRemove(id, out var upload) ? upload : throw new JsonRpcException(InvalidParams, $"unknown upload: {id}");

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _uploads.Keys)
        {
            if (_uploads.TryRemove(id, out var upload))
                await upload.DisposeAsync().ConfigureAwait(false);
        }
    }
}
