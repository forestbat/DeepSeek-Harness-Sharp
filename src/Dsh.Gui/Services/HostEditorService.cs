using System.Collections.Concurrent;
using Dsh.Core;
using Dsh.Ide.Bridge;

namespace Dsh.Gui.Services;

/// <summary>Core service facade for an explicit Host destination; synchronous projections never block the UI on RPC.</summary>
public sealed class HostEditorService(IRemoteEditors editor) : IEditorService
{
    private readonly RemoteCatalog _catalog = new(editor);
    private readonly ConcurrentDictionary<string, EditorRemoteStatus> _status = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EditorBinding> _bindings = new(StringComparer.Ordinal);
    private EditorDecision? _lastDecision;
    public IEditorCatalog Catalog => _catalog;
    public EditorDecision? LastDecision => Volatile.Read(ref _lastDecision);

    public async Task<EditorRemoteStatus> RefreshAsync(string workspaceRoot, CancellationToken cancellationToken = default, string? sessionId = null)
    {
        var status = await editor.StatusAsync(workspaceRoot, cancellationToken, sessionId).ConfigureAwait(false);
        _status[workspaceRoot] = status;
        if (status.Binding is { } binding)
            _bindings[Key(workspaceRoot, sessionId)] = binding;
        else
            _bindings.TryRemove(Key(workspaceRoot, sessionId), out _);
        Volatile.Write(ref _lastDecision, status.LastDecision);
        return status;
    }
    public EditorWorkspace InspectWorkspace(string root)
        => _status.TryGetValue(root, out var status) ? status.Workspace : new EditorWorkspace(root, []);
    public EditorBinding? GetBinding(string root, string? sessionId = null)
        => _bindings.TryGetValue(Key(root, sessionId), out var binding) ? binding : _status.TryGetValue(root, out var status) && (status.Binding?.SessionId is null || status.Binding.SessionId == sessionId) ? status.Binding : null;
    public void Bind(EditorBinding binding) => _bindings[Key(binding.WorkspaceRoot, binding.SessionId)] = binding;
    private static string Key(string root, string? session) => root + "\0" + session;
    public async ValueTask<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        => await editor.DoctorAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
    public async ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Editor is null && _bindings.TryGetValue(Key(request.WorkspaceRoot, request.SessionId), out var binding))
            request = request with { Editor = binding.Editor.Value, RememberSelection = true };
        var result = await editor.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _lastDecision, result.Decision);
        if (result.Success && !result.Printed && !request.DryRun && request.RememberSelection && result.Editor is { } selected)
            Bind(new(request.WorkspaceRoot, selected, request.SessionId));
        return result;
    }

    private sealed class RemoteCatalog(IRemoteEditors editor) : IEditorCatalog
    {
        private EditorDescriptor[] _descriptors = [];
        public IReadOnlyList<EditorDescriptor> Descriptors => Volatile.Read(ref _descriptors);
        public EditorDescriptor? Find(EditorId id) => Descriptors.FirstOrDefault(value => value.Id == id);
        public IDisposable Register(EditorDescriptor descriptor)
            => throw new NotSupportedException("Editor templates are configured on the destination, not through the Host protocol.");
        public async ValueTask<IReadOnlyList<EditorInstallation>> DetectAsync(bool refresh = false, CancellationToken cancellationToken = default)
        {
            var list = await editor.ListAsync(cancellationToken, refresh).ConfigureAwait(false);
            Volatile.Write(ref _descriptors, list.Descriptors.ToArray());
            return list.Installations;
        }
    }
}