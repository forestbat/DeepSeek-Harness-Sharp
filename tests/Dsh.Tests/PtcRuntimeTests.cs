#pragma warning disable CA2255
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Ptc;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 测试宿主同时也是 --ptc-host 脚本宿主: 真实子进程测试用 Environment.ProcessPath 重启本 exe。 */
internal static class PtcHostShim
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Any(argument => argument == PtcScriptHost.HostArgument))
            Environment.Exit(PtcScriptHost.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult());
    }
}

public sealed class PtcRuntimeTests
{
    private static JsonObject ObjectSchema(params (string Name, string Type, bool Required)[] properties)
    {
        var propertiesNode = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, isRequired) in properties)
        {
            propertiesNode[name] = new JsonObject { ["type"] = type };
            if (isRequired)
                required.Add(JsonValue.Create(name));
        }
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = propertiesNode,
        };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    private static string TextOf(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    [Fact]
    public void PtcEventCodecs_AreRegistered()
    {
        Assert.True(SessionEventCodec.IsRegistered(PtcEvents.DispatchStart));
        Assert.True(SessionEventCodec.IsRegistered(PtcEvents.Dispatch));
    }

    [Fact]
    public void SdkRender_IsDeterministicAndSorted()
    {
        var alpha = new ToolSchema("alpha", "Alpha tool", ObjectSchema(("x", "string", true)));
        var beta = new ToolSchema("beta", "Beta tool", ObjectSchema(("y", "integer", false)));
        var runCode = new ToolSchema(PtcTransport.RunCodeName, "run code", new JsonObject());

        var first = PtcSdkRenderer.Render([beta, runCode, alpha]);
        var second = PtcSdkRenderer.Render([alpha, beta, runCode]);

        Assert.Equal(first, second);
        var alphaAt = first.IndexOf("public static Task<JsonNode?> alpha(", StringComparison.Ordinal);
        var betaAt = first.IndexOf("public static Task<JsonNode?> beta(", StringComparison.Ordinal);
        Assert.True(alphaAt >= 0 && betaAt > alphaAt);
        Assert.DoesNotContain("public static Task<JsonNode?> run_code(", first);
    }

    private sealed class CompositionHome : IDisposable
    {
        public CompositionHome(string settings)
        {
            Root = Path.Combine(Path.GetTempPath(), $"dsh-ptc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "settings.yaml"), settings);
            Home = new HarnessHome(Root);
        }

        public string Root { get; }

        public HarnessHome Home { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task PtcScope_ProjectsOnlyRunCodeAndSdkSections()
    {
        using var home = new CompositionHome("plugins: {}\n");
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));
        var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var prompt = app.Ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var scope = new ScopeKey();
        using var presentation = tools.PresentAs(ToolPresentationMode.Ptc, scope);

        var assembly = await prompt.Assemble(new AssembleContext(scope));

        Assert.Equal([PtcTransport.RunCodeName], assembly.Tools.Select(schema => schema.Name).ToList());
        Assert.Contains(assembly.Sections, section => section.Name == "tools:sdk" && section.Text.Length > 0);
        Assert.Contains(assembly.Sections, section => section.Name == "tools:ptc-only" && section.Text.Length > 0);
    }

    [Fact]
    public async Task BothScope_ProjectsNativePlusRunCode()
    {
        using var home = new CompositionHome("plugins: {}\n");
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));
        var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var prompt = app.Ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var scope = new ScopeKey();
        using var presentation = tools.PresentAs(ToolPresentationMode.Both, scope);

        var assembly = await prompt.Assemble(new AssembleContext(scope));

        Assert.Contains(assembly.Tools, schema => schema.Name == PtcTransport.RunCodeName);
        Assert.True(assembly.Tools.Count > 1);
        Assert.Contains(assembly.Sections, section => section.Name == "tools:sdk" && section.Text.Length > 0);
        Assert.Contains(assembly.Sections, section => section.Name == "tools:ptc-only" && section.Text.Length == 0);
    }

    [Fact]
    public async Task PtcPresentation_WithoutTransport_FailsLoudly()
    {
        using var home = new CompositionHome("""
            plugins:
              "@deepseek-ai/dsh-ptc": false
            """);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));
        var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var prompt = app.Ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var scope = new ScopeKey();
        using var presentation = tools.PresentAs(ToolPresentationMode.Ptc, scope);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => prompt.Assemble(new AssembleContext(scope)));
    }

    private sealed class Harness : IDisposable
    {
        private readonly SubprocessService _subprocess;

        public Harness()
        {
            Ctx = new Context();
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            _subprocess = new SubprocessService(Ctx);
            _ = new PtcTransport(Ctx, Environment.CurrentDirectory);
        }

        public Context Ctx { get; }

        public ToolRuntime Tools { get; }

        public void Dispose() => _subprocess.Dispose();
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(Context ctx)
        {
            Ctx = ctx;
            var id = SessionId.Create($"session-{Guid.NewGuid():N}");
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 0,
                Cwd = Path.GetTempPath(),
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

    private static void RegisterEcho(ToolRuntime tools, string name)
        => tools.Register(new ToolDefinition
        {
            Name = name,
            Description = $"{name} echo tool",
            Parameters = ObjectSchema(("text", "string", true)),
            Output = new ToolOutputDefinition(
                new JsonObject(),
                (_, value) => [new TextBlock(value.GetProperty("echoed").GetString() ?? "")]),
            Execute = (arguments, _) => Task.FromResult<object?>(new JsonObject
            {
                ["echoed"] = arguments.TryGetProperty("text", out var text) ? text.GetString() : null,
            }),
        });

    private static Task<ToolExecutionResult> RunCode(ToolRuntime tools, IAgent agent, string code, long? timeoutMs = null)
    {
        var arguments = new JsonObject { ["code"] = code, ["description"] = "test program" };
        if (timeoutMs is not null)
            arguments["timeoutMs"] = timeoutMs;
        return tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = PtcTransport.RunCodeName,
            Arguments = JsonDocument.Parse(arguments.ToJsonString()).RootElement,
            Agent = agent,
            Signal = default,
        });
    }

    [Fact]
    public async Task RunCode_PureComputation_ReturnsValue()
    {
        using var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        using var presentation = harness.Tools.PresentAs(ToolPresentationMode.Ptc, agent.ScopeKey);

        var result = await RunCode(harness.Tools, agent, "return JsonValue.Create(3 + 4);", timeoutMs: 20000);

        Assert.False(result.IsError, TextOf(result));
        var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
        Assert.Equal(7, value.GetProperty("result").GetInt32());
    }

    [Fact]
    public async Task RunCode_BindingCall_LandsDispatchEventsAndReturnsValue()
    {
        using var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        RegisterEcho(harness.Tools, "echo");
        using var presentation = harness.Tools.PresentAs(ToolPresentationMode.Ptc, agent.ScopeKey);

        var result = await RunCode(harness.Tools, agent, """
            var r = await tools.echo(new JsonObject { ["text"] = "hi" });
            Console.WriteLine("called " + r["echoed"]);
            return r;
            """, timeoutMs: 20000);

        Assert.False(result.IsError, TextOf(result));
        var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
        Assert.Equal("hi", value.GetProperty("result").GetProperty("echoed").GetString());
        Assert.Contains("called hi", value.GetProperty("logs").GetString());
        Assert.Contains("called hi", TextOf(result));
        Assert.Contains("echoed", TextOf(result));
        Assert.Contains(agent.Session.SnapshotEvents(), sessionEvent => sessionEvent.Data is PtcDispatchStartPayload);
        Assert.Contains(agent.Session.SnapshotEvents(), sessionEvent => sessionEvent.Data is PtcDispatchPayload);
    }

    [Fact]
    public async Task RunCode_InfiniteLoop_TimesOutAndIsKilled()
    {
        using var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        using var presentation = harness.Tools.PresentAs(ToolPresentationMode.Ptc, agent.ScopeKey);
        var stopwatch = Stopwatch.StartNew();

        var result = await RunCode(harness.Tools, agent, "while (true) { }", timeoutMs: 750);

        stopwatch.Stop();
        Assert.True(result.IsError);
        Assert.Contains("timeout", TextOf(result));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"run took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunCode_CompileError_ReportsDiagnostics()
    {
        using var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        using var presentation = harness.Tools.PresentAs(ToolPresentationMode.Ptc, agent.ScopeKey);

        var result = await RunCode(harness.Tools, agent, "this is not valid C# ;;;");

        Assert.True(result.IsError);
        Assert.Contains("exception", TextOf(result));
        Assert.Contains("CS", TextOf(result));
    }

    [Fact]
    public async Task DirectToolCall_InPtcMode_IsRejectedPointingAtRunCode()
    {
        using var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        RegisterEcho(harness.Tools, "echo");
        using var presentation = harness.Tools.PresentAs(ToolPresentationMode.Ptc, agent.ScopeKey);

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = "echo",
            Arguments = JsonDocument.Parse("""{"text":"hi"}""").RootElement,
            Agent = agent,
            Signal = default,
        });

        Assert.True(result.IsError);
        Assert.Contains(PtcTransport.RunCodeName, TextOf(result));
    }
}
