using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Agent;

public sealed record AgentTargetsToolResult(
    IReadOnlyList<RemoteExecutionTarget> Targets,
    IReadOnlyList<AgentProfile> Agents);

public sealed record AgentWakeToolResult(
    AgentId AgentId,
    SessionId SessionId,
    string Status);

public sealed record AgentSuspendToolResult(
    AgentId AgentId,
    bool Suspended);

/** Registers model-facing agent coordination tools on ToolRuntime with AOT-safe schemas and serialization. */
public static class AgentCoordinationTools
{
    public static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (tools is null)
            return new Bundle([]);

        List<IDisposable> subscriptions = [];
        IDisposable[] registrations =
        [
            tools.Register(TargetsTool(ctx)),
            tools.Register(SpawnTool(ctx)),
            tools.Register(WakeTool(ctx)),
            tools.Register(SuspendTool(ctx)),
            tools.Register(MessageTool(ctx)),
            tools.Register(SubscribeTool(ctx, subscriptions)),
        ];
        return new Bundle(registrations, subscriptions);
    }

    private static ToolDefinition TargetsTool(Context ctx) => new()
    {
        Name = "agent_targets",
        Description = "List available local and remote agent execution targets and known persistent agent profiles.",
        Parameters = EmptyObjectSchema(),
        Output = Output(Schema("""
            {
              "type": "object",
              "required": ["targets", "agents"],
              "properties": {
                "targets": { "type": "array" },
                "agents": { "type": "array" }
              }
            }
            """)),
        IsConcurrencySafe = _ => true,
        Execute = async (_, exec) =>
        {
            var router = ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName, false);
            var targets = router is null ? [] : await router.ListTargetsAsync(exec.Signal).ConfigureAwait(false);
            var manager = ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false);
            var profiles = manager?.List() ?? [];
            return JsonSerializer.SerializeToElement(
                new AgentTargetsToolResult(targets, profiles),
                DshAgentJsonContext.Default.AgentTargetsToolResult);
        },
    };

    private static ToolDefinition SpawnTool(Context ctx) => new()
    {
        Name = "agent_spawn",
        Description = "Spawn or dispatch a persistent agent on a local or remote execution target.",
        Parameters = Schema("""
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "name": { "type": "string", "description": "Display name for the spawned agent." },
                "prompt": { "type": "string", "description": "Optional initial task prompt to dispatch." },
                "target_id": { "type": "string", "description": "Target identifier from agent_targets." },
                "backend": { "type": "string", "description": "Backend kind: local, fleet, ma2a, a2a, acp, or plugin." },
                "cwd": { "type": "string", "description": "Optional working directory." },
                "agent_id": { "type": "string", "description": "Optional existing AgentId to dispatch to." },
                "agent_preset": { "type": "string", "description": "Optional agent preset name." },
                "wait_for_completion": { "type": "boolean", "description": "Wait for the initial prompt turn to finish." }
              }
            }
            """),
        Output = Output(Schema("""
            {
              "type": "object",
              "required": ["agentId", "backendKind", "targetId", "state", "completed"],
              "properties": {
                "agentId": { "type": "string" },
                "sessionId": { "type": "string" },
                "backendKind": { "type": "string" },
                "targetId": { "type": "string" },
                "state": { "type": "string" },
                "summary": { "type": "string" },
                "completed": { "type": "boolean" },
                "output": { "type": "string" },
                "error": { "type": "string" },
                "updatedAt": { "type": "integer" }
              }
            }
            """)),
        Execute = async (args, exec) =>
        {
            var router = ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName)
                ?? throw new InvalidOperationException("agent_spawn requires the agentExecutionRouter service");
            var backendText = StringArg(args, "backend");
            AgentBackendKind? preferredBackend = backendText is not null && Enum.TryParse<AgentBackendKind>(backendText, ignoreCase: true, out var parsedBackend)
                ? parsedBackend
                : null;
            var rawAgentId = StringArg(args, "agent_id");
            var request = new AgentDispatchRequest(
                TargetId: StringArg(args, "target_id"),
                PreferredBackend: preferredBackend,
                Prompt: StringArg(args, "prompt"),
                Cwd: StringArg(args, "cwd"),
                Name: StringArg(args, "name"),
                AgentId: rawAgentId is { Length: > 0 } ? AgentId.Create(rawAgentId) : null,
                WaitForCompletion: BoolArg(args, "wait_for_completion") ?? false,
                Sender: SenderFrom(exec.Agent),
                AgentPreset: StringArg(args, "agent_preset"));
            var handle = await router.DispatchAsync(request, exec.Signal).ConfigureAwait(false);
            var snapshot = await handle.GetStatusAsync(exec.Signal).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(snapshot, DshAgentJsonContext.Default.AgentExecutionSnapshot);
        },
    };

    private static ToolDefinition WakeTool(Context ctx) => new()
    {
        Name = "agent_wake",
        Description = "Wake a suspended or offline persistent agent and flush its offline mailbox.",
        Parameters = Schema("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["agent_id"],
              "properties": {
                "agent_id": { "type": "string", "description": "Target AgentId to wake." }
              }
            }
            """),
        Output = Output(Schema("""
            {
              "type": "object",
              "required": ["agentId", "sessionId", "status"],
              "properties": {
                "agentId": { "type": "string" },
                "sessionId": { "type": "string" },
                "status": { "type": "string" }
              }
            }
            """)),
        Execute = async (args, exec) =>
        {
            var manager = ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName)
                ?? throw new InvalidOperationException("agent_wake requires the persistentAgents service");
            var agentId = RequireStringArg(args, "agent_id");
            var agent = await manager.WakeAsync(AgentId.Create(agentId), exec.Signal).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(
                new AgentWakeToolResult(agent.AgentId, agent.Id, agent.Status.ToString()),
                DshAgentJsonContext.Default.AgentWakeToolResult);
        },
    };

    private static ToolDefinition SuspendTool(Context ctx) => new()
    {
        Name = "agent_suspend",
        Description = "Suspend a live persistent agent, releasing its runtime handle while keeping its profile and session on disk.",
        Parameters = Schema("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["agent_id"],
              "properties": {
                "agent_id": { "type": "string", "description": "Target AgentId to suspend." }
              }
            }
            """),
        Output = Output(Schema("""
            {
              "type": "object",
              "required": ["agentId", "suspended"],
              "properties": {
                "agentId": { "type": "string" },
                "suspended": { "type": "boolean" }
              }
            }
            """)),
        Execute = async (args, exec) =>
        {
            var manager = ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName)
                ?? throw new InvalidOperationException("agent_suspend requires the persistentAgents service");
            var targetId = AgentId.Create(RequireStringArg(args, "agent_id"));
            if (exec.Agent?.AgentId == targetId)
                throw new InvalidOperationException($"agent \"{targetId}\" cannot suspend itself from within its own running turn");
            await manager.SuspendAsync(targetId, exec.Signal).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(
                new AgentSuspendToolResult(targetId, true),
                DshAgentJsonContext.Default.AgentSuspendToolResult);
        },
    };

    private static ToolDefinition MessageTool(Context ctx) => new()
    {
        Name = "agent_message",
        Description = "Send an envelope to a target agent (live steer/followup or wake/offline delivery).",
        Parameters = Schema("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["target_agent_id", "body"],
              "properties": {
                "target_agent_id": { "type": "string", "description": "Target AgentId." },
                "body": { "type": "string", "description": "Message body." },
                "intent": { "type": "string", "description": "Message intent: notify, task, steer, or control." },
                "target_session_id": { "type": "string", "description": "Optional specific SessionId under the target agent." },
                "correlation_id": { "type": "string", "description": "Optional correlation id." },
                "idempotency_key": { "type": "string", "description": "Optional idempotency key for deduplication." },
                "wake_if_suspended": { "type": "boolean", "description": "Wake suspended target agent if true (default true)." }
              }
            }
            """),
        Output = Output(Schema("""
            {
              "type": "object",
              "required": ["envelopeId", "status", "targetAgentId"],
              "properties": {
                "envelopeId": { "type": "string" },
                "status": { "type": "string" },
                "targetAgentId": { "type": "string" },
                "deliveredSessionId": { "type": "string" },
                "reason": { "type": "string" }
              }
            }
            """)),
        Execute = async (args, exec) =>
        {
            var bus = ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName)
                ?? throw new InvalidOperationException("agent_message requires the agentBus service");
            var targetId = AgentId.Create(RequireStringArg(args, "target_agent_id"));
            var body = RequireStringArg(args, "body");
            var intentText = StringArg(args, "intent");
            var intent = intentText is not null && Enum.TryParse<AgentMessageIntent>(intentText, ignoreCase: true, out var parsedIntent)
                ? parsedIntent
                : AgentMessageIntent.Task;
            var targetSessionText = StringArg(args, "target_session_id");
            var trust = AgentTurnAttribution.Current?.TrustLevel == MessageTrustLevel.ExternalAgent
                ? MessageTrustLevel.ExternalAgent
                : MessageTrustLevel.TrustedPeerAgent;
            var envelope = new AgentEnvelope(
                $"env-{Guid.NewGuid():N}",
                targetId,
                targetSessionText is { Length: > 0 } ? SessionId.Create(targetSessionText) : null,
                SenderFrom(exec.Agent),
                trust,
                intent,
                body,
                StringArg(args, "correlation_id"),
                StringArg(args, "idempotency_key"),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                BoolArg(args, "wake_if_suspended") ?? true);
            var delivery = await bus.SendAsync(envelope, exec.Signal).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(delivery, DshAgentJsonContext.Default.AgentDeliveryResult);
        },
    };

    private static readonly AgentRuntimeEventKind[] DefaultWakeSubscriptionKinds =
    [
        AgentRuntimeEventKind.TurnCompleted,
        AgentRuntimeEventKind.TurnFailed,
        AgentRuntimeEventKind.ApprovalRequested,
    ];

    private static ToolDefinition SubscribeTool(Context ctx, List<IDisposable> subscriptions) => new()
    {
        Name = "agent_subscribe",
        Description = "Subscribe an agent to runtime lifecycle events emitted by another agent.",
        Parameters = Schema("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["publisher_agent_id"],
              "properties": {
                "publisher_agent_id": { "type": "string", "description": "Publisher AgentId to observe." },
                "subscriber_agent_id": { "type": "string", "description": "Subscriber AgentId (defaults to caller agent)." },
                "kinds": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional list of AgentRuntimeEventKind values."
                },
                "wake_subscriber": { "type": "boolean", "description": "Wake subscriber if suspended when event occurs (default true)." }
              }
            }
            """),
        Output = Output(Schema("""
            {
              "type": "object",
              "required": ["subscriptionId", "publisherAgentId", "wakeSubscriber"],
              "properties": {
                "subscriptionId": { "type": "string" },
                "publisherAgentId": { "type": "string" },
                "subscriberAgentId": { "type": "string" },
                "kinds": { "type": "array", "items": { "type": "string" } },
                "wakeSubscriber": { "type": "boolean" }
              }
            }
            """)),
        Execute = (args, exec) =>
        {
            var bus = ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName)
                ?? throw new InvalidOperationException("agent_subscribe requires the agentBus service");
            var publisherId = AgentId.Create(RequireStringArg(args, "publisher_agent_id"));
            var subscriberText = StringArg(args, "subscriber_agent_id");
            AgentId? subscriberId = subscriberText is { Length: > 0 }
                ? AgentId.Create(subscriberText)
                : exec.Agent?.AgentId;
            var wakeSubscriber = BoolArg(args, "wake_subscriber") ?? true;
            var kinds = ParseKinds(args);
            if (kinds is null && wakeSubscriber)
                kinds = DefaultWakeSubscriptionKinds;
            var subscription = new AgentEventSubscription(
                $"sub-{Guid.NewGuid():N}",
                publisherId,
                subscriberId,
                kinds,
                wakeSubscriber);
            var handle = bus.Subscribe(subscription);
            lock (subscriptions)
                subscriptions.Add(handle);
            return Task.FromResult<object?>(
                JsonSerializer.SerializeToElement(subscription, DshAgentJsonContext.Default.AgentEventSubscription));
        },
    };

    private static IReadOnlyList<AgentRuntimeEventKind>? ParseKinds(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("kinds", out var element) || element.ValueKind != JsonValueKind.Array)
            return null;
        var parsed = new List<AgentRuntimeEventKind>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && Enum.TryParse<AgentRuntimeEventKind>(item.GetString(), ignoreCase: true, out var kind))
            {
                parsed.Add(kind);
            }
        }
        return parsed.Count > 0 ? parsed : null;
    }

    private static MessagePrincipal SenderFrom(IAgent? agent)
        => agent is null
            ? new MessagePrincipal(PrincipalKind.System, "coordination-tools", "AgentCoordinationTools")
            : new MessagePrincipal(PrincipalKind.Agent, agent.AgentId.Value);

    private static string RequireStringArg(JsonElement args, string name)
        => StringArg(args, name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"\"{name}\" must be a non-empty string");

    private static string? StringArg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool? BoolArg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    private static ToolOutputDefinition Output(JsonObject schema)
        => new(schema, (_, value) => [new TextBlock(value.GetRawText())]);

    private static JsonObject EmptyObjectSchema()
        => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };

    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();

    private sealed class Bundle(IDisposable[] registrations, List<IDisposable>? subscriptions = null) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations)
                registration.Dispose();
            if (subscriptions is null)
                return;
            lock (subscriptions)
            {
                foreach (var subscription in subscriptions)
                    subscription.Dispose();
                subscriptions.Clear();
            }
        }
    }
}
