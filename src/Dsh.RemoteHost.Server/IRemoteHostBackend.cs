namespace Dsh.RemoteHost;

/** A host composes its required session/decision/registry services and explicitly supplies optional domains. */
public interface IRemoteHostBackend
{
    HostPendingDecisions Pending { get; }
    HostSessionRegistry Sessions { get; }
    IRemoteSessionBackend Session { get; }
    Task<HostInfo> InfoAsync(CancellationToken cancellationToken);

    IRemoteAgentBackend? Agent { get; }
    IRemotePtyBackend? Pty { get; }
    HostFileSystem? Files { get; }
    HostGitService? Git { get; }
    HostNotificationCenter? Notifications { get; }
    IRemoteSessionEditingBackend? SessionEditing { get; }
    IRemotePluginsBackend? Plugins { get; }
    IRemoteNetworkBackend? Network { get; }
    IRemoteEditorBackend? Editor => null;
}

/** Required session execution and event stream. Subscribe installs its live listener before returning. */
public interface IRemoteSessionBackend
{
    Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken);
    Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken);
    Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken);
    Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken);
    Task InterruptAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken);
    Task<RemoteHostStream<RemoteSessionEvent>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken);
}

public interface IRemoteSessionEditingBackend
{
    Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken);
    Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken);
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken);
}

public interface IRemoteAgentBackend
{
    Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken);
    Task<RemoteSessionSummary> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken);
    Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken);
    Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken);
    Task StopAgentAsync(string sessionId, CancellationToken cancellationToken);
    Task<RemoteSessionSummary> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken);
}

public interface IRemotePtyBackend
{
    Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken);
    Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken);
    Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken);
    Task StopPtyAsync(string ptyId, CancellationToken cancellationToken);
    Task<RemoteHostStream<RemotePtyOutput>> AttachPtyAsync(string ptyId, CancellationToken cancellationToken);
}

public interface IRemotePluginsBackend
{
    Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken);
    Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken);
}

public interface IRemoteNetworkBackend
{
    Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken);
    Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken);
    Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken);
}

public interface IRemoteNetworkReloadBackend
{
    Task ReloadAsync(CancellationToken cancellationToken);
}

public interface IRemoteEditorBackend
{
    Dsh.Core.IEditorService Service { get; }
    Dsh.Ide.Bridge.EditorClientDispatcher? Clients { get; }
    HostPathPolicy? Paths => null;
}


