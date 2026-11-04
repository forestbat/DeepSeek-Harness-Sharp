using Dsh.Core;
using Dsh.Workspace;
using Dsh.Ide.Bridge;

namespace Dsh.Gui.Services;

/// <summary>Owns the selected chat's authenticated, typed IDE client registration.</summary>
public sealed class GuiEditorClientBinding(IEditorService? local, Func<string, EditorRequest, EditorRequest>? trustedMap = null, Func<string, string, bool>? hasMapping = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IWorkspaceHost? _client;
    private IRemoteEditors? _editors;
    private IAsyncDisposable? _registration;
    private string? _session;
    private string? _workspace;
    private string? _machine;
    private long _generation;
    private bool _disposed;

    public async Task UpdateAsync(IWorkspaceMachine? machine, string session, string workspace)
    {
        var generation = Interlocked.Increment(ref _generation);
        if (local is null || _disposed) return;
        try
        {
            await _gate.WaitAsync(_lifetime.Token);
            try
            {
                if (generation != Volatile.Read(ref _generation) || _disposed) return;
                var machineName = machine?.Machine.Name;
                var client = machine?.State == MachineConnectionState.Connected ? (await machine.WaitConnectedAsync(_lifetime.Token)).Client : null;
                if (!ReferenceEquals(_client, client) || _workspace != workspace || _machine != machineName)
                {
                    if (_registration is not null) await _registration.DisposeAsync();
                    _registration = null;
                    _client = null;
                    _editors = null;
                    _session = null;
                    _workspace = workspace;
                    _machine = machineName;
                    if (client is null || machineName is null || client.GetCapability<IRemoteEditors>() is not { } editors) return;
                    _registration = await editors.RegisterClientAsync(local, _lifetime.Token,
                        trustedMap is { } map ? request => map(machineName, request) : null,
                        isLocalMachine: machine?.Machine.IsLocal == true,
                        hasMappings: hasMapping?.Invoke(machineName, workspace) == true);
                    _client = client;
                    _editors = editors;
                }
                if (_editors is not null && _session != session)
                {
                    if (await _editors.SelectForSessionAsync(session, _lifetime.Token))
                        _session = session;
                }
            }
            finally { _gate.Release(); }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or Dsh.Transport.JsonRpcException)
        {
            // A disconnect retires this authenticated peer; the next Fleet notification uses the new lease.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        try
        {
            if (_registration is not null) await _registration.DisposeAsync();
            _registration = null;
            _client = null;
            _editors = null;
        }
        finally { _gate.Release(); }
        _lifetime.Dispose();
    }
}
