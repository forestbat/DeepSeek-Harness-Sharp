using System.Runtime.CompilerServices;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Presets;

/**
 * 交互 preset(persona + 工具组合)控制器:与上游四模式对齐,与审批策略/计划模式正交。
 * 状态持久在会话事件 preset/mode;scoped 效果(persona/工具白名单)是内存态,
 * agent 创建(含恢复)时按事件回放重放。
 */
public sealed class PresetController : Service
{
    public const string ServiceName = "presets";
    public const string PluginName = "dsh-presets";
    public const string MinimalPersona = "You are a helpful software engineer assistant.";

    private const string PtcNotReady =
        "PTC preset 依赖 PTC 运行时，尚未移植（见 plans/PTC运行时调研与移植立项-2026-09-25.md）";

    private const string CreativeNotReady =
        "创造模式随 PTC 立项阶段四（运行时检查工具 + plugin-manager）落地，尚未就绪";

    private static readonly string[] ShellToolNames = ["bash", "pwsh"];

    private sealed class EffectHolder
    {
        public List<IDisposable> Effects = [];
    }

    private readonly ConditionalWeakTable<IAgent, EffectHolder> _applied = new();

    static PresetController() => PresetModePayload.RegisterCodec();

    public PresetController(Context ctx) : base(ctx, ServiceName)
    {
        var offCreated = ctx.On<AgentCreatedNotification>(
            notification => Apply(notification.Agent, PresetOf(notification.Agent.Session)),
            new EventOptions { Global = true });
        var offDisposed = ctx.On<AgentDisposedNotification>(
            notification => Release(notification.Agent),
            new EventOptions { Global = true });
        ctx.Effect(() => (Action)(() => { offCreated(); offDisposed(); }), $"{PluginName}: unsubscribe agent lifecycle");

        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false);
        commands?.Register(new CommandDefinition
        {
            Name = "preset",
            Description = "Switch the interaction preset (persona + toolset)",
            Input = new CommandInputDescriptor("[standard|minimal|ptc|creative]"),
            Handler = invocation => Task.FromResult(HandleCommand(invocation)),
        });
    }

    public static PresetController Register(Context ctx) => new(ctx);

    /** 最近一次 preset/mode 事件生效;无事件即 standard。 */
    public static string PresetOf(Session session)
    {
        for (var seq = session.Seq - 1; seq >= 0; seq--)
        {
            if (session.EventAt(seq)?.Data is PresetModePayload payload)
                return payload.Preset;
        }
        return InteractionPreset.Standard;
    }

    public CommandResult Set(IAgent agent, string preset)
    {
        var target = preset.Trim().ToLowerInvariant();
        if (!InteractionPreset.IsKnown(target))
        {
            return new CommandResult.Error(
                $"unknown preset \"{preset}\"; available: {string.Join(", ", InteractionPreset.All)}");
        }
        if (target == InteractionPreset.Ptc)
            return new CommandResult.Error(PtcNotReady);
        if (target == InteractionPreset.Creative)
            return new CommandResult.Error(CreativeNotReady);
        var current = PresetOf(agent.Session);
        if (target == current)
            return new CommandResult.Success($"Preset is already {target}.");
        agent.Session.Append(new PresetModePayload(target));
        Apply(agent, target);
        agent.Inject(Narration(target));
        return new CommandResult.Success(target == InteractionPreset.Standard
            ? "Preset: standard — default persona and toolset."
            : "Preset: minimal — bare assistant persona, persistent shell only.");
    }

    private CommandResult HandleCommand(CommandInvocation invocation)
    {
        var input = invocation.RawInput.Trim();
        if (input.Length == 0)
        {
            return new CommandResult.Success(
                $"Current preset: {PresetOf(invocation.Agent.Session)}. "
                + $"Available: {string.Join(", ", InteractionPreset.All)} "
                + $"(ptc: 未移植; creative: 未就绪).");
        }
        return Set(invocation.Agent, input);
    }

    private void Apply(IAgent agent, string preset)
    {
        var holder = _applied.GetOrCreateValue(agent);
        Release(agent);
        if (preset != InteractionPreset.Minimal)
            return;
        var systemPrompt = Ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)
            ?? throw new InvalidOperationException("presets requires the systemPrompt service");
        var tools = Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("presets requires the tools service");
        var shell = ShellToolNames.Where(name => tools.Get(name) is not null).ToList();
        if (shell.Count == 0)
        {
            throw new InvalidOperationException(
                "minimal preset requires a registered persistent shell tool (bash/pwsh), but neither is registered");
        }
        holder.Effects.Add(systemPrompt.Section(
            PromptSection.Literal(SystemPrompt.PersonaSection, PromptOrders.DeploymentPersona, MinimalPersona, complete: true),
            agent.ScopeKey));
        holder.Effects.Add(systemPrompt.SuppressRuntimeContext(agent.ScopeKey));
        holder.Effects.Add(tools.Restrict(new ToolRestriction(Allow: shell), agent.ScopeKey));
    }

    private void Release(IAgent agent)
    {
        if (!_applied.TryGetValue(agent, out var holder))
            return;
        foreach (var effect in holder.Effects)
            effect.Dispose();
        holder.Effects.Clear();
    }

    private static UserMessage Narration(string preset)
    {
        var text = preset == InteractionPreset.Minimal
            ? "The user switched this session to the minimal preset: bare assistant persona; only the persistent shell tool is available."
            : "The user switched this session back to the standard preset: the default persona and toolset apply.";
        return MessageFactory.CreateUserMessage(
            [new TextBlock(text)],
            new PluginMessageSource(PluginName, ContextForms.Notice, Summary: text));
    }
}
