using Dsh.Core;
using Dsh.Runtime;
using Dsh.Agent;
using Dsh.Safety;

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
            catch (Exception error) when (PathAbsence.IsAbsent(error))
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
            await SettingsMutation.WriteTextAsync(path, null, text, run, signal: cancellationToken).ConfigureAwait(false);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.tmp";
        await File.WriteAllTextAsync(temporary, text, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporary, path, overwrite: true);
    }
}
