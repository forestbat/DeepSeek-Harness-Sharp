using Dsh.Runtime;
using Dsh.Skills;

namespace Dsh.Tests;

// This adapter observes real fixture filesystem I/O and an explicitly released asynchronous reader.
internal sealed class ObservedSkillFileSystem : ISkillFs
{
    private readonly Dsh.Tools.LocalFsService _local = new(new Context());
    public int Operations { get; private set; }
    public int Reads { get; private set; }
    public bool BlockReads { get; set; }
    public bool ReadWasCancelled { get; private set; }
    public TaskCompletionSource<bool> ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> ResumeRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> ReadFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<SkillFsTarget> Resolve(string path)
    {
        Operations++;
        return Task.FromResult(new SkillFsTarget((string)_local.Resolve(path)));
    }

    public Task<IReadOnlyList<SkillFsDirEntry>> ListDir(SkillFsTarget target)
    {
        Operations++;
        IReadOnlyList<SkillFsDirEntry> entries = [.. Directory.EnumerateFileSystemEntries(target.DisplayPath).Select(path =>
            new SkillFsDirEntry(Path.GetFileName(path), Directory.Exists(path) ? SkillFsEntryTypes.Directory : SkillFsEntryTypes.File, new SkillFsTarget(path)))];
        return Task.FromResult(entries);
    }

    public Task<SkillFsInfo?> Stat(SkillFsTarget target, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        Operations++;
        var info = (IDictionary<string, object?>?)_local.Stat(target.DisplayPath, signal);
        return Task.FromResult<SkillFsInfo?>(info is null ? null : new SkillFsInfo((string)info["type"]!,
            info.TryGetValue("size", out var size) ? (long)size! : 0));
    }

    public async Task<string> ReadText(SkillFsTarget target, CancellationToken signal)
    {
        Operations++;
        ReadEntered.TrySetResult(true);
        try
        {
            if (BlockReads) await ResumeRead.Task.WaitAsync(signal);
            var text = await File.ReadAllTextAsync(target.DisplayPath, signal);
            Reads++;
            return text;
        }
        catch (OperationCanceledException) when (signal.IsCancellationRequested)
        {
            ReadWasCancelled = true;
            throw;
        }
        finally
        {
            ReadFinished.TrySetResult(true);
        }
    }

    public void ResetObservations()
    {
        Operations = 0;
        Reads = 0;
        ReadWasCancelled = false;
    }
}
