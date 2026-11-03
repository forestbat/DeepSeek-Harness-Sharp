using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Dsh.Sandbox;

/// <summary>Collects descendant topology with fixed workers and joins every worker before returning.</summary>
internal static class FileSystemTreeTraversal
{
    private const int WorkerCount = 4;
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false
    };

    public static void Collect(string root, Action<string, bool> accept, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var directories = Channel.CreateUnbounded<string>();
        var pending = 1;
        Exception? failure = null;
        directories.Writer.TryWrite(root);
        var workers = new Task[WorkerCount];
        for (var index = 0; index < workers.Length; index++)
            workers[index] = Task.Run(RunWorkerAsync);
        try { Task.WhenAll(workers).GetAwaiter().GetResult(); }
        catch
        {
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }

        async Task RunWorkerAsync()
        {
            try
            {
                await foreach (var directory in directories.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    foreach (var (path, isDirectory) in new FileSystemEnumerable<(string Path, bool Directory)>(directory,
                        static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory), Options))
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        accept(path, isDirectory);
                        if (!isDirectory)
                            continue;
                        Interlocked.Increment(ref pending);
                        directories.Writer.TryWrite(path);
                    }
                    if (Interlocked.Decrement(ref pending) == 0)
                        directories.Writer.TryComplete();
                }
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref failure, error, null);
                stop.Cancel();
                directories.Writer.TryComplete();
                throw;
            }
        }
    }
}
