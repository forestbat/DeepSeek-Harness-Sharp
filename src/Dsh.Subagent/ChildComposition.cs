using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Subagent;

public sealed record ChildComposition(string? Persona = null, ToolRestriction? ToolFilter = null, JsonObject? OutputSchema = null);

public sealed record DelegatedPolicyOverrides(ApprovalPolicy? ApprovalPolicy);

public static class ChildCompositionSupport
{
    public const string DelegationContextText =
        "You are running as a delegated subagent within your delegation scope: "
        + "operations that require approval are rejected automatically, and your "
        + "sandbox mode is fixed at the delegation point and cannot be changed or "
        + "widened for this session.";

    public static SessionHeader ChildSessionHeader(IAgent parent, int childDepth, SessionId childId, bool isSeeded)
    {
        var parentHeader = parent.Session.Header;
        return new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = childId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Cwd = parentHeader.Cwd,
            ParentSession = parent.Id,
            IsSeeded = isSeeded,
            Origin = "subagent",
            DelegationDepth = childDepth,
            AgentPreset = parentHeader.AgentPreset,
        };
    }

    public static AgentOptions ParentAgentOptionsForDelegation(IAgent parent)
    {
        var requestConfig = parent.Session.RequestHeader()?.Config;
        if (requestConfig is null)
            return parent.Options with { };
        return new AgentOptions(
            requestConfig.Provider,
            requestConfig.Model,
            requestConfig.ReasoningEffort,
            parent.Options.MaxTokens);
    }

    public static AgentOptions ResolveChildAgentOptions(IAgent parent, AgentOptions? requested)
    {
        var parentOptions = ParentAgentOptionsForDelegation(parent);
        var resolved = new AgentOptions(
            requested?.Provider ?? parentOptions.Provider,
            requested?.Model ?? parentOptions.Model,
            requested?.ReasoningEffort ?? parentOptions.ReasoningEffort,
            requested?.MaxTokens ?? parentOptions.MaxTokens);
        var routeChanged = resolved.Provider != parentOptions.Provider || resolved.Model != parentOptions.Model;
        return routeChanged && requested?.ReasoningEffort is null
            ? resolved with { ReasoningEffort = null }
            : resolved;
    }

    public static DelegatedPolicyOverrides CaptureDelegatedPolicyOverrides(IAgent parent)
        => new(parent.Ctx.Get(ApprovalService.ServiceName, false) is null ? null : ApprovalPolicy.Never);

    public static void AppendDelegatedPolicyOverrides(Session childSession, DelegatedPolicyOverrides inherited)
    {
        if (inherited.ApprovalPolicy is { } policy)
            childSession.Append(new ApprovalPolicyPayload(policy, "delegation"));
    }

    public static bool AdmitsTool(ToolRestriction filter, string name)
        => (filter.Allow is null || filter.Allow.Contains(name))
           && (filter.Deny is null || !filter.Deny.Contains(name));

    public static IDisposable ApplyChildComposition(
        Context childCtx, IAgent parent, IAgent child, ChildComposition composition, StructuredOutputAttachment? structured)
    {
        DshScope.BindScopeParent(child.ScopeKey, parent.ScopeKey);
        var disposables = new List<IDisposable>();
        if (composition.ToolFilter is { } filter)
        {
            ValidateToolFilter(childCtx, filter);
            disposables.Add(new FuncDispose(childCtx.On(
                ToolRuntime.PreExecuteEvent,
                (thisArg, args) => DenyFilteredTool(child, filter, args))));
        }
        disposables.Add(new FuncDispose(childCtx.On(
            SystemPrompt.AssembleEvent,
            (thisArg, args) => TransformAssembly(child, composition, structured, args))));
        return new DisposeBundle(disposables);
    }

    private static void ValidateToolFilter(Context childCtx, ToolRestriction filter)
    {
        var tools = childCtx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var known = tools.Schemas().Select(schema => schema.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = (filter.Allow ?? []).Concat(filter.Deny ?? []).Where(name => !known.Contains(name)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"tools.restrict() names unknown global tool{(unknown.Count > 1 ? "s" : "")} "
                + $"{string.Join(", ", unknown.Select(name => $"\"{name}\""))}; "
                + $"known global tools: {string.Join(", ", known.Order())}");
        }
    }

    private static ValueTask<object?> DenyFilteredTool(IAgent child, ToolRestriction filter, object?[] args)
    {
        var exec = (ToolRunContext)args[0]!;
        var next = (Func<ValueTask<object?>>)args[^1]!;
        if (!ReferenceEquals(exec.Agent, child) || AdmitsTool(filter, exec.Name))
            return next();
        return new ValueTask<object?>(new PreToolDecision.Deny($"unknown tool \"{exec.Name}\""));
    }

    private static async ValueTask<object?> TransformAssembly(
        IAgent child, ChildComposition composition, StructuredOutputAttachment? structured, object?[] args)
    {
        var next = (Func<ValueTask<object?>>)args[^1]!;
        var result = await next();
        if (result is not PromptAssembly assembly || ((AssembleContext)args[1]!).Scope != child.ScopeKey)
            return result;
        var sections = assembly.Sections;
        if (composition.Persona is { } persona)
        {
            sections = sections
                .Select(section => section.Name == SystemPrompt.PersonaSection ? section with { Text = persona } : section)
                .ToList();
        }
        var tools = assembly.Tools;
        if (composition.ToolFilter is { } filter)
            tools = tools.Where(tool => AdmitsTool(filter, tool.Name)).ToList();
        if (structured is not null)
        {
            tools = [..tools, structured.Schema];
            sections = [..sections, new AssembledSection(StructuredOutputAttachment.SectionName, StructuredOutputAttachment.Instruction)];
        }
        return ReferenceEquals(sections, assembly.Sections) && ReferenceEquals(tools, assembly.Tools)
            ? assembly
            : assembly with { Sections = sections, Tools = tools };
    }

    private sealed class FuncDispose(Func<bool> dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }

    private sealed class DisposeBundle(List<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
