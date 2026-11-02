using Dsh.Core;
using Dsh.Ide.Selection;

namespace Dsh.Ide.Launch;

public sealed class EditorLauncher(IEditorCatalog catalog, EditorOptions? options = null, IEditorExecutor? executor = null, IReadOnlyList<string>? trustedSnapshotRoots = null, Func<bool>? canLaunch = null) : IEditorService
{
    private readonly EditorOptions options = options ?? new();
    private readonly IEditorExecutor executor = executor ?? new EditorExecutor();
    private readonly EditorBindings bindings = new();
    private readonly string[] trustedSnapshotRoots = trustedSnapshotRoots?.Select(EditorPaths.PhysicalPath).ToArray() ?? [];
    private EditorDecision? lastDecision;
    public IEditorCatalog Catalog => catalog;
    public EditorDecision? LastDecision => Volatile.Read(ref lastDecision);
    public EditorWorkspace InspectWorkspace(string root) => EditorSelection.InspectWorkspace(root);
    public EditorBinding? GetBinding(string root, string? sessionId = null) => bindings.Get(root, sessionId);
    public void Bind(EditorBinding binding)
    {
        if (catalog.Find(binding.Editor) is null) throw new ArgumentException("Cannot bind an unregistered editor.");
        bindings.Set(binding);
    }
    public async ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ExecutionTarget == EditorExecutionTarget.Print && !request.Print) request = request with { Print = true };
            if (!request.Print && !request.DryRun && request.ExecutionTarget != EditorExecutionTarget.Print && canLaunch is not null && !canLaunch())
                return Failure(request, EditorErrorCode.PERMISSION_DENIED, "The IDE plugin does not have process launch permission.");
            if (request.RemoteUri is { } remoteUri) request = request with { RemoteUri = EditorTemplates.ValidateRemoteUri(remoteUri) };
            request = EditorPaths.Normalize(request, options.AllowOutsideWorkspace, trustedSnapshotRoots);
            var workspace = InspectWorkspace(request.WorkspaceRoot);
            if (workspace.ProjectPath is { } project)
            {
                project = Path.GetFullPath(project, workspace.Root);
                if (!options.AllowOutsideWorkspace && !EditorPaths.IsWithin(project, workspace.Root)) throw new EditorOutsideWorkspaceException();
                workspace = workspace with { ProjectPath = project };
            }
            var candidates = Candidates(request, workspace);
            var detected = await catalog.DetectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return await TryCandidates(request, workspace, candidates, detected, cancellationToken).ConfigureAwait(false);
        }
        catch (EditorOutsideWorkspaceException) { return Failure(request, EditorErrorCode.OUTSIDE_WORKSPACE, "Editor target resolves outside the workspace."); }
        catch (FileNotFoundException) { return Failure(request, EditorErrorCode.NOT_FOUND, "Editor diff requires two existing files."); }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return Failure(request, EditorErrorCode.PERMISSION_DENIED, "Permission denied while inspecting editor configuration or paths."); }
        catch (YamlDotNet.Core.YamlException) { return Failure(request, EditorErrorCode.INVALID_CONFIGURATION, "Workspace IDE preferences contain invalid YAML."); }
        catch (ArgumentException) { return Failure(request, EditorErrorCode.INVALID_CONFIGURATION, "Editor configuration or request is invalid."); }
        catch (IOException) { return Failure(request, EditorErrorCode.LAUNCH_FAILED, "Editor configuration or target could not be accessed."); }
    }
    private List<(EditorId Id, string Source)> Candidates(EditorRequest request, EditorWorkspace workspace)
    {
        var result = new List<(EditorId Id, string Source)>();
        void Add(string? id, string source)
        {
            if (!string.IsNullOrWhiteSpace(id) && id != "auto" && !result.Any(c => c.Id.Value == id)) result.Add((new(id), source));
        }
        if (request.Editor is { Length: > 0 } explicitId && explicitId != "auto") { Add(explicitId, "explicit"); return result; }
        Add(workspace.PreferredEditor, "workspace");
        Add(GetBinding(workspace.Root, request.SessionId)?.Editor.Value, "binding");
        foreach (var id in request.TriggerEditors) Add(id, "trigger");
        Add(options.DefaultEditor, "global-default");
        foreach (var descriptor in options.Editors) Add(descriptor.Id.Value, "global");
        foreach (var descriptor in catalog.Descriptors) Add(descriptor.Id.Value, "registered");
        Add(request.HostEditor ?? EditorSelection.DetectHostEditor(options.HostDetection), "host");
        return result;
    }
    private async ValueTask<EditorResult> TryCandidates(EditorRequest request, EditorWorkspace workspace, List<(EditorId Id, string Source)> candidates, IReadOnlyList<EditorInstallation> installations, CancellationToken ct)
    {
        var steps = new List<EditorDecisionStep>();
        var eligible = new List<(EditorDescriptor Descriptor, EditorInstallation Installation)>();
        var explicitChoice = request.Editor is { Length: > 0 } && request.Editor != "auto";
        var matched = candidates.Where(c => catalog.Find(c.Id) is { } descriptor && EditorSelection.Matches(descriptor, request, workspace)
            && (request.Action == EditorAction.OpenWorkspace ? descriptor.When.Workspace.Length > 0 : descriptor.When.Extensions.Length > 0 || descriptor.When.Globs.Length > 0)).Select(c => c.Id).ToHashSet();
        var preferred = candidates.Where(c => c.Source is "workspace" or "binding" or "trigger").Select(c => c.Id).ToHashSet();
        var hasMatch = matched.Count > 0;
        var ordered = explicitChoice ? candidates : candidates.Where(c => preferred.Contains(c.Id) || matched.Contains(c.Id) || !hasMatch && !options.StrictMatch)
            .Concat(options.StrictMatch ? [] : candidates.Where(c => !preferred.Contains(c.Id) && !matched.Contains(c.Id) && hasMatch)).ToList();
        foreach (var (id, source) in candidates)
        {
            var descriptor = catalog.Find(id);
            var installation = installations.FirstOrDefault(i => i.Editor == id);
            var supported = descriptor is not null && descriptor.Actions.ContainsKey(request.Action) && descriptor.Capabilities.HasFlag(EditorActionCapabilities.For(request.Action))
                && (request.RemoteUri is null || EditorTemplates.SupportsRemoteUri(descriptor, request.Action));
            var error = descriptor is null || installation is null && !request.Print ? EditorErrorCode.NOT_FOUND : !supported ? EditorErrorCode.UNSUPPORTED : EditorErrorCode.None;
            var reason = descriptor is null ? "Unknown editor ID." : installation is null ? request.Print ? "Print-only uses the configured command without a local installation." : "No installation detected." : !supported ? "Action is not supported by this profile." : matched.Contains(id) ? "Target conditions match." : "Target conditions do not match.";
            var reuse = request.ReuseWindow ?? options.ReuseWindow;
            if (descriptor is not null && (reuse == EditorReuseWindow.Always && descriptor.ReuseArguments.Length == 0 || reuse == EditorReuseWindow.Never && descriptor.NewWindowArguments.Length == 0))
            {
                error = EditorErrorCode.UNSUPPORTED;
                reason = "This profile cannot guarantee the requested window reuse policy.";
            }
            if (request.ExcludedEditors.Contains(id.Value, StringComparer.Ordinal)) { error = EditorErrorCode.UNSUPPORTED; reason = "Editor excluded by trigger cooldown or de-duplication."; }
            if (!explicitChoice && options.StrictMatch && !matched.Contains(id)) { error = EditorErrorCode.NOT_FOUND; reason = "Strict target matching rejected this profile."; }
            steps.Add(new(id, source, installation is not null, supported, matched.Contains(id), error, reason));
        }
        foreach (var (id, _) in ordered)
        {
            var step = steps.First(s => s.Editor == id);
            if (step.Error != EditorErrorCode.None) continue;
            var descriptor = catalog.Find(id)!;
            eligible.Add((descriptor, installations.FirstOrDefault(i => i.Editor == id) ?? new(id, descriptor.Command, "print-template")));
        }
        EditorExecutionResult? lastFailure = null;
        for (var index = 0; index < eligible.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (descriptor, installation) = eligible[index];
            var command = EditorTemplates.Create(descriptor, installation, request, workspace, options);
            var executed = request.DryRun ? new EditorExecutionResult(true, PrintableCommand: EditorTemplates.Printable(command), Printed: true) : await executor.ExecuteAsync(command, ct).ConfigureAwait(false);
            var stepIndex = steps.FindIndex(s => s.Editor == descriptor.Id);
            steps[stepIndex] = steps[stepIndex] with
            {
                Error = executed.Error,
                Source = installation.Source == "print-template" ? "configured-print" : steps[stepIndex].Source,
                Reason = executed.Success
                    ? installation.Source == "print-template" ? "Configured printable command; installation unverified; no process started."
                    : request.DryRun ? "Dry-run selected; no process started."
                    : executed.Printed ? "Printable command returned; no process started." : "Editor launched."
                    : executed.Message ?? "Launch failed.",
            };
            if (!executed.Success) { lastFailure = executed; continue; }
            if (request.RememberSelection && !request.DryRun && !executed.Printed) Bind(new(workspace.Root, descriptor.Id, request.SessionId));
            return Complete(new EditorResult
            {
                Success = true, Message = executed.Message, Editor = descriptor.Id, Executable = command.Executable, Arguments = command.Arguments, WorkingDirectory = command.WorkingDirectory,
                ProcessId = executed.ProcessId, ExitCode = executed.ExitCode, PrintableCommand = executed.PrintableCommand, Printed = executed.Printed, TerminalHost = executed.TerminalHost,
                Alternatives = eligible.Where((_, i) => i > index).Select(e => e.Descriptor.Id).ToArray(),
                Decision = new(request.TriggerId, candidates.Select(c => c.Id).ToArray(), steps.ToArray()),
            });
        }
        if (request.AllowOsDefault && !explicitChoice && request.RemoteUri is null && options.Fallback == EditorFallback.OsDefault && !options.StrictMatch)
        {
            var fallback = await LaunchOsDefault(request, workspace, ct).ConfigureAwait(false);
            return Complete(fallback with { Decision = new(request.TriggerId, candidates.Select(c => c.Id).ToArray(), steps.ToArray()) });
        }
        var finalError = lastFailure?.Error ?? (explicitChoice && steps.Count > 0 ? steps[0].Error : EditorErrorCode.NOT_FOUND);
        return Complete(new() { Error = finalError, Message = lastFailure?.Message ?? "No installed editor supports this action and target.", Decision = new(request.TriggerId, candidates.Select(c => c.Id).ToArray(), steps.ToArray()) });
    }
    private async ValueTask<EditorResult> LaunchOsDefault(EditorRequest request, EditorWorkspace workspace, CancellationToken ct)
    {
        if (request.Action is not (EditorAction.OpenFile or EditorAction.OpenWorkspace or EditorAction.Reveal)) return new() { Error = EditorErrorCode.UNSUPPORTED, Message = "OS default cannot perform this action." };
        var path = request.Path ?? workspace.ProjectPath ?? workspace.Root;
        if (OperatingSystem.IsWindows())
        {
            if (request.DryRun || request.Print) return new() { Success = true, Printed = true, PrintableCommand = $"Start-Process -FilePath {EditorTemplates.QuotePowerShell(path)}" };
            if (!EditorExecutor.HasDisplay()) return new() { Error = EditorErrorCode.NO_DISPLAY, Message = "No display is available." };
            try
            {
                var id = await Task.Run(() => { ct.ThrowIfCancellationRequested(); using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, WindowStyle = request.Focus == EditorFocus.Always ? System.Diagnostics.ProcessWindowStyle.Normal : System.Diagnostics.ProcessWindowStyle.Minimized }); return process?.Id; }, ct).ConfigureAwait(false);
                return new() { Success = true, Editor = new("os-default"), ProcessId = id };
            }
            catch (System.ComponentModel.Win32Exception) { return new() { Error = EditorErrorCode.LAUNCH_FAILED, Message = "OS default application could not be launched." }; }
        }
        var command = new EditorLaunchCommand { Editor = "os-default", Executable = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open", Arguments = OperatingSystem.IsMacOS() && (request.Focus ?? options.Focus) == EditorFocus.Never ? ["-g", path] : [path], WorkingDirectory = workspace.Root, Target = request.Print || request.DryRun ? EditorExecutionTarget.Print : request.ExecutionTarget };
        var execution = await executor.ExecuteAsync(command, ct).ConfigureAwait(false);
        return new() { Success = execution.Success, Error = execution.Error, Message = execution.Message, Editor = command.Editor, ProcessId = execution.ProcessId, ExitCode = execution.ExitCode, Executable = command.Executable, Arguments = command.Arguments, Printed = execution.Printed, PrintableCommand = execution.PrintableCommand };
    }
    private EditorResult Complete(EditorResult result) { Volatile.Write(ref lastDecision, result.Decision); return result; }
    private EditorResult Failure(EditorRequest request, EditorErrorCode code, string message) => Complete(new() { Error = code, Message = message, Decision = new(request.TriggerId, [], []) });
    public async ValueTask<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<EditorDiagnostic>();
        try { var workspace = InspectWorkspace(workspaceRoot); diagnostics.Add(new("workspace", Directory.Exists(workspace.Root), "Workspace preference configuration parsed.")); }
        catch (Exception error) when (error is IOException or ArgumentException or YamlDotNet.Core.YamlException or UnauthorizedAccessException) { diagnostics.Add(new("workspace", false, "Workspace preferences cannot be read or validated.")); }
        diagnostics.Add(new("display", EditorExecutor.HasDisplay(), EditorExecutor.HasDisplay() ? "Graphical display is available." : "No graphical display; use a PTY, --print, or client execution."));
        diagnostics.Add(new("terminal", new SystemTerminalHost().IsAvailable(new() { Editor = "doctor", Executable = "", WorkingDirectory = workspaceRoot }), "System terminal availability; injected DSH PTY is checked when launching."));
        var installations = await catalog.DetectAsync(refresh: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var descriptor in catalog.Descriptors)
        {
            var found = installations.FirstOrDefault(i => i.Editor == descriptor.Id);
            diagnostics.Add(new("installation", found is not null, found is null ? "No installation in PATH or known platform locations." : $"Detected via {found.Source}.", descriptor.Id));
            if (options.Focus == EditorFocus.Never && descriptor.Kind != EditorKind.Terminal && descriptor.BackgroundArguments.Length == 0)
                diagnostics.Add(new("focus", false, "This profile has no background CLI switch; window-manager focus policy cannot be guaranteed.", descriptor.Id));
        }
        return diagnostics;
    }
}
