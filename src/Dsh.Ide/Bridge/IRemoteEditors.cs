using Dsh.Core;

namespace Dsh.Ide.Bridge;

/** Optional typed IDE capability; its connection owner controls the lifetime. */
public interface IRemoteEditors
{
    Task<EditorRemoteList> ListAsync(CancellationToken cancellationToken = default, bool refresh = false);
    Task<EditorRemoteStatus> StatusAsync(string workspaceRoot, CancellationToken cancellationToken = default, string? sessionId = null);
    Task<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default);
    Task<EditorResult> OpenAsync(EditorRequest request, CancellationToken cancellationToken = default);
    Task<IAsyncDisposable> RegisterClientAsync(IEditorService localService, CancellationToken cancellationToken = default,
        Func<EditorRequest, EditorRequest>? map = null, bool isLocalMachine = false, bool hasMappings = false);
    Task<bool> SelectForSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
