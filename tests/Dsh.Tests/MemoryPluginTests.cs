using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Memory;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class MemoryPluginTests
{
    [Fact]
    public void Factory_UsesFileBackendByDefault()
    {
        var store = MemoryStoreFactory.Create(new MemorySettings(), Path.GetTempPath());

        var file = Assert.IsType<FileMemoryStore>(store);
        Assert.EndsWith(".dsh-memory.md", file.Description);
    }

    [Fact]
    public void Factory_UsesMongoBackendWhenConfigured()
    {
        var store = MemoryStoreFactory.Create(new MemorySettings
        {
            Backend = "mongo",
            Mongo = new MemoryMongoSettings { Database = "dsh", Collection = "memory", Key = "k1" },
        }, Path.GetTempPath());

        var mongo = Assert.IsType<MongoTextStore>(store);
        Assert.Equal("dsh.memory#k1", mongo.Description);
        mongo.Dispose();
    }

    [Fact]
    public async Task FileStore_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsh-memory-{Guid.NewGuid():N}", "memory.md");
        try
        {
            var store = new FileMemoryStore(path);
            Assert.Null(await store.GetAsync(TestContext.Current.CancellationToken));

            await store.SetAsync("# Memory\n- fact", TestContext.Current.CancellationToken);

            Assert.Equal("# Memory\n- fact", await store.GetAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task SaveTool_RememberCorrectForgetSkip_WhenEnabled()
    {
        using var fixture = new ToolFixture(enabled: true);

        var remembered = await fixture.Execute("""{"action":"remember","key":"build.command","text":"dotnet build","section":"Commands"}""");
        Assert.IsType<ToolExecutionResult.Success>(remembered);
        var text = File.ReadAllText(fixture.MemoryPath);
        Assert.Contains("## Commands", text);
        Assert.Contains("- build.command :: dotnet build (", text);

        var corrected = await fixture.Execute("""{"action":"correct","key":"no.force.push","text":"never force push"}""");
        Assert.IsType<ToolExecutionResult.Success>(corrected);
        Assert.Contains("## Corrections", File.ReadAllText(fixture.MemoryPath));

        var forgotten = await fixture.Execute("""{"action":"forget","key":"build.command"}""");
        Assert.IsType<ToolExecutionResult.Success>(forgotten);
        Assert.DoesNotContain("build.command", File.ReadAllText(fixture.MemoryPath));

        var before = File.ReadAllText(fixture.MemoryPath);
        var skipped = await fixture.Execute("""{"action":"skip","reason":"user preference"}""");
        Assert.IsType<ToolExecutionResult.Success>(skipped);
        Assert.Equal(before, File.ReadAllText(fixture.MemoryPath));
    }

    [Fact]
    public async Task SaveTool_FailsWhenMemoryDisabled()
    {
        using var fixture = new ToolFixture(enabled: false);

        var result = await fixture.Execute("""{"action":"remember","key":"a","text":"b"}""");

        Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.False(File.Exists(fixture.MemoryPath));
    }

    [Fact]
    public async Task SaveTool_RequiresTextForRemember()
    {
        using var fixture = new ToolFixture(enabled: true);

        var result = await fixture.Execute("""{"action":"remember","key":"a"}""");

        Assert.IsType<ToolExecutionResult.Failure>(result);
    }

    private sealed class ToolFixture : IDisposable
    {
        private readonly string _root;
        private readonly ToolRuntime _tools;
        private readonly IDisposable _tool;

        public ToolFixture(bool enabled)
        {
            _root = Path.Combine(Path.GetTempPath(), $"dsh-memory-tool-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            MemoryPath = Path.Combine(_root, "memory.md");
            File.WriteAllText(Path.Combine(_root, "settings.yaml"), $"""
                memory:
                  enabled: {(enabled ? "true" : "false")}
                  file: {MemoryPath}
                """);
            var home = HarnessHome.Resolve(_root);
            var options = new HarnessOptions(home, _root);
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            _tools = new ToolRuntime(ctx);
            var memory = new ProjectMemory(
                new FileMemoryStore(MemoryPath),
                Path.Combine(_root, ".dsh-memory"));
            _tool = MemorySaveTool.Register(ctx, options, memory);
        }

        public string MemoryPath { get; }

        public Task<ToolExecutionResult> Execute(string arguments)
            => _tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create("call-1"),
                Name = "memory_save",
                Arguments = JsonDocument.Parse(arguments).RootElement,
                Signal = default,
            });

        public void Dispose()
        {
            _tool.Dispose();
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }
}
