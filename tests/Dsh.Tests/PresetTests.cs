using System.Text.Json.Nodes;
using Dsh.Inspection;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Presets;
using Dsh.Ptc;

namespace Dsh.Tests;

public class PresetTests
{
    private sealed class Harness
    {
        public Context Ctx { get; } = new();
        public SystemPrompt Prompt { get; }
        public ToolRuntime Tools { get; }
        public CommandsService Commands { get; }
        public PresetController Presets { get; }

        public Harness(bool creative = false, bool ptc = false)
        {
            Prompt = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Tools.Register(EchoTool("bash"));
            Tools.Register(EchoTool("read"));
            Commands = new CommandsService(Ctx);
            if (ptc)
                Ctx.Provide(IToolPresentation.ServiceName, new FakePtcTransport());
            if (creative)
                CreativeToolset.Register(Ctx);
            Presets = PresetController.Register(Ctx);
        }

        private static ToolDefinition EchoTool(string name)
            => new()
            {
                Name = name,
                Description = $"{name} tool",
                Parameters = new JsonObject(),
                Output = new ToolOutputDefinition(
                    new JsonObject(),
                    (_, value) => [new TextBlock(value.GetProperty("ran").GetString()!)]),
                Execute = (_, _) => Task.FromResult<object?>(new { ran = "yes" }),
            };
    }

    private sealed class FakePtcTransport : IToolPresentation
    {
        public ToolDefinition TransportDefinition { get; } = new()
        {
            Name = PtcTransport.RunCodeName,
            Description = "run_code tool",
            Parameters = new JsonObject(),
            Output = new ToolOutputDefinition(new JsonObject(), (_, _) => []),
            Execute = (_, _) => Task.FromResult<object?>(null),
        };

        public bool IsAvailable => true;
        public string TransportToolName => PtcTransport.RunCodeName;
        public string SdkSection(ScopeKey? scope) => "";
        public string TransportOnlySection(ScopeKey? scope) => "";
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

        public UserMessage? Injected { get; private set; }

        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task WhenIdle() => Task.CompletedTask;
        public void Send(UserMessage message, string target, bool wakeup) { }
        public void Followup(UserMessage message) { }
        public void Steer(UserMessage message) { }
        public void Inject(UserMessage message) => Injected = message;
    }

    [Fact]
    public void Default_Preset_Is_Standard()
    {
        var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        Assert.Equal(InteractionPreset.Standard, PresetController.PresetOf(agent.Session));
    }

    [Fact]
    public void Payload_Codec_Roundtrip()
    {
        PresetModePayload.RegisterCodec();
        var sessionEvent = new SessionEvent
        {
            Type = PresetEvents.Mode,
            Seq = 0,
            Time = 1,
            Data = new PresetModePayload(InteractionPreset.Minimal),
        };
        var json = DshJson.Serialize(sessionEvent);
        var parsed = DshJson.Deserialize<SessionEvent>(json);
        var payload = Assert.IsType<PresetModePayload>(parsed!.Data);
        Assert.Equal(InteractionPreset.Minimal, payload.Preset);
    }

    [Fact]
    public void Set_Minimal_Appends_Event_And_Restricts_Tools()
    {
        var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        var result = harness.Presets.Set(agent, "minimal");
        Assert.IsType<CommandResult.Success>(result);
        Assert.Equal(InteractionPreset.Minimal, PresetController.PresetOf(agent.Session));
        var names = harness.Tools.Schemas(agent.ScopeKey).Select(tool => tool.Name).ToList();
        Assert.Equal(["bash"], names);
        Assert.NotNull(harness.Tools.Get("bash", agent.ScopeKey));
        Assert.Null(harness.Tools.Get("read", agent.ScopeKey));
        Assert.NotNull(agent.Injected);
    }

    [Fact]
    public async Task Minimal_Replaces_Persona_And_Suppresses_Runtime_Context()
    {
        var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        harness.Presets.Set(agent, "minimal");
        var scoped = await harness.Prompt.Assemble(new AssembleContext(agent.ScopeKey, Agent: agent));
        var section = Assert.Single(scoped.Sections);
        Assert.Equal(SystemPrompt.PersonaSection, section.Name);
        Assert.Equal(PresetController.MinimalPersona, section.Text);
        Assert.Empty(scoped.Contexts);
        var global = await harness.Prompt.Assemble(new AssembleContext());
        Assert.True(global.Sections.Count > 1);
        Assert.Contains(global.Sections, candidate => candidate.Name == SystemPrompt.PersonaSection
            && candidate.Text != PresetController.MinimalPersona);
    }

    [Fact]
    public void Set_Standard_After_Minimal_Restores_Toolset()
    {
        var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        harness.Presets.Set(agent, "minimal");
        var result = harness.Presets.Set(agent, "standard");
        Assert.IsType<CommandResult.Success>(result);
        Assert.Equal(InteractionPreset.Standard, PresetController.PresetOf(agent.Session));
        var names = harness.Tools.Schemas(agent.ScopeKey).Select(tool => tool.Name).Order().ToList();
        Assert.Equal(["bash", "read"], names);
    }

    [Fact]
    public void Set_Same_Preset_Is_Noop()
    {
        var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        var result = harness.Presets.Set(agent, "standard");
        var success = Assert.IsType<CommandResult.Success>(result);
        Assert.Contains("already", success.Text);
        Assert.Equal(InteractionPreset.Standard, PresetController.PresetOf(agent.Session));
    }

    [Theory]
    [InlineData("ptc")]
    [InlineData("creative")]
    public void Unavailable_Presets_Are_Rejected_Without_Event(string preset)
    {
        var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        var result = harness.Presets.Set(agent, preset);
        Assert.IsType<CommandResult.Error>(result);
        Assert.Equal(InteractionPreset.Standard, PresetController.PresetOf(agent.Session));
    }

    [Fact]
    public void Unknown_Preset_Fails()
    {
        var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        var result = harness.Presets.Set(agent, "nonsense");
        Assert.IsType<CommandResult.Error>(result);
    }

    [Fact]
    public void Agent_Created_Replays_Persisted_Preset()
    {
        var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        agent.Session.Append(new PresetModePayload(InteractionPreset.Minimal));
        harness.Ctx.Emit(new AgentCreatedNotification(agent));
        var names = harness.Tools.Schemas(agent.ScopeKey).Select(tool => tool.Name).ToList();
        Assert.Equal(["bash"], names);
        harness.Ctx.Emit(new AgentDisposedNotification(agent));
        names = harness.Tools.Schemas(agent.ScopeKey).Select(tool => tool.Name).Order().ToList();
        Assert.Equal(["bash", "read"], names);
    }

    [Fact]
    public async Task Slash_Preset_Command_Switches_And_Reports()
    {
        var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        var empty = await harness.Commands.Execute(agent, "/preset", TestContext.Current.CancellationToken);
        var emptySuccess = Assert.IsType<CommandResult.Success>(empty?.Result);
        Assert.Contains("standard", emptySuccess.Text);
        var execution = await harness.Commands.Execute(agent, "/preset minimal", TestContext.Current.CancellationToken);
        Assert.IsType<CommandResult.Success>(execution?.Result);
        Assert.Equal(InteractionPreset.Minimal, PresetController.PresetOf(agent.Session));
        var failed = await harness.Commands.Execute(agent, "/preset ptc", TestContext.Current.CancellationToken);
        Assert.IsType<CommandResult.Error>(failed?.Result);
    }

    [Fact]
    public void Set_Creative_Exposes_Creative_And_Standard_Tools()
    {
        var harness = new Harness(creative: true);
        var agent = new FakeAgent(harness.Ctx);
        var result = harness.Presets.Set(agent, "creative");
        Assert.IsType<CommandResult.Success>(result);
        Assert.Equal(InteractionPreset.Creative, PresetController.PresetOf(agent.Session));
        var names = harness.Tools.Schemas(agent.ScopeKey).Select(tool => tool.Name).Order().ToList();
        Assert.Equal(["bash", "cordis_inspect_list", "cordis_inspect_query", "plugin_manager", "read"], names);
    }

    [Theory]
    [InlineData("standard", new[] { "bash", "read" })]
    [InlineData("minimal", new[] { "bash" })]
    public void Switching_From_Creative_Removes_Creative_Tools(string preset, string[] expected)
    {
        var harness = new Harness(creative: true);
        var agent = new FakeAgent(harness.Ctx);
        harness.Presets.Set(agent, "creative");
        Assert.IsType<CommandResult.Success>(harness.Presets.Set(agent, preset));
        var names = harness.Tools.Schemas(agent.ScopeKey).Select(tool => tool.Name).Order().ToList();
        Assert.Equal(expected.Order().ToList(), names);
    }

    [Fact]
    public void Switching_To_Ptc_Removes_Creative_Tools()
    {
        var harness = new Harness(creative: true, ptc: true);
        var agent = new FakeAgent(harness.Ctx);
        harness.Presets.Set(agent, "creative");
        Assert.IsType<CommandResult.Success>(harness.Presets.Set(agent, "ptc"));
        var names = harness.Tools.Schemas(agent.ScopeKey).Select(tool => tool.Name).ToList();
        Assert.Contains(PtcTransport.RunCodeName, names);
        Assert.DoesNotContain("plugin_manager", names);
        Assert.DoesNotContain("cordis_inspect_list", names);
        Assert.DoesNotContain("cordis_inspect_query", names);
    }

    [Fact]
    public async Task Creative_Guidance_Section_Is_Scoped()
    {
        var harness = new Harness(creative: true);
        var agent = new FakeAgent(harness.Ctx);
        harness.Presets.Set(agent, "creative");
        var guidance = harness.Ctx.Get<ICreativeToolset>(ICreativeToolset.ServiceName)!.GuidanceSection;
        var scoped = await harness.Prompt.Assemble(new AssembleContext(agent.ScopeKey));
        Assert.Contains(scoped.Sections, section => section.Name == guidance.Name);
        var other = await harness.Prompt.Assemble(new AssembleContext(new ScopeKey()));
        Assert.DoesNotContain(other.Sections, section => section.Name == guidance.Name);
    }

    [Fact]
    public void Set_Creative_Without_Toolset_Is_NotReady()
    {
        var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        var result = harness.Presets.Set(agent, "creative");
        var error = Assert.IsType<CommandResult.Error>(result);
        Assert.Contains("未就绪", error.Text);
        Assert.Equal(InteractionPreset.Standard, PresetController.PresetOf(agent.Session));
    }
}
