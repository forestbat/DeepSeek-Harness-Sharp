using System.Text;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Core;

public readonly record struct SettingsMutationRequest(string Path, string Before, string After, ToolRunContext Context);

public interface ISettingsMutationGate
{
    const string ServiceName = "settingsMutations";
    string SettingsPath { get; }
    bool Owns(string path);
    Task CommitAsync(SettingsMutationRequest request, Func<string, CancellationToken, Task> commit, CancellationToken cancellationToken = default);
}

public static class SettingsMutation
{
    private const int ReadBufferBytes = 4096;

    public static ISettingsMutationGate RequireGate(ToolRunContext run)
        => (run.Agent?.Ctx ?? run.HostContext)?.Get<ISettingsMutationGate>(ISettingsMutationGate.ServiceName, false)
            ?? throw new InvalidOperationException("Settings mutations require the active approval service.");

    public static async Task<string> ReadTextAsync(string path, CancellationToken signal)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            bufferSize: ReadBufferBytes, options: FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(signal).ConfigureAwait(false);
    }

    public static async Task WriteTextAsync(string path, string before, string after, ToolRunContext run,
        FileMode mode = FileMode.Create)
    {
        var context = run.Agent?.Ctx ?? run.HostContext;
        var gate = context?.Get<ISettingsMutationGate>(ISettingsMutationGate.ServiceName, false);
        var active = gate?.SettingsPath ?? context?.GetProp(ISettingsMutationContext.SettingsPathProperty) as string;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var isSettings = active is not null && comparer.Equals(CanonicalFilePath.Canonicalize(path), CanonicalFilePath.Canonicalize(active));
        if (isSettings)
            await RequireGate(run).CommitAsync(new(path, before, after, run),
                (temporary, signal) => WriteFileAsync(temporary, after, FileMode.Create, signal), run.Signal).ConfigureAwait(false);
        else
        {
            await WriteOrdinaryFileAsync(path, after, mode, run.Signal).ConfigureAwait(false);
            run.RecordFileChange(path);
        }
    }

    private static async Task WriteOrdinaryFileAsync(string path, string text, FileMode mode, CancellationToken signal)
    {
        if (mode is not (FileMode.Create or FileMode.CreateNew or FileMode.Open))
        {
            await WriteFileAsync(path, text, mode, signal).ConfigureAwait(false);
            return;
        }
        signal.ThrowIfCancellationRequested();
        FilePathAccess.DemandOrdinaryFile(path, allowMissing: mode != FileMode.Open);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        var ownsTemporary = false;
        try
        {
            await using (var stream = OwnerOnlyFiles.Create(temporary))
            {
                ownsTemporary = true;
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(text.AsMemory(), signal).ConfigureAwait(false);
                await writer.FlushAsync(signal).ConfigureAwait(false);
            }
            signal.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows() && mode == FileMode.Open)
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            if (mode == FileMode.CreateNew)
                File.Move(temporary, path);
            else if (mode == FileMode.Open || File.Exists(path))
                File.Replace(temporary, path, destinationBackupFileName: null);
            else
                File.Move(temporary, path);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
                File.Delete(temporary);
        }
    }

    private static async Task WriteFileAsync(string path, string text, FileMode mode, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        FilePathAccess.DemandOrdinaryFile(path, allowMissing: mode != FileMode.Open);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows()
            && mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate or FileMode.Append)
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), signal).ConfigureAwait(false);
        await writer.FlushAsync(signal).ConfigureAwait(false);
        if (mode == FileMode.Open)
            stream.SetLength(stream.Position);
    }
}

public sealed record SettingsMutationAuditPayload(
    string Id, ToolCallId CallId, ToolCallId RootCallId, string ToolName,
    string Path, string Diff, string BeforeHash, string AfterHash, string Outcome) : SessionEventPayload
{
    public const string EventType = "settings/mutation";
    public override string Type => EventType;
}
