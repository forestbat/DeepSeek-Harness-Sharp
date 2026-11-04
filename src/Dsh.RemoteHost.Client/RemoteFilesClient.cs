using System.Text.Json;
using static Dsh.RemoteHost.RemoteHostRpc;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Files domain. */
public sealed class RemoteFilesClient : IWorkspaceFiles
{
    private readonly RemoteHostClient _host;

    internal RemoteFilesClient(RemoteHostClient host) => _host = host;

    public async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var content = await _host.SendAsync(FilesProtocol.MethodFileRead, Serialize(new RemoteFilePath(path), FilesProtocolJsonContext.Default.RemoteFilePath), FilesProtocolJsonContext.Default.RemoteFileContent, cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(content.Base64);
    }

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => _host.SendAsync(FilesProtocol.MethodFileWrite, Serialize(new RemoteFileContent(path, Convert.ToBase64String(content.Span)), FilesProtocolJsonContext.Default.RemoteFileContent), FilesProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => _host.SendAsync(FilesProtocol.MethodFileList, Serialize(new RemoteFilePath(path), FilesProtocolJsonContext.Default.RemoteFilePath), FilesProtocolJsonContext.Default.RemoteDirectoryListing, cancellationToken);

    public Task<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken = default)
        => _host.SendAsync(FilesProtocol.MethodFsStat, FilePath(path), FilesProtocolJsonContext.Default.RemoteFileStat, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => _host.SendAsync(FilesProtocol.MethodFsMkdir, FilePath(path), FilesProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task RenameAsync(string from, string to, bool overwrite = false, CancellationToken cancellationToken = default)
        => _host.SendAsync(FilesProtocol.MethodFsRename, Serialize(new RemoteFsRename(from, to, overwrite), FilesProtocolJsonContext.Default.RemoteFsRename), FilesProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task DeleteAsync(string path, bool recursive = false, CancellationToken cancellationToken = default)
        => _host.SendAsync(FilesProtocol.MethodFsDelete, Serialize(new RemoteFsDelete(path, recursive), FilesProtocolJsonContext.Default.RemoteFsDelete), FilesProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<RemoteUploadRef> BeginUploadAsync(string path, bool overwrite = true, CancellationToken cancellationToken = default)
        => _host.SendAsync(FilesProtocol.MethodFsUploadBegin, Serialize(new RemoteUploadBegin(path, overwrite), FilesProtocolJsonContext.Default.RemoteUploadBegin), FilesProtocolJsonContext.Default.RemoteUploadRef, cancellationToken);

    public Task<long> UploadChunkAsync(string uploadId, long offset, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => _host.SendAsync(
            FilesProtocol.MethodFsUploadChunk,
            Serialize(new RemoteUploadChunk(uploadId, offset, Convert.ToBase64String(content.Span)), FilesProtocolJsonContext.Default.RemoteUploadChunk),
            FilesProtocolJsonContext.Default.Int64,
            cancellationToken);

    public Task<RemoteFileStat> CommitUploadAsync(string uploadId, long? size = null, CancellationToken cancellationToken = default)
        => _host.SendAsync(FilesProtocol.MethodFsUploadCommit, Serialize(new RemoteUploadCommit(uploadId, size), FilesProtocolJsonContext.Default.RemoteUploadCommit), FilesProtocolJsonContext.Default.RemoteFileStat, cancellationToken);

    public Task AbortUploadAsync(string uploadId, CancellationToken cancellationToken = default)
        => _host.SendAsync(FilesProtocol.MethodFsUploadAbort, Serialize(new RemoteUploadRef(uploadId), FilesProtocolJsonContext.Default.RemoteUploadRef), FilesProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<RemoteFileChunk> DownloadChunkAsync(string path, long offset, int? length = null, CancellationToken cancellationToken = default)
        => _host.SendAsync(FilesProtocol.MethodFsDownload, Serialize(new RemoteDownloadRequest(path, offset, length), FilesProtocolJsonContext.Default.RemoteDownloadRequest), FilesProtocolJsonContext.Default.RemoteFileChunk, cancellationToken);

    public async Task<RemoteFileStat> UploadFileAsync(string path, Stream source, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        var upload = await BeginUploadAsync(path, overwrite, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[upload.ChunkBytes];
        long sent = 0;
        try
        {
            int read;
            while ((read = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)) > 0)
                sent = await UploadChunkAsync(upload.UploadId, sent, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            return await CommitUploadAsync(upload.UploadId, sent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortUploadQuietlyAsync(upload.UploadId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<long> DownloadFileAsync(string path, Stream destination, CancellationToken cancellationToken = default)
    {
        long offset = 0;
        while (true)
        {
            var chunk = await DownloadChunkAsync(path, offset, null, cancellationToken).ConfigureAwait(false);
            var bytes = Convert.FromBase64String(chunk.Base64);
            await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            offset += bytes.Length;
            if (chunk.Eof || bytes.Length == 0)
                return offset;
        }
    }

    /** The upload is already gone when the host rejected it; a closed connection discards it on the host side anyway. */
    private async Task AbortUploadQuietlyAsync(string uploadId)
    {
        if (_host.IsClosed)
            return;
        try
        {
            await AbortUploadAsync(uploadId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or ObjectDisposedException)
        {
        }
    }

    private static JsonElement FilePath(string path) => Serialize(new RemoteFilePath(path), FilesProtocolJsonContext.Default.RemoteFilePath);
}



