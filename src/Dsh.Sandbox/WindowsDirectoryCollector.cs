using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace Dsh.Sandbox;

/// <summary>Collects non-reparse descendants with bounded, dedicated directory-enumeration threads.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDirectoryCollector
{
    private const int WorkerCount = 4;
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        BufferSize = 16 * 1024
    };

    /// <summary>
    /// Visits descendants, excluding the root, serially on the calling thread.
    /// Each directory retains its native entry order; directories are published in FIFO discovery order, matching recursive FileSystemEnumerable on an unchanged tree.
    /// No callback runs after this method returns or throws.
    /// The callback may use a plain dictionary, but must only collect topology: callers must finish all roots before modifying any ACL.
    /// </summary>
    /// <remarks>
    /// Child directories are opened by full path when a worker reaches them.
    /// Concurrent changes can therefore produce a different observation or an error than recursive FileSystemEnumerable, which opens children relative to their parent at discovery.
    /// Missing directories and other enumeration errors are not ignored.
    /// </remarks>
    public static void Collect(string root, Action<string, bool> accept, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(accept);
        cancellationToken.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var jobs = new BlockingCollection<DirectoryWork>(WorkerCount);
        var slots = new DirectoryWork[WorkerCount];
        var workers = new Thread[WorkerCount];
        var started = 0;
        ExceptionDispatchInfo? failure = null;
        try
        {
            var available = new Stack<DirectoryWork>(WorkerCount);
            var inFlight = new Queue<DirectoryWork>(WorkerCount);
            var directories = new Queue<string>();
            directories.Enqueue(root);
            for (var index = 0; index < WorkerCount; index++)
            {
                var slot = new DirectoryWork();
                slots[index] = slot;
                available.Push(slot);
                var worker = new Thread(RunWorker) { IsBackground = true, Name = "Sandbox directory collector" };
                workers[index] = worker;
                worker.Start();
                started++;
            }

            while (directories.Count != 0 || inFlight.Count != 0)
            {
                stop.Token.ThrowIfCancellationRequested();
                while (available.Count != 0 && directories.TryDequeue(out var directory))
                {
                    var work = available.Pop();
                    work.Path = directory;
                    work.Completed.Reset();
                    jobs.Add(work, stop.Token);
                    inFlight.Enqueue(work);
                }

                var completed = inFlight.Dequeue();
                completed.Completed.Wait(stop.Token);
                Volatile.Read(ref failure)?.Throw();
                // Publish in submission order so subdirectories join the FIFO in the order used by stable path-length sorting in Grant.
                foreach (var entry in completed.Entries)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    accept(entry.Path, entry.Directory);
                    if (entry.Directory)
                        directories.Enqueue(entry.Path);
                }
                completed.Entries.Clear();
                completed.Path = "";
                available.Push(completed);
            }
            stop.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && Volatile.Read(ref failure) is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch
        {
            Volatile.Read(ref failure)?.Throw();
            throw;
        }
        finally
        {
            stop.Cancel();
            jobs.CompleteAdding();
            for (var index = 0; index < started; index++)
                workers[index].Join();
            foreach (var slot in slots)
                slot?.Dispose();
        }

        void RunWorker()
        {
            try
            {
                foreach (var work in jobs.GetConsumingEnumerable(stop.Token))
                {
                    try
                    {
                        foreach (var entry in new FileSystemEnumerable<(string Path, bool Directory)>(work.Path,
                            static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory), Options))
                        {
                            stop.Token.ThrowIfCancellationRequested();
                            work.Entries.Add(entry);
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
                    finally
                    {
                        work.Completed.Set();
                    }
                    if (stop.IsCancellationRequested)
                        return;
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

    private sealed class DirectoryWork : IDisposable
    {
        public string Path = "";
        public readonly List<(string Path, bool Directory)> Entries = [];
        public readonly ManualResetEventSlim Completed = new(false);

        public void Dispose() => Completed.Dispose();
    }
}
