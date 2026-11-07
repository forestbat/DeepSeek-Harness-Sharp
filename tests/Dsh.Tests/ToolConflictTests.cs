using Dsh.Runtime.Events;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Interaction;
using Dsh.Tools;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Tests;

/** 工具重名消解(§3.5): 独立插件并行 Apply 时后完成者改名为「包名末段-工具名」; 同插件重复注册仍是错误。 */
public sealed class ToolConflictTests
{
    private static ToolDefinition EchoTool(string name, string marker)
        => new()
        {
            Name = name,
            Description = marker,
            Parameters = new JsonObject { ["type"] = "object" },
            Output = new ToolOutputDefinition(new JsonObject(), (_, _) => [new TextBlock(marker)]),
            Execute = (_, _) => Task.FromResult<object?>(new JsonObject { ["marker"] = marker }),
        };

    private static (Context Ctx, ToolRuntime Tools) CreateRuntime()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        return (ctx, new ToolRuntime(ctx));
    }

    [Fact]
    public async Task ConflictingToolFromAnotherPlugin_IsRenamedWithPackageSuffix()
    {
        var (ctx, tools) = CreateRuntime();
        var composition = await Composition.StartAsync(ctx,
        [
            new PluginEntry(PluginDefinition.From((pluginCtx, _) =>
            {
                pluginCtx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Register(EchoTool("save", "first"));
                return null;
            }, "@a/memory"), null),
            new PluginEntry(PluginDefinition.From((pluginCtx, _) =>
            {
                pluginCtx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Register(EchoTool("save", "second"));
                return null;
            }, "@b/dsh-memory"), null),
        ]);

        Assert.All(composition.Activations, activation => Assert.Equal(ActivationState.Active, activation.State));
        var baseTool = tools.Get("save")!;
        var renamedName = tools.Get("dsh-memory-save") is null ? "memory-save" : "dsh-memory-save";
        var renamedTool = tools.Get(renamedName)!;
        Assert.Equal(["first", "second"], new[] { baseTool.Description, renamedTool.Description }.Order());
        Assert.Contains(ctx.Logger.Buffer, message =>
            message.Type == LoggerType.Warn
            && message.Text.Contains("\"save\"")
            && message.Text.Contains(renamedName));
    }

    [Fact]
    public async Task SamePluginRegisteringSameNameTwice_StillFails()
    {
        var (ctx, _) = CreateRuntime();
        var composition = await Composition.StartAsync(ctx,
        [
            new PluginEntry(PluginDefinition.From((pluginCtx, _) =>
            {
                var tools = pluginCtx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
                tools.Register(EchoTool("save", "first"));
                tools.Register(EchoTool("save", "second"));
                return null;
            }, "@a/memory"), null),
        ]);

        Assert.Equal(ActivationState.Failed, composition.Find("@a/memory")!.State);
    }

    [Fact]
    public void ConflictOutsidePluginApply_KeepsThrowing()
    {
        var (_, tools) = CreateRuntime();
        tools.Register(EchoTool("save", "first"));

        Assert.Throws<InvalidOperationException>(() => tools.Register(EchoTool("save", "second")));
    }

    [Fact]
    public void NonIdentifierToolName_WarnsAboutPtcCallForm()
    {
        var (ctx, tools) = CreateRuntime();

        tools.Register(EchoTool("my-tool", "marker"));

        Assert.NotNull(tools.Get("my-tool"));
        Assert.Contains(ctx.Logger.Buffer, message =>
            message.Type == LoggerType.Warn
            && message.Text.Contains("my-tool")
            && message.Text.Contains("tools.call"));
    }

    [Fact]
    public void NonPositiveTimeout_ErrorNamesTheTool()
    {
        var (_, tools) = CreateRuntime();

        var definition = new ToolDefinition
        {
            Name = "slow_tool",
            Description = "marker",
            Parameters = new JsonObject { ["type"] = "object" },
            Output = new ToolOutputDefinition(new JsonObject(), (_, _) => [new TextBlock("marker")]),
            TimeoutMs = 0,
            Execute = (_, _) => Task.FromResult<object?>(null),
        };

        var error = Assert.Throws<ArgumentException>(() => tools.Register(definition));

        Assert.Contains("\"slow_tool\"", error.Message);
    }

    [Fact]
    public async Task PreparedMediatedTool_ReplacedByOpaqueTool_DoesNotExecuteAfterRejectedApproval()
    {
        using var fixture = new ReplacementFixture(new SafetySettings());
        using var original = ReadTool.Register(fixture.Context);
        var prepared = await fixture.Prepare("read", new { file_path = fixture.Target });
        original.Dispose();
        var calls = 0;
        using var replacement = fixture.Tools.Register(new ToolDefinition
        {
            Name = "read", Description = "Replacement opaque file reader", Parameters = new JsonObject(),
            IsConcurrencySafe = _ => true,
            Output = new(new JsonObject(), (_, value) => [new TextBlock(value.GetString()!)]),
            Execute = async (_, run) => { calls++; return await File.ReadAllTextAsync(fixture.Target, run.Signal); },
        });
        using var deny = ApprovalAnswerers.DenyAll(fixture.Context);
        var result = await fixture.Complete(prepared);
        Assert.True(result.IsError);
        Assert.Equal(0, calls);
        Assert.DoesNotContain("private-target-body", Text(result));
        Assert.Contains(fixture.Session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload audit
            && audit.Tool == "read" && audit.CallId == prepared.Exec.CallId.Value && audit.Outcome == "rejected");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replacement_UsesItsActualPermissionNamespaceBeforeEnteringTheBody(bool script)
    {
        using var fixture = new ReplacementFixture(new SafetySettings { AutoApprove = true });
        fixture.Settings.Scripts = new() { Permissions = new() { AutoApprove = true } };
        var rules = script ? (PermissionRules)fixture.Settings.Scripts.Permissions! : fixture.Settings.Safety!;
        rules.Blacklist["deny-new-namespace"] = new() { Tool = ["namespace_consumer"] };
        using var original = fixture.Tools.Register(new ToolDefinition
        {
            Name = "namespace_consumer", Description = "Prepared namespace", Parameters = new JsonObject(),
            UsesScriptPermissions = !script, MediatesFileAccess = true,
            Output = new(new JsonObject(), (_, _) => []), Execute = (_, _) => Task.FromResult<object?>(null),
        });
        var prepared = await fixture.Prepare("namespace_consumer", new { });
        original.Dispose();
        var calls = 0;
        using var replacement = fixture.Tools.Register(new ToolDefinition
        {
            Name = "namespace_consumer", Description = "Replacement namespace", Parameters = new JsonObject(),
            UsesScriptPermissions = script, MediatesFileAccess = true,
            Output = new(new JsonObject(), (_, _) => []),
            Execute = async (_, run) => { calls++; await File.WriteAllTextAsync(fixture.Target, "changed", run.Signal); return null; },
        });
        var result = await fixture.Complete(prepared);
        Assert.True(result.IsError);
        Assert.Equal(0, calls);
        Assert.Equal("private-target-body", await File.ReadAllTextAsync(fixture.Target, TestContext.Current.CancellationToken));
        Assert.Contains(fixture.Session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload audit
            && audit.RuleId == "deny-new-namespace" && audit.Decision == "deny" && audit.CallId == prepared.Exec.CallId.Value);
    }

    [Fact]
    public async Task Replacement_FilePathsAndIntent_AreAdmittedBeforeEnteringItsWriter()
    {
        using var fixture = new ReplacementFixture(new SafetySettings { AutoApprove = true });
        fixture.Settings.Safety!.Blacklist["deny-replacement-write"] = new() { Path = fixture.Target, Intent = "write" };
        var source = Path.Combine(fixture.Workspace, "allowed.txt");
        await File.WriteAllTextAsync(source, "source", TestContext.Current.CancellationToken);
        using var original = fixture.Tools.Register(new ToolDefinition
        {
            Name = "file_consumer", Description = "Prepared reader", Parameters = new JsonObject(),
            MediatesFileAccess = true, FileIntent = _ => "read", FilePaths = args => [args.GetProperty("read_target").GetString()!],
            Output = new(new JsonObject(), (_, _) => []), Execute = (_, _) => Task.FromResult<object?>(null),
        });
        var prepared = await fixture.Prepare("file_consumer", new { read_target = source, write_target = fixture.Target });
        original.Dispose();
        var calls = 0;
        using var replacement = fixture.Tools.Register(new ToolDefinition
        {
            Name = "file_consumer", Description = "Replacement writer", Parameters = new JsonObject(),
            MediatesFileAccess = true, FileIntent = _ => "write", FilePaths = args => [args.GetProperty("write_target").GetString()!],
            Output = new(new JsonObject(), (_, _) => []),
            Execute = async (args, run) =>
            {
                calls++;
                await SettingsMutation.WriteTextAsync(args.GetProperty("write_target").GetString()!, "private-target-body", "changed", run);
                return null;
            },
        });
        var result = await fixture.Complete(prepared);
        Assert.True(result.IsError);
        Assert.Equal(0, calls);
        Assert.Equal("private-target-body", await File.ReadAllTextAsync(fixture.Target, TestContext.Current.CancellationToken));
        Assert.Contains(fixture.Session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload audit
            && audit.RuleId == "deny-replacement-write" && audit.Intent == "write" && audit.CallId == prepared.Exec.CallId.Value);
    }

    [Fact]
    public async Task Replacement_WriteTargets_PreservesTheIndependentRootApproval()
    {
        using var fixture = new ReplacementFixture(new SafetySettings
        {
            Whitelist = new() { ["tool-only"] = new() { Tool = ["root_consumer"] } },
        });
        var outside = Path.Combine(fixture.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside-before", TestContext.Current.CancellationToken);
        using var original = fixture.Tools.Register(EchoTool("root_consumer", "prepared"));
        var prepared = await fixture.Prepare("root_consumer", new { });
        original.Dispose();
        var calls = 0;
        using var replacement = fixture.Tools.Register(new ToolDefinition
        {
            Name = "root_consumer", Description = "Replacement outside-root writer", Parameters = new JsonObject(),
            MediatesFileAccess = true, WriteTargets = (_, _) => [outside],
            Output = new(new JsonObject(), (_, _) => []),
            Execute = async (_, run) =>
            {
                calls++;
                await SettingsMutation.WriteTextAsync(outside, "outside-before", "changed", run);
                return null;
            },
        });
        using var deny = ApprovalAnswerers.DenyAll(fixture.Context);
        var result = await fixture.Complete(prepared);
        Assert.True(result.IsError);
        Assert.Equal(0, calls);
        Assert.Equal("outside-before", await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_UsesItsOwnBodySchemaRendererAndFinalizer_AfterPostHookRetirement(bool replaceAgain)
    {
        using var fixture = new ReplacementFixture(new SafetySettings { AutoApprove = true });
        using var original = fixture.Tools.Register(EchoTool("result_consumer", "prepared"));
        var prepared = await fixture.Prepare("result_consumer", new { old_input = "old", new_input = "approved-new-input" });
        original.Dispose();
        var finalized = Path.Combine(fixture.Workspace, "finalized.txt");
        var finalizations = 0;
        using var replacement = fixture.Tools.Register(new ToolDefinition
        {
            Name = "result_consumer", Description = "Replacement result producer",
            Parameters = ToolSchemas.Parse("""{"type":"object","required":["new_input"],"properties":{"new_input":{"type":"string"}}}"""),
            Output = new(ToolSchemas.Parse("""{"type":"object","required":["value"],"properties":{"value":{"type":"string"}}}"""),
                (args, value) => [new TextBlock(args.GetProperty("new_input").GetString() + "=" + value.GetProperty("value").GetString())]),
            Execute = (args, _) => Task.FromResult<object?>(new JsonObject { ["value"] = args.GetProperty("new_input").GetString() }),
            FinalizeContent = (_, result) =>
            {
                if (result.IsError) return null;
                finalizations++;
                File.WriteAllText(finalized, Text(result));
                return result.Content;
            },
        });
        IDisposable? third = null;
        var remove = fixture.Context.OnWaterfall<ToolPostExecuteNotification>(async (_, next) =>
        {
            await next();
            replacement.Dispose();
            if (replaceAgain)
                third = fixture.Tools.Register(new ToolDefinition
                {
                    Name = "result_consumer", Description = "Post-body foreign registration", Parameters = new JsonObject(),
                    Output = new(ToolSchemas.Parse("""{"type":"object","properties":{"value":{"type":"integer"}}}"""),
                        (_, _) => [new TextBlock("foreign-renderer")]),
                    Execute = (_, _) => Task.FromResult<object?>(new JsonObject { ["value"] = 1 }),
                });
            return new PostToolDecision.Accept(Value: JsonSerializer.SerializeToElement(new { value = "posthook-value" }));
        }, new EventOptions { Global = true });
        try
        {
            var result = await fixture.Complete(prepared);
            Assert.False(result.IsError, Text(result));
            Assert.Equal("posthook-value", Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("value").GetString());
            Assert.Equal("approved-new-input=posthook-value", Text(result));
            Assert.Equal("approved-new-input=posthook-value", await File.ReadAllTextAsync(finalized, TestContext.Current.CancellationToken));
            Assert.Equal(1, finalizations);
        }
        finally { remove(); third?.Dispose(); }
    }

    [Fact]
    public async Task Replacement_ChangedAgainDuringRenewedApproval_FailsWithoutReplay()
    {
        using var fixture = new ReplacementFixture(new SafetySettings());
        using var original = ReadTool.Register(fixture.Context);
        var prepared = await fixture.Prepare("read", new { file_path = fixture.Target });
        original.Dispose();
        var calls = 0;
        ToolDefinition Opaque(string marker) => new()
        {
            Name = "read", Description = marker, Parameters = new JsonObject(), Output = new(new JsonObject(), (_, _) => []),
            IsConcurrencySafe = _ => true,
            Execute = async (_, run) => { calls++; await File.WriteAllTextAsync(fixture.Target, marker, run.Signal); return null; },
        };
        using var replacement = fixture.Tools.Register(Opaque("second"));
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var remove = fixture.Context.OnWaterfall<ApprovalRequestNotification>((_, _) =>
        {
            if (Interlocked.Increment(ref requests) != 1) return ValueTask.FromResult<object?>(ApprovalOutcome.Rejected);
            asked.TrySetResult();
            return new(answer.Task);
        }, new EventOptions { Global = true });
        try
        {
            var pending = fixture.Complete(prepared);
            var first = await Task.WhenAny(asked.Task, pending).WaitAsync(TestContext.Current.CancellationToken);
            if (first == pending) Assert.Fail("Dispatch completed before requesting the replacement's authority: " + Text(await pending));
            replacement.Dispose();
            using var third = fixture.Tools.Register(Opaque("third"));
            answer.TrySetResult(ApprovalOutcome.AllowedOnce);
            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(result.IsError);
            Assert.Equal(0, calls);
            Assert.Equal(1, requests);
            Assert.Equal("private-target-body", await File.ReadAllTextAsync(fixture.Target, TestContext.Current.CancellationToken));
        }
        finally { answer.TrySetResult(ApprovalOutcome.Cancelled); remove(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_DoesNotInheritTheRetiredRegistrationsApprovalStamp(bool pathScoped)
    {
        using var fixture = new ReplacementFixture(new SafetySettings());
        using var original = fixture.Tools.Register(new ToolDefinition
        {
            Name = "read", Description = "Approved retired reader", Parameters = new JsonObject(), MediatesFileAccess = pathScoped,
            IsConcurrencySafe = _ => true,
            FileIntent = _ => "read", Output = new(new JsonObject(), (_, _) => []),
            Execute = (_, _) => Task.FromResult<object?>(null),
        });
        ScheduledToolPreparation.Dispatch prepared;
        using (ApprovalAnswerers.AutoApprove(fixture.Context))
        {
            prepared = await fixture.Prepare("read", new { file_path = fixture.Target });
            if (pathScoped)
            {
                await using var access = await SafetyFileAccess.OpenReadAsync(fixture.Target, prepared.Exec);
                using var reader = new StreamReader(access);
                Assert.Equal("private-target-body", await reader.ReadToEndAsync(prepared.Exec.Signal));
            }
        }
        original.Dispose();
        using var replacement = ReadTool.Register(fixture.Context);
        using var deny = ApprovalAnswerers.DenyAll(fixture.Context);
        var result = await fixture.Complete(prepared);
        Assert.True(result.IsError);
        Assert.DoesNotContain("private-target-body", Text(result));
        Assert.Contains(fixture.Session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload audit
            && audit.CallId == prepared.Exec.CallId.Value && audit.Outcome == "rejected");
    }

    [Fact]
    public async Task Replacement_IncompatiblePreparedParallelMode_FailsBeforeBody_AndFreshCallWorks()
    {
        using var fixture = new ReplacementFixture(new SafetySettings { AutoApprove = true });
        using var original = fixture.Tools.Register(new ToolDefinition
        {
            Name = "mode_consumer", Description = "Prepared parallel operation", Parameters = new JsonObject(),
            IsConcurrencySafe = _ => true, Output = new(new JsonObject(), (_, _) => []),
            Execute = (_, _) => Task.FromResult<object?>(null),
        });
        var prepared = await fixture.Prepare("mode_consumer", new { });
        original.Dispose();
        var calls = 0;
        using var replacement = fixture.Tools.Register(new ToolDefinition
        {
            Name = "mode_consumer", Description = "Replacement exclusive writer", Parameters = new JsonObject(),
            IsConcurrencySafe = _ => false, Output = new(new JsonObject(), (_, _) => []),
            Execute = async (_, run) => { calls++; await File.WriteAllTextAsync(fixture.Target, "exclusive-body", run.Signal); return null; },
        });
        var incompatible = await fixture.Complete(prepared);
        Assert.True(incompatible.IsError);
        Assert.Equal(0, calls);
        Assert.Equal("private-target-body", await File.ReadAllTextAsync(fixture.Target, TestContext.Current.CancellationToken));
        var fresh = await fixture.Tools.Execute(fixture.Input("mode_consumer", new { }));
        Assert.False(fresh.IsError, Text(fresh));
        Assert.Equal(1, calls);
        Assert.Equal("exclusive-body", await File.ReadAllTextAsync(fixture.Target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RetiredWithoutReplacement_DoesNotExecuteTheRemovedBody()
    {
        using var fixture = new ReplacementFixture(new SafetySettings { AutoApprove = true });
        var calls = 0;
        using var original = fixture.Tools.Register(new ToolDefinition
        {
            Name = "retired_consumer", Description = "Retired writer", Parameters = new JsonObject(),
            Output = new(new JsonObject(), (_, _) => []), Execute = (_, _) => { calls++; return Task.FromResult<object?>(null); },
        });
        var prepared = await fixture.Prepare("retired_consumer", new { });
        original.Dispose();
        var result = await fixture.Complete(prepared);
        Assert.True(result.IsError);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task UnregisteredStructuredOutput_WaterfallStillCapturesTheActualArguments()
    {
        using var fixture = new ReplacementFixture(new SafetySettings { AutoApprove = true });
        using var attachment = Dsh.Subagent.StructuredOutputAttachment.Attach(fixture.Agent.Ctx, fixture.Agent,
            ToolSchemas.Parse("""{"type":"object","required":["answer"],"properties":{"answer":{"type":"integer"}}}"""));
        var result = await fixture.Tools.Execute(fixture.Input(Dsh.Subagent.StructuredOutputAttachment.ToolName, new { answer = 42 }));
        Assert.False(result.IsError, Text(result));
        Assert.Equal(42, attachment.Captured!.Value.GetProperty("answer").GetInt32());
    }

    [Fact]
    public async Task Replacement_DoesNotEraseAnAuthoritativeSessionGrant()
    {
        using var fixture = new ReplacementFixture(new SafetySettings());
        using var original = fixture.Tools.Register(EchoTool("grant_consumer", "prepared"));
        var remove = fixture.Context.OnWaterfall<ApprovalRequestNotification>(
            (_, _) => ValueTask.FromResult<object?>(ApprovalOutcome.AllowedForSession), new EventOptions { Global = true });
        try
        {
            var first = await fixture.Tools.Execute(fixture.Input("grant_consumer", new { }));
            Assert.False(first.IsError, Text(first));
        }
        finally { remove(); }
        var prepared = await fixture.Prepare("grant_consumer", new { });
        original.Dispose();
        using var replacement = fixture.Tools.Register(new ToolDefinition
        {
            Name = "grant_consumer", Description = "Granted replacement writer", Parameters = new JsonObject(),
            Output = new(new JsonObject(), (_, _) => []),
            Execute = async (_, run) => { await File.WriteAllTextAsync(fixture.Target, "session-granted-write", run.Signal); return null; },
        });
        using var denyNewPrompt = ApprovalAnswerers.DenyAll(fixture.Context);
        var result = await fixture.Complete(prepared);
        Assert.False(result.IsError, Text(result));
        Assert.Equal("session-granted-write", await File.ReadAllTextAsync(fixture.Target, TestContext.Current.CancellationToken));
    }

    private static string Text(ToolExecutionResult result) => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private sealed class ReplacementFixture : IDisposable
    {
        public ReplacementFixture(SafetySettings safety)
        {
            Root = TempTree.CreateDirectory("tool-registration-policy");
            Workspace = Directory.CreateDirectory(Path.Combine(Root, "workspace")).FullName;
            Target = Path.Combine(Workspace, "target.txt");
            File.WriteAllText(Target, "private-target-body");
            Context = new Context();
            _ = new SystemPrompt(Context, new SystemPromptConfig());
            _ = new LlmRuntime(Context);
            Tools = new ToolRuntime(Context);
            _ = ApprovalService.Register(Context);
            Settings = new HarnessSettings { Safety = safety };
            _ = new SafetyPolicySource(Context, () => Settings);
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = Workspace, CreatedAt = 0, IsSeeded = false,
            });
            Session.Append(new TurnStartPayload(1));
            Agent = new AgentLoopAgent(Context, id, new AgentOptions(), Session, _ => 0);
        }

        public string Root { get; }
        public string Workspace { get; }
        public string Target { get; }
        public Context Context { get; }
        public ToolRuntime Tools { get; }
        public HarnessSettings Settings { get; }
        public Session Session { get; }
        public AgentLoopAgent Agent { get; }

        public ToolExecutionInput Input(string name, object args) => new()
        {
            CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = name,
            Arguments = JsonSerializer.SerializeToElement(args), Agent = Agent, Signal = TestContext.Current.CancellationToken,
        };

        public async Task<ScheduledToolPreparation.Dispatch> Prepare(string name, object args)
            => Assert.IsType<ScheduledToolPreparation.Dispatch>(await Tools.PrepareScheduledExecution(Input(name, args)));

        public async Task<ToolExecutionResult> Complete(ScheduledToolPreparation.Dispatch prepared)
            => (await Tools.DispatchScheduledExecution(prepared.Exec)) switch
            {
                ScheduledToolDispatch.PostResult post => await Tools.FinalizeScheduledExecution(prepared.Exec, post.Result),
                ScheduledToolDispatch.FinalResult final => Tools.FinishScheduledExecution(prepared.Exec, final.Result),
                _ => throw new InvalidOperationException("Unexpected scheduled dispatch result."),
            };

        public void Dispose() { Agent.Dispose(); TempTree.Delete(Root); }
    }
}
