using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Skills;

/**
 * 官方 DSH 风格: skill 目录作为 user-role 会话前缀提醒, 完整正文由 skill 工具按需加载。
 * 目录只发布一次, 变更时发布替换提示; 不在 system 中常驻完整 skill 正文。
 */
public sealed class SkillCatalog : IDisposable
{
    public const string PluginName = "@deepseek-ai/dsh-skill-catalog";
    public const string ToolName = "skill";
    public const int DescriptionMaxLength = 500;

    private readonly SkillRegistry _registry;
    private readonly ToolRuntime? _tools;
    private readonly List<IDisposable> _registrations = [];
    private readonly ConditionalWeakTable<Session, CatalogState> _states = new();
    private bool _disposed;

    public SkillCatalog(Context ctx, SkillRegistry registry)
    {
        _registry = registry;
        ctx.OnWaterfall<AgentPreStepNotification>(PreStep, new EventOptions { Global = true });
        _tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (_tools is not null)
            _registrations.Add(_tools.Register(CreateTool()));
        ctx.Effect(() => (Action)(() => Dispose()), $"{PluginName}: dispose");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (var index = _registrations.Count - 1; index >= 0; index--)
            _registrations[index].Dispose();
        _registrations.Clear();
    }

    public static string RenderCatalog(IReadOnlyList<SkillSummary> skills, bool update, bool incomplete)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<system-reminder>");
        builder.AppendLine(update
            ? "The available skill catalog changed. This complete catalog replaces every earlier available-skills list in this session:"
            : "A skill is a reusable set of task-specific instructions. The following skills are available in this session:");
        builder.AppendLine();
        builder.AppendLine("<available_skills>");
        foreach (var skill in skills)
            builder.AppendLine($"- `{skill.Name}`: {EscapeText(Truncate(skill.Description, DescriptionMaxLength))}");
        builder.AppendLine("</available_skills>");
        builder.AppendLine();
        if (incomplete)
            builder.AppendLine("Some skills could not be checked. Only confirmed skills are listed; discovery will be retried on the next request.");
        if (skills.Count == 0)
            builder.AppendLine("No skills are currently available through the `skill` tool. Do not infer skill instructions from an empty catalog.");
        else
        {
            builder.AppendLine("If the user names a skill, or the task clearly matches a skill's description, call the `skill` tool with the exact skill name before taking task actions.");
            builder.AppendLine("This catalog contains summaries only; do not infer or follow a skill's instructions until it has been loaded.");
        }
        builder.AppendLine("A user may also invoke a skill directly; its <skill_content> block then appears in this conversation. Follow it, and do not call the `skill` tool again for that skill.");
        builder.Append("</system-reminder>");
        return builder.ToString();
    }

    private async ValueTask<object?> PreStep(AgentPreStepNotification notification, Func<ValueTask<object?>> next)
    {
        var decision = await next();
        if (decision is not PreStepDecision.Enter enter || notification.Payload.Signal.IsCancellationRequested)
            return decision;
        var payload = notification.Payload;
        var session = payload.Agent.Session;
        var state = _states.GetValue(session, RecoverState);
        var snapshot = await _registry.Snapshot(new SkillViewOptions
        {
            Cwd = session.WorkingDirectory,
            Scope = payload.Agent.ScopeKey,
            Signal = payload.Signal,
        });
        var skills = _tools?.Get(ToolName, payload.Agent.ScopeKey) is null
            ? []
            : snapshot.Skills.Where(skill => skill.Invocation.ModelInvocable).ToList();
        var digest = Digest(skills, snapshot.Complete);
        if (state.Published && state.Digest == digest)
            return decision;
        if (!state.Published && skills.Count == 0 && snapshot.Complete)
            return decision;
        var update = state.Published;
        state.Published = true;
        state.Digest = digest;
        var message = MessageFactory.CreateUserText(
            RenderCatalog(skills, update, !snapshot.Complete),
            new PluginMessageSource(PluginName, ContextForms.Catalog, Summary: digest));
        return enter with { Messages = [.. enter.Messages, message] };
    }

    private ToolDefinition CreateTool()
    {
        const string outputSchema = """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["name", "provider", "content"],
              "properties": {
                "name": { "type": "string" },
                "provider": { "type": "string" },
                "content": { "type": "string" }
              }
            }
            """;
        return new ToolDefinition
        {
            Name = ToolName,
            Description = "Load the full instructions for a skill. Call it before acting on a task that names or clearly matches a skill in the session skill catalog.",
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["name"],
                  "properties": {
                    "name": { "type": "string", "description": "The exact skill name from the available skills list." }
                  }
                }
                """)!.AsObject(),
            Output = new ToolOutputDefinition(
                JsonNode.Parse(outputSchema)!.AsObject(),
                (_, value) => [new TextBlock(RenderLoadedSkill(value))]),
            IsConcurrencySafe = _ => true,
            Execute = async (args, exec) =>
            {
                var name = args.GetProperty("name").GetString() ?? "";
                if (!SkillRegistry.IsSkillName(name))
                    throw new ArgumentException($"invalid skill name \"{name}\"");
                var skill = await _registry.Get(name, new SkillViewOptions
                {
                    Cwd = exec.Agent?.Session.WorkingDirectory,
                    Scope = exec.Agent?.ScopeKey,
                    Signal = exec.Signal,
                }) ?? throw new InvalidOperationException($"skill \"{name}\" is unknown or no longer available");
                if (!skill.Invocation.ModelInvocable)
                    throw new InvalidOperationException($"skill \"{name}\" is not available for model invocation");
                return new SkillLoadValue(skill.Name, skill.Provider, skill.Content);
            },
        };
    }

    private static string RenderLoadedSkill(JsonElement value)
        => SkillRender.RenderSkillContent(
            value.GetProperty("name").GetString() ?? "",
            value.GetProperty("provider").GetString() ?? "",
            null,
            value.GetProperty("content").GetString() ?? "");

    private static CatalogState RecoverState(Session session)
    {
        var state = new CatalogState();
        foreach (var sessionEvent in session.SnapshotEvents())
        {
            if (sessionEvent.Data is not UserMessagePayload payload
                || payload.Message.Source is not PluginMessageSource source
                || source.Plugin != PluginName
                || source.Form != ContextForms.Catalog
                || source.Summary is not { } digest)
                continue;
            state.Published = true;
            state.Digest = digest;
        }
        return state;
    }

    private static string Digest(IReadOnlyList<SkillSummary> skills, bool complete)
    {
        var text = string.Join('\n', skills.Select(skill => $"{skill.Name}\u001f{skill.Description}")) + $"\u001f{complete}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : $"{value[..(max - 1)]}…";

    private static string EscapeText(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private sealed class CatalogState
    {
        public bool Published { get; set; }
        public string Digest { get; set; } = "";
    }

    private sealed record SkillLoadValue(string Name, string Provider, string Content);
}


