using Dsh.Core;
using Dsh.Ide.Bridge;
using Dsh.RemoteHost;

namespace Dsh.Host;

internal sealed class HarnessRemoteEditorBackend(IEditorService service, EditorClientDispatcher? clients, HostPathPolicy paths) : IRemoteEditorBackend
{
    public IEditorService Service { get; } = service;
    public EditorClientDispatcher? Clients { get; } = clients;
    public HostPathPolicy? Paths { get; } = paths;
}
