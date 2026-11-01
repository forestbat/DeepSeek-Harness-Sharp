using System.ComponentModel;
using Dsh.Transport;

namespace Dsh.Tests;

public sealed class PinnedFileAccessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(PinnedFileMode.Overwrite)]
    [InlineData(PinnedFileMode.Append)]
    public void ActualHandleDenial_PrecedesTruncationOrAppend(PinnedFileMode mode)
    {
        using var files = new FileFixture();
        Assert.Throws<UnauthorizedAccessException>(() => PinnedFileAccess.Open(files.Target, mode, target =>
        {
            if (!target.IsDirectory && target.Identity is not null)
                throw new UnauthorizedAccessException("The opened object is denied.");
        }));
        Assert.Equal("before", File.ReadAllText(files.Target));
    }

    [Fact]
    public async Task Read_UsesPinnedAncestor_WhenItsNameIsReplaced()
    {
        using var files = new FileFixture();
        var attempted = false;
        await using var access = PinnedFileAccess.Open(files.Target, PinnedFileMode.Read, target =>
        {
            if (target.IsDirectory || target.Identity is not null || attempted)
                return;
            attempted = true;
            if (OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() => Directory.Move(files.Parent, files.Relocated));
            else
            {
                Directory.Move(files.Parent, files.Relocated);
                Directory.CreateSymbolicLink(files.Parent, files.Protected);
            }
        });
        using var reader = new StreamReader(access.Stream, leaveOpen: true);
        Assert.Equal("before", await reader.ReadToEndAsync(Ct));
        Assert.True(attempted);
        Assert.Equal("protected", File.ReadAllText(files.ProtectedTarget));
    }

    [Theory]
    [InlineData(PinnedFileMode.Replace)]
    [InlineData(PinnedFileMode.Overwrite)]
    public async Task Write_UsesPinnedAncestor_WhenItsNameIsReplaced(PinnedFileMode mode)
    {
        using var files = new FileFixture();
        var attempted = false;
        await using var access = PinnedFileAccess.Open(files.Target, mode, target =>
        {
            if (target.IsDirectory || attempted)
                return;
            attempted = true;
            if (OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() => Directory.Move(files.Parent, files.Relocated));
            else
            {
                Directory.Move(files.Parent, files.Relocated);
                Directory.CreateSymbolicLink(files.Parent, files.Protected);
            }
        });
        await access.Stream.WriteAsync("after"u8.ToArray(), Ct);
        await access.CompleteAsync(Ct);
        Assert.True(attempted);
        var actual = OperatingSystem.IsWindows() ? files.Target : Path.Combine(files.Relocated, "value.txt");
        Assert.Equal("after", File.ReadAllText(actual));
        Assert.Equal("protected", File.ReadAllText(files.ProtectedTarget));
    }

    [Fact]
    public void CreateNew_CollisionPreservesExistingObject()
    {
        using var files = new FileFixture();
        var error = Assert.Throws<Win32Exception>(() => PinnedFileAccess.Open(files.Target, PinnedFileMode.CreateNew, _ => { }));
        Assert.Equal(OperatingSystem.IsWindows() ? 80 : 17, error.NativeErrorCode);
        Assert.Equal("before", File.ReadAllText(files.Target));
    }

    [Fact]
    public async Task CancelledReplacement_DiscardsOnlyOwnedTemporary()
    {
        using var files = new FileFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using (var access = PinnedFileAccess.Open(files.Target, PinnedFileMode.Replace, _ => { }))
        {
            await access.Stream.WriteAsync("after"u8.ToArray(), Ct);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => access.CompleteAsync(cancellation.Token));
        }
        Assert.Equal("before", File.ReadAllText(files.Target));
        Assert.Equal(["value.txt"], Directory.GetFiles(files.Parent).Select(Path.GetFileName).ToArray());
        Assert.Equal("protected", File.ReadAllText(files.ProtectedTarget));
    }

    [Fact]
    public async Task Replacement_PublishesNewSnapshot_WhileReaderKeepsOldSnapshot()
    {
        using var files = new FileFixture();
        await using var old = new FileStream(files.Target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        await using (var access = PinnedFileAccess.Open(files.Target, PinnedFileMode.Replace, _ => { }))
        {
            await access.Stream.WriteAsync("after"u8.ToArray(), Ct);
            await access.CompleteAsync(Ct);
        }
        using var reader = new StreamReader(old, leaveOpen: true);
        Assert.Equal("before", await reader.ReadToEndAsync(Ct));
        Assert.Equal("after", await File.ReadAllTextAsync(files.Target, Ct));
    }

    private sealed class FileFixture : IDisposable
    {
        private string Root { get; } = TempTree.CreateDirectory("pinned-files");
        public string Parent => Path.Combine(Root, "ordinary");
        public string Relocated => Path.Combine(Root, "relocated");
        public string Protected => Path.Combine(Root, "protected");
        public string Target => Path.Combine(Parent, "value.txt");
        public string ProtectedTarget => Path.Combine(Protected, "value.txt");

        public FileFixture()
        {
            Directory.CreateDirectory(Parent);
            Directory.CreateDirectory(Protected);
            File.WriteAllText(Target, "before");
            File.WriteAllText(ProtectedTarget, "protected");
        }

        public void Dispose() => TempTree.Delete(Root);
    }
}
