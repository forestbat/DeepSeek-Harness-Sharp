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
using Dsh.ScriptHost;

namespace Dsh.Tests;

/** 测试宿主同时提供脚本宿主与独立原生 probe 入口;真实子进程复用当前 apphost/dotnet 入口,不启动 harness。 */
internal static class PtcHostShim
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Any(argument => argument == PtyConsoleResizeTests.ProbeArgument))
            Environment.Exit(PtyConsoleResizeTests.RunProbe(args));
        if (args.Any(argument => argument == UnixPtyDescriptorOwnershipTests.SpawnFailureProbeArgument))
            Environment.Exit(UnixPtyDescriptorOwnershipTests.RunSpawnFailureProbe());
        if (args.Any(argument => argument == PtcScriptHost.HostArgument))
            Environment.Exit(PtcScriptHost.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult());
    }
}

[Collection(SerialProcessCollection.Name)]
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
    public void SdkRender_IsDeterministicAndSorted()
    {
        var alpha = new ToolSchema("alpha", "Alpha tool", ObjectSchema(("x", "string", true)));
        var beta = new ToolSchema("beta", "Beta tool", ObjectSchema(("y", "integer", false)));
        var runCode = new ToolSchema(ToolRunCode.ToolName, "run code", new JsonObject());

        var first = PtcSdkRenderer.Render([beta, runCode, alpha], debugBatching: false);
        var second = PtcSdkRenderer.Render([alpha, beta, runCode], debugBatching: false);

        Assert.Equal(first, second);
        var alphaAt = first.IndexOf("public static Task<JsonNode?> alpha(", StringComparison.Ordinal);
        var betaAt = first.IndexOf("public static Task<JsonNode?> beta(", StringComparison.Ordinal);
        Assert.True(alphaAt >= 0 && betaAt > alphaAt);
        Assert.DoesNotContain("public static Task<JsonNode?> run_code(", first);
    }

    [Fact]
    public void SdkRender_NonIdentifierTool_DeclaredAsCallForm()
    {
        var odd = new ToolSchema("my-tool", "Odd tool", ObjectSchema(("x", "string", true)));
        var alpha = new ToolSchema("alpha", "Alpha tool", ObjectSchema(("x", "string", true)));

        var rendered = PtcSdkRenderer.Render([odd, alpha], debugBatching: false);

        Assert.Contains("public static Task<JsonNode?> alpha(", rendered);
        Assert.DoesNotContain("public static Task<JsonNode?> my-tool(", rendered);
        Assert.Contains("""await tools.call("my-tool", args)""", rendered);
        Assert.Contains("tools.call", rendered);
    }

    [Fact]
    public async Task RunCode_CallForm_ReachesNonIdentifierTool()
    {
        using var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        RegisterEcho(harness.Tools, "my-tool");

        var result = await RunCode(harness.Tools, agent, """
            var r = await tools.call("my-tool", new JsonObject { ["text"] = "hi" });
            return r;
            """, timeoutMs: 20000);

        Assert.False(result.IsError, TextOf(result));
        var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
        Assert.Equal("hi", value.GetProperty("result").GetProperty("echoed").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunCode_NativeToolResultsCanBeComposedIntoNewJsonValues(bool namedBinding)
    {
        using var home = new CompositionHome("");
        using var harness = new Harness();
        using var write = Dsh.Tools.WriteTool.Register(harness.Ctx);
        var agent = new FakeAgent(harness.Ctx);
        var path = Path.Combine(home.Root, "composed-result.txt");
        var invocation = namedBinding ? "tools.write(arguments)" : """tools.call("write", arguments)""";
        var result = await RunCode(harness.Tools, agent, $$"""
            var arguments = new JsonObject { ["file_path"] = {{JsonSerializer.Serialize(path)}}, ["content"] = "sdk-owned-result" };
            var written = await {{invocation}};
            return new JsonObject { ["nested"] = written };
            """, timeoutMs: 20000);

        Assert.False(result.IsError, TextOf(result));
        var nested = Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("result").GetProperty("nested");
        Assert.Equal(path, nested.GetProperty("path").GetString());
        Assert.Equal("create", nested.GetProperty("operation").GetString());
        Assert.Equal("sdk-owned-result", nested.GetProperty("after").GetString());
        Assert.Equal("sdk-owned-result", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
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
    public async Task PtcPlugin_ProjectsNativeToolsAndScopeVisibleSdk()
    {
        using var home = new CompositionHome("plugins: { \"@deepseek-ai/dsh-toon\": false }\n");
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));
        var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var prompt = app.Ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var scope = new ScopeKey();

        var assembly = await prompt.Assemble(new AssembleContext(scope));

        Assert.Contains(assembly.Tools, schema => schema.Name == ToolRunCode.ToolName);
        Assert.Contains(assembly.Tools, schema => schema.Name == "write");
        var sdk = Assert.Single(assembly.Sections, section => section.Name == "tools:sdk");
        Assert.Contains("public static Task<JsonNode?> write(", sdk.Text);

        using (tools.Restrict(new ToolRestriction(Deny: ["write"]), scope))
        {
            var restricted = await prompt.Assemble(new AssembleContext(scope));
            Assert.Contains(restricted.Tools, schema => schema.Name == ToolRunCode.ToolName);
            Assert.DoesNotContain(restricted.Tools, schema => schema.Name == "write");
            Assert.DoesNotContain("public static Task<JsonNode?> write(",
                Assert.Single(restricted.Sections, section => section.Name == "tools:sdk").Text);
        }

        using (tools.Restrict(new ToolRestriction(Deny: [ToolRunCode.ToolName]), scope))
        {
            var restricted = await prompt.Assemble(new AssembleContext(scope));
            Assert.DoesNotContain(restricted.Tools, schema => schema.Name == ToolRunCode.ToolName);
            Assert.Contains(restricted.Tools, schema => schema.Name == "write");
            Assert.DoesNotContain(restricted.Sections, section => section.Name == "tools:sdk" && section.Text.Length > 0);
        }
    }

    [Fact]
    public async Task DisabledPtcPlugin_LeavesNativeToolsAvailable()
    {
        using var home = new CompositionHome("""
            plugins:
              "@deepseek-ai/dsh-ptc": false
              "@deepseek-ai/dsh-toon": false
            """);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root));
        var prompt = app.Ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;

        var assembly = await prompt.Assemble(new AssembleContext(new ScopeKey()));

        Assert.DoesNotContain(assembly.Tools, schema => schema.Name == ToolRunCode.ToolName);
        Assert.Contains(assembly.Tools, schema => schema.Name == "write");
        Assert.DoesNotContain(assembly.Sections, section => section.Name == "tools:sdk" && section.Text.Length > 0);
    }

    private sealed class Harness : IDisposable
    {
        private readonly SubprocessService _subprocess;
        private readonly IDisposable _registration;

        public Harness()
        {
            Ctx = new Context();
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            _subprocess = new SubprocessService(Ctx);
            _registration = new Dsh.Ptc.Plugin().Apply(Ctx, null);
        }

        public Context Ctx { get; }

        public ToolRuntime Tools { get; }

        public void Dispose()
        {
            _registration.Dispose();
            _subprocess.Dispose();
        }
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
            Name = ToolRunCode.ToolName,
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

        var result = await RunCode(harness.Tools, agent, "this is not valid C# ;;;");

        Assert.True(result.IsError);
        Assert.Contains("exception", TextOf(result));
        Assert.Contains("CS", TextOf(result));
    }

    [Fact]
    public async Task RunCode_AndDirectTools_CoexistAndRespectScopeRestrictions()
    {
        using var home = new CompositionHome("");
        using var harness = new Harness();
        using var write = Dsh.Tools.WriteTool.Register(harness.Ctx);
        var agent = new FakeAgent(harness.Ctx);
        var directPath = Path.Combine(home.Root, "direct.txt");
        var nestedPath = Path.Combine(home.Root, "nested.txt");
        var blockedPath = Path.Combine(home.Root, "blocked.txt");

        Task<ToolExecutionResult> DirectWrite(string path) => harness.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = "write",
            Arguments = JsonSerializer.SerializeToElement(new JsonObject { ["file_path"] = path, ["content"] = "direct" }),
            Agent = agent,
            Signal = TestContext.Current.CancellationToken,
        });

        Task<ToolExecutionResult> NestedWrite(string path)
        {
            var arguments = new JsonObject { ["file_path"] = path, ["content"] = "nested" };
            var literal = JsonSerializer.Serialize(arguments.ToJsonString());
            return RunCode(harness.Tools, agent,
                $"return await tools.call(\"write\", JsonNode.Parse({literal})!.AsObject());",
                timeoutMs: 20000);
        }

        var direct = await DirectWrite(directPath);
        Assert.False(direct.IsError, TextOf(direct));
        Assert.Equal("direct", File.ReadAllText(directPath));
        var nested = await NestedWrite(nestedPath);
        Assert.False(nested.IsError, TextOf(nested));
        Assert.Equal("nested", File.ReadAllText(nestedPath));

        using var restriction = harness.Tools.Restrict(new ToolRestriction(Deny: ["write"]), agent.ScopeKey);
        Assert.True((await DirectWrite(blockedPath)).IsError);
        var blocked = await NestedWrite(blockedPath);
        Assert.True(blocked.IsError, TextOf(blocked));
        Assert.False(File.Exists(blockedPath));
    }
}
