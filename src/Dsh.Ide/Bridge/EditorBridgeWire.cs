using System.Text.Json.Serialization;
using Dsh.Core;

namespace Dsh.Ide.Bridge;

/// <summary>Optional authenticated IDE domain and reverse client extension protocol, version 1.</summary>
public static class EditorProtocol
{
    public const int Version = 1;
    public const string Capability = "ide.editor";
    public const string ClientCapability = "ide.client";
    public const string MethodList = "host.ide.list";
    public const string MethodStatus = "host.ide.status";
    public const string MethodDoctor = "host.ide.doctor";
    public const string MethodOpen = "host.ide.open";
    public const string MethodCancel = "host.ide.cancel";
    public const string MethodRegister = "host.ide.client.register";
    public const string MethodUnregister = "host.ide.client.unregister";
    public const string MethodSelect = "host.ide.client.select";
    public const string MethodClientOpen = "client.ide.open";
    public const string MethodClientCancel = "client.ide.cancel";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodList, MethodStatus, MethodDoctor, MethodOpen, MethodCancel, MethodRegister, MethodUnregister, MethodSelect]);
}

public sealed record EditorClientInfo(string Id);
public sealed record EditorRemoteStatus(EditorWorkspace Workspace, EditorBinding? Binding, EditorDecision? LastDecision, IReadOnlyList<EditorClientInfo> Clients);
public sealed record EditorRemoteList(IReadOnlyList<EditorDescriptor> Descriptors, IReadOnlyList<EditorInstallation> Installations);

public sealed record RemoteEditorWorkspace(string WorkspaceRoot, string? SessionId = null);
public sealed record RemoteEditorList(bool Refresh = false);
public sealed record RemoteEditorOpen(Guid OperationId, EditorRequest Request);
public sealed record RemoteEditorCancel(Guid OperationId);
public sealed record RemoteEditorClientRegistration(int ProtocolVersion = EditorProtocol.Version, bool IsLocalMachine = false, bool HasMappings = false);
public sealed record RemoteEditorSelect(string SessionId);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RemoteEditorWorkspace))]
[JsonSerializable(typeof(RemoteEditorList))]
[JsonSerializable(typeof(RemoteEditorOpen))]
[JsonSerializable(typeof(RemoteEditorCancel))]
[JsonSerializable(typeof(RemoteEditorClientRegistration))]
[JsonSerializable(typeof(RemoteEditorSelect))]
[JsonSerializable(typeof(EditorRemoteList))]
[JsonSerializable(typeof(EditorRemoteStatus))]
[JsonSerializable(typeof(EditorRequest))]
[JsonSerializable(typeof(EditorResult))]
[JsonSerializable(typeof(IReadOnlyList<EditorDiagnostic>))]
[JsonSerializable(typeof(bool))]
public partial class EditorProtocolJsonContext : JsonSerializerContext;
