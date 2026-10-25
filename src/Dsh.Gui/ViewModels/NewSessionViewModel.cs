using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Fleet;
using Dsh.GitInspect;
using Dsh.Gui.Services;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.ViewModels;

public enum NewSessionWorktree
{
    None,
    Existing,
    New,
}

/** An agent of a machine's catalog; agents the machine cannot run are listed but cannot be chosen. */
public sealed class AgentChoiceViewModel(RemoteAgentCatalogEntry entry)
{
    /** Agents the machine cannot run are drawn faded. */
    private const double UnavailableOpacity = 0.45;

    public RemoteAgentCatalogEntry Entry { get; } = entry;

    public double ItemOpacity => IsAvailable ? 1 : UnavailableOpacity;

    public string Label => Entry.DisplayName;

    public bool IsAvailable => Entry.Available;

    public string Detail => Entry switch
    {
        { Available: false } => "未安装",
        { Version: { Length: > 0 } version } => version,
        { RequiresCommand: true } => "需要填写命令",
        _ => "",
    };

    public override string ToString() => Label;
}

/**
 * The new-session form: machine, agent (from the machine's catalog), project directory, terminal or ACP mode, git worktree, initial prompt, and the command of a user-defined agent.
 * Harness sessions start with the GUI's current model; everything else is decided by the host (host.agent.start).
 */
public sealed partial class NewSessionViewModel : ObservableObject, IDisposable
{
    private readonly FleetWorkspace _fleet;
    private readonly FleetSessionActions _actions;
    private CancellationTokenSource? _catalogLoad;
    private bool _disposed;

    public NewSessionViewModel(FleetWorkspace fleet, FleetSessionActions actions, string? machine, string? projectPath)
    {
        _fleet = fleet;
        _actions = actions;
        Machines = [.. fleet.Client.Machines.Select(item => item.Machine.Name)];
        _projectPath = projectPath ?? "";
        Machine = machine ?? fleet.Client.Machines.FirstOrDefault(item => item.State == MachineConnectionState.Connected)?.Machine.Name ?? Machines.FirstOrDefault();
    }

    public IReadOnlyList<string> Machines { get; }

    public ObservableCollection<AgentChoiceViewModel> Agents { get; } = [];

    public ObservableCollection<GitWorktree> Worktrees { get; } = [];

    [ObservableProperty]
    private string? _machine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseMode), nameof(NeedsCommand), nameof(TakesPrompt))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AgentChoiceViewModel? _agent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _projectPath;

    [ObservableProperty]
    private bool _useAcp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExistingWorktree), nameof(IsNewWorktree))]
    private NewSessionWorktree _worktree = NewSessionWorktree.None;

    [ObservableProperty]
    private GitWorktree? _existingWorktree;

    [ObservableProperty]
    private string _newWorktreePath = "";

    [ObservableProperty]
    private string _newBranch = "";

    [ObservableProperty]
    private string _initialPrompt = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _commandLine = "";

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isBusy;

    /** Agents that run both in a terminal and over ACP let the user choose. */
    public bool CanChooseMode => Agent?.Entry is { SupportsTerminal: true, SupportsAcp: true };

    public bool NeedsCommand => Agent?.Entry.RequiresCommand == true;

    /** A user-defined terminal command takes its prompt as a "{prompt}" argument instead. */
    public bool TakesPrompt => Agent?.Entry.AgentKind != Dsh.AgentAdapters.AgentKinds.Terminal;

    public bool IsExistingWorktree => Worktree == NewSessionWorktree.Existing;

    public bool IsNewWorktree => Worktree == NewSessionWorktree.New;

    public RemoteSessionSummary? Started { get; private set; }

    public event Action<NewSessionViewModel>? Completed;

    /** The view shows the remote folder picker for the machine's files domain and returns the chosen directory. */
    public event Func<RemoteFilesClient, string?, Task<string?>>? BrowseRequested;

    partial void OnMachineChanged(string? value)
    {
        if (_disposed)
            return;
        _catalogLoad?.Cancel();
        _catalogLoad?.Dispose();
        _catalogLoad = new CancellationTokenSource();
        _ = LoadCatalogAsync(_catalogLoad.Token);
    }

    partial void OnAgentChanged(AgentChoiceViewModel? value) => UseAcp = value?.Entry is { SupportsTerminal: false, SupportsAcp: true };

    partial void OnWorktreeChanged(NewSessionWorktree value)
    {
        if (value == NewSessionWorktree.Existing)
            _ = LoadWorktreesAsync();
    }

    [RelayCommand]
    private void SetWorktree(string mode) => Worktree = Enum.Parse<NewSessionWorktree>(mode);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (BrowseRequested is not { } browse || await LeaseAsync() is not { } lease)
            return;
        if (await browse(lease.Client.Files, ProjectPath.Length > 0 ? ProjectPath : null) is { } picked)
            ProjectPath = picked;
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _catalogLoad?.Cancel();
        _catalogLoad?.Dispose();
        _catalogLoad = null;
    }

    private bool CanStart()
        => !IsBusy && Agent is { IsAvailable: true } && ProjectPath.Trim().Length > 0 && (!NeedsCommand || CommandLine.Trim().Length > 0);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (Machine is not { } machine)
            return;
        IsBusy = true;
        Status = "正在启动…";
        try
        {
            Started = await _actions.StartAsync(machine, BuildRequest(), CancellationToken.None);
            Completed?.Invoke(this);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            Status = $"启动失败: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /** The host.agent.start request the form describes. */
    public RemoteAgentStartRequest BuildRequest()
    {
        var entry = Agent?.Entry ?? throw new InvalidOperationException("no agent chosen");
        var prompt = InitialPrompt.Trim();
        return new RemoteAgentStartRequest(
            entry.AgentKind,
            ProjectPath.Trim(),
            InitialPrompt: prompt.Length > 0 ? prompt : null,
            Mode: entry.AgentKind == HostAgentKinds.Dsharp ? null : UseAcp ? RemoteAgentModes.Acp : RemoteAgentModes.Terminal,
            Command: NeedsCommand ? ParseCommand(CommandLine) : null,
            Worktree: WorktreeChoice(),
            Title: Title.Trim().Length > 0 ? Title.Trim() : null);
    }

    private RemoteWorktreeChoice? WorktreeChoice() => Worktree switch
    {
        NewSessionWorktree.Existing when ExistingWorktree is { } existing => new RemoteWorktreeChoice(existing.Path),
        NewSessionWorktree.New when NewWorktreePath.Trim().Length > 0 => new RemoteWorktreeChoice(
            NewWorktreePath.Trim(), NewBranch.Trim().Length > 0 ? NewBranch.Trim() : null),
        _ => null,
    };

    private async Task LoadCatalogAsync(CancellationToken cancellationToken)
    {
        Agents.Clear();
        Agent = null;
        if (Machine is null || _fleet.Find(Machine) is not { } connection)
        {
            Status = Machine is null ? "" : $"{Machine} 不存在";
            return;
        }
        Status = connection.State == MachineConnectionState.Connected ? "" : $"等待 {Machine} 连接";
        try
        {
            var lease = await connection.WaitConnectedAsync(cancellationToken);
            var entries = await lease.Client.Agent.AgentCatalogAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in entries)
                Agents.Add(new AgentChoiceViewModel(entry));
            Agent = Agents.FirstOrDefault(item => item.IsAvailable);
            Status = "";
        }
        catch (Exception error) when (error is IOException or JsonRpcException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
                Status = $"读取 Agent 列表失败: {error.Message}";
        }
    }

    private async Task LoadWorktreesAsync()
    {
        Worktrees.Clear();
        if (ProjectPath.Trim().Length == 0 || await LeaseAsync() is not { } lease)
            return;
        try
        {
            foreach (var worktree in await lease.Client.Git.ListWorktreesAsync(ProjectPath.Trim(), CancellationToken.None))
                Worktrees.Add(worktree);
            ExistingWorktree = Worktrees.FirstOrDefault(item => !item.IsMain) ?? Worktrees.FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or JsonRpcException or OperationCanceledException)
        {
            Status = $"读取 worktree 失败: {error.Message}";
        }
    }

    /** The machine's connection when it is connected now; the form does not wait for an unreachable machine. */
    private async Task<HostLease?> LeaseAsync()
    {
        if (Machine is null || _fleet.Find(Machine) is not { State: MachineConnectionState.Connected } connection)
        {
            Status = Machine is null ? "" : $"{Machine} 未连接";
            return null;
        }
        return await connection.WaitConnectedAsync(CancellationToken.None);
    }

    /** Splits a command line into words: whitespace separates, single or double quotes keep spaces, a backslash escapes the next character outside single quotes. */
    public static RemoteAgentCommand ParseCommand(string commandLine)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        char? quote = null;
        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (character == '\\' && quote != '\'' && index + 1 < commandLine.Length)
            {
                word.Append(commandLine[++index]);
                inWord = true;
            }
            else if (quote is not null)
            {
                if (character == quote)
                    quote = null;
                else
                    word.Append(character);
            }
            else if (character is '"' or '\'')
                (quote, inWord) = (character, true);
            else if (!char.IsWhiteSpace(character))
            {
                word.Append(character);
                inWord = true;
            }
            else if (inWord)
            {
                words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
        }
        if (inWord)
            words.Add(word.ToString());
        return words.Count > 0 ? new RemoteAgentCommand(words[0], words[1..]) : throw new ArgumentException("the command is empty");
    }
}


