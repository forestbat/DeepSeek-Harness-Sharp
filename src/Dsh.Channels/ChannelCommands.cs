using Dsh.Core;
using Dsh.Interaction;
using Dsh.Runtime;

namespace Dsh.Channels;

public static class ChannelCommands
{
    public static IDisposable Apply(Context ctx, ExternalChannelRouter router, ChannelWebhookListener listener)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false);
        if (commands is null)
            return new DisposeBundle([]);

        var command = commands.Register(new CommandDefinition
        {
            Name = "channel",
            Description = "Manage external channel bindings, outbound sends, triggers, and webhook listener",
            Input = new CommandInputDescriptor("list | bind <id> <adapter> <channelKey> <agentId> [outboundUrl] | unbind <id> | send <id> <body> | triggers | listener <status|start [port]|stop>"),
            Subcommands =
            [
                new CommandDescriptor("list", "List configured channel bindings"),
                new CommandDescriptor("bind", "Bind an external channel to an agent", new CommandInputDescriptor("<id> <adapter> <channelKey> <agentId> [outboundUrl]")),
                new CommandDescriptor("unbind", "Remove a channel binding", new CommandInputDescriptor("<bindingId>")),
                new CommandDescriptor("send", "Send an outbound channel message", new CommandInputDescriptor("<bindingId> <body>")),
                new CommandDescriptor("triggers", "List automation trigger rules"),
                new CommandDescriptor("trigger-add", "Add an automation trigger rule", new CommandInputDescriptor("<name> <sourceKind> <eventPattern> <agentId> <promptTemplate>")),
                new CommandDescriptor("trigger-remove", "Remove an automation trigger rule", new CommandInputDescriptor("<ruleId>")),
                new CommandDescriptor("listener", "Inspect or control the local webhook HTTP listener", new CommandInputDescriptor("<status|start [port]|stop>")),
            ],
            Handler = invocation => HandleChannelAsync(invocation, router, listener),
        });

        return command;
    }

    private static async Task<CommandResult> HandleChannelAsync(
        CommandInvocation invocation,
        ExternalChannelRouter router,
        ChannelWebhookListener listener)
    {
        var tokens = invocation.RawInput.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : "list";
        var rest = tokens.Length > 1 ? tokens[1].Trim() : "";

        switch (sub)
        {
            case "list":
            {
                var bindings = router.ListBindings();
                if (bindings.Count == 0)
                    return new CommandResult.Success("No channel bindings configured.");
                return new CommandResult.Success(string.Join('\n', bindings.Select(b =>
                    $"{b.BindingId} [{b.AdapterKind}:{b.ChannelKey}] -> {b.TargetAgentId.Value} (enabled={b.Enabled})")));
            }
            case "bind":
            {
                var parts = rest.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                    return new CommandResult.Error("usage: /channel bind <id> <adapter> <channelKey> <agentId> [outboundUrl]");
                var binding = router.Bind(new ChannelBinding(
                    parts[0],
                    parts[1],
                    parts[2],
                    AgentId.Create(parts[3]),
                    OutboundUrl: parts.Length >= 5 ? parts[4] : null));
                return new CommandResult.Success($"Bound {binding.BindingId} ({binding.AdapterKind}:{binding.ChannelKey}) -> {binding.TargetAgentId.Value}");
            }
            case "unbind":
            {
                if (string.IsNullOrWhiteSpace(rest))
                    return new CommandResult.Error("usage: /channel unbind <bindingId>");
                return new CommandResult.Success(router.Unbind(rest)
                    ? $"Unbound {rest}"
                    : $"Binding '{rest}' not found.");
            }
            case "send":
            {
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return new CommandResult.Error("usage: /channel send <bindingId> <body>");
                await router.SendOutboundAsync(new ChannelOutboundMessage(parts[0], parts[1]), invocation.Signal).ConfigureAwait(false);
                return new CommandResult.Success($"Sent message to {parts[0]}");
            }
            case "triggers":
            {
                var triggers = router.ListTriggers();
                if (triggers.Count == 0)
                    return new CommandResult.Success("No automation triggers configured.");
                return new CommandResult.Success(string.Join('\n', triggers.Select(t =>
                    $"{t.RuleId}: {t.Name} [{t.SourceKind}:{t.EventPattern}] -> {t.TargetAgentId.Value} (enabled={t.Enabled})")));
            }
            case "trigger-add":
            {
                var parts = rest.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                    return new CommandResult.Error("usage: /channel trigger-add <name> <sourceKind> <eventPattern> <agentId> <promptTemplate>");
                var rule = router.AddTrigger(new AutomationTriggerRule(
                    $"trg-{Guid.NewGuid():N}",
                    parts[0],
                    parts[1],
                    parts[2],
                    AgentId.Create(parts[3]),
                    parts[4]));
                return new CommandResult.Success($"Added trigger {rule.RuleId} ({rule.SourceKind}:{rule.EventPattern}) -> {rule.TargetAgentId.Value}");
            }
            case "trigger-remove":
            {
                if (string.IsNullOrWhiteSpace(rest))
                    return new CommandResult.Error("usage: /channel trigger-remove <ruleId>");
                return new CommandResult.Success(router.RemoveTrigger(rest)
                    ? $"Removed trigger {rest}"
                    : $"Trigger '{rest}' not found.");
            }
            case "listener":
            {
                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var op = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
                switch (op)
                {
                    case "status":
                        return new CommandResult.Success(listener.IsListening
                            ? $"Webhook listener running at {listener.BaseUri}"
                            : "Webhook listener stopped.");
                    case "start":
                        var port = parts.Length >= 2 && int.TryParse(parts[1], out var parsedPort) ? parsedPort : 0;
                        var boundPort = await listener.StartListenerAsync(port, cancellationToken: invocation.Signal).ConfigureAwait(false);
                        return new CommandResult.Success($"Webhook listener started on port {boundPort} ({listener.BaseUri})");
                    case "stop":
                        await listener.StopAsync().ConfigureAwait(false);
                        return new CommandResult.Success("Webhook listener stopped.");
                    default:
                        return new CommandResult.Error("usage: /channel listener <status|start [port]|stop>");
                }
            }
            default:
                return new CommandResult.Error($"Unknown /channel subcommand '{sub}'.");
        }
    }

    private sealed class DisposeBundle(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
