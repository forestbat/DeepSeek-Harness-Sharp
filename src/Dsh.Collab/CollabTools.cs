using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Collab;

public static class CollabTools
{
    public const string RoomCreateTool = "room_create";
    public const string RoomInviteTool = "room_invite";
    public const string RoomJoinTool = "room_join";
    public const string RoomPostTool = "room_post";
    public const string RoomReadTool = "room_read";
    public const string AgentDiscoverTool = "agent_discover";
    public const string AgentTrustTool = "agent_trust";

    public static IDisposable Apply(Context ctx, ICollaborationRoomService rooms, IAgentDiscoveryService discovery)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (tools is null)
            return new DisposeBundle([]);

        return new DisposeBundle(
        [
            tools.Register(CreateRoomTool(rooms)),
            tools.Register(InviteRoomTool(rooms)),
            tools.Register(JoinRoomTool(rooms)),
            tools.Register(PostRoomTool(rooms)),
            tools.Register(ReadRoomTool(rooms)),
            tools.Register(DiscoverAgentTool(discovery)),
            tools.Register(TrustAgentTool(discovery)),
        ]);
    }

    private static ToolDefinition CreateRoomTool(ICollaborationRoomService rooms) => new()
    {
        Name = RoomCreateTool,
        Description = "Create a multi-user collaboration room and add the caller as owner.",
        Parameters = Schema(
            ["name"],
            new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Room display name." },
                ["allow_collaborator_prompts"] = new JsonObject { ["type"] = "boolean" },
                ["allow_collaborator_approvals"] = new JsonObject { ["type"] = "boolean" },
                ["allow_observer_read"] = new JsonObject { ["type"] = "boolean" },
                ["require_explicit_agent_mention"] = new JsonObject { ["type"] = "boolean" },
            }),
        Output = ObjectOutput(value => $"Created room {value.GetProperty("roomId").GetString()} ({value.GetProperty("name").GetString()})."),
        Execute = async (args, exec) =>
        {
            var name = RequireString(args, "name");
            var policy = new RoomPolicy(
                BoolOr(args, "allow_collaborator_prompts", true),
                BoolOr(args, "allow_collaborator_approvals", false),
                BoolOr(args, "allow_observer_read", true),
                BoolOr(args, "require_explicit_agent_mention", true));
            var room = await rooms.CreateRoomAsync(name, CallerPrincipal(exec), policy, exec.Signal).ConfigureAwait(false);
            return new JsonObject
            {
                ["roomId"] = room.Id.Value,
                ["name"] = room.Name,
                ["ownerPrincipalId"] = room.OwnerPrincipalId,
            };
        },
    };

    private static ToolDefinition InviteRoomTool(ICollaborationRoomService rooms) => new()
    {
        Name = RoomInviteTool,
        Description = "Create a cryptographically random invite token for a collaboration room.",
        Parameters = Schema(
            ["room_id", "role"],
            new JsonObject
            {
                ["room_id"] = new JsonObject { ["type"] = "string" },
                ["role"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("Owner", "Approver", "Collaborator", "Observer", "AgentParticipant"),
                },
                ["ttl_seconds"] = new JsonObject { ["type"] = "integer" },
                ["max_uses"] = new JsonObject { ["type"] = "integer" },
            }),
        Output = ObjectOutput(value => $"Created invite {value.GetProperty("inviteId").GetString()} (role {value.GetProperty("role").GetString()})."),
        Execute = async (args, exec) =>
        {
            var roomId = RoomId.Create(RequireString(args, "room_id"));
            var role = Enum.Parse<RoomRole>(RequireString(args, "role"), ignoreCase: true);
            var ttlSeconds = IntOr(args, "ttl_seconds");
            var maxUses = IntOr(args, "max_uses") ?? 1;
            TimeSpan? ttl = ttlSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
            var invite = await rooms.CreateInviteAsync(roomId, CallerPrincipal(exec), role, ttl, maxUses, exec.Signal).ConfigureAwait(false);
            return new JsonObject
            {
                ["inviteId"] = invite.InviteId,
                ["roomId"] = invite.RoomId.Value,
                ["token"] = invite.Token,
                ["role"] = invite.Role.ToString(),
                ["expiresAt"] = invite.ExpiresAt,
                ["maxUses"] = invite.MaxUses,
            };
        },
    };

    private static ToolDefinition JoinRoomTool(ICollaborationRoomService rooms) => new()
    {
        Name = RoomJoinTool,
        Description = "Redeem an invite token to join a collaboration room.",
        Parameters = Schema(
            ["token"],
            new JsonObject
            {
                ["token"] = new JsonObject { ["type"] = "string" },
                ["agent_id"] = new JsonObject { ["type"] = "string" },
            }),
        Output = ObjectOutput(value => $"Joined room {value.GetProperty("roomId").GetString()} ({value.GetProperty("name").GetString()})."),
        Execute = async (args, exec) =>
        {
            var token = RequireString(args, "token");
            var agentId = OptionalString(args, "agent_id") is { } raw ? AgentId.Create(raw) : (AgentId?)null;
            var room = await rooms.RedeemInviteAsync(token, CallerPrincipal(exec), agentId, exec.Signal).ConfigureAwait(false);
            return new JsonObject
            {
                ["roomId"] = room.Id.Value,
                ["name"] = room.Name,
                ["participantCount"] = room.Participants.Count,
            };
        },
    };

    private static ToolDefinition PostRoomTool(ICollaborationRoomService rooms) => new()
    {
        Name = RoomPostTool,
        Description = "Post a message to a collaboration room, optionally targeting an @agent participant.",
        Parameters = Schema(
            ["room_id", "body"],
            new JsonObject
            {
                ["room_id"] = new JsonObject { ["type"] = "string" },
                ["body"] = new JsonObject { ["type"] = "string" },
                ["target_agent_id"] = new JsonObject { ["type"] = "string" },
                ["reply_to_message_id"] = new JsonObject { ["type"] = "string" },
            }),
        Output = ObjectOutput(value => $"Posted room message {value.GetProperty("messageId").GetString()} (seq {value.GetProperty("seq").GetInt64()})."),
        Execute = async (args, exec) =>
        {
            var roomId = RoomId.Create(RequireString(args, "room_id"));
            var body = RequireString(args, "body");
            var targetAgentId = OptionalString(args, "target_agent_id") is { } raw ? AgentId.Create(raw) : (AgentId?)null;
            var replyTo = OptionalString(args, "reply_to_message_id");
            var message = await rooms.PostMessageAsync(roomId, CallerPrincipal(exec), body, targetAgentId, replyTo, exec.Signal).ConfigureAwait(false);
            return new JsonObject
            {
                ["messageId"] = message.MessageId,
                ["roomId"] = message.RoomId.Value,
                ["seq"] = message.Seq,
                ["targetAgentId"] = message.TargetAgentId?.Value,
            };
        },
    };

    private static ToolDefinition ReadRoomTool(ICollaborationRoomService rooms) => new()
    {
        Name = RoomReadTool,
        Description = "Read messages from a collaboration room after a sequence cursor.",
        Parameters = Schema(
            ["room_id"],
            new JsonObject
            {
                ["room_id"] = new JsonObject { ["type"] = "string" },
                ["after_seq"] = new JsonObject { ["type"] = "integer" },
                ["limit"] = new JsonObject { ["type"] = "integer" },
            }),
        Output = ObjectOutput(value => $"Read {value.GetProperty("messages").GetArrayLength()} room message(s)."),
        Execute = (args, _) =>
        {
            var roomId = RoomId.Create(RequireString(args, "room_id"));
            var afterSeq = LongOr(args, "after_seq") ?? 0;
            var limit = IntOr(args, "limit") ?? 100;
            var messages = rooms.ReadMessages(roomId, afterSeq, limit);
            var rows = new JsonArray();
            foreach (var message in messages)
            {
                rows.Add(new JsonObject
                {
                    ["messageId"] = message.MessageId,
                    ["seq"] = message.Seq,
                    ["senderPrincipalId"] = message.SenderPrincipalId,
                    ["senderDisplayName"] = message.SenderDisplayName,
                    ["body"] = message.Body,
                    ["timestamp"] = message.Timestamp,
                    ["targetAgentId"] = message.TargetAgentId?.Value,
                    ["replyToMessageId"] = message.ReplyToMessageId,
                });
            }
            return Task.FromResult<object?>(new JsonObject
            {
                ["roomId"] = roomId.Value,
                ["messages"] = rows,
            });
        },
    };

    private static ToolDefinition DiscoverAgentTool(IAgentDiscoveryService discovery) => new()
    {
        Name = AgentDiscoverTool,
        Description = "Discover local or remote agents, import a shared agent card, or export a local agent card. Discovery never grants trust permissions.",
        Parameters = Schema(
            [],
            new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string" },
                ["import_card_json"] = new JsonObject { ["type"] = "string" },
                ["export_agent_id"] = new JsonObject { ["type"] = "string" },
            }),
        Output = ObjectOutput(value =>
        {
            if (value.TryGetProperty("exportedCardJson", out _))
                return "Exported local agent card.";
            if (value.TryGetProperty("importedCardId", out var imported))
                return $"Imported card {imported.GetString()} (untrusted until explicitly approved).";
            return $"Discovered {value.GetProperty("cards").GetArrayLength()} agent card(s).";
        }),
        Execute = async (args, exec) =>
        {
            if (OptionalString(args, "export_agent_id") is { } exportId)
            {
                var json = discovery.ExportLocalCardJson(AgentId.Create(exportId));
                return new JsonObject { ["exportedCardJson"] = json };
            }
            if (OptionalString(args, "import_card_json") is { } cardJson)
            {
                var card = discovery.ImportCard(cardJson, "tool")
                    ?? throw new InvalidOperationException("Invalid agent card JSON.");
                return new JsonObject
                {
                    ["importedCardId"] = card.CardId,
                    ["name"] = card.Name,
                    ["status"] = discovery.GetTrust(card.CardId)?.Status.ToString() ?? AgentTrustStatus.PendingApproval.ToString(),
                };
            }
            var query = OptionalString(args, "query");
            var cards = await discovery.DiscoverAsync(query, exec.Signal).ConfigureAwait(false);
            var rows = new JsonArray();
            foreach (var card in cards)
            {
                rows.Add(new JsonObject
                {
                    ["cardId"] = card.CardId,
                    ["name"] = card.Name,
                    ["backendKind"] = card.BackendKind.ToString(),
                    ["endpoint"] = card.Endpoint,
                    ["trustStatus"] = discovery.GetTrust(card.CardId)?.Status.ToString() ?? AgentTrustStatus.Discovered.ToString(),
                });
            }
            return new JsonObject { ["cards"] = rows };
        },
    };

    private static ToolDefinition TrustAgentTool(IAgentDiscoveryService discovery) => new()
    {
        Name = AgentTrustTool,
        Description = "List, inspect, grant, or revoke explicit trust permissions for discovered agents.",
        Parameters = Schema(
            ["action"],
            new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("list", "get", "grant", "revoke"),
                },
                ["card_id"] = new JsonObject { ["type"] = "string" },
                ["allow_messaging"] = new JsonObject { ["type"] = "boolean" },
                ["allow_task_delegation"] = new JsonObject { ["type"] = "boolean" },
                ["allow_event_subscription"] = new JsonObject { ["type"] = "boolean" },
                ["allow_room_participation"] = new JsonObject { ["type"] = "boolean" },
                ["allow_file_access"] = new JsonObject { ["type"] = "boolean" },
                ["allow_tool_control"] = new JsonObject { ["type"] = "boolean" },
                ["note"] = new JsonObject { ["type"] = "string" },
            }),
        Output = ObjectOutput(value =>
            value.TryGetProperty("status", out var status)
                ? $"Trust status for {value.GetProperty("cardId").GetString()}: {status.GetString()}."
                : $"Listed {value.GetProperty("records").GetArrayLength()} trust record(s)."),
        Execute = (args, exec) =>
        {
            var action = RequireString(args, "action").ToLowerInvariant();
            if (action == "list")
            {
                var records = new JsonArray();
                foreach (var record in discovery.ListTrust())
                    records.Add(SerializeTrust(record));
                return Task.FromResult<object?>(new JsonObject { ["records"] = records });
            }

            var cardId = RequireString(args, "card_id");
            var actor = CallerPrincipal(exec).Id;
            if (action is "grant" or "revoke" && AgentTurnAttribution.Current?.TrustLevel == MessageTrustLevel.ExternalAgent)
                throw new UnauthorizedAccessException("ExternalAgent turns cannot modify agent trust permissions.");
            return action switch
            {
                "get" => Task.FromResult<object?>(SerializeTrust(discovery.GetTrust(cardId)
                    ?? throw new InvalidOperationException($"No trust record found for '{cardId}'."))),
                "grant" => Task.FromResult<object?>(SerializeTrust(discovery.GrantTrust(
                    cardId,
                    new AgentTrustPermissions(
                        BoolOr(args, "allow_messaging", true),
                        BoolOr(args, "allow_task_delegation", false),
                        BoolOr(args, "allow_event_subscription", true),
                        BoolOr(args, "allow_room_participation", false),
                        BoolOr(args, "allow_file_access", false),
                        BoolOr(args, "allow_tool_control", false)),
                    actor,
                    OptionalString(args, "note")))),
                "revoke" => Task.FromResult<object?>(SerializeTrust(discovery.RevokeTrust(cardId, actor, OptionalString(args, "note")))),
                _ => throw new ArgumentException($"Unsupported agent_trust action '{action}'."),
            };
        },
    };

    private static JsonObject SerializeTrust(AgentTrustRecord record) => new()
    {
        ["cardId"] = record.CardId,
        ["name"] = record.Name,
        ["endpoint"] = record.Endpoint,
        ["status"] = record.Status.ToString(),
        ["allowMessaging"] = record.Permissions.AllowMessaging,
        ["allowTaskDelegation"] = record.Permissions.AllowTaskDelegation,
        ["allowEventSubscription"] = record.Permissions.AllowEventSubscription,
        ["allowRoomParticipation"] = record.Permissions.AllowRoomParticipation,
        ["allowFileAccess"] = record.Permissions.AllowFileAccess,
        ["allowToolControl"] = record.Permissions.AllowToolControl,
        ["updatedBy"] = record.UpdatedByPrincipalId,
        ["note"] = record.Note,
    };

    private static MessagePrincipal CallerPrincipal(ToolRunContext exec)
    {
        if (AgentTurnAttribution.Current is { } attribution)
            return attribution.Sender;
        if (exec.Agent is { } agent)
            return new MessagePrincipal(PrincipalKind.Human, agent.AgentId.Value, agent.AgentId.Value);
        return new MessagePrincipal(PrincipalKind.Human, "owner", "owner");
    }

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

    private static int? IntOr(JsonElement args, string name)
        => args.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : null;

    private static long? LongOr(JsonElement args, string name)
        => args.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt64()
            : null;

    private sealed class DisposeBundle(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
