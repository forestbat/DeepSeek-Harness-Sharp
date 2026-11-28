using Dsh.Core;
using Dsh.Interaction;
using Dsh.Runtime;

namespace Dsh.Collab;

public static class CollabCommands
{
    private const string DefaultOperatorId = "owner";

    public static IDisposable Apply(Context ctx, ICollaborationRoomService rooms, IAgentDiscoveryService discovery)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false);
        if (commands is null)
            return new DisposeBundle([]);

        var roomCommand = commands.Register(new CommandDefinition
        {
            Name = "room",
            Description = "Manage collaboration rooms, invites, agents, and messages",
            Input = new CommandInputDescriptor("list | create <name> | invite <roomId> <role> | join <token> | post <roomId> <body> | read <roomId> | add-agent <roomId> <agentId>"),
            Subcommands =
            [
                new CommandDescriptor("list", "List collaboration rooms"),
                new CommandDescriptor("create", "Create a new room", new CommandInputDescriptor("<name>")),
                new CommandDescriptor("invite", "Create a room invite", new CommandInputDescriptor("<roomId> <role> [ttlSeconds] [maxUses]")),
                new CommandDescriptor("join", "Redeem an invite token", new CommandInputDescriptor("<token>")),
                new CommandDescriptor("post", "Post a message to a room", new CommandInputDescriptor("<roomId> <body>")),
                new CommandDescriptor("read", "Read messages in a room", new CommandInputDescriptor("<roomId> [afterSeq]")),
                new CommandDescriptor("add-agent", "Add an agent participant to a room", new CommandInputDescriptor("<roomId> <agentId>")),
            ],
            Handler = invocation => HandleRoomAsync(invocation, rooms),
        });

        var discoverCommand = commands.Register(new CommandDefinition
        {
            Name = "discover",
            Description = "Discover agents and manage explicit agent trust permissions",
            Input = new CommandInputDescriptor("list [query] | export <agentId> | import <json> | trust <cardId> | revoke <cardId> [reason]"),
            Subcommands =
            [
                new CommandDescriptor("list", "Discover available agents", new CommandInputDescriptor("[query]")),
                new CommandDescriptor("export", "Export local agent card JSON", new CommandInputDescriptor("<agentId>")),
                new CommandDescriptor("import", "Import a shared agent card JSON (untrusted)", new CommandInputDescriptor("<json>")),
                new CommandDescriptor("trust", "Grant explicit trust to a discovered card", new CommandInputDescriptor("<cardId>")),
                new CommandDescriptor("revoke", "Revoke trust from a discovered card", new CommandInputDescriptor("<cardId> [reason]")),
            ],
            Handler = invocation => HandleDiscoverAsync(invocation, discovery),
        });

        return new DisposeBundle([roomCommand, discoverCommand]);
    }

    private static async Task<CommandResult> HandleRoomAsync(CommandInvocation invocation, ICollaborationRoomService rooms)
    {
        var tokens = invocation.RawInput.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : "list";
        var rest = tokens.Length > 1 ? tokens[1].Trim() : "";
        var actor = new MessagePrincipal(PrincipalKind.Human, DefaultOperatorId, DefaultOperatorId);

        switch (sub)
        {
            case "list":
            {
                var list = rooms.ListRooms();
                if (list.Count == 0)
                    return new CommandResult.Success("No collaboration rooms.");
                return new CommandResult.Success(string.Join('\n', list.Select(r => $"{r.Id.Value}: {r.Name} ({r.Participants.Count} participants)")));
            }
            case "create":
            {
                if (string.IsNullOrWhiteSpace(rest))
                    return new CommandResult.Error("usage: /room create <name>");
                var created = await rooms.CreateRoomAsync(rest, actor, cancellationToken: invocation.Signal).ConfigureAwait(false);
                return new CommandResult.Success($"Created room {created.Id.Value}: {created.Name}");
            }
            case "invite":
            {
                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !Enum.TryParse<RoomRole>(parts[1], ignoreCase: true, out var role))
                    return new CommandResult.Error("usage: /room invite <roomId> <Owner|Approver|Collaborator|Observer|AgentParticipant> [ttlSeconds] [maxUses]");
                TimeSpan? ttl = parts.Length >= 3 && int.TryParse(parts[2], out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
                var maxUses = parts.Length >= 4 && int.TryParse(parts[3], out var uses) ? uses : 1;
                var invite = await rooms.CreateInviteAsync(RoomId.Create(parts[0]), actor, role, ttl, maxUses, invocation.Signal).ConfigureAwait(false);
                return new CommandResult.Success($"Invite {invite.InviteId} ({invite.Role}): {invite.Token}");
            }
            case "join":
            {
                if (string.IsNullOrWhiteSpace(rest))
                    return new CommandResult.Error("usage: /room join <token>");
                var joined = await rooms.RedeemInviteAsync(rest, actor, cancellationToken: invocation.Signal).ConfigureAwait(false);
                return new CommandResult.Success($"Joined room {joined.Id.Value}: {joined.Name}");
            }
            case "post":
            {
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return new CommandResult.Error("usage: /room post <roomId> <body>");
                var posted = await rooms.PostMessageAsync(RoomId.Create(parts[0]), actor, parts[1], cancellationToken: invocation.Signal).ConfigureAwait(false);
                return new CommandResult.Success($"Posted #{posted.Seq} ({posted.MessageId})");
            }
            case "read":
            {
                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1)
                    return new CommandResult.Error("usage: /room read <roomId> [afterSeq]");
                var afterSeq = parts.Length >= 2 && long.TryParse(parts[1], out var seq) ? seq : 0;
                var messages = rooms.ReadMessages(RoomId.Create(parts[0]), afterSeq);
                if (messages.Count == 0)
                    return new CommandResult.Success("No room messages.");
                return new CommandResult.Success(string.Join('\n', messages.Select(m => $"#{m.Seq} [{m.SenderDisplayName}] {m.Body}")));
            }
            case "add-agent":
            {
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return new CommandResult.Error("usage: /room add-agent <roomId> <agentId>");
                var room = await rooms.AddAgentAsync(RoomId.Create(parts[0]), actor, AgentId.Create(parts[1]), cancellationToken: invocation.Signal).ConfigureAwait(false);
                return new CommandResult.Success($"Added agent {parts[1]} to {room.Id.Value}");
            }
            default:
                return new CommandResult.Error($"Unknown /room subcommand '{sub}'.");
        }
    }

    private static async Task<CommandResult> HandleDiscoverAsync(CommandInvocation invocation, IAgentDiscoveryService discovery)
    {
        var tokens = invocation.RawInput.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : "list";
        var rest = tokens.Length > 1 ? tokens[1].Trim() : "";

        switch (sub)
        {
            case "list":
            {
                var cards = await discovery.DiscoverAsync(string.IsNullOrWhiteSpace(rest) ? null : rest, invocation.Signal).ConfigureAwait(false);
                if (cards.Count == 0)
                    return new CommandResult.Success("No agents discovered.");
                var lines = cards.Select(card =>
                {
                    var trust = discovery.GetTrust(card.CardId)?.Status.ToString() ?? nameof(AgentTrustStatus.Discovered);
                    return $"{card.CardId} [{card.BackendKind}] {card.Name} ({trust}) -> {card.Endpoint}";
                });
                return new CommandResult.Success(string.Join('\n', lines));
            }
            case "export":
            {
                if (string.IsNullOrWhiteSpace(rest))
                    return new CommandResult.Error("usage: /discover export <agentId>");
                return new CommandResult.Success(discovery.ExportLocalCardJson(AgentId.Create(rest)));
            }
            case "import":
            {
                if (string.IsNullOrWhiteSpace(rest))
                    return new CommandResult.Error("usage: /discover import <json>");
                var card = discovery.ImportCard(rest, "command");
                return card is null
                    ? new CommandResult.Error("Invalid agent card JSON.")
                    : new CommandResult.Success($"Imported {card.CardId} ({card.Name}) as PendingApproval (no permissions granted).");
            }
            case "trust":
            {
                if (string.IsNullOrWhiteSpace(rest))
                    return new CommandResult.Error("usage: /discover trust <cardId>");
                var trusted = discovery.GrantTrust(rest, new AgentTrustPermissions(), DefaultOperatorId, "Granted via /discover trust");
                return new CommandResult.Success($"Trusted {trusted.CardId} ({trusted.Status}).");
            }
            case "revoke":
            {
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1)
                    return new CommandResult.Error("usage: /discover revoke <cardId> [reason]");
                var revoked = discovery.RevokeTrust(parts[0], DefaultOperatorId, parts.Length > 1 ? parts[1] : null);
                return new CommandResult.Success($"Revoked {revoked.CardId} ({revoked.Status}).");
            }
            default:
                return new CommandResult.Error($"Unknown /discover subcommand '{sub}'.");
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
