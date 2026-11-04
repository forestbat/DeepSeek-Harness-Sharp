using Dsh.Core;
using Dsh.Ide.Bridge;
using Dsh.Llm;
using Dsh.Ide.Triggers;
using Dsh.Runtime;

namespace Dsh.Ide.Surfaces;

public sealed class EditorInteractionService(Context ctx, IEditorService editors, EditorTriggerService triggers) : IEditorService
{
    public IEditorCatalog Catalog => editors.Catalog;
    public EditorDecision? LastDecision => editors.LastDecision;
    public EditorWorkspace InspectWorkspace(string root) => editors.InspectWorkspace(root);
    public EditorBinding? GetBinding(string root, string? sessionId = null) => editors.GetBinding(root, sessionId);
    public void Bind(EditorBinding binding) => editors.Bind(binding);
    public ValueTask<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        => editors.DoctorAsync(workspaceRoot, cancellationToken);

    public async ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TriggerId is not null || request.Print || request.DryRun)
            return await editors.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.TurnId is null && request.SessionId is { } sessionId)
        {
            var session = ctx.Get<SessionStore>(SessionStore.ServiceName, false)?.Get(SessionId.Create(sessionId));
            request = request with { TurnId = EditorSessionContext.TurnId(ctx, session) };
        }
        var outcomes = await triggers.ExecuteAsync(new("manual", request)
        {
            SessionSource = "manual",
            MachineType = MachineTypeFor(request.SessionId),
        }, cancellationToken).ConfigureAwait(false);
        var results = outcomes.SelectMany(outcome => outcome.Results).ToArray();
        return results.Length switch
        {
            0 => await editors.LaunchAsync(request, cancellationToken).ConfigureAwait(false),
            1 => results[0],
            _ => new EditorResult
            {
                Success = results.All(result => result.Success),
                Error = results.FirstOrDefault(result => !result.Success)?.Error ?? EditorErrorCode.None,
                Message = $"Completed {results.Length} editor actions; inspect results for each launch.",
                Results = results,
                Decision = results[^1].Decision,
            },
        };
    }

    private string MachineTypeFor(string? sessionId)
        => ctx.Get<EditorClientDispatcher>(EditorClientDispatcher.ServiceName, false)?.TryIsLocalMachine(sessionId) is { } local
            ? local ? "local" : "remote"
            : ctx.Get<EditorExecutionContext>(EditorExecutionContext.ServiceName, false)?.IsRemoteHost == true ? "remote" : "local";
}
