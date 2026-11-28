using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Channels;

public static class ChannelTools
{
    public const string ChannelBindTool = "channel_bind";
    public const string ChannelSendTool = "channel_send";
    public const string TriggerAddTool = "trigger_add";
    public const string TriggerListTool = "trigger_list";

    public static IDisposable Apply(Context ctx, ExternalChannelRouter router)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (tools is null)
            return new DisposeBundle([]);

        return new DisposeBundle(
        [
            tools.Register(BindTool(router)),
            tools.Register(SendTool(router)),
            tools.Register(AddTriggerTool(router)),
            tools.Register(ListTriggersTool(router)),
        ]);
    }

    private static ToolDefinition BindTool(ExternalChannelRouter router) => new()
    {
        Name = ChannelBindTool,
        Description = "Bind, unbind, or list external channel bindings (github, slack, discord) routed to persistent agents.",
        Parameters = Schema(
            ["action"],
            new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("bind", "unbind", "list"),
                },
                ["binding_id"] = new JsonObject { ["type"] = "string" },
                ["adapter_kind"] = new JsonObject { ["type"] = "string" },
                ["channel_key"] = new JsonObject { ["type"] = "string" },
                ["target_agent_id"] = new JsonObject { ["type"] = "string" },
                ["target_room_id"] = new JsonObject { ["type"] = "string" },
                ["outbound_url"] = new JsonObject { ["type"] = "string" },
                ["secret_ref"] = new JsonObject { ["type"] = "string" },
                ["secret"] = new JsonObject { ["type"] = "string" },
                ["enabled"] = new JsonObject { ["type"] = "boolean" },
            }),
        Output = ObjectOutput(value =>
            value.TryGetProperty("bindings", out var bindings)
                ? $"Listed {bindings.GetArrayLength()} channel binding(s)."
                : value.TryGetProperty("unbound", out var unbound)
                    ? $"Unbind {value.GetProperty("bindingId").GetString()}: {unbound.GetBoolean()}."
                    : $"Bound channel {value.GetProperty("bindingId").GetString()} -> {value.GetProperty("targetAgentId").GetString()}."),
        Execute = (args, _) =>
        {
            var action = RequireString(args, "action").ToLowerInvariant();
            if (action == "list")
            {
                var rows = new JsonArray();
                foreach (var binding in router.ListBindings())
                {
                    rows.Add(new JsonObject
                    {
                        ["bindingId"] = binding.BindingId,
                        ["adapterKind"] = binding.AdapterKind,
                        ["channelKey"] = binding.ChannelKey,
                        ["targetAgentId"] = binding.TargetAgentId.Value,
                        ["targetRoomId"] = binding.TargetRoomId?.Value,
                        ["outboundUrl"] = binding.OutboundUrl,
                        ["enabled"] = binding.Enabled,
                    });
                }
                return Task.FromResult<object?>(new JsonObject { ["bindings"] = rows });
            }

            var bindingId = RequireString(args, "binding_id");
            if (action == "unbind")
            {
                return Task.FromResult<object?>(new JsonObject
                {
                    ["bindingId"] = bindingId,
                    ["unbound"] = router.Unbind(bindingId),
                });
            }

            if (action != "bind")
                throw new ArgumentException($"Unsupported channel_bind action '{action}'.");

            var secretRef = OptionalString(args, "secret_ref");
            if (OptionalString(args, "secret") is { } secretValue)
            {
                secretRef ??= $"binding:{bindingId}";
                router.SetSecret(secretRef, secretValue);
            }

            var bound = router.Bind(new ChannelBinding(
                bindingId,
                RequireString(args, "adapter_kind"),
                RequireString(args, "channel_key"),
                AgentId.Create(RequireString(args, "target_agent_id")),
                OptionalString(args, "target_room_id") is { } room ? RoomId.Create(room) : null,
                secretRef,
                OptionalString(args, "outbound_url"),
                BoolOr(args, "enabled", true)));

            return Task.FromResult<object?>(new JsonObject
            {
                ["bindingId"] = bound.BindingId,
                ["adapterKind"] = bound.AdapterKind,
                ["channelKey"] = bound.ChannelKey,
                ["targetAgentId"] = bound.TargetAgentId.Value,
                ["enabled"] = bound.Enabled,
            });
        },
    };

    private static ToolDefinition SendTool(ExternalChannelRouter router) => new()
    {
        Name = ChannelSendTool,
        Description = "Send an outbound message through a bound external channel adapter.",
        Parameters = Schema(
            ["binding_id", "body"],
            new JsonObject
            {
                ["binding_id"] = new JsonObject { ["type"] = "string" },
                ["body"] = new JsonObject { ["type"] = "string" },
                ["thread_id"] = new JsonObject { ["type"] = "string" },
                ["reply_to_message_id"] = new JsonObject { ["type"] = "string" },
            }),
        Output = ObjectOutput(value => $"Sent outbound message on binding {value.GetProperty("bindingId").GetString()}."),
        Execute = async (args, exec) =>
        {
            var bindingId = RequireString(args, "binding_id");
            var body = RequireString(args, "body");
            await router.SendOutboundAsync(
                new ChannelOutboundMessage(bindingId, body, OptionalString(args, "thread_id"), OptionalString(args, "reply_to_message_id")),
                exec.Signal).ConfigureAwait(false);
            return new JsonObject
            {
                ["bindingId"] = bindingId,
                ["sent"] = true,
            };
        },
    };

    private static ToolDefinition AddTriggerTool(ExternalChannelRouter router) => new()
    {
        Name = TriggerAddTool,
        Description = "Register an automation trigger rule matching external or agent runtime events to wake or message a target agent.",
        Parameters = Schema(
            ["name", "source_kind", "event_pattern", "target_agent_id", "prompt_template"],
            new JsonObject
            {
                ["rule_id"] = new JsonObject { ["type"] = "string" },
                ["name"] = new JsonObject { ["type"] = "string" },
                ["source_kind"] = new JsonObject { ["type"] = "string" },
                ["event_pattern"] = new JsonObject { ["type"] = "string" },
                ["target_agent_id"] = new JsonObject { ["type"] = "string" },
                ["prompt_template"] = new JsonObject { ["type"] = "string" },
                ["wake_agent"] = new JsonObject { ["type"] = "boolean" },
                ["enabled"] = new JsonObject { ["type"] = "boolean" },
            }),
        Output = ObjectOutput(value => $"Added trigger {value.GetProperty("ruleId").GetString()} ({value.GetProperty("sourceKind").GetString()}:{value.GetProperty("eventPattern").GetString()})."),
        Execute = (args, _) =>
        {
            var ruleId = OptionalString(args, "rule_id") ?? $"trg-{Guid.NewGuid():N}";
            var rule = router.AddTrigger(new AutomationTriggerRule(
                ruleId,
                RequireString(args, "name"),
                RequireString(args, "source_kind"),
                RequireString(args, "event_pattern"),
                AgentId.Create(RequireString(args, "target_agent_id")),
                RequireString(args, "prompt_template"),
                BoolOr(args, "enabled", true),
                BoolOr(args, "wake_agent", true)));
            return Task.FromResult<object?>(new JsonObject
            {
                ["ruleId"] = rule.RuleId,
                ["name"] = rule.Name,
                ["sourceKind"] = rule.SourceKind,
                ["eventPattern"] = rule.EventPattern,
                ["targetAgentId"] = rule.TargetAgentId.Value,
            });
        },
    };

    private static ToolDefinition ListTriggersTool(ExternalChannelRouter router) => new()
    {
        Name = TriggerListTool,
        Description = "List configured automation trigger rules, optionally filtered by source kind.",
        Parameters = Schema(
            [],
            new JsonObject
            {
                ["source_kind"] = new JsonObject { ["type"] = "string" },
            }),
        Output = ObjectOutput(value => $"Listed {value.GetProperty("triggers").GetArrayLength()} trigger rule(s)."),
        Execute = (args, _) =>
        {
            var sourceFilter = OptionalString(args, "source_kind");
            var rows = new JsonArray();
            foreach (var rule in router.ListTriggers())
            {
                if (sourceFilter is not null && !string.Equals(rule.SourceKind, sourceFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                rows.Add(new JsonObject
                {
                    ["ruleId"] = rule.RuleId,
                    ["name"] = rule.Name,
                    ["sourceKind"] = rule.SourceKind,
                    ["eventPattern"] = rule.EventPattern,
                    ["targetAgentId"] = rule.TargetAgentId.Value,
                    ["enabled"] = rule.Enabled,
                    ["wakeAgent"] = rule.WakeAgent,
                });
            }
            return Task.FromResult<object?>(new JsonObject { ["triggers"] = rows });
        },
    };

    private static JsonObject Schema(string[] required, JsonObject properties)
    {
        var requiredArray = new JsonArray();
        foreach (var item in required)
            requiredArray.Add(item);
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = requiredArray,
            ["properties"] = properties,
        };
    }

    private static ToolOutputDefinition ObjectOutput(Func<JsonElement, string> summarize)
        => new(new JsonObject { ["type"] = "object" }, (_, value) => [new TextBlock(summarize(value))]);

    private static string RequireString(JsonElement args, string name)
        => OptionalString(args, name) ?? throw new ArgumentException($"Missing required string argument '{name}'.");

    private static string? OptionalString(JsonElement args, string name)
        => args.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() is { Length: > 0 } value ? value : null
            : null;

    private static bool BoolOr(JsonElement args, string name, bool fallback)
        => args.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : fallback;

    private sealed class DisposeBundle(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
