using System.Text;
using System.Text.Json;
using Dsh.Runtime;
using Dsh.Agent;
using Dsh.Core;
using Dsh.Safety;

namespace Dsh.Safety;


/** Authorizes actual host-mediated operations; ordinary IO does not pin file identity. */
public static class SafetyFileAccess
{
    public static Task AuthorizeAsync(string path, string intent, ToolRunContext run,
        CancellationToken signal, SettingsApproval? settings = null)
    {
        signal.ThrowIfCancellationRequested();
        var safety = SafetyBoundary.Resolve(run.Agent?.Ctx ?? run.HostContext);
        return safety?.AuthorizeFileAsync(run.PermissionNamespace, run, path, intent, signal, settings: settings) ?? Task.CompletedTask;
    }

    public static async Task<FileStream> OpenReadAsync(string path, ToolRunContext run, SettingsApproval? settings = null)
    {
        await AuthorizeAsync(path, "read", run, run.Signal, settings).ConfigureAwait(false);
        FilePathAccess.DemandOrdinaryFile(path, allowMissing: false);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous);
    }

    public static async Task<IReadOnlyList<(string Path, bool IsDirectory)>> ReadDirectoryAsync(string path, ToolRunContext run,
        CancellationToken? signal = null, bool followLinks = false)
    {
        using var fused = FusedSignal.Fuse(run.Signal, signal ?? default);
        var token = fused.Token;
        await AuthorizeAsync(path, "read", run, token).ConfigureAwait(false);
        var entries = new List<(string Path, bool IsDirectory)>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            token.ThrowIfCancellationRequested();
            if (!followLinks && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            await AuthorizeAsync(entry, "read", run, token).ConfigureAwait(false);
            entries.Add((entry, Directory.Exists(entry)));
        }
        return entries;
    }

    public static async Task<string> ReadTextAsync(string path, ToolRunContext run, CancellationToken? signal = null,
        SettingsApproval? settings = null)
    {
        using var fused = FusedSignal.Fuse(run.Signal, signal ?? default);
        await AuthorizeAsync(path, "read", run, fused.Token, settings).ConfigureAwait(false);
        FilePathAccess.DemandOrdinaryFile(path, allowMissing: false);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false));
        return await reader.ReadToEndAsync(fused.Token).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadBytesAsync(string path, ToolRunContext run, long maximum)
    {
        await using var stream = await OpenReadAsync(path, run).ConfigureAwait(false);
        if (stream.Length > maximum) throw new IOException("The file exceeds the permitted read size.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, run.Signal).ConfigureAwait(false);
        return bytes;
    }
}
