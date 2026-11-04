using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace Dsh.Sandbox;

/// <summary>Collects non-reparse descendants with bounded, dedicated directory-enumeration threads.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDirectoryCollector
{
    private const int WorkerCount = 8;
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        BufferSize = 16 * 1024
    };

    /// <summary>
    /// Visits descendants, excluding the root, serially on the calling thread.
    /// Entries retain recursive FileSystemEnumerable's breadth-first order on an unchanged tree.
    /// No callback runs after this method returns or throws.
    /// The callback may use a plain dictionary, but must only collect topology: callers must finish all roots before modifying any ACL.
    /// </summary>
    /// <remarks>
    /// Directories are opened by full path when a worker reaches them, rather than relative to their parent at discovery.
    /// Concurrent changes can therefore produce a different observation or an error than recursive FileSystemEnumerable.
    /// The work queue and retained topology grow with the tree; only the number of enumeration threads is bounded.
    /// Missing directories and other enumeration errors are not ignored.
    /// </remarks>
    public static void Collect(string root, Action<string, bool> accept, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(accept);
        cancellationToken.ThrowIfCancellationRequested();
        var rootEntries = new List<Entry>();
        foreach (var entry in new FileSystemEnumerable<Entry>(root, TransformEntry, Options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rootEntries.Add(entry);
        }

        var directories = new Queue<DirectoryWork>();
        foreach (var entry in rootEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            accept(entry.Path, entry.Child is not null);
            if (entry.Child is { } child)
                directories.Enqueue(child);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (directories.Count == 0)
            return;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var jobs = new BlockingCollection<DirectoryWork>();
        // Count both queued and active directories, adding each child before its parent can finish.
        var pending = directories.Count;
        foreach (var directory in directories)
            jobs.Add(directory);
        var workers = new Thread[WorkerCount];
        var started = 0;
        ExceptionDispatchInfo? failure = null;
        try
        {
            for (var index = 0; index < workers.Length && !stop.IsCancellationRequested; index++)
            {
                var worker = new Thread(RunWorker) { IsBackground = true, Name = "Sandbox directory collector" };
                workers[index] = worker;
                worker.Start();
                started++;
            }
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null);
            stop.Cancel();
        }
        finally
        {
            for (var index = 0; index < started; index++)
                workers[index].Join();
        }

        Volatile.Read(ref failure)?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        while (directories.TryDequeue(out var directory))
        {
            foreach (var entry in directory.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                accept(entry.Path, entry.Child is not null);
                if (entry.Child is { } child)
                    directories.Enqueue(child);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        void RunWorker()
        {
            try
            {
                foreach (var work in jobs.GetConsumingEnumerable(stop.Token))
                {
                    foreach (var entry in new FileSystemEnumerable<Entry>(work.Path, TransformEntry, Options))
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        work.Entries.Add(entry);
                        if (entry.Child is { } child)
                        {
                            Interlocked.Increment(ref pending);
                            jobs.Add(child, stop.Token);
                        }
                    }
                    if (Interlocked.Decrement(ref pending) == 0)
                        jobs.CompleteAdding();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null);
                stop.Cancel();
            }
        }
    }

    private static Entry TransformEntry(ref FileSystemEntry entry)
    {
        var path = entry.ToFullPath();
        return new Entry(path, entry.IsDirectory ? new DirectoryWork(path) : null);
    }

    private readonly record struct Entry(string Path, DirectoryWork? Child);

    private sealed class DirectoryWork(string path)
    {
        public readonly string Path = path;
        public readonly List<Entry> Entries = [];
    }
}
