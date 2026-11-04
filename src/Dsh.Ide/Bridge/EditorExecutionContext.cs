using Dsh.Core;

namespace Dsh.Ide.Bridge;

/// <summary>Execution routing owned by the composition root and its authenticated UI clients, never by a model or workspace.</summary>
public sealed class EditorExecutionContext
{
    public const string ServiceName = "editorExecutionContext";
    private readonly AsyncLocal<ITerminalHost?> _terminal = new();
    public ITerminalHost? TerminalHost => _terminal.Value;
    public bool IsRemoteHost { get; set; }

    public IEditorService BindTerminal(IEditorService editors, ITerminalHost terminal)
        => new TerminalService(this, editors, terminal);

    private sealed class TerminalService(EditorExecutionContext context, IEditorService editors, ITerminalHost terminal) : IEditorService
    {
        public IEditorCatalog Catalog => editors.Catalog;
        public EditorDecision? LastDecision => editors.LastDecision;
        public EditorWorkspace InspectWorkspace(string root) => editors.InspectWorkspace(root);
        public EditorBinding? GetBinding(string root, string? sessionId = null) => editors.GetBinding(root, sessionId);
        public void Bind(EditorBinding binding) => editors.Bind(binding);
        public async ValueTask<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        {
            var previous = context._terminal.Value;
            context._terminal.Value = terminal;
            try
            {
                return await editors.DoctorAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                context._terminal.Value = previous;
            }
        }

        public async ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default)
        {
            var previous = context._terminal.Value;
            context._terminal.Value = terminal;
            try
            {
                return await editors.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                context._terminal.Value = previous;
            }
        }
    }
}
