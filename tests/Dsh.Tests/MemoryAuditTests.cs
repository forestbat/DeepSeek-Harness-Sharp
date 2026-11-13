using Dsh.Memory;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class MemoryAuditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryIdentity_DistinguishesActualDirectoriesAndMissingPaths(bool longPaths)
    {
        var root = TempTree.CreateDirectory("memory-directory-identity");
        try
        {
            var parent = root;
            const int LongPathLength = 300;
            if (longPaths)
                while (parent.Length < LongPathLength)
                    parent = Path.Combine(parent, "nested-directory");
            var first = Directory.CreateDirectory(Path.Combine(parent, "first")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(parent, "second")).FullName;
            var missing = Path.Combine(parent, "missing");

            Assert.True(FilePathAccess.SameDirectory(first, first));
            Assert.False(FilePathAccess.SameDirectory(first, second));
            Assert.False(FilePathAccess.SameDirectory(first, missing));
            Assert.False(FilePathAccess.SameDirectory(missing, first));
            Assert.False(FilePathAccess.SameDirectory(missing, missing));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public async Task Workspace_KeepsCaseDistinctProjectRecordsSeparate()
    {
        using var repository = new GitTestRepository();
        var otherRoot = Path.Combine(repository.Sandbox, "Repo");
        if (Directory.Exists(otherRoot))
            Assert.Skip("The test filesystem does not distinguish directory name casing.");
        repository.Git("init", "-q", otherRoot);
        using var workspace = new MemoryWorkspace(MemoryPluginConfig.Resolve(null));
        var first = workspace.For(repository.Root);
        var second = workspace.For(otherRoot);

        await first.RememberAsync("build.command", "build alpha", "Commands", "test", TestContext.Current.CancellationToken);
        await second.RememberAsync("build.command", "build beta", "Commands", "test", TestContext.Current.CancellationToken);

        Assert.Equal("build alpha", Assert.Single(await first.FindAsync("build", "Commands", TestContext.Current.CancellationToken)).Text);
        Assert.Equal("build beta", Assert.Single(await second.FindAsync("build", "Commands", TestContext.Current.CancellationToken)).Text);
        Assert.Contains("build alpha", await File.ReadAllTextAsync(Path.Combine(repository.Root, MemoryStoreFactory.DefaultFileName), TestContext.Current.CancellationToken));
        Assert.Contains("build beta", await File.ReadAllTextAsync(Path.Combine(otherRoot, MemoryStoreFactory.DefaultFileName), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Workspace_KeepsCaseDistinctProjectEnablementSeparate()
    {
        using var repository = new GitTestRepository();
        var otherRoot = Path.Combine(repository.Sandbox, "Repo");
        if (Directory.Exists(otherRoot))
            Assert.Skip("The test filesystem does not distinguish directory name casing.");
        repository.Git("init", "-q", otherRoot);
        using var workspace = new MemoryWorkspace(MemoryPluginConfig.Resolve(null));
        var first = workspace.For(repository.Root);
        var second = workspace.For(otherRoot);

        await second.SetEnabledAsync(false, TestContext.Current.CancellationToken);

        Assert.True(first.IsEnabled);
        Assert.False(second.IsEnabled);
        using var reopened = new MemoryWorkspace(MemoryPluginConfig.Resolve(null));
        Assert.True(reopened.For(repository.Root).IsEnabled);
        Assert.False(reopened.For(otherRoot).IsEnabled);
    }

    [Fact]
    public void Workspace_ReusesTheSameProjectFromNestedDirectories()
    {
        using var repository = new GitTestRepository();
        var nested = Directory.CreateDirectory(Path.Combine(repository.Root, "src")).FullName;
        using var workspace = new MemoryWorkspace(MemoryPluginConfig.Resolve(null));

        var memory = workspace.For(repository.Root);

        Assert.Same(memory, workspace.For(repository.Root));
        Assert.Same(memory, workspace.For(nested));
    }

    [Fact]
    public async Task Workspace_ReusesCaseAliasesAndSharesEnablement()
    {
        using var repository = new GitTestRepository();
        var alias = Path.Combine(repository.Sandbox, "Repo");
        if (!Directory.Exists(alias))
            Assert.Skip("The test filesystem distinguishes directory name casing.");
        using var workspace = new MemoryWorkspace(MemoryPluginConfig.Resolve(null));
        var original = workspace.For(repository.Root);
        var aliased = workspace.For(alias);
        Assert.True(original.IsEnabled);

        await aliased.SetEnabledAsync(false, TestContext.Current.CancellationToken);

        Assert.False(original.IsEnabled);
        Assert.Same(original, aliased);
        await original.SetEnabledAsync(true, TestContext.Current.CancellationToken);
        Assert.True(aliased.IsEnabled);
    }
}
