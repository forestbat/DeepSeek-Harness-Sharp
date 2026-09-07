using System.Text;
using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Boot;

public static class ModelCommand
{
    public static IDisposable Register(Context ctx, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        var settings = HarnessSettings.Load(home);
        return commands.Register(new CommandDefinition
        {
            Name = "model",
            Description = "List or switch model/config",
            Input = new CommandInputDescriptor("config-name | provider/model"),
            Handler = invocation =>
            {
                var agent = invocation.Agent as AgentLoopAgent;
                if (agent is null)
                    return Task.FromResult<CommandResult>(new CommandResult.Error("the model command requires an AgentLoopAgent"));
                var raw = invocation.RawInput.Trim();
                if (raw.Length == 0)
                {
                    var builder = new StringBuilder();
                    foreach (var (name, entry) in settings.Configs)
                        builder.AppendLine($"{name}: {entry.Provider}/{entry.Model}");
                    if (settings.Default is { } defaultName)
                        builder.AppendLine($"default: {defaultName}");
                    return Task.FromResult<CommandResult>(new CommandResult.Success(builder.ToString().TrimEnd()));
                }

                string provider;
                string model;
                string? reasoningEffort = null;
                if (settings.Configs.TryGetValue(raw, out var selectedConfig))
                {
                    provider = selectedConfig.Provider ?? "";
                    model = selectedConfig.Model ?? "";
                    reasoningEffort = selectedConfig.ReasoningEffort;
                }
                else
                {
                    var parts = raw.Split('/');
                    if (parts.Length != 2)
                        return Task.FromResult<CommandResult>(new CommandResult.Error("usage: /model <config-name> or /model <provider/model>"));
                    provider = parts[0];
                    model = parts[1];
                }

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

                var header = RequestHeader.Canonicalize(new EpochHeader(new LlmCallConfig(
                    provider,
                    model,
                    reasoningEffort is null ? null : ReasoningEffortId.Create(reasoningEffort))));
                agent.Session.Append(new RequestHeaderPayload(header, RequestHeaderReasons.Change, true));
                return Task.FromResult<CommandResult>(new CommandResult.Success($"switched to {provider}/{model}"));
            },
        });
    }
}
