using Dsh.Core;
using System.ComponentModel;

namespace Dsh.Memory;

/** 文件后端:项目记忆存在一个 markdown 文件里(默认 项目根/.dsh-memory.md)。 */
public sealed class FileMemoryStore(string path) : IMemoryStore
{
    public string Description => path;

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default, ToolRunContext? run = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (run is not null)
        {
            try
            {
                return await SafetyFileAccess.ReadTextAsync(path, run, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException
                || error is Win32Exception native && (native.NativeErrorCode == 2
                    || OperatingSystem.IsWindows() && native.NativeErrorCode == 3))
            {
                cancellationToken.ThrowIfCancellationRequested();
                run.Signal.ThrowIfCancellationRequested();
                return null;
            }
        }
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task SetAsync(string text, CancellationToken cancellationToken = default, ToolRunContext? run = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (run is not null)
        {
            var before = run.Agent?.Ctx.Get<ISettingsMutationGate>(ISettingsMutationGate.ServiceName, false)?.Owns(path) == true && File.Exists(path)
                ? await SafetyFileAccess.ReadTextAsync(path, run, cancellationToken, settingsReview: true).ConfigureAwait(false)
                : "";
            await SettingsMutation.WriteTextAsync(path, before, text, run, signal: cancellationToken).ConfigureAwait(false);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.tmp";
        await File.WriteAllTextAsync(temporary, text, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporary, path, overwrite: true);
    }
}
