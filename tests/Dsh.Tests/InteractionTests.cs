using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

public class InteractionTests
{
    private sealed class Harness : IDisposable
    {
        public Context Ctx { get; } = new();
        public ToolRuntime Tools { get; }

        public Harness()
        {
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
        }

        public void Dispose()
        {
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
        public List<UserMessage> Injected { get; } = [];

        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task WhenIdle() => Task.CompletedTask;
        public void Send(UserMessage message, string target, bool wakeup) { }
        public void Followup(UserMessage message) { }
        public void Steer(UserMessage message) { }
        public void Inject(UserMessage message) => Injected.Add(message);
    }

    private static FakeAgent CreateAgent(Harness harness, bool openTurn = true)
    {
        var agent = new FakeAgent(harness.Ctx);
        if (openTurn)
            agent.Session.Append(new TurnStartPayload(1));
        return agent;
    }

    private static readonly AskUserQuestionItem SampleQuestion = new(
        "q1",
        "Pick one",
        Options: [new AskUserQuestionOption("alpha"), new AskUserQuestionOption("beta")]);

    public sealed class Approval
    {
        [Fact]
        public async Task AutoApprove_Allows_And_Logs_Audit_Pair()
        {
            using var harness = new Harness();
            var approval = ApprovalService.Register(harness.Ctx);
            using var answerer = ApprovalAnswerers.AutoApprove(harness.Ctx);
            var agent = CreateAgent(harness);

            var outcome = await approval.Request(
                new ApprovalRequest(agent, "bash", ToolCallId.Create("call-1"), "needs it"), default);

            Assert.Equal(ApprovalOutcome.AllowedOnce, outcome);
            var payloads = agent.Session.SnapshotEvents().Select(e => e.Data).ToList();
            var asked = Assert.IsType<ApprovalAskedPayload>(payloads[1]);
            var decided = Assert.IsType<ApprovalDecidedPayload>(payloads[2]);
            Assert.Equal("bash", asked.ToolName);
            Assert.Equal("needs it", asked.Reason);
            Assert.Equal(asked.Id, decided.Id);
            Assert.Equal(ApprovalOutcome.AllowedOnce, decided.Outcome);
        }

        [Fact]
        public async Task DenyAll_Rejects()
        {
            using var harness = new Harness();
            var approval = ApprovalService.Register(harness.Ctx);
            using var answerer = ApprovalAnswerers.DenyAll(harness.Ctx);
            var agent = CreateAgent(harness);

            var outcome = await approval.Request(new ApprovalRequest(agent, "bash", ToolCallId.Create("call-1")), default);

            Assert.Equal(ApprovalOutcome.Rejected, outcome);
            var decided = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<ApprovalDecidedPayload>().Single();
            Assert.Equal(ApprovalOutcome.Rejected, decided.Outcome);
        }

        [Fact]
        public async Task NoAnswerer_Fails_Closed_Unavailable()
        {
            using var harness = new Harness();
            var approval = ApprovalService.Register(harness.Ctx);
            var agent = CreateAgent(harness);

            var outcome = await approval.Request(new ApprovalRequest(agent, "bash", ToolCallId.Create("call-1")), default);

            Assert.Equal(ApprovalOutcome.Unavailable, outcome);
        }

        [Fact]
        public async Task ThrowingAnswerer_Fails_Closed_Unavailable()
        {
            using var harness = new Harness();
            var approval = ApprovalService.Register(harness.Ctx);
            harness.Ctx.On(ApprovalEvents.Request, (_, _) => throw new InvalidOperationException("boom"),
                new EventOptions { Global = true });
            var agent = CreateAgent(harness);

            var outcome = await approval.Request(new ApprovalRequest(agent, "bash", ToolCallId.Create("call-1")), default);

            Assert.Equal(ApprovalOutcome.Unavailable, outcome);
        }

        [Fact]
        public async Task NeverPolicy_Rejects_Without_Dispatching_Answerers()
        {
            using var harness = new Harness();
            var approval = ApprovalService.Register(harness.Ctx, new ApprovalConfig(ApprovalPolicy.Never));
            using var answerer = ApprovalAnswerers.AutoApprove(harness.Ctx);
            var agent = CreateAgent(harness);

            var outcome = await approval.Request(new ApprovalRequest(agent, "bash", ToolCallId.Create("call-1")), default);

            Assert.Equal(ApprovalOutcome.Rejected, outcome);
        }

        [Fact]
        public async Task SetPolicy_Override_Wins_Over_Config_And_Notifies_Agent()
        {
            using var harness = new Harness();
            var approval = ApprovalService.Register(harness.Ctx);
            using var answerer = ApprovalAnswerers.AutoApprove(harness.Ctx);
            var agent = CreateAgent(harness);

            approval.SetPolicy(agent, ApprovalPolicy.Never);

            Assert.Equal(ApprovalPolicy.Never, approval.OverrideOf(agent.Session));
            var message = Assert.Single(agent.Injected);
            var text = Assert.IsType<TextBlock>(message.Content[0]).Text;
            Assert.Contains("ask", text);
            Assert.Contains("never", text);
            var outcome = await approval.Request(new ApprovalRequest(agent, "bash", ToolCallId.Create("call-1")), default);
            Assert.Equal(ApprovalOutcome.Rejected, outcome);
        }

        [Fact]
        public void SetPolicy_Same_Value_Is_A_Noop()
        {
            using var harness = new Harness();
            var approval = ApprovalService.Register(harness.Ctx);
            var agent = CreateAgent(harness);
            var before = agent.Session.Seq;

            approval.SetPolicy(agent, ApprovalPolicy.Ask);

            Assert.Equal(before, agent.Session.Seq);
            Assert.Empty(agent.Injected);
        }

        [Fact]
        public async Task Cancelled_Signal_Resolves_Cancelled()
        {
            using var harness = new Harness();
            var approval = ApprovalService.Register(harness.Ctx);
            using var answerer = ApprovalAnswerers.AutoApprove(harness.Ctx);
            var agent = CreateAgent(harness);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await approval.Request(new ApprovalRequest(agent, "bash", ToolCallId.Create("call-1")), cts.Token);

            Assert.Equal(ApprovalOutcome.Cancelled, outcome);
        }

        [Fact]
        public async Task Request_Outside_Open_Turn_Throws_And_Logs_Nothing()
        {
            using var harness = new Harness();
            var approval = ApprovalService.Register(harness.Ctx);
            var agent = CreateAgent(harness, openTurn: false);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => approval.Request(new ApprovalRequest(agent, "bash", ToolCallId.Create("call-1")), default));

            Assert.Empty(agent.Session.SnapshotEvents());
        }

        [Fact]
        public async Task Request_After_Turn_End_Throws()
        {
            using var harness = new Harness();
            var approval = ApprovalService.Register(harness.Ctx);
            var agent = CreateAgent(harness);
            agent.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => approval.Request(new ApprovalRequest(agent, "bash", ToolCallId.Create("call-1")), default));
        }
    }

    public sealed class UserQuestions
    {
        [Fact]
        public async Task Empty_Questions_Throws()
        {
            using var harness = new Harness();
            var service = UserQuestionService.Register(harness.Ctx);

            var error = await Assert.ThrowsAsync<UserQuestionException>(
                () => service.Ask(new AskUserQuestionRequest([])));

            Assert.Equal(UserQuestionException.EmptyQuestions, error.Code);
        }

        [Fact]
        public async Task Aborted_Signal_Throws_AskAborted()
        {
            using var harness = new Harness();
            var service = UserQuestionService.Register(harness.Ctx);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var error = await Assert.ThrowsAsync<UserQuestionException>(
                () => service.Ask(new AskUserQuestionRequest([SampleQuestion]), cts.Token));

            Assert.Equal(UserQuestionException.AskAborted, error.Code);
        }

        [Fact]
        public async Task No_Answerer_Throws_NoProvider()
        {
            using var harness = new Harness();
            var service = UserQuestionService.Register(harness.Ctx);

            var error = await Assert.ThrowsAsync<UserQuestionException>(
                () => service.Ask(new AskUserQuestionRequest([SampleQuestion])));

            Assert.Equal(UserQuestionException.NoProvider, error.Code);
        }

        [Fact]
        public async Task Answerer_Claims_Request()
        {
            using var harness = new Harness();
            var service = UserQuestionService.Register(harness.Ctx);
            AskUserQuestionRequest? seen = null;
            harness.Ctx.On(UserQuestionService.RequestEvent, (_, args) =>
            {
                seen = (AskUserQuestionRequest)args[0]!;
                return new ValueTask<object?>(new AskUserQuestionAnswer(
                    [new AskUserQuestionAnswerItem("q1", ["beta"], "custom note")]));
            });

            var answer = await service.Ask(new AskUserQuestionRequest([SampleQuestion]));

            Assert.NotNull(seen);
            var item = Assert.Single(answer.Answers);
            Assert.Equal("q1", item.Id);
            Assert.Equal(["beta"], item.Selected);
            Assert.Equal("custom note", item.Custom);
        }

        [Fact]
        public async Task Headless_Returns_First_Option_Or_Empty_Selection()
        {
            using var harness = new Harness();
            var service = UserQuestionService.Register(harness.Ctx);
            using var answerer = UserQuestionAnswerers.Headless(harness.Ctx);

            var answer = await service.Ask(new AskUserQuestionRequest(
                [SampleQuestion, new AskUserQuestionItem("q2", "Free text?")]));

            Assert.Equal(["alpha"], answer.Answers[0].Selected);
            Assert.Equal([], answer.Answers[1].Selected);
        }

        [Fact]
        public async Task BadIntent_Approve_Label_Not_An_Option_Throws()
        {
            using var harness = new Harness();
            var service = UserQuestionService.Register(harness.Ctx);
            var question = SampleQuestion with
            {
                Detail = "plan body",
                Intent = new AskUserQuestionIntent(AskUserQuestionIntentKind.PlanReview, "gamma"),
            };

            var error = await Assert.ThrowsAsync<UserQuestionException>(
                () => service.Ask(new AskUserQuestionRequest([question])));

            Assert.Equal(UserQuestionException.BadIntent, error.Code);
        }

        [Fact]
        public async Task BadIntent_Without_Detail_Throws()
        {
            using var harness = new Harness();
            var service = UserQuestionService.Register(harness.Ctx);
            var question = SampleQuestion with
            {
                Intent = new AskUserQuestionIntent(AskUserQuestionIntentKind.PlanReview, "alpha"),
            };

            var error = await Assert.ThrowsAsync<UserQuestionException>(
                () => service.Ask(new AskUserQuestionRequest([question])));

            Assert.Equal(UserQuestionException.BadIntent, error.Code);
        }

        [Fact]
        public async Task Unregistered_Agent_Throws_CallerNotLive()
        {
            using var harness = new Harness();
            var service = UserQuestionService.Register(harness.Ctx);
            var agent = CreateAgent(harness);

            var error = await Assert.ThrowsAsync<UserQuestionException>(
                () => service.Ask(new AskUserQuestionRequest([SampleQuestion], agent)));

            Assert.Equal(UserQuestionException.CallerNotLive, error.Code);
        }

        [Fact]
        public async Task Live_Agent_Uses_Scoped_Answerer()
        {
            using var harness = new Harness();
            var service = UserQuestionService.Register(harness.Ctx);
            using var answerer = UserQuestionAnswerers.Headless(harness.Ctx);
            var registry = new AgentRegistry(harness.Ctx);
            var agent = CreateAgent(harness);
            registry.Register(agent);

            var answer = await service.Ask(new AskUserQuestionRequest([SampleQuestion], agent));

            Assert.Equal(["alpha"], answer.Answers[0].Selected);
        }
    }

    public sealed class Commands
    {
        private static CommandDefinition Command(string name, string text)
            => new()
            {
                Name = name,
                Description = $"command {name}",
                Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success(text)),
            };

        [Theory]
        [InlineData("/goal", "goal", "")]
        [InlineData("/goal create the thing", "goal", " create the thing")]
        [InlineData("/goal\ncreate the thing", "goal", "\ncreate the thing")]
        [InlineData("/goal_name-2\t x ", "goal_name-2", "\t x ")]
        public void ParseCommand_Parses_Without_Normalizing_Input(string line, string name, string rawInput)
        {
            var parsed = CommandsService.ParseCommand(line);

            Assert.NotNull(parsed);
            Assert.Equal(name, parsed.Name);
            Assert.Equal(rawInput, parsed.RawInput);
        }

        [Theory]
        [InlineData("goal")]
        [InlineData(" /goal")]
        [InlineData("/")]
        [InlineData("/Goal")]
        [InlineData("/goal/path")]
        public void ParseCommand_Rejects_Non_Commands(string line)
        {
            Assert.Null(CommandsService.ParseCommand(line));
        }

        [Fact]
        public void Register_Rejects_Invalid_Metadata()
        {
            using var harness = new Harness();
            var commands = CommandsService.Register(harness.Ctx);

            Assert.Throws<ArgumentException>(() => commands.Register(Command("Bad", "x")));
            Assert.Throws<ArgumentException>(() => commands.Register(Command("bad", "x") with { Description = " " }));
        }

        [Fact]
        public void Register_Duplicate_Name_Throws()
        {
            using var harness = new Harness();
            var commands = CommandsService.Register(harness.Ctx);
            using var first = commands.Register(Command("goal", "one"));

            Assert.Throws<InvalidOperationException>(() => commands.Register(Command("goal", "two")));
        }

        [Fact]
        public void List_Returns_Sorted_Descriptors()
        {
            using var harness = new Harness();
            var commands = CommandsService.Register(harness.Ctx);
            using var zeta = commands.Register(Command("zeta", "z"));
            using var alpha = commands.Register(new CommandDefinition
            {
                Name = "alpha",
                Description = "a",
                Input = new CommandInputDescriptor("<target>"),
                Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
            });
            var agent = CreateAgent(harness);

            var listed = commands.List(agent);

            Assert.Equal(["alpha", "zeta"], listed.Select(descriptor => descriptor.Name).ToList());
            Assert.Equal("<target>", listed[0].Input?.Hint);
            Assert.NotNull(commands.Find(agent, "zeta"));
            Assert.Null(commands.Find(agent, "missing"));
        }

        [Fact]
        public async Task Execute_Unknown_Or_Non_Command_Returns_Null_And_Logs_Nothing()
        {
            using var harness = new Harness();
            var commands = CommandsService.Register(harness.Ctx);
            var agent = CreateAgent(harness);

            Assert.Null(await commands.Execute(agent, "not a command"));
            Assert.Null(await commands.Execute(agent, "/missing"));
            Assert.Equal(1, agent.Session.Seq);
        }

        [Fact]
        public async Task Execute_Runs_Handler_And_Logs_Lifecycle_Pair()
        {
            using var harness = new Harness();
            var commands = CommandsService.Register(harness.Ctx);
            using var registration = commands.Register(Command("goal", "ran"));
            var agent = CreateAgent(harness);

            var execution = await commands.Execute(agent, "/goal do it");

            Assert.NotNull(execution);
            var success = Assert.IsType<CommandResult.Success>(execution.Result);
            Assert.Equal("ran", success.Text);
            var payloads = agent.Session.SnapshotEvents().Select(e => e.Data).ToList();
            var run = Assert.IsType<CommandRunPayload>(payloads[1]);
            var done = Assert.IsType<CommandDonePayload>(payloads[2]);
            Assert.Equal("goal", run.Name);
            Assert.Equal(" do it", run.Args);
            Assert.Equal("user", run.Source);
            Assert.Equal(run.CommandId, done.CommandId);
            Assert.Equal("success", done.Kind);
            Assert.Equal("ran", done.Text);
            Assert.Equal(run.CommandId, execution.CommandId);
        }

        [Fact]
        public async Task Execute_RecordInputFalse_Omits_Args()
        {
            using var harness = new Harness();
            var commands = CommandsService.Register(harness.Ctx);
            using var registration = commands.Register(Command("goal", "ran") with { RecordInput = false });
            var agent = CreateAgent(harness);

            var execution = await commands.Execute(agent, "/goal secret input");

            Assert.NotNull(execution);
            var run = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<CommandRunPayload>().Single();
            Assert.Null(run.Args);
        }

        [Fact]
        public async Task Execute_Handler_Error_Result_Logs_Done_Error()
        {
            using var harness = new Harness();
            var commands = CommandsService.Register(harness.Ctx);
            using var registration = commands.Register(new CommandDefinition
            {
                Name = "goal",
                Description = "command goal",
                Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Error("nope")),
            });
            var agent = CreateAgent(harness);

            var execution = await commands.Execute(agent, "/goal");

            var error = Assert.IsType<CommandResult.Error>(execution!.Result);
            Assert.Equal("nope", error.Text);
            var done = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<CommandDonePayload>().Single();
            Assert.Equal("error", done.Kind);
            Assert.Equal("nope", done.Text);
        }

        [Fact]
        public async Task Execute_Throwing_Handler_Settles_Done_Error_And_Rethrows()
        {
            using var harness = new Harness();
            var commands = CommandsService.Register(harness.Ctx);
            using var registration = commands.Register(new CommandDefinition
            {
                Name = "goal",
                Description = "command goal",
                Handler = _ => throw new InvalidOperationException("handler exploded"),
            });
            var agent = CreateAgent(harness);

            await Assert.ThrowsAsync<InvalidOperationException>(() => commands.Execute(agent, "/goal"));

            var done = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<CommandDonePayload>().Single();
            Assert.Equal("error", done.Kind);
            Assert.Contains("handler exploded", done.Text);
        }

        [Fact]
        public async Task Scoped_Registration_Shadows_Global_For_That_Agent()
        {
            using var harness = new Harness();
            var commands = CommandsService.Register(harness.Ctx);
            using var global = commands.Register(Command("shared", "global"));
            var agent = CreateAgent(harness);
            var other = CreateAgent(harness);
            using var scoped = commands.Register(Command("shared", "scoped"), agent.ScopeKey);

            var agentExecution = await commands.Execute(agent, "/shared");
            Assert.Equal("scoped", Assert.IsType<CommandResult.Success>(agentExecution!.Result).Text);

            var otherExecution = await commands.Execute(other, "/shared");
            Assert.Equal("global", Assert.IsType<CommandResult.Success>(otherExecution!.Result).Text);
        }

        [Fact]
        public void Disposing_Registration_Removes_Command()
        {
            using var harness = new Harness();
            var commands = CommandsService.Register(harness.Ctx);
            var agent = CreateAgent(harness);
            var registration = commands.Register(Command("temporary", "t"));

            registration.Dispose();

            Assert.Null(commands.Find(agent, "temporary"));
        }
    }

    public sealed class ToolApproval : IDisposable
    {
        private readonly Harness _harness = new();
        private int _ran;

        public ToolApproval()
        {
            _harness.Tools.Register(new ToolDefinition
            {
                Name = "guarded",
                Description = "guarded tool",
                Parameters = new JsonObject(),
                Output = new ToolOutputDefinition(
                    new JsonObject(),
                    (_, value) => [new TextBlock(value.GetProperty("ran").GetString()!)]),
                Execute = (_, _) =>
                {
                    _ran++;
                    return Task.FromResult<object?>(new { ran = "yes" });
                },
            });
            _harness.Ctx.On(ToolRuntime.PreExecuteEvent,
                (_, _) => new ValueTask<object?>(new PreToolDecision.Ask()),
                new EventOptions { Global = true });
        }

        public void Dispose() => _harness.Dispose();

        private Task<ToolExecutionResult> Execute(IAgent agent)
            => _harness.Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = "guarded",
                Arguments = JsonDocument.Parse("{}").RootElement,
                Agent = agent,
                Signal = default,
            });

        [Fact]
        public async Task Allowed_Tool_Executes()
        {
            ApprovalService.Register(_harness.Ctx);
            using var answerer = ApprovalAnswerers.AutoApprove(_harness.Ctx);
            var agent = CreateAgent(_harness);

            var result = await Execute(agent);

            Assert.False(result.IsError);
            Assert.Equal(1, _ran);
            Assert.Equal("yes", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Contains(agent.Session.SnapshotEvents(), e => e.Data is ApprovalAskedPayload);
            Assert.Contains(agent.Session.SnapshotEvents(), e => e.Data is ApprovalDecidedPayload);
        }

        [Fact]
        public async Task Rejected_Tool_Is_Denied()
        {
            ApprovalService.Register(_harness.Ctx);
            using var answerer = ApprovalAnswerers.DenyAll(_harness.Ctx);
            var agent = CreateAgent(_harness);

            var result = await Execute(agent);

            Assert.True(result.IsError);
            Assert.Equal(0, _ran);
            Assert.Contains("the user rejected tool", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        }

        [Fact]
        public async Task Unavailable_Channel_Is_Denied()
        {
            ApprovalService.Register(_harness.Ctx);
            var agent = CreateAgent(_harness);

            var result = await Execute(agent);

            Assert.True(result.IsError);
            Assert.Equal(0, _ran);
            Assert.Contains("no approval channel is available", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        }

        [Fact]
        public async Task Missing_Approval_Service_Is_Denied()
        {
            var agent = CreateAgent(_harness);

            var result = await Execute(agent);

            Assert.True(result.IsError);
            Assert.Equal(0, _ran);
            Assert.Contains("requires approval", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        }
    }
}
