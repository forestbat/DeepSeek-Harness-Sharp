using Dsh.Boot;

namespace Dsh.Tests;

public sealed class HarnessStorageTests
{
    [Fact]
    public async Task ApplyStorageRoot_MovesExistingDataDirectories()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var home = new HarnessHome(Path.Combine(dir, "home"));
            home.Ensure();
            var target = Path.Combine(dir, "data");
            await File.WriteAllTextAsync(home.SettingsFile,
                $"global_default_model: p/m\nstorage:\n  root: {target.Replace('\\', '/')}\n",
                TestContext.Current.CancellationToken);
            Directory.CreateDirectory(home.SessionsPath);
            await File.WriteAllTextAsync(Path.Combine(home.SessionsPath, "s.jsonl"), "x",
                TestContext.Current.CancellationToken);
            var settings = HarnessSettings.Load(home);

            var resolved = HarnessStorage.ApplyStorageRoot(home, settings);

            Assert.Equal(Path.GetFullPath(target), resolved.Root);
            Assert.True(File.Exists(resolved.SettingsFile));
            var moved = Path.Combine(resolved.SessionsPath, "s.jsonl");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!File.Exists(moved) && DateTime.UtcNow < deadline)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(moved));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void ApplyStorageRoot_KeepsHomeWhenRootUnset()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var home = new HarnessHome(Path.Combine(dir, "home"));
            home.Ensure();
            var settings = HarnessSettings.Load(home);

            var resolved = HarnessStorage.ApplyStorageRoot(home, settings);

            Assert.Equal(home.Root, resolved.Root);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Resolve_PrefersExplicitRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-storage-{Guid.NewGuid():N}");
        Assert.Equal(Path.GetFullPath(dir), HarnessHome.Resolve(dir).Root);
    }
}
