using System.Text.Json;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Interaction.AskUser;
using Dsh.Llm;

namespace Dsh.Tests;

public class AskUserToolTests
{
    private sealed class Harness : IDisposable
    {
        public Context Ctx { get; } = new();
        public AgentRegistry Agents { get; }
        public ToolRuntime Tools { get; }

        public Harness()
        {
            Agents = new AgentRegistry(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            UserQuestionService.Register(Ctx);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeAgent(Context ctx, Session session) : IAgent
    {
        public FakeAgent(Context ctx) : this(ctx, Session.Create(SessionId.Create($"session-{Guid.NewGuid():N}")))
        {
        }

        public SessionId Id { get; } = session.Id;
        public Session Session { get; } = session;
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; } = ctx;
        public AgentStatus Status { get; set; } = AgentStatus.Running;
        public AgentOptions Options { get; } = new();

        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task WhenIdle() => Task.CompletedTask;
        public void Send(UserMessage message, string target, bool wakeup) { }
        public void Followup(UserMessage message) { }
        public void Steer(UserMessage message) { }
        public void Inject(UserMessage message) { }
    }

    private static Task<ToolExecutionResult> Execute(Harness harness, string argsJson, IAgent? agent = null, CancellationToken signal = default)
        => harness.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = "ask_user_question",
            Arguments = JsonDocument.Parse(argsJson).RootElement,
            Agent = agent,
            Signal = signal,
        });

    private static IDisposable Answerer(
        Harness harness,
        Func<AskUserQuestionRequest, AskUserQuestionAnswer> answer,
        bool global = false)
    {
        var dispose = harness.Ctx.OnWaterfall<UserQuestionsRequestNotification>(
            (notification, _) => ValueTask.FromResult<object?>(answer(notification.Request)),
            global ? new EventOptions { Global = true } : null);
        return new AnswererSubscription(dispose);
    }

    private sealed class AnswererSubscription(Func<bool> dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Fact]
    public void Registers_Model_Facing_Tool_Schema()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);

        var schema = Assert.Single(harness.Tools.Schemas(), tool => tool.Name == "ask_user_question");
        Assert.Equal("object", schema.Parameters["type"]!.GetValue<string>());
        var required = schema.Parameters["required"]!.AsArray()!;
        Assert.Equal("questions", Assert.Single(required)!.GetValue<string>());
        var itemProperties = schema.Parameters["properties"]!["questions"]!["items"]!["properties"]!;
        Assert.Equal("string", itemProperties["id"]!["type"]!.GetValue<string>());
        Assert.Equal("string", itemProperties["question"]!["type"]!.GetValue<string>());
        Assert.Equal("string", itemProperties["header"]!["type"]!.GetValue<string>());
        Assert.Equal("string", itemProperties["detail"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", itemProperties["allowCustom"]!["type"]!.GetValue<string>());
        Assert.Equal("array", itemProperties["options"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", itemProperties["multi_select"]!["type"]!.GetValue<string>());
        var optionProperties = itemProperties["options"]!["items"]!["properties"]!;
        Assert.Equal("string", optionProperties["label"]!["type"]!.GetValue<string>());
        Assert.Equal("string", optionProperties["description"]!["type"]!.GetValue<string>());
        Assert.Equal("string", optionProperties["value"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", optionProperties["disabled"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", optionProperties["default"]!["type"]!.GetValue<string>());
        Assert.Null(optionProperties["recommended"]);
        Assert.Null(optionProperties["preview"]);
    }

    [Fact]
    public async Task Asks_Provider_And_Projects_Structured_Answers_To_Text()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);
        var seen = new List<AskUserQuestionRequest>();
        using var answerer = Answerer(harness, request =>
        {
            seen.Add(request);
            return new AskUserQuestionAnswer([new AskUserQuestionAnswerItem("pkg", ["pnpm"])]);
        });

        var result = await Execute(harness, """
            { "questions": [{ "id": "pkg", "question": "Which package manager should I use?", "options": [{ "label": "pnpm", "description": "Use pnpm workspaces." }] }] }
            """, signal: TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal("""{"status":"answered","answers":[{"id":"pkg","selected":["pnpm"]}],"reason":null}""", Assert.IsType<TextBlock>(result.Content[0]).Text);
        var request = Assert.Single(seen);
        var question = Assert.Single(request.Questions);
        Assert.Equal("pkg", question.Id);
        Assert.Equal("Which package manager should I use?", question.Question);
        var option = Assert.Single(question.Options!);
        Assert.Equal("pnpm", option.Label);
        Assert.Equal("Use pnpm workspaces.", option.Description);
    }

    [Fact]
    public async Task Recommended_Option_Labels_Pass_Through_Without_Schema_Fields()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);
        var seen = new List<AskUserQuestionRequest>();
        using var answerer = Answerer(harness, request =>
        {
            seen.Add(request);
            return new AskUserQuestionAnswer([new AskUserQuestionAnswerItem("pkg", ["pnpm (Recommended)"])]);
        });

        await Execute(harness, """
            { "questions": [{ "id": "pkg", "question": "Which package manager should I use?", "options": [{ "label": "pnpm (Recommended)" }, { "label": "npm" }] }] }
            """, signal: TestContext.Current.CancellationToken);

        var options = Assert.Single(seen).Questions[0].Options!;
        Assert.Equal(["pnpm (Recommended)", "npm"], options.Select(option => option.Label).ToList());
        Assert.All(options, option => Assert.Null(option.Description));
    }

    [Fact]
    public async Task Projects_Custom_Answers_And_Multi_Select_Choices()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);
        AskUserQuestionRequest? seen = null;
        harness.Ctx.OnWaterfall<UserQuestionsRequestNotification>((notification, _) =>
        {
            seen = notification.Request;
            return ValueTask.FromResult<object?>(new AskUserQuestionAnswer(
            [
                new AskUserQuestionAnswerItem("targets", ["tests", "docs"], "release notes"),
                new AskUserQuestionAnswerItem("labels-only", ["tests"]),
                new AskUserQuestionAnswerItem("notes", [], "ship today"),
            ]));
        }, new EventOptions { Global = true, Prepend = true });

        var result = await Execute(harness, """
            { "questions": [
              { "id": "targets", "question": "What should I update?", "options": [{ "label": "tests" }, { "label": "docs" }], "multi_select": true },
              { "id": "labels-only", "question": "Which labels should I keep?", "options": [{ "label": "tests" }, { "label": "docs" }], "multi_select": true },
              { "id": "notes", "question": "Any note?" }
            ] }
            """, signal: TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        const string expected = """{"status":"answered","answers":[{"id":"targets","selected":["tests","docs"],"custom":"release notes"},{"id":"labels-only","selected":["tests"]},{"id":"notes","selected":[],"custom":"ship today"}],"reason":null}""";
        Assert.Equal(expected, Assert.IsType<TextBlock>(result.Content[0]).Text);
        Assert.Equal(expected, ((ToolExecutionResult.Success)result).Value.GetRawText());
        Assert.NotNull(seen);
        Assert.True(seen.Questions[0].MultiSelect);
        Assert.False(seen.Questions[2].MultiSelect);
    }

    [Fact]
    public async Task Passes_Stable_Values_Disabled_Default_And_Custom_Policy_To_Provider()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);
        AskUserQuestionRequest? seen = null;
        using var answerer = Answerer(harness, request =>
        {
            seen = request;
            return new AskUserQuestionAnswer([new AskUserQuestionAnswerItem("mode", ["safe"])]);
        });

        var result = await Execute(harness, """
            {"questions":[
              {"id":"mode","question":"Choose mode","detail":"Changes production settings.","allowCustom":false,
               "options":[{"label":"Safe","description":"Read only","value":"safe","default":true},
                          {"label":"Dangerous","value":"dangerous","disabled":true}]},
              {"id":"note","question":"Anything else?"}
            ]}
            """, signal: TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.NotNull(seen);
        var question = seen.Questions[0];
        Assert.False(question.AllowCustom);
        Assert.Equal("Changes production settings.", question.Detail);
        Assert.Equal(new AskUserQuestionOption("Safe", "Read only", "safe", Default: true), question.Options![0]);
        Assert.Equal(new AskUserQuestionOption("Dangerous", Value: "dangerous", Disabled: true), question.Options[1]);
        Assert.True(seen.Questions[1].AllowCustom);
        Assert.Equal("safe", Assert.Single(((ToolExecutionResult.Success)result).Value.GetProperty("answers")[0].GetProperty("selected").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Dismissed_Group_Is_A_Structured_Success_Without_Partial_Answers()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);
        using var answerer = Answerer(harness, _ =>
            new AskUserQuestionAnswer([new AskUserQuestionAnswerItem("first", ["A"])], "dismissed", "user_cancelled"));

        var result = await Execute(harness, """
            {"questions":[{"id":"first","question":"First?"},{"id":"second","question":"Second?"}]}
            """, signal: TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        const string expected = """{"status":"dismissed","answers":[],"reason":"user_cancelled"}""";
        Assert.Equal(expected, ((ToolExecutionResult.Success)result).Value.GetRawText());
        Assert.Equal(expected, Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task Provider_Failure_Remains_Distinct_From_User_Dismissal()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);
        harness.Ctx.OnWaterfall<UserQuestionsRequestNotification>((_, _) =>
            throw new InvalidOperationException("provider exploded"));

        var result = await Execute(harness, """{"questions":[{"id":"q","question":"Continue?"}]}""",
            signal: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("provider exploded", ((ToolExecutionResult.Failure)result).Error.Message);
    }

    [Fact]
    public async Task Tool_Signal_Reaches_The_User_Questions_Service()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);
        using var cts = new CancellationTokenSource();
        harness.Ctx.OnWaterfall<UserQuestionsRequestNotification>((_, _) =>
        {
            cts.Cancel();
            throw new InvalidOperationException("provider exploded");
        });

        var result = await Execute(harness, """{ "questions": [{ "id": "continue", "question": "Continue?" }] }""", signal: cts.Token);

        Assert.True(result.IsError);
        Assert.Equal(UserQuestionException.AskAborted, ((ToolExecutionResult.Failure)result).Error.Info?.Code);
    }

    [Fact]
    public async Task Passes_Header_And_Live_Root_Agent_Through()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);
        var seen = new List<AskUserQuestionRequest>();
        using var answerer = Answerer(harness, request =>
        {
            seen.Add(request);
            return new AskUserQuestionAnswer([new AskUserQuestionAnswerItem("continue", ["ok"])]);
        }, global: true);
        var agent = new FakeAgent(harness.Ctx);
        harness.Agents.Register(agent);

        var result = await Execute(harness, """{ "questions": [{ "id": "continue", "header": "Confirm", "question": "Continue?" }] }""", agent, TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal("""{"status":"answered","answers":[{"id":"continue","selected":["ok"]}],"reason":null}""", Assert.IsType<TextBlock>(result.Content[0]).Text);
        var request = Assert.Single(seen);
        Assert.Same(agent, request.Agent);
        Assert.Equal("Confirm", Assert.Single(request.Questions).Header);
    }

    [Fact]
    public async Task No_Provider_Returns_Structured_Error()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);

        var result = await Execute(harness, """{ "questions": [{ "id": "continue", "question": "Continue?" }] }""", signal: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal(UserQuestionException.NoProvider, ((ToolExecutionResult.Failure)result).Error.Info?.Code);
    }

    [Fact]
    public async Task Delegated_Caller_Returns_Structured_Error()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);
        var seen = new List<AskUserQuestionRequest>();
        using var answerer = Answerer(harness, request =>
        {
            seen.Add(request);
            return new AskUserQuestionAnswer([new AskUserQuestionAnswerItem("continue", ["ok"])]);
        }, global: true);
        var root = new FakeAgent(harness.Ctx);
        var child = new FakeAgent(harness.Ctx);
        harness.Agents.Register(root);
        harness.Agents.Enter(child, root);

        var result = await Execute(harness, """{ "questions": [{ "id": "continue", "question": "Continue?" }] }""", child, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var failure = (ToolExecutionResult.Failure)result;
        Assert.Equal(UserQuestionException.DelegatedCaller, failure.Error.Info?.Code);
        Assert.Equal(
            "Error: human interaction is unavailable while the calling agent is owned by another live agent; include the unresolved question or decision in the child agent's final result",
            Assert.IsType<TextBlock>(result.Content[0]).Text);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task Empty_Question_Batches_Return_Structured_Error()
    {
        using var harness = new Harness();
        using var tool = AskUserTool.Register(harness.Ctx);

        var result = await Execute(harness, """{ "questions": [] }""", signal: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal(UserQuestionException.EmptyQuestions, ((ToolExecutionResult.Failure)result).Error.Info?.Code);
    }

    [Fact]
    public void Disposing_The_Registration_Removes_The_Tool()
    {
        using var harness = new Harness();
        var tool = AskUserTool.Register(harness.Ctx);
        Assert.NotNull(harness.Tools.Get("ask_user_question"));

        tool.Dispose();

        Assert.Null(harness.Tools.Get("ask_user_question"));
    }
}
