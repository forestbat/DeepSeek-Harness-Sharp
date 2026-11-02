using Dsh.Core;
using System.ComponentModel;

namespace Dsh.Memory;

/** 文件后端:项目记忆存在一个 markdown 文件里(默认 项目根/.dsh-memory.md)。 */
public sealed class FileMemoryStore(string path) : IMemoryStore
{
    public string Description => path;

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SettingsMutationScope.Current is { } scope)
        {
            try
            {
                return await SafetyFileAccess.ReadTextAsync(path, scope.Context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException
                || error is Win32Exception native && (native.NativeErrorCode == 2
                    || OperatingSystem.IsWindows() && native.NativeErrorCode == 3))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scope.Context.Signal.ThrowIfCancellationRequested();
                return null;
            }
        }
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task SetAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SettingsMutationScope.Current is { } scope)
        {
            var before = scope.Gate?.Owns(path) == true
                ? await SettingsMutationScope.ReadTextAsync(path, scope.Context, settingsReview: true,
                    signal: cancellationToken).ConfigureAwait(false)
                : "";
            await SettingsMutationScope.WriteTextAsync(path, before, text, scope.Context,
                signal: cancellationToken).ConfigureAwait(false);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.tmp";
        await File.WriteAllTextAsync(temporary, text, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporary, path, overwrite: true);
    }
}
