using System.IO.Enumeration;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Dsh.Sandbox;

namespace Dsh.Tests;

[SupportedOSPlatform("windows")]
[Trait("Category", "SandboxNative")]
public sealed class WindowsDirectoryCollectorTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("windows-directory-collector");

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void CompleteTree_PreservesRecursiveEnumerationAndStableGrantOrder()
    {
        RequireWindows();
        var expectedCount = PopulateTree();
        var hidden = Path.Combine(_root, "hidden-system.txt");
        File.WriteAllText(hidden, "included");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        expectedCount++;
        // Compare against the actual recursive implementation replaced at AddTree, not a second traversal algorithm.
        var expected = new FileSystemEnumerable<(string Path, bool Directory)>(_root,
            static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                BufferSize = 16 * 1024
            }).ToArray();
        var actual = new List<(string Path, bool Directory)>();

        WindowsDirectoryCollector.Collect(_root, (path, directory) => actual.Add((path, directory)), TestContext.Current.CancellationToken);

        Assert.Equal(expectedCount, actual.Count);
        Assert.True(expected.SequenceEqual(actual), "Collection must retain native entry order and FIFO directory order.");
        Assert.True(expected.OrderBy(static entry => entry.Path.Length).SequenceEqual(actual.OrderBy(static entry => entry.Path.Length)),
            "Stable equal-length ACL modification order must not change.");
        Assert.Equal(actual.Count, actual.Select(static entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(actual, static entry => Assert.Equal(Directory.Exists(entry.Path), entry.Directory));
    }

    [Fact]
    public void ReparseEntries_AreExcludedWithoutFollowingExternalTargetsOrCycles()
    {
        RequireWindows();
        var tree = Directory.CreateDirectory(Path.Combine(_root, "tree")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(tree, "nested")).FullName;
        var input = Path.Combine(nested, "input.txt");
        File.WriteAllText(input, "included");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "not-in-tree.txt"), "excluded");
        var externalLink = Path.Combine(tree, "external");
        var cycleLink = Path.Combine(tree, "cycle");
        var fileLink = Path.Combine(tree, "alias.txt");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(externalLink, outside);
                Directory.CreateSymbolicLink(cycleLink, tree);
                File.CreateSymbolicLink(fileLink, input);
            }
            catch (IOException error) when ((error.HResult & 0xffff) == 1314)
            {
                Assert.Skip("Creating Windows symbolic links requires Developer Mode or the symbolic-link privilege.");
            }

            var actual = new List<(string Path, bool Directory)>();
            WindowsDirectoryCollector.Collect(tree, (path, directory) => actual.Add((path, directory)), TestContext.Current.CancellationToken);

            Assert.True(actual.SequenceEqual(new[] { (nested, true), (input, false) }),
                "Only real descendants, not reparse entries or their targets, belong to the collected topology.");
        }
        finally
        {
            // Remove links before TempTree's recursive cleanup so the cycle is never traversed by the fixture.
            File.Delete(fileLink);
            if (Directory.Exists(cycleLink))
                Directory.Delete(cycleLink);
            if (Directory.Exists(externalLink))
                Directory.Delete(externalLink);
        }
    }

    [Fact]
    public void PreCanceledCollection_DoesNotOpenMissingRootOrVisitEntries()
    {
        RequireWindows();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var visits = 0;

        var error = Assert.ThrowsAny<OperationCanceledException>(() => WindowsDirectoryCollector.Collect(
            Path.Combine(_root, "missing"), (_, _) => visits++, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, visits);
    }

    [Fact]
    public void CancellationFromVisitor_StopsPublicationBeforeTheNextEntry()
    {
        RequireWindows();
        PopulateTree();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var files = 0;

        var error = Assert.ThrowsAny<OperationCanceledException>(() => WindowsDirectoryCollector.Collect(_root, (_, directory) =>
        {
            if (directory)
                return;
            files++;
            cancellation.Cancel();
        }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, files);
    }

    [Fact]
    public void VisitorFailure_IsPreservedAndStopsPublication()
    {
        RequireWindows();
        PopulateTree();
        var failure = new InvalidOperationException("Visitor rejected the collected entry.");
        var files = 0;

        var observed = Assert.Throws<InvalidOperationException>(() => WindowsDirectoryCollector.Collect(_root, (_, directory) =>
        {
            if (directory)
                return;
            files++;
            throw failure;
        }, TestContext.Current.CancellationToken));

        Assert.Same(failure, observed);
        Assert.Equal(1, files);
    }

    [Fact]
    public void MissingRoot_IsReportedWithoutPublishingEntries()
    {
        RequireWindows();
        var visits = 0;

        Assert.Throws<DirectoryNotFoundException>(() => WindowsDirectoryCollector.Collect(
            Path.Combine(_root, "missing"), (_, _) => visits++, TestContext.Current.CancellationToken));

        Assert.Equal(0, visits);
    }

    [Fact]
    public void DiscoveredDirectoryRemovedByVisitor_IsReportedRatherThanSilentlySkipped()
    {
        RequireWindows();
        var child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        File.WriteAllText(Path.Combine(child, "input.txt"), "removed before child enumeration");
        var visits = 0;

        Assert.Throws<DirectoryNotFoundException>(() => WindowsDirectoryCollector.Collect(_root, (path, directory) =>
        {
            visits++;
            Assert.True(directory && string.Equals(child, path, StringComparison.Ordinal));
            Directory.Delete(child, recursive: true);
        }, TestContext.Current.CancellationToken));

        Assert.Equal(1, visits);
    }

    [Fact]
    public void DeniedDirectoryListing_IsReportedAndDoesNotPublishPartialSuccess()
    {
        RequireWindows();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "denied"));
        File.WriteAllText(Path.Combine(directory.FullName, "input.txt"), "not enumerable");
        using var identity = WindowsIdentity.GetCurrent();
        var security = directory.GetAccessControl(AccessControlSections.Access);
        var original = security.GetSecurityDescriptorBinaryForm();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        var visits = 0;
        try
        {
            directory.SetAccessControl(security);

            Assert.Throws<UnauthorizedAccessException>(() => WindowsDirectoryCollector.Collect(directory.FullName,
                (_, _) => visits++, TestContext.Current.CancellationToken));

            Assert.Equal(0, visits);
        }
        finally
        {
            security.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            directory.SetAccessControl(security);
        }
    }

    private int PopulateTree()
    {
        var count = 0;
        for (var branch = 0; branch < 7; branch++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(_root, $"branch-{branch:00}")).FullName;
            count++;
            // Force multiple 16 KiB native result buffers in every branch, with non-ASCII and long names.
            for (var file = 0; file < 96; file++)
            {
                File.WriteAllText(Path.Combine(directory, $"entry-{file:000}-λ-{new string('x', 80)}.txt"), "data");
                count++;
            }
            for (var depth = 0; depth < 3; depth++)
            {
                directory = Directory.CreateDirectory(Path.Combine(directory, $"level-{depth}")).FullName;
                File.WriteAllText(Path.Combine(directory, "same-length.txt"), "data");
                count += 2;
            }
        }
        return count;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This collector regression requires the Windows filesystem enumeration implementation.");
    }
}
