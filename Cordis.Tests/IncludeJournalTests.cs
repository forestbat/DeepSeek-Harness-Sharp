using Cordis.Loader;

namespace Cordis.Tests;

public class IncludeJournalTests
{
    [Fact]
    public void Reconcile_FileWinsAndReportsConflict()
    {
        var baseData = new List<EntryOptions>
        {
            new()
            {
                Id = "a",
                Name = "plugin-a",
                Config = new Dictionary<string, object?> { ["tag"] = "file" },
            },
        };
        var theirsData = new List<EntryOptions>
        {
            new()
            {
                Id = "a",
                Name = "plugin-a",
                Config = new Dictionary<string, object?> { ["tag"] = "external" },
            },
        };
        var journal = new IncludeJournal();
        var runtime = new EntryOptions
        {
            Id = "a",
            Name = "plugin-a",
            Config = new Dictionary<string, object?> { ["tag"] = "runtime" },
        };
        journal.RecordUpdate("a", baseData[0], runtime, null, 0);

        var conflicts = journal.Reconcile(
            IncludeJournal.Flatten(baseData),
            IncludeJournal.Flatten(theirsData),
            (_, _) => true);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("a", conflict.Id);
        Assert.Contains("modified both in file and at runtime", conflict.Reason);
        Assert.True(journal.IsEmpty);
    }

    [Fact]
    public void PatchIndex_OwnsPatchKeysAndInsertedEntries()
    {
        var patches = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["id"] = "a",
                ["config"] = new Dictionary<string, object?> { ["tag"] = "patch" },
            },
            new()
            {
                ["insert"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["id"] = "x", ["name"] = "plugin-x" },
                },
            },
        };
        var index = new IncludePatchIndex(patches);

        Assert.False(index.FileOwned("a", "config"));
        Assert.True(index.FileOwned("a", "name"));
        Assert.Equal(IncludePatchOwnerKind.Insert, index.Entry("x").Kind);
        Assert.False(index.FileOwned("x"));
    }

    [Fact]
    public async Task WriteAtomic_NoPartialFile_AndStaleWriteDetected()
    {
        var tempDir = Path.Combine(AppContext.BaseDirectory, "tmp", $"include-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "config.yml");
            await File.WriteAllTextAsync(path, "old");

            await IncludeFileWriter.WriteAtomicAsync(path, "new", "old");

            Assert.Equal("new", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(tempDir));

            await Assert.ThrowsAsync<IncludeStaleWriteException>(
                () => IncludeFileWriter.WriteAtomicAsync(path, "other", "old"));
            Assert.Equal("new", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
