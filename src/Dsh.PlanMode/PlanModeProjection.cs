using System.Text.Json;
using Dsh.Core;
using Dsh.Interaction;

namespace Dsh.PlanMode;

public sealed record PlanRunning(string CommandId, bool Wanted);

public sealed record PlanUnitState
{
    public bool Active { get; init; }
    public bool? Wanted { get; init; }
    public PlanRunning? Running { get; init; }
    public bool? ActiveAtLastHeader { get; init; }
}

public sealed record PlanProjectionView(bool Active, bool Pending);

public sealed record PlanModePayload(bool Active) : SessionEventPayload
{
    public override string Type => "plan/mode";

    public static void RegisterCodec()
        => SessionEventCodec.Register<PlanModePayload>("plan/mode");
}

public static class PlanProjectionDefinition
{
    public const string Key = "plan";
    public const int StateVersion = 3;

    public static readonly SessionProjectionDefinition<PlanUnitState> Instance = new(
        Key,
        StateVersion,
        (_, _) => new PlanUnitState(),
        Apply);

    public static PlanProjectionView View(PlanUnitState state)
    {
        var wanted = state.Running?.Wanted ?? state.Wanted;
        return new PlanProjectionView(state.Active, wanted is { } target && target != state.Active);
    }

    private static PlanUnitState Apply(PlanUnitState state, SessionEvent sessionEvent)
        => sessionEvent.Data switch
        {
            CommandRunPayload { Name: "plan" } run when run.Args is not null
                => state with
                {
                    Running = new PlanRunning(run.CommandId, run.Args.Trim() != "off"),
                },
            CommandDonePayload done when state.Running?.CommandId == done.CommandId
                => state with
                {
                    Wanted = done.Kind == "success" && state.Running.Wanted != state.Active
                        ? state.Running.Wanted
                        : null,
                    Running = null,
                },
            PlanModePayload planMode => state with
            {
                Active = planMode.Active,
                Wanted = null,
            },
            RequestHeaderPayload => state with
            {
                ActiveAtLastHeader = state.Active,
            },
            _ => state,
        };
}
