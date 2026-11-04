using Dsh.Core;

namespace Dsh.Ide.Bridge;

/// <summary>Client dispatch is deliberately outside the local launcher's installation and template pipeline.</summary>
public sealed class EditorBridgeService(IEditorService local, EditorClientDispatcher clients) : IEditorService
{
    public IEditorCatalog Catalog => local.Catalog;
    public EditorDecision? LastDecision
    {
        get => Volatile.Read(ref field) ?? local.LastDecision;
        private set => Volatile.Write(ref field, value);
    }
    public EditorWorkspace InspectWorkspace(string root) => local.InspectWorkspace(root);
    public EditorBinding? GetBinding(string root, string? sessionId = null) => local.GetBinding(root, sessionId);
    public void Bind(EditorBinding binding) => local.Bind(binding);
    public ValueTask<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        => local.DoctorAsync(workspaceRoot, cancellationToken);

    public async ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ExecutionTarget == EditorExecutionTarget.Automatic)
            request = request with { ExecutionTarget = clients.TargetFor(request.SessionId) ?? EditorExecutionTarget.Local };
        if (request.ExecutionTarget != EditorExecutionTarget.Client || request.Print)
        {
            var localResult = await local.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
            LastDecision = localResult.Decision;
            return localResult;
        }
        var result = await clients.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        LastDecision = result.Decision;
        return result;
    }
}
