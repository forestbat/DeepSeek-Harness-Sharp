using System.Text;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Memory;

namespace Dsh.Tests;

public sealed class ProjectMemoryTests
{
    [Fact]
    public async Task RememberCorrectForget_MutateFileAndAudit()
    {
        using var fixture = new Fixture();
        var memory = fixture.Memory;

        var remembered = await memory.RememberAsync("build.command", "dotnet build", "Commands", "tool", TestContext.Current.CancellationToken);
        Assert.False(remembered.Replaced);
        Assert.Equal("Commands", remembered.Section);

        var corrected = await memory.CorrectAsync("no.force.push", "never force push", "tool", TestContext.Current.CancellationToken);
        Assert.Equal("Corrections", corrected.Section);

        var again = await memory.RememberAsync("build.command", "dotnet build -c Release", "Commands", "tool", TestContext.Current.CancellationToken);
        Assert.True(again.Replaced);

        var text = File.ReadAllText(fixture.MemoryPath);
        Assert.Contains("## Commands", text);
        Assert.Contains("- build.command :: dotnet build -c Release (", text);
        Assert.Contains("## Corrections", text);
        Assert.Contains("- no.force.push :: never force push (", text);

        var forgotten = await memory.ForgetAsync("build.command", "tool", TestContext.Current.CancellationToken);
        Assert.Equal("Commands", forgotten.Section);
        Assert.DoesNotContain("build.command", File.ReadAllText(fixture.MemoryPath));

        var audit = File.ReadAllLines(fixture.AuditPath)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();
        Assert.Equal(4, audit.Count);
        Assert.Equal("remember", audit[0].GetProperty("action").GetString());
        Assert.Equal("correct", audit[1].GetProperty("action").GetString());
        Assert.Equal("forget", audit[3].GetProperty("action").GetString());
        Assert.Equal("tool", audit[0].GetProperty("source").GetString());
        Assert.Matches(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", audit[0].GetProperty("at").GetString()!);
    }

    [Fact]
    public async Task Forget_UnknownKey_Throws()
    {
        using var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Memory.ForgetAsync("missing", "tool", TestContext.Current.CancellationToken));

        Assert.Contains("missing", error.Message);
        Assert.False(File.Exists(fixture.AuditPath));
    }

    [Fact]
    public async Task BuildIndex_OrdersCorrectionsFirstAndIncludesDigests()
    {
        using var fixture = new Fixture();
        var memory = fixture.Memory;
        await memory.RememberAsync("f1", "a fact", "Facts", "tool", TestContext.Current.CancellationToken);
        await memory.CorrectAsync("c1", "a correction", "tool", TestContext.Current.CancellationToken);
        await memory.RememberAsync("d1", "a decision", "Decisions", "tool", TestContext.Current.CancellationToken);
        await memory.WriteDigestAsync(SessionId.Create("s-1"), "topic", "did work", TestContext.Current.CancellationToken);

        var index = await memory.BuildIndexAsync(cancellationToken: TestContext.Current.CancellationToken);

        var correctionsAt = index.IndexOf("## Corrections", StringComparison.Ordinal);
        var decisionsAt = index.IndexOf("## Decisions", StringComparison.Ordinal);
        var factsAt = index.IndexOf("## Facts", StringComparison.Ordinal);
        var digestsAt = index.IndexOf("## Recent sessions", StringComparison.Ordinal);
        Assert.True(correctionsAt >= 0 && correctionsAt < decisionsAt);
        Assert.True(decisionsAt < factsAt);
        Assert.True(factsAt < digestsAt);
        Assert.Contains("- [topic] did work (", index);
    }

    [Fact]
    public async Task BuildIndex_EmptyMemory_ReportsEmpty()
    {
        using var fixture = new Fixture();

        var index = await fixture.Memory.BuildIndexAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("is empty", index);
    }

    [Fact]
    public async Task BuildIndex_TruncatesToBudgetKeepingPriority()
    {
        using var fixture = new Fixture();
        var memory = fixture.Memory;
        await memory.CorrectAsync("c1", "keep me", "tool", TestContext.Current.CancellationToken);
        for (var index = 0; index < 30; index++)
            await memory.RememberAsync($"f{index}", new string('x', 100), "Facts", "tool", TestContext.Current.CancellationToken);

        var result = await memory.BuildIndexAsync(budgetBytes: 512, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= 512 + 32);
        Assert.Contains("## Corrections", result);
        Assert.Contains("… (truncated)", result);
        Assert.DoesNotContain("f29", result);
    }

    [Fact]
    public async Task RecentDigests_NewestFirst()
    {
        using var fixture = new Fixture();
        var memory = fixture.Memory;
        await memory.WriteDigestAsync(SessionId.Create("s-old"), "old", "old summary", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(fixture.DigestPath("s-old"), DateTime.UtcNow.AddDays(-1));
        await memory.WriteDigestAsync(SessionId.Create("s-new"), "new", "new summary", TestContext.Current.CancellationToken);

        var digests = memory.RecentDigests(5);

        Assert.Equal(2, digests.Count);
        Assert.Equal("s-new", digests[0].Id.Value);
        Assert.Equal("new summary", digests[0].Summary);
        Assert.Equal("s-old", digests[1].Id.Value);
        Assert.NotNull(digests[0].UpdatedAt);
    }

    [Fact]
    public async Task Show_ListsContentAndDigests()
    {
        using var fixture = new Fixture();
        var memory = fixture.Memory;
        await memory.RememberAsync("a", "1", null, "tool", TestContext.Current.CancellationToken);
        await memory.WriteDigestAsync(SessionId.Create("s-1"), "t", "s", TestContext.Current.CancellationToken);

        var show = await memory.ShowAsync(TestContext.Current.CancellationToken);

        Assert.Contains(fixture.MemoryPath, show);
        Assert.Contains("- a :: 1 (", show);
        Assert.Contains("s-1", show);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"dsh-pmem-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            MemoryPath = Path.Combine(_root, ".dsh-memory.md");
            var sidecar = Path.Combine(_root, ".dsh-memory");
            AuditPath = Path.Combine(sidecar, "decisions.jsonl");
            Memory = new ProjectMemory(new FileMemoryStore(MemoryPath), sidecar);
        }

        public ProjectMemory Memory { get; }

        public string MemoryPath { get; }

        public string AuditPath { get; }

        public string DigestPath(string sessionId) => Path.Combine(_root, ".dsh-memory", "sessions", $"{sessionId}.md");

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }
}
