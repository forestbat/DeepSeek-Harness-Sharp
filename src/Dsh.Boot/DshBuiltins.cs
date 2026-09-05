using Cordis;
using Dsh.Compaction;
using Dsh.Core;
using Dsh.Goal;
using Dsh.Interaction;
using Dsh.Interaction.AskUser;
using Dsh.Jobs;
using Dsh.PlanMode;
using Dsh.Skills;
using Dsh.Subagent;
using Dsh.Terminal;
using Dsh.Tools;
using Dsh.Web;
using Dsh.Workflow;

namespace Dsh.Boot;

// cordis.yml 里 `@deepseek-ai/...` 包名到本地已移植实现的映射;未命中的包名由 DshModuleImporter 回退给 NodeImporter。
public static class DshBuiltins
{
    public const string ToolBash = "@deepseek-ai/dsh-tool-bash";
    public const string ToolPwsh = "@deepseek-ai/dsh-tool-pwsh";
    public const string ToolFs = "@deepseek-ai/dsh-tool-fs";
    public const string ToolFsSearch = "@deepseek-ai/dsh-tool-fs-search";
    public const string ToolTodo = "@deepseek-ai/dsh-tool-todo";
    public const string ToolStrReplaceEditor = "@deepseek-ai/dsh-tool-str-replace-editor";
    public const string FsLocal = "@deepseek-ai/dsh-fs-local";
    public const string Persona = "@deepseek-ai/dsh-persona";
    public const string TokenMeter = "@deepseek-ai/dsh-token-meter";
    public const string CompactionBasic = "@deepseek-ai/dsh-compaction-basic";
    public const string CompactionToolResultPruner = "@deepseek-ai/dsh-compaction-tool-result-pruner";
    public const string CommandCompact = "@deepseek-ai/dsh-command-compact";
    public const string JobsLocal = "@deepseek-ai/dsh-jobs-local";
    public const string ToolJobs = "@deepseek-ai/dsh-tool-jobs";
    public const string ToolBashPersistent = "@deepseek-ai/dsh-tool-bash-persistent";
    public const string Subagent = "@deepseek-ai/dsh-subagent";
    public const string SubagentSpawnInProcess = "@deepseek-ai/dsh-subagent-spawn-in-process";
    public const string SubagentForkInProcess = "@deepseek-ai/dsh-subagent-fork-in-process";
    public const string ToolSubagent = "@deepseek-ai/dsh-tool-subagent";
    public const string ToolSubagentControl = "@deepseek-ai/dsh-tool-subagent-control";
    public const string ToolSubagentControlListAgents = "@deepseek-ai/dsh-tool-subagent-control/list-agents";
    public const string Skill = "@deepseek-ai/dsh-skill";
    public const string SkillFilesystem = "@deepseek-ai/dsh-skill-filesystem";
    public const string Goal = "@deepseek-ai/dsh-goal";
    public const string ToolGoal = "@deepseek-ai/dsh-tool-goal";
    public const string ToolAskUser = "@deepseek-ai/dsh-tool-ask-user";
    public const string PlanMode = "@deepseek-ai/dsh-plan-mode";
    public const string Web = "@deepseek-ai/dsh-web";
    public const string WebFetchHttp = "@deepseek-ai/dsh-web-fetch-http";
    public const string WebSearchDeepseek = "@deepseek-ai/dsh-web-search-deepseek";
    public const string ToolWeb = "@deepseek-ai/dsh-tool-web";
    public const string ToolWorkflow = "@deepseek-ai/dsh-tool-workflow";
    public const string ToolRalph = "@deepseek-ai/dsh-tool-ralph";
    public const string WorkflowWorkerThread = "@deepseek-ai/dsh-workflow-worker-thread";
    public const string Terminal = "@deepseek-ai/dsh-terminal";
    public const string TerminalBash = "@deepseek-ai/dsh-terminal-bash";
    public const string ToolTerminal = "@deepseek-ai/dsh-tool-terminal";

    public static IReadOnlyDictionary<string, PluginDefinition> All { get; } = new Dictionary<string, PluginDefinition>
    {
        [ToolBash] = Define(ToolBash, [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SubprocessService.ServiceName],
            (ctx, _) => BashTool.Register(ctx)),
        [ToolPwsh] = Define(ToolPwsh, [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SubprocessService.ServiceName],
            (ctx, _) => PwshTool.Register(ctx)),
        [ToolFs] = Define(ToolFs, [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
            (ctx, _) => new DisposableBundle(ReadTool.Register(ctx), WriteTool.Register(ctx), EditTool.Register(ctx))),
        [ToolFsSearch] = Define(ToolFsSearch, [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
            (ctx, _) => new DisposableBundle(GlobTool.Register(ctx), GrepTool.Register(ctx))),
        [ToolTodo] = Define(ToolTodo, [ToolRuntime.ServiceName],
            (ctx, config) => TodoWriteTool.Register(ctx, ConfigOf(config)?.GetValueOrDefault("allowParallelInProgress") as bool? ?? true)),
        [ToolStrReplaceEditor] = Define(ToolStrReplaceEditor, [ToolRuntime.ServiceName],
            (ctx, config) => StrReplaceEditorTool.Register(ctx, StrReplaceEditorConfigFrom(config))),
        [FsLocal] = Define(FsLocal, [],
            (ctx, config) => LocalFsService.Register(ctx, ConfigOf(config)?.GetValueOrDefault("cwd") as string)),
        [Persona] = Define(Persona, [SystemPrompt.ServiceName], ApplyPersona),
        [TokenMeter] = Define(TokenMeter, [],
            (ctx, _) => Compaction.TokenMeter.Register(ctx)),
        [CompactionToolResultPruner] = Define(CompactionToolResultPruner, [Compaction.TokenMeter.ServiceName],
            (ctx, config) => ToolResultPruner.Register(ctx, PruneConfigFrom(config))),
        [CompactionBasic] = Define(CompactionBasic,
            [LlmRuntime.ServiceName, Compaction.TokenMeter.ServiceName, SessionStore.ServiceName],
            (ctx, config) => BasicCompactionEngine.Register(ctx, BasicCompactionConfigFrom(config))),
        [CommandCompact] = Define(CommandCompact,
            [CommandsService.ServiceName, CompactionEngine.ServiceName],
            (ctx, _) => Compaction.CompactCommand.Register(ctx)),
        [JobsLocal] = Define(JobsLocal, [],
            (ctx, config) =>
            {
                _ = new LocalJobsService(ctx, new LocalJobsConfig
                {
                    MaxConcurrentJobsPerOwner = IntOf(ConfigOf(config), "maxConcurrentJobsPerOwner") ?? LocalJobsConfig.DefaultMaxConcurrentJobsPerOwner,
                });
                return new DisposableBundle();
            }),
        [ToolJobs] = Define(ToolJobs, [ToolRuntime.ServiceName, SystemPrompt.ServiceName, JobsService.ServiceName],
            (ctx, config) =>
            {
                var dict = ConfigOf(config);
                return Jobs.ToolJobs.Register(ctx, new ToolJobsConfig
                {
                    WaitTimeoutMs = LongOf(dict, "waitTimeoutMs") ?? new ToolJobsConfig().WaitTimeoutMs,
                    MaxWaitTimeoutMs = LongOf(dict, "maxWaitTimeoutMs") ?? new ToolJobsConfig().MaxWaitTimeoutMs,
                    CompletionDelivery = dict?.GetValueOrDefault("completionDelivery") as string == "quiet"
                        ? CompletionDelivery.Quiet : new ToolJobsConfig().CompletionDelivery,
                    MaxConsecutiveWakes = IntOf(dict, "maxConsecutiveWakes") ?? new ToolJobsConfig().MaxConsecutiveWakes,
                });
            }),
        [ToolBashPersistent] = Define(ToolBashPersistent, [ToolRuntime.ServiceName, SubprocessService.ServiceName],
            (ctx, config) =>
            {
                var dict = ConfigOf(config);
                return PersistentBashTool.Register(ctx, new PersistentBashConfig
                {
                    BashPath = dict?.GetValueOrDefault("bashPath") as string,
                    TimeoutMs = LongOf(dict, "timeoutMs") ?? new PersistentBashConfig().TimeoutMs,
                    MaxOutputChars = IntOf(dict, "maxOutputChars") ?? new PersistentBashConfig().MaxOutputChars,
                    Description = dict?.GetValueOrDefault("description") as string ?? PersistentBashConfig.DefaultDescription,
                });
            }),
        [Subagent] = Define(Subagent, [SystemPrompt.ServiceName],
            (ctx, _) =>
            {
                SubagentRuntime.Register(ctx);
                return new DisposableBundle();
            }),
        [SubagentSpawnInProcess] = Define(SubagentSpawnInProcess, [SubagentRuntime.ServiceName],
            (ctx, config) => SubagentInProcessProviders.RegisterSpawn(ctx, ConfigOf(config)?.GetValueOrDefault("providerName") as string)),
        [SubagentForkInProcess] = Define(SubagentForkInProcess, [SubagentRuntime.ServiceName],
            (ctx, config) => SubagentInProcessProviders.RegisterFork(ctx, ConfigOf(config)?.GetValueOrDefault("providerName") as string)),
        [ToolSubagent] = Define(ToolSubagent, [ToolRuntime.ServiceName, SubagentRuntime.ServiceName, LlmRuntime.ServiceName],
            (ctx, config) => SubagentTool.Apply(ctx, config)),
        [ToolSubagentControl] = Define(ToolSubagentControl, [ToolRuntime.ServiceName],
            (ctx, _) => SubagentControlTools.Apply(ctx)),
        [ToolSubagentControlListAgents] = Define(ToolSubagentControlListAgents, [ToolRuntime.ServiceName],
            (ctx, _) => SubagentControlTools.ApplyListAgents(ctx)),
        [Skill] = Define(Skill, [],
            (ctx, config) =>
            {
                _ = new SkillRegistry(ctx, SkillRegistryConfigFrom(config));
                return new DisposableBundle();
            }),
        [SkillFilesystem] = Define(SkillFilesystem, [SkillRegistry.ServiceName],
            (ctx, config) => Skills.SkillFilesystem.Apply(ctx, SkillFilesystemConfigFrom(config))),
        [Goal] = Define(Goal, [AgentRegistry.ServiceName, SessionProjectionRegistry.ServiceName],
            (ctx, config) =>
            {
                _ = new GoalService(ctx, GoalServiceConfigFrom(config));
                return new DisposableBundle();
            }),
        [ToolGoal] = Define(ToolGoal,
            [ToolRuntime.ServiceName, SystemPrompt.ServiceName, AgentRegistry.ServiceName, SessionProjectionRegistry.ServiceName, GoalService.ServiceName],
            (ctx, config) => GoalTools.Apply(ctx, GoalToolsConfigFrom(config))),
        [ToolAskUser] = Define(ToolAskUser, [ToolRuntime.ServiceName, UserQuestionService.ServiceName],
            (ctx, _) => AskUserTool.Register(ctx)),
        [PlanMode] = Define(PlanMode,
            [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SessionProjectionRegistry.ServiceName, UserQuestionService.ServiceName],
            (ctx, config) =>
            {
                _ = new PlanModeController(ctx, PlanModeConfigFrom(config));
                return new DisposableBundle();
            }),
        [Web] = Define(Web, [], (ctx, config) => Dsh.Web.WebRuntime.Apply(ctx, config)),
        [WebFetchHttp] = Define(WebFetchHttp, [WebRuntime.ServiceName], (ctx, config) => Dsh.Web.WebFetchHttp.Apply(ctx, config)),
        [WebSearchDeepseek] = Define(WebSearchDeepseek, [WebRuntime.ServiceName], (ctx, config) => Dsh.Web.WebSearchDeepseek.Apply(ctx, config)),
        [ToolWeb] = Define(ToolWeb, [ToolRuntime.ServiceName, SystemPrompt.ServiceName, WebRuntime.ServiceName], (ctx, config) => Dsh.Web.ToolWeb.Apply(ctx, config)),
        [WorkflowWorkerThread] = Define(WorkflowWorkerThread, [SubagentRuntime.ServiceName],
            (ctx, config) => Dsh.Workflow.WorkerThreadWorkflowEngine.Register(ctx, config)),
        [ToolWorkflow] = Define(ToolWorkflow, [ToolRuntime.ServiceName, WorkflowEngine.ServiceName, SystemPrompt.ServiceName],
            (ctx, config) => Dsh.Workflow.ToolWorkflow.Apply(ctx, config)),
        [ToolRalph] = Define(ToolRalph, [ToolRuntime.ServiceName, WorkflowEngine.ServiceName, SubagentRuntime.ServiceName, SystemPrompt.ServiceName],
            (ctx, config) => Dsh.Workflow.ToolRalph.Apply(ctx, config)),
        [Terminal] = Define(Terminal, [TerminalSessionService.ServiceName],
            (ctx, _) =>
            {
                _ = new TerminalSessionService(ctx);
                return new DisposableBundle();
            }),
        [TerminalBash] = Define(TerminalBash,
            [TerminalSessionService.ServiceName, SubprocessService.ServiceName],
            (ctx, config) => Dsh.Terminal.TerminalBash.Register(ctx, TerminalBashConfigFrom(config))),
        [ToolTerminal] = Define(ToolTerminal,
            [TerminalSessionService.ServiceName, ToolRuntime.ServiceName, SystemPrompt.ServiceName],
            (ctx, config) => Dsh.Terminal.TerminalTools.Register(ctx, TerminalToolsConfigFrom(config))),
    };

    private static PluginDefinition Define(string name, string[] inject, Func<Context, object?, IDisposable> apply)
        => new()
        {
            Name = name,
            Inject = inject.ToDictionary<string, string, object?>(key => key, _ => null),
            Callback = new DelegatePluginCallback((ctx, config) =>
            {
                var registration = apply(ctx, config);
                return (Action)(() => registration.Dispose());
            }),
        };

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

    private static StrReplaceEditorConfig StrReplaceEditorConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new StrReplaceEditorConfig
        {
            MaxOutputChars = dict?.GetValueOrDefault("maxOutputChars") is long maxOutputChars
                ? (int)maxOutputChars
                : new StrReplaceEditorConfig().MaxOutputChars,
            Description = dict?.GetValueOrDefault("description") as string,
        };
    }

    private static ToolResultPruneConfig PruneConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new ToolResultPruneConfig
        {
            ThresholdChars = IntOf(dict, "thresholdChars"),
            HeadChars = IntOf(dict, "headChars"),
            TailChars = IntOf(dict, "tailChars"),
        };
    }

    private static BasicCompactionConfig BasicCompactionConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new BasicCompactionConfig
        {
            ThresholdRatio = DoubleOf(dict, "thresholdRatio"),
            RetainRatio = DoubleOf(dict, "retainRatio"),
            RetainTokens = IntOf(dict, "retainTokens"),
            SummarizationProvider = dict?.GetValueOrDefault("summarizationProvider") as string,
            SummarizationModel = dict?.GetValueOrDefault("summarizationModel") as string,
            MaxTokens = IntOf(dict, "maxTokens"),
            CompactionRetries = IntOf(dict, "compactionRetries"),
            MaxOverflowRetries = IntOf(dict, "maxOverflowRetries"),
            ModelPolicies = ModelPoliciesFrom(dict?.GetValueOrDefault("modelPolicies")),
            Auto = dict?.GetValueOrDefault("auto") as bool?,
        };
    }

    private static IReadOnlyList<ModelCompactPolicyConfig>? ModelPoliciesFrom(object? value)
    {
        if (value is not IEnumerable<object?> items)
            return null;
        var policies = new List<ModelCompactPolicyConfig>();
        foreach (var item in items)
        {
            if (ConfigOf(item) is not { } dict)
                continue;
            policies.Add(new ModelCompactPolicyConfig
            {
                Provider = dict.GetValueOrDefault("provider") as string ?? "",
                Model = dict.GetValueOrDefault("model") as string ?? "",
                ThresholdRatio = DoubleOf(dict, "thresholdRatio"),
                RetainRatio = DoubleOf(dict, "retainRatio"),
                RetainTokens = IntOf(dict, "retainTokens"),
                SummarizationProvider = dict.GetValueOrDefault("summarizationProvider") as string,
                SummarizationModel = dict.GetValueOrDefault("summarizationModel") as string,
                MaxTokens = IntOf(dict, "maxTokens"),
                CompactionRetries = IntOf(dict, "compactionRetries"),
                MaxOverflowRetries = IntOf(dict, "maxOverflowRetries"),
            });
        }
        return policies;
    }

    private static int? IntOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => (int)value,
            int value => value,
            _ => null,
        };

    private static long? LongOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => value,
            int value => value,
            _ => null,
        };

    private static double? DoubleOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            double value => value,
            long value => value,
            int value => value,
            _ => null,
        };

    private static SkillRegistryConfig SkillRegistryConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new SkillRegistryConfig
        {
            CollectCacheMaxEntries = IntOf(dict, "collectCacheMaxEntries") ?? new SkillRegistryConfig().CollectCacheMaxEntries,
        };
    }

    private static SkillFilesystemConfig SkillFilesystemConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new SkillFilesystemConfig
        {
            ProviderName = dict?.GetValueOrDefault("providerName") as string ?? new SkillFilesystemConfig().ProviderName,
            IncludeDefaultRoots = dict?.GetValueOrDefault("includeDefaultRoots") as bool? ?? new SkillFilesystemConfig().IncludeDefaultRoots,
            DshHome = dict?.GetValueOrDefault("dshHome") as string,
            AgentsHome = dict?.GetValueOrDefault("agentsHome") as string,
            CustomSkillDirs = StringListOf(dict?.GetValueOrDefault("customSkillDirs")),
            Watch = dict?.GetValueOrDefault("watch") as bool? ?? new SkillFilesystemConfig().Watch,
            WatchUsePolling = dict?.GetValueOrDefault("watchUsePolling") as bool? ?? new SkillFilesystemConfig().WatchUsePolling,
            WatchStabilityThresholdMs = IntOf(dict, "watchStabilityThresholdMs") ?? new SkillFilesystemConfig().WatchStabilityThresholdMs,
            WatchPollIntervalMs = IntOf(dict, "watchPollIntervalMs") ?? new SkillFilesystemConfig().WatchPollIntervalMs,
            WatchMaxProjects = IntOf(dict, "watchMaxProjects") ?? new SkillFilesystemConfig().WatchMaxProjects,
            WatchFollowSymlinks = dict?.GetValueOrDefault("watchFollowSymlinks") as bool? ?? new SkillFilesystemConfig().WatchFollowSymlinks,
            BundledSkillDir = dict?.GetValueOrDefault("bundledSkillDir") as string,
        };
    }

    private static GoalServiceConfig GoalServiceConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new GoalServiceConfig
        {
            DefaultMaxGoalRounds = LongOf(dict, "defaultMaxGoalRounds") ?? new GoalServiceConfig().DefaultMaxGoalRounds,
        };
    }

    private static GoalToolsConfig GoalToolsConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new GoalToolsConfig
        {
            BlockedAfterConsecutiveRounds = LongOf(dict, "blockedAfterConsecutiveRounds") ?? GoalToolsConfig.DefaultBlockedAfterConsecutiveRounds,
        };
    }

    private static PlanModeConfig PlanModeConfigFrom(object? config)
        => new()
        {
            Section = ConfigOf(config)?.GetValueOrDefault("section") as string ?? "",
        };

    private static TerminalBashConfig TerminalBashConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new TerminalBashConfig
        {
            BackendType = dict?.GetValueOrDefault("backendType") as string ?? TerminalBashConfig.DefaultBackendType,
            ShellDialect = ShellDialectOf(dict?.GetValueOrDefault("shellDialect") as string),
            ShellPath = dict?.GetValueOrDefault("shellPath") as string,
            ShellArgs = StringListOf(dict?.GetValueOrDefault("shellArgs")),
            Rows = IntOf(dict, "rows") ?? TerminalBashConfig.DefaultRows,
            Cols = IntOf(dict, "cols") ?? TerminalBashConfig.DefaultCols,
            ScrollbackLines = IntOf(dict, "scrollbackLines") ?? TerminalBashConfig.DefaultScrollbackLines,
            ScrollbackMaxBytes = IntOf(dict, "scrollbackMaxBytes") ?? TerminalBashConfig.DefaultScrollbackMaxBytes,
            MaxReadBytes = IntOf(dict, "maxReadBytes") ?? TerminalBashConfig.DefaultMaxReadBytes,
            PollIntervalMs = IntOf(dict, "pollIntervalMs") ?? TerminalBashConfig.DefaultPollIntervalMs,
            ExactProbeAfterMs = IntOf(dict, "exactProbeAfterMs") ?? TerminalBashConfig.DefaultExactProbeAfterMs,
            IdleSilenceMs = IntOf(dict, "idleSilenceMs") ?? TerminalBashConfig.DefaultIdleSilenceMs,
            HandoffGraceMs = IntOf(dict, "handoffGraceMs") ?? TerminalBashConfig.DefaultHandoffGraceMs,
            TimeoutMs = IntOf(dict, "timeoutMs") ?? TerminalBashConfig.DefaultTimeoutMs,
            DisposeGraceMs = IntOf(dict, "disposeGraceMs") ?? TerminalBashConfig.DefaultDisposeGraceMs,
        };
    }

    private static TerminalToolsConfig TerminalToolsConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new TerminalToolsConfig
        {
            EnableRunInBackground = dict?.GetValueOrDefault("enableRunInBackground") as bool? ?? new TerminalToolsConfig().EnableRunInBackground,
            MaxResultBytes = IntOf(dict, "maxResultBytes") ?? TerminalToolsConfig.DefaultMaxResultBytes,
        };
    }

    private static ShellDialect ShellDialectOf(string? value)
        => value == "pwsh" ? ShellDialect.Pwsh : ShellDialect.Bash;

    private static IReadOnlyList<string>? StringListOf(object? value)
        => value is IEnumerable<object?> items
            ? items.OfType<string>().ToList()
            : null;

    // TS persona 行的模板插值发生在渲染期(PromptRender),此处只注册字面 section。
    // includeRuntimeContext: false 对应 TS 的 suppressRuntimeContext。
    private static IDisposable ApplyPersona(Context ctx, object? config)
    {
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var dict = ConfigOf(config);
        var section = systemPrompt.ReplacePersona(
            dict?.GetValueOrDefault("text") as string ?? "",
            dict?.GetValueOrDefault("complete") is true);
        if (dict?.GetValueOrDefault("includeRuntimeContext") is not false)
            return section;
        return new DisposableBundle(section, systemPrompt.SuppressRuntimeContext());
    }

    private sealed class DisposableBundle(params IDisposable?[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable?.Dispose();
        }
    }
}
