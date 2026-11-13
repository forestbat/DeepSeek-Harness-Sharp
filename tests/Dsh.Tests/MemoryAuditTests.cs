using Dsh.Memory;

namespace Dsh.Tests;

public sealed class MemoryAuditTests
{
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
}
