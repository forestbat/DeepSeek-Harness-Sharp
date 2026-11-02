using System.Runtime.CompilerServices;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.AgentInstructions.Plugin.AgentInstructions)]

namespace Dsh.AgentInstructions;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string AgentInstructions = "@deepseek-ai/dsh-agent-instructions";

    public string[] Inject => packageName switch
    {
        AgentInstructions => [SystemPrompt.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        AgentInstructions => RegisterAgentInstructions(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterAgentInstructions(Context ctx)
    {
        var options = ctx.GetProp("harnessOptions") as HarnessOptions
            ?? throw new InvalidOperationException("harnessOptions is required for the agent-instructions plugin");
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var dynamic = new DynamicInstructions(options.Home);
        var unsubscribe = ctx.OnWaterfall<ToolPostExecuteNotification>(
            dynamic.Post,
            new EventOptions { Global = true });
        return new DisposableBundle(
            systemPrompt.Section(new PromptSection(
                "agent-instructions",
                context => AgentInstructionsText.Render(options.Home, context.Agent?.Session.Header.Cwd ?? options.Cwd))),
            new ActionDisposable(() => { unsubscribe(); }));
    }

    private sealed class DynamicInstructions(HarnessHome home)
    {
        private readonly ConditionalWeakTable<Session, LoadedState> _loaded = new();

        public async ValueTask<object?> Post(ToolPostExecuteNotification notification, Func<ValueTask<object?>> next)
        {
            var decision = await next();
            if (decision is not PostToolDecision.Accept accept
                || notification.Run.Agent is not { } agent
                || notification.Result is not ToolExecutionResult.Success
                || notification.Run.Name != "read"
                || !notification.Run.Arguments.TryGetProperty("file_path", out var pathElement)
                || pathElement.ValueKind != System.Text.Json.JsonValueKind.String)
                return decision;
            var target = Resolve(agent.Session, pathElement.GetString());
            if (target is null)
                return decision;
            var state = _loaded.GetValue(agent.Session, value => new LoadedState(
                AgentInstructionsText.Discover(home, value.Header.Cwd)));
            var text = AgentInstructionsText.RenderNew(home, Path.GetDirectoryName(target), state.Paths);
            if (text.Length == 0)
                return decision;
            return new PostToolDecision.Accept(
                Content: [.. accept.Content ?? notification.Result.Content,
                    new TextBlock($"\n\n<system-reminder>\n{text}\n</system-reminder>")],
                Value: accept.Value,
                AdditionalContexts: accept.AdditionalContexts);
        }

        private static string? Resolve(Session session, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            var cwd = session.WorkingDirectory ?? session.Header.Cwd;
            return string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFullPath(Path.Combine(cwd, path));
        }
    }

    private sealed class LoadedState(IEnumerable<string> paths)
    {
        public HashSet<string> Paths { get; } = new(paths, StringComparer.Ordinal);
    }

    private sealed class ActionDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private sealed class DisposableBundle(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            for (var index = disposables.Length - 1; index >= 0; index--)
                disposables[index].Dispose();
        }
    }
}
