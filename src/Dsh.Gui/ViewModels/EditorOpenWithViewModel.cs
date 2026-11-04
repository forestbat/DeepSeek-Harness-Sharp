using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Core;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

public sealed record EditorChoice(string Id, string Label, string Executable);

public sealed partial class EditorOpenWithViewModel : ObservableObject
{
    private readonly IEditorService? _local;
    private readonly Func<CancellationToken, Task<IEditorService>> _remote;
    private readonly string _workspace;
    private readonly string _machine;
    private readonly string? _session;
    private readonly string? _turn;
    private long _generation;

    public EditorOpenWithViewModel(IEditorService? local, Func<CancellationToken, Task<IEditorService>> remote,
        string workspace, string machine, string? session = null, string? turn = null)
    {
        (_local, _remote, _workspace, _machine, _session, _turn) = (local, remote, workspace, machine, session, turn);
        _path = workspace;
        _destination = IsRemoteMachine ? "remote" : "local";
        _localWorkspace = IsRemoteMachine ? "" : workspace;
    }

    public bool IsRemoteMachine => _machine != "local";
    public string[] Destinations { get; } = ["local", "remote"];
    public ObservableCollection<EditorChoice> Editors { get; } = [];
    [ObservableProperty] private EditorChoice? _selectedEditor;
    [ObservableProperty] private string _destination;
    [ObservableProperty] private string _localWorkspace;
    [ObservableProperty] private string _path;
    [ObservableProperty] private bool _isDirectory = true;
    [ObservableProperty] private bool _rememberSelection = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    public string TargetDescription => Destination == "remote"
        ? $"Remote host: {_machine} · {_workspace} · {Path}"
        : $"This computer · {LocalWorkspace} · {Path}";
    partial void OnDestinationChanged(string value) { OnPropertyChanged(nameof(TargetDescription)); _ = RefreshAsync(); }
    partial void OnPathChanged(string value) => OnPropertyChanged(nameof(TargetDescription));
    partial void OnLocalWorkspaceChanged(string value) => OnPropertyChanged(nameof(TargetDescription));

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        var destination = Destination;
        var root = destination == "local" ? LocalWorkspace : _workspace;
        IsBusy = true;
        try
        {
            var service = await ServiceAsync(destination);
            var detected = await service.Catalog.DetectAsync(refresh: true);
            if (generation != _generation) return;
            Editors.Clear();
            foreach (var installation in detected)
            {
                var descriptor = service.Catalog.Find(installation.Editor);
                Editors.Add(new(installation.Editor.Value, descriptor?.DisplayName is { Length: > 0 } name ? name : installation.Editor.Value, installation.Executable));
            }
            var binding = root.Length == 0 ? null : service.GetBinding(root, _session);
            SelectedEditor = Editors.FirstOrDefault(editor => editor.Id == binding?.Editor.Value) ?? Editors.FirstOrDefault();
            Status = Editors.Count == 0 ? "No editors detected for this destination." : "";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { if (generation == _generation) Status = error.Message; }
        finally { if (generation == _generation) IsBusy = false; }
    }

    [RelayCommand]
    public async Task OpenAsync()
    {
        if (SelectedEditor is null) { Status = "Choose a detected editor first."; return; }
        IsBusy = true;
        try
        {
            var destination = Destination;
            var request = CreateRequest();
            var service = await ServiceAsync(destination);
            var result = await service.LaunchAsync(request);
            Status = result.Success ? $"Opened with {result.Editor?.Value ?? request.Editor}." : $"{result.Error}: {result.Message}";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Status = error.Message; }
        finally { IsBusy = false; }
    }

    public EditorRequest CreateRequest()
    {
        var root = _workspace;
        var path = Path;
        if (Destination == "local")
        {
            if (string.IsNullOrWhiteSpace(LocalWorkspace))
                throw new ArgumentException("Set the corresponding local workspace path before opening a remote file locally.");
            root = System.IO.Path.GetFullPath(LocalWorkspace);
            if (IsRemoteMachine)
            {
                var style = _workspace.Contains('\\') ? RemotePathStyle.Windows : RemotePathStyle.Posix;
                var normalizedRoot = _workspace.Replace('\\', '/').TrimEnd('/');
                var normalizedPath = Path.Replace('\\', '/');
                var comparison = style == RemotePathStyle.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!normalizedPath.Equals(normalizedRoot, comparison) && !normalizedPath.StartsWith(normalizedRoot + "/", comparison))
                    throw new ArgumentException("The remote target must be within its workspace before local mapping.");
                var relative = normalizedPath[normalizedRoot.Length..].TrimStart('/');
                path = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            }
        }
        return new EditorRequest
        {
            WorkspaceRoot = root, Path = path, Action = IsDirectory ? EditorAction.OpenWorkspace : EditorAction.OpenFile,
            Editor = SelectedEditor?.Id, RememberSelection = RememberSelection, SessionId = _session, TurnId = _turn,
            AllowOsDefault = true,
            MachineId = Destination == "remote" ? _machine : null, ExecutionTarget = EditorExecutionTarget.Local,
        };
    }

    private async Task<IEditorService> ServiceAsync(string destination) => destination == "remote"
        ? await _remote(CancellationToken.None)
        : _local ?? throw new InvalidOperationException("The local IDE service is unavailable.");
}
