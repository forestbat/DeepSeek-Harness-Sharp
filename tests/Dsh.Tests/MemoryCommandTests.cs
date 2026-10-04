using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Memory;
using Dsh.Plugins;

namespace Dsh.Tests;

public sealed class MemoryCommandTests
{
    [Fact]
    public async Task ToggleOnOff_DrivesPluginManager()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-memory-cmd", Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDir);
        try
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var commands = CommandsService.Register(ctx);
            var plugins = new FakePluginManager();
            ctx.Provide("pluginManager", plugins);
            using var registration = MemoryCommand.Register(ctx);
            var agent = new FakeAgent(ctx);

            var enabled = await commands.Execute(agent, "/memory on", TestContext.Current.CancellationToken);
            Assert.NotNull(enabled);
            Assert.IsType<CommandResult.Success>(enabled.Result);
            Assert.Equal(["enable:@deepseek-ai/dsh-memory"], plugins.Calls);

            var disabled = await commands.Execute(agent, "/memory off", TestContext.Current.CancellationToken);
            Assert.NotNull(disabled);
            Assert.IsType<CommandResult.Success>(disabled.Result);
            Assert.Equal(["enable:@deepseek-ai/dsh-memory", "disable:@deepseek-ai/dsh-memory"], plugins.Calls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PromptSections_FollowProjectMemoryPresence()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-memory-cmd", Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDir);
        try
        {
            var ctx = new Context();
            var systemPrompt = new SystemPrompt(ctx, new SystemPromptConfig());
            _ = CommandsService.Register(ctx);
            using var registration = MemoryCommand.Register(ctx);

            var empty = await systemPrompt.Assemble(new AssembleContext());
            Assert.DoesNotContain("Project memory is enabled", PromptRender.RenderPrompt(empty));

            ctx.Provide(MemoryServices.ProjectMemory, new ProjectMemory(
                new FileMemoryStore(Path.Combine(projectDir, ".dsh-memory.md")),
                Path.Combine(projectDir, ".dsh-memory")));
            var assembly = await systemPrompt.Assemble(new AssembleContext());
            var prompt = PromptRender.RenderPrompt(assembly);
            Assert.Contains("Project memory is enabled", prompt);
            Assert.Contains(".dsh-memory.md", prompt);
            Assert.Contains(assembly.Contexts, context => context.Text.Contains("Project memory"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Show_PrintsMemoryFileAndDigests()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-memory-cmd", Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDir);
        try
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var commands = CommandsService.Register(ctx);
            using var registration = MemoryCommand.Register(ctx);
            var agent = new FakeAgent(ctx);

            var hidden = await commands.Execute(agent, "/memory show", TestContext.Current.CancellationToken);
            Assert.NotNull(hidden);
            Assert.IsType<CommandResult.Error>(hidden.Result);

            await File.WriteAllTextAsync(Path.Combine(projectDir, ".dsh-memory.md"), "## Facts\n- a :: 1 (2026-09-16T09:35:17Z)\n", TestContext.Current.CancellationToken);
            var sidecar = Path.Combine(projectDir, ".dsh-memory");
            var memory = new ProjectMemory(new FileMemoryStore(Path.Combine(projectDir, ".dsh-memory.md")), sidecar);
            ctx.Provide(MemoryServices.ProjectMemory, memory);
            await memory.WriteDigestAsync(SessionId.Create("s-1"), "topic", "summary text", TestContext.Current.CancellationToken);

            var shown = await commands.Execute(agent, "/memory show", TestContext.Current.CancellationToken);

            Assert.NotNull(shown);
            var success = Assert.IsType<CommandResult.Success>(shown.Result);
            Assert.Contains("- a :: 1 (", success.Text);
            Assert.Contains("s-1", success.Text);
            Assert.Contains("summary text", success.Text);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ShowWithoutMemoryPlugin_ReturnsError()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-memory-cmd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var commands = CommandsService.Register(ctx);
            using var registration = MemoryCommand.Register(ctx);
            var agent = new FakeAgent(ctx);

            var shown = await commands.Execute(agent, "/memory show", TestContext.Current.CancellationToken);

            Assert.NotNull(shown);
            var error = Assert.IsType<CommandResult.Error>(shown.Result);
            Assert.Contains("/memory on", error.Text);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RejectsUnknownOption()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-memory-cmd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var commands = CommandsService.Register(ctx);
            using var registration = MemoryCommand.Register(ctx);
            var agent = new FakeAgent(ctx);

            var result = await commands.Execute(agent, "/memory maybe", TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.IsType<CommandResult.Error>(result.Result);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProjectContext_FollowsAgentWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-memory-cmd", Guid.NewGuid().ToString("N"));
        var projectA = Path.Combine(root, "a");
        var projectB = Path.Combine(root, "b");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);
        try
        {
            var ctx = new Context();
            var systemPrompt = new SystemPrompt(ctx, new SystemPromptConfig());
            _ = CommandsService.Register(ctx);
            using var registration = MemoryCommand.Register(ctx);
            using var workspace = new MemoryWorkspace(MemoryPluginConfig.Resolve(null));
            ctx.Provide(MemoryServices.Provider, workspace);
            await File.WriteAllTextAsync(Path.Combine(projectA, ".dsh-memory.md"), "## Facts\n- a.marker :: from-a (2026-01-01T00:00:00Z)\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(projectB, ".dsh-memory.md"), "## Facts\n- b.marker :: from-b (2026-01-01T00:00:00Z)\n", TestContext.Current.CancellationToken);

            var assemblyA = await systemPrompt.Assemble(new AssembleContext(Agent: new FakeAgent(ctx, projectA)));
            var assemblyB = await systemPrompt.Assemble(new AssembleContext(Agent: new FakeAgent(ctx, projectB)));
            var contextA = assemblyA.Contexts.Single(item => item.Name == "memory:project");
            var contextB = assemblyB.Contexts.Single(item => item.Name == "memory:project");

            Assert.Contains("from-a", contextA.Text);
            Assert.DoesNotContain("from-b", contextA.Text);
            Assert.Contains("from-b", contextB.Text);
            Assert.DoesNotContain("from-a", contextB.Text);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakePluginManager : IPluginManager
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<string> PackageNames => [];

        public string Describe(string package) => "unknown";

        public Task<string> AddAsync(string packageOrPath) => Task.FromResult("");

        public Task<string> RemoveAsync(string package, bool force = false) => Task.FromResult("");

        public Task<string> DisableAsync(string package)
        {
            Calls.Add($"disable:{package}");
            return Task.FromResult("disabled");
        }

        public Task<string> EnableAsync(string package)
        {
            Calls.Add($"enable:{package}");
            return Task.FromResult("enabled");
        }
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(Context ctx, string? cwd = null)
        {
            Ctx = ctx;
            var id = SessionId.Create($"session-{Guid.NewGuid():N}");
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 0,
                Cwd = cwd ?? Path.GetTempPath(),
                IsSeeded = false,
            });
        }

        public SessionId Id => Session.Id;
        public Session Session { get; }
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; }
        public AgentStatus Status => AgentStatus.Idle;
        public AgentOptions Options { get; } = new();

        public void Cancel(AgentCancelCause cause, bool keepInbox = false)
        {
        }

        public Task WhenIdle() => Task.CompletedTask;

        public void Send(UserMessage message, string target, bool wakeup)
        {
        }

        public void Followup(UserMessage message)
        {
        }

        public void Steer(UserMessage message)
        {
        }

        public void Inject(UserMessage message)
        {
        }
    }
}
