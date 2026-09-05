using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;

namespace Dsh.Goal;

public sealed record GoalChangePayload : SessionEventPayload
{
    public const string EventType = "goal/change";

    public override string Type => EventType;

    public required JsonElement Raw { get; init; }

    public GoalChange? Decoded { get; init; }

    public static GoalChangePayload FromChange(GoalChange change)
        => new() { Raw = JsonDocument.Parse(Serialize(change)).RootElement, Decoded = change };

    public static void RegisterCodec()
        => SessionEventCodec.Register<GoalChangePayload>(
            EventType,
            static (element, _) => new GoalChangePayload { Raw = element.Clone() },
            static (payload, writer, _) => payload.Raw.WriteTo(writer));

    private static string Serialize(GoalChange change)
    {
        var node = new JsonObject
        {
            ["kind"] = EventType,
            ["version"] = GoalChange.ChangeVersion,
        };
        switch (change)
        {
            case GoalChange.Snapshot snapshot:
            {
                node["operation"] = GoalNames.Of(snapshot.Operation);
                node["goal"] = SerializeSnapshot(snapshot.Goal);
                node["roundsStarted"] = snapshot.RoundsStarted;
                node["createdAt"] = snapshot.CreatedAt;
                node["updatedAt"] = snapshot.UpdatedAt;
                break;
            }
            case GoalChange.Clear clear:
            {
                node["operation"] = GoalNames.Of(GoalOperation.Clear);
                node["cleared"] = new JsonObject
                {
                    ["id"] = clear.Cleared.Id.Value,
                    ["revision"] = clear.Cleared.Revision,
                };
                node["clearedAt"] = clear.ClearedAt;
                break;
            }
        }
        return node.ToJsonString();
    }

    private static JsonObject SerializeSnapshot(GoalSnapshot goal)
    {
        var node = new JsonObject
        {
            ["id"] = goal.Id.Value,
            ["revision"] = goal.Revision,
            ["objective"] = goal.Objective,
            ["phase"] = GoalNames.Of(goal.Phase),
            ["maxGoalRounds"] = goal.MaxGoalRounds,
        };
        if (goal.BlockedReason is { } reason)
        {
            node["blockedReason"] = new JsonObject
            {
                ["code"] = reason.Code,
                ["message"] = reason.Message,
            };
        }
        return node;
    }
}
