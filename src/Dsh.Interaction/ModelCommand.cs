using System.Text;
using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Interaction;

public static class ModelCommand
{
    public static IDisposable Register(Context ctx, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false)!;
        return commands.Register(new CommandDefinition
        {
            Name = "model",
            Description = "List or switch provider/model",
            Input = new CommandInputDescriptor("provider/model"),
            Handler = invocation =>
            {
                var agent = invocation.Agent as AgentLoopAgent;
                if (agent is null)
                    return Task.FromResult<CommandResult>(new CommandResult.Error("the model command requires an AgentLoopAgent"));
                var settings = HarnessSettings.Load(home);
                var raw = invocation.RawInput.Trim();
                if (raw.Length == 0)
                {
                    var builder = new StringBuilder();
                    if (settings.GlobalDefaultModel is { } defaultModel)
                        builder.AppendLine($"default: {defaultModel}");
                    foreach (var (providerName, providerEntry) in settings.Providers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                    {
                        foreach (var modelId in providerEntry.Models.Keys.OrderBy(id => id, StringComparer.Ordinal))
                            builder.AppendLine($"{providerName}/{modelId}");
                    }
                    return Task.FromResult<CommandResult>(new CommandResult.Success(builder.ToString().TrimEnd()));
                }

                var parts = raw.Split('/');
                if (parts.Length != 2)
                    return Task.FromResult<CommandResult>(new CommandResult.Error("usage: /model <provider/model>"));
                var provider = parts[0];
                var model = parts[1];
                if (provider.Length == 0 || model.Length == 0)
                    return Task.FromResult<CommandResult>(new CommandResult.Error("provider and model must not be empty"));
                var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
                try
                {
                    llm.ResolveModelInfo(provider, model);
                }
                catch (LlmException error)
                {
                    return Task.FromResult<CommandResult>(new CommandResult.Error(error.Failure.Message));
                }

                var header = RequestHeader.Canonicalize(new EpochHeader(new LlmCallConfig(provider, model)));
                agent.Session.Append(new RequestHeaderPayload(header, RequestHeaderReasons.Change, true));
                return Task.FromResult<CommandResult>(new CommandResult.Success($"switched to {provider}/{model}"));
            },
        });
    }
}
