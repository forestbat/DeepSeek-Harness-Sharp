using System.Collections.Concurrent;
using Dsh.Sandbox;

namespace Dsh.Tests;

public sealed class FileSystemTreeTraversalTests : IDisposable
{
    private const int BranchCount = 64;
    private readonly string _root = TempTree.CreateDirectory("filesystem-tree-traversal");
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task Collect_VisitsEveryDescendantExactlyOnce_IncludingEmptyAndUnicodeDirectories()
    {
        var expected = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var index = 0; index < BranchCount; index++)
        {
            var branch = Directory.CreateDirectory(Path.Combine(_root, $"branch-{index}")).FullName;
            var empty = Directory.CreateDirectory(Path.Combine(branch, "empty-λ")).FullName;
            var file = Path.Combine(branch, "data.txt");
            await File.WriteAllTextAsync(file, "content", TestContext.Current.CancellationToken);
            expected.Add(branch, true);
            expected.Add(empty, true);
            expected.Add(file, false);
        }
        var actual = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        FileSystemTreeTraversal.Collect(_root, (path, directory) => Assert.True(actual.TryAdd(path, directory)), TestContext.Current.CancellationToken);
        Assert.Equal(expected.OrderBy(static pair => pair.Key), actual.OrderBy(static pair => pair.Key));
    }

    [Fact]
    public void Collect_PreservesTheOriginalFailure_InsteadOfMaskingItWithWorkerCancellation()
    {
        for (var index = 0; index < BranchCount; index++)
            Directory.CreateDirectory(Path.Combine(_root, $"branch-{index}"));
        var failure = new InvalidDataException("The consumer rejected an entry.");
        var actual = Assert.Throws<InvalidDataException>(() => FileSystemTreeTraversal.Collect(_root, (_, _) => throw failure, TestContext.Current.CancellationToken));
        Assert.Same(failure, actual);
    }

    [Fact(Timeout = 30000)]
    public async Task Collect_CancellationWaitsForActiveVisitorsBeforeReturning()
    {
        for (var index = 0; index < BranchCount; index++)
        {
            var branch = Directory.CreateDirectory(Path.Combine(_root, $"branch-{index}")).FullName;
            Directory.CreateDirectory(Path.Combine(branch, "child"));
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var release = new ManualResetEventSlim();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visitors = 0;
        var active = 0;
        var operation = Task.Run(() => FileSystemTreeTraversal.Collect(_root, (path, _) =>
        {
            if (Path.GetFileName(path) != "child")
                return;
            Interlocked.Increment(ref active);
            if (Interlocked.Increment(ref visitors) == 2)
                bothEntered.TrySetResult();
            try { release.Wait(TestContext.Current.CancellationToken); }
            finally { Interlocked.Decrement(ref active); }
        }, stop.Token), TestContext.Current.CancellationToken);
        try
        {
            await bothEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            stop.Cancel();
            Assert.False(operation.IsCompleted);
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public void Collect_ReportsARealMissingDirectoryFailure()
    {
        Assert.Throws<DirectoryNotFoundException>(() => FileSystemTreeTraversal.Collect(Path.Combine(_root, "missing"), (_, _) => Assert.Fail("A missing directory has no entries."), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Collect_DoesNotTraverseDirectoryReparsePoints()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var input = Path.Combine(_root, "input.txt");
        await File.WriteAllBytesAsync(input, [], TestContext.Current.CancellationToken);
        var granted = Directory.CreateDirectory(Path.Combine(_root, "granted")).FullName;
        var link = Path.Combine(granted, "link");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (UnauthorizedAccessException) { Assert.Skip("Directory symbolic links require the host symbolic-link privilege."); }
        var fileLink = Path.Combine(granted, "file-link");
        File.CreateSymbolicLink(fileLink, input);
        var actual = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        FileSystemTreeTraversal.Collect(granted, (path, directory) => actual.TryAdd(path, directory), TestContext.Current.CancellationToken);
        Assert.Empty(actual);
    }
}
