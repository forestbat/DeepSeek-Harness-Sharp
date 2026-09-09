using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Interaction;

public static class ReasoningCommand
{
    public static IDisposable Register(Context ctx)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false)!;
        return commands.Register(new CommandDefinition
        {
            Name = "reasoning",
            Description = "Show or set reasoning effort",
            Input = new CommandInputDescriptor("effort"),
            Handler = invocation =>
            {
                var agent = invocation.Agent as AgentLoopAgent;
                if (agent is null)
                    return Task.FromResult<CommandResult>(new CommandResult.Error("the reasoning command requires an AgentLoopAgent"));
                var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
                var currentHeader = agent.Session.RequestHeader();
                var provider = currentHeader?.Config.Provider ?? agent.Options.Provider ?? "";
                var model = currentHeader?.Config.Model ?? agent.Options.Model ?? "";
                var raw = invocation.RawInput.Trim();
                if (raw.Length == 0)
                {
                    var info = llm.ResolveModelInfo(provider, model);
                    if (info.Reasoning is null)
                        return Task.FromResult<CommandResult>(new CommandResult.Success("the current model does not expose reasoning efforts"));
                    var lines = info.Reasoning.Efforts.Select(effort =>
                        $"{effort.Id}: {effort.Name}{(info.Reasoning.DefaultEffort == effort.Id ? " (default)" : "")}");
                    return Task.FromResult<CommandResult>(new CommandResult.Success(string.Join('\n', lines)));
                }

                var requested = ReasoningEffortId.Create(raw);
                var resolved = llm.ResolveModelInfo(provider, model);
                if (resolved.Reasoning is null || !resolved.Reasoning.Efforts.Any(effort => effort.Id == requested))
                    return Task.FromResult<CommandResult>(new CommandResult.Error($"model {provider}/{model} does not support reasoning effort \"{raw}\""));
                var config = currentHeader?.Config ?? new LlmCallConfig(provider, model);
                var header = RequestHeader.Canonicalize(new EpochHeader(config with { ReasoningEffort = requested }));
                agent.Session.Append(new RequestHeaderPayload(header, RequestHeaderReasons.Change, true));
                return Task.FromResult<CommandResult>(new CommandResult.Success($"reasoning effort set to {requested} for {provider}/{model}"));
            },
        });
    }
}
