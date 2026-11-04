namespace Dsh.Core;

public readonly record struct EditorId(string Value)
{
    public override string ToString() => Value;
    public static implicit operator EditorId(string value) => new(value);
    public static implicit operator string(EditorId value) => value.Value;
}

public enum EditorKind { Gui, Terminal, Hybrid }
[Flags]
public enum EditorCapability
{
    None = 0, OpenWorkspace = 1, OpenFile = 2, OpenAtPosition = 4, Diff = 8,
    Reveal = 16, Focus = 32, Terminal = 64, History = 128, Remote = 256, Restore = 512,
}
public enum EditorAction { OpenWorkspace, OpenFile, OpenAtPosition, OpenDiff, Reveal, Focus, OpenTerminal, Restore }
public enum EditorFocus { Never, IfRunning, Always }
public enum EditorReuseWindow { Auto, Always, Never }
public enum EditorExecutionTarget { Local, Client, Print, Automatic }
public enum EditorFallback { None, OsDefault }
public enum EditorErrorCode { None, NOT_FOUND, LAUNCH_FAILED, UNSUPPORTED, NO_DISPLAY, OUTSIDE_WORKSPACE, NO_TERMINAL_HOST, INVALID_CONFIGURATION, PERMISSION_DENIED, CANCELLED, CLIENT_UNAVAILABLE }

public sealed record EditorConditions
{
    public string[] Workspace { get; init; } = [];
    public string[] Extensions { get; init; } = [];
    public string[] Globs { get; init; } = [];
}

public sealed record EditorDescriptor
{
    public required EditorId Id { get; init; }
    public string DisplayName { get; init; } = "";
    public EditorKind Kind { get; init; }
    public required string Command { get; init; }
    public string[] ExecutableCandidates { get; init; } = [];
    public string[] RemoteExecutableCandidates { get; init; } = [];
    public IReadOnlyDictionary<EditorAction, string[]> Actions { get; init; } = new Dictionary<EditorAction, string[]>();
    public IReadOnlyDictionary<EditorAction, string[]> RemoteActions { get; init; } = new Dictionary<EditorAction, string[]>();
    public IReadOnlyList<EditorDiscoveryCommand> DiscoveryCommands { get; init; } = [];
    public EditorCapability Capabilities { get; init; }
    public Dsh.Ide.History.IdeWorkspaceKind HistoryProvider { get; init; }
    public EditorConditions When { get; init; } = new();
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string? WorkingDirectory { get; init; }
    public string[] ReuseArguments { get; init; } = [];
    public string[] NewWindowArguments { get; init; } = [];
    public string[] BackgroundArguments { get; init; } = [];
    public string[] FocusArguments { get; init; } = [];
    public string[] BundleNames { get; init; } = [];
}

public sealed record EditorDiscoveryCommand(string Platform, string Command, string[] Arguments);

public sealed record EditorInstallation(EditorId Editor, string Executable, string Source, string? Version = null, bool IsBundle = false);
public sealed record EditorWorkspace(string Root, string[] Markers)
{
    public string? ProjectPath { get; init; }
    public string? PreferredEditor { get; init; }
    public string[] TriggerOrder { get; init; } = [];
    public bool? TriggersEnabled { get; init; }
}
public sealed record EditorBinding(string WorkspaceRoot, EditorId Editor, string? SessionId = null);

// Requests contain no executable, argument template, environment, or working-directory overrides.
public sealed record EditorRequest
{
    public required string WorkspaceRoot { get; init; }
    public EditorAction Action { get; init; } = EditorAction.OpenWorkspace;
    public string? Path { get; init; }
    public string? Left { get; init; }
    public string? Right { get; init; }
    public int Line { get; init; } = 1;
    public int Column { get; init; } = 1;
    public string? Editor { get; init; }
    public string[] TriggerEditors { get; init; } = [];
    public string[] ExcludedEditors { get; init; } = [];
    public string? TriggerId { get; init; }
    public string? SessionId { get; init; }
    public string? TurnId { get; init; }
    public string? MachineId { get; init; }
    public string? HostEditor { get; init; }
    public bool DryRun { get; init; }
    public bool Print { get; init; }
    public bool AllowOsDefault { get; init; }
    public bool RememberSelection { get; init; }
    public EditorFocus? Focus { get; init; }
    public EditorReuseWindow? ReuseWindow { get; init; }
    public EditorExecutionTarget ExecutionTarget { get; init; } = EditorExecutionTarget.Automatic;
    public string? RemoteUri { get; init; }
}

public sealed record EditorDecisionStep(EditorId Editor, string Source, bool Installed, bool Supported, bool Matched, EditorErrorCode Error = EditorErrorCode.None, string? Reason = null);
public sealed record EditorDecision(string? TriggerId, EditorId[] CandidateOrder, EditorDecisionStep[] Steps);
public sealed record EditorResult
{
    public bool Success { get; init; }
    public EditorErrorCode Error { get; init; }
    public string? Message { get; init; }
    public EditorId? Editor { get; init; }
    public string? Executable { get; init; }
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public int? ProcessId { get; init; }
    public int? ExitCode { get; init; }
    public string? PrintableCommand { get; init; }
    public bool Printed { get; init; }
    public string? TerminalHost { get; init; }
    public EditorId[] Alternatives { get; init; } = [];
    public EditorDecision Decision { get; init; } = new(null, [], []);
    public EditorResult[] Results { get; init; } = [];
}

public sealed record EditorLaunchCommand
{
    public required EditorId Editor { get; init; }
    public required string Executable { get; init; }
    public string[] Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public EditorKind Kind { get; init; }
    public EditorFocus Focus { get; init; }
    public EditorExecutionTarget Target { get; init; }
    public string? RemoteUri { get; init; }
    public string? SessionId { get; init; }
    public string? MachineId { get; init; }
    public bool IsBundle { get; init; }
    public bool WaitForExit { get; init; }
}
public sealed record EditorExecutionResult(bool Success, EditorErrorCode Error = EditorErrorCode.None, string? Message = null, int? ProcessId = null, string? PrintableCommand = null, bool Printed = false, string? TerminalHost = null, int? ExitCode = null);
public sealed record EditorDiagnostic(string Check, bool Success, string Message, EditorId? Editor = null);

public interface IEditorCatalog
{
    const string ServiceName = "editorCatalog";
    IReadOnlyList<EditorDescriptor> Descriptors { get; }
    EditorDescriptor? Find(EditorId id);
    IDisposable Register(EditorDescriptor descriptor);
    ValueTask<IReadOnlyList<EditorInstallation>> DetectAsync(bool refresh = false, CancellationToken cancellationToken = default);
}
public interface IEditorLauncher
{
    const string ServiceName = "editorLauncher";
    ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default);
}
public interface IEditorService : IEditorLauncher
{
    new const string ServiceName = "ide";
    IEditorCatalog Catalog { get; }
    EditorDecision? LastDecision { get; }
    EditorWorkspace InspectWorkspace(string root);
    EditorBinding? GetBinding(string root, string? sessionId = null);
    void Bind(EditorBinding binding);
    ValueTask<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default);
}
public interface IEditorExecutor
{
    ValueTask<EditorExecutionResult> ExecuteAsync(EditorLaunchCommand command, CancellationToken cancellationToken = default);
}
public interface ITerminalHost
{
    string Name { get; }
    bool IsAvailable(EditorLaunchCommand command);
    ValueTask<EditorExecutionResult> LaunchAsync(EditorLaunchCommand command, CancellationToken cancellationToken = default);
}
