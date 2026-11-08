namespace Dsh.Tests;

public class TempTreeTests
{
    [Fact(Timeout = 5000)]
    public async Task DeleteOnce_RemovesOwnedReadOnlyFiles()
    {
        var root = TempTree.CreateDirectory("readonly-cleanup");
        try
        {
            var directory = Path.Combine(root, "objects");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "owned-object");
            await File.WriteAllTextAsync(file, "owned test data", TestContext.Current.CancellationToken);
            File.SetAttributes(file, FileAttributes.ReadOnly);

            TempTree.DeleteOnce(root);

            Assert.False(Directory.Exists(root));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task DeleteOnce_ReportsAnOwnedFileStillHeldWithoutDeleteSharing()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Windows file sharing prevents deletion while the owned handle is open.");
        var root = TempTree.CreateDirectory("locked-cleanup");
        try
        {
            var file = Path.Combine(root, "owned-object");
            await File.WriteAllTextAsync(file, "owned test data", TestContext.Current.CancellationToken);
            using (var handle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => TempTree.DeleteOnce(root));
                Assert.True(Directory.Exists(root));
            }
            Assert.Equal("owned test data", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            TempTree.DeleteOnce(root);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }
}
