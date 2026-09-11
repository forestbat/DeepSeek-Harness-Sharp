using Dsh.Interaction;

namespace Dsh.Tui;

public sealed class CommandMenuState
{
    public enum MenuStage
    {
        Root,
        Subcommand,
        Argument,
    }

    private readonly IReadOnlyList<CommandDescriptor> _commands;
    private readonly Func<CommandDescriptor, IReadOnlyList<string>>? _candidateProvider;
    private IReadOnlyList<CommandDescriptor> _rootFiltered = [];
    private IReadOnlyList<CommandDescriptor> _subFiltered = [];
    private IReadOnlyList<string> _argumentCandidates = [];
    private IReadOnlyList<CommandArgumentSchema> _argumentSchemas = [];
    private readonly List<string> _argumentValues = [];
    private CommandDescriptor? _command;
    private CommandDescriptor? _subcommand;
    private string _prefix = "/";
    private string _query = "";
    private int _argumentIndex;

    public CommandMenuState(
        IReadOnlyList<CommandDescriptor> commands,
        Func<CommandDescriptor, IReadOnlyList<string>>? candidateProvider = null)
    {
        _commands = commands;
        _candidateProvider = candidateProvider;
        UpdateRoot("");
    }

    public bool IsActive { get; private set; } = true;

    public MenuStage Stage { get; private set; } = MenuStage.Root;

    public IReadOnlyList<string> Candidates => Stage switch
    {
        MenuStage.Root => [.. _rootFiltered.Select(command => command.Name)],
        MenuStage.Subcommand => [.. _subFiltered.Select(command => command.Name)],
        MenuStage.Argument => _argumentCandidates,
        _ => [],
    };

    public int SelectedIndex { get; private set; }

    public string Prefix => _prefix;

    public string Query => _query;

    public int ArgumentIndex => _argumentIndex;

    public CommandArgumentSchema? CurrentArgumentSchema
        => Stage == MenuStage.Argument && _argumentIndex >= 0 && _argumentIndex < _argumentSchemas.Count
            ? _argumentSchemas[_argumentIndex]
            : null;

    public string Prompt => Stage switch
    {
        MenuStage.Subcommand => $"Subcommands for /{_command!.Name}",
        MenuStage.Argument => CurrentArgumentSchema?.Hint ?? CurrentArgumentSchema?.Name
            ?? _subcommand?.MenuSchema?.Prompt ?? _command?.MenuSchema?.Prompt ?? "Argument",
        _ => "Commands",
    };

    public CommandDescriptor? CurrentCommand => _command;

    public CommandDescriptor? CurrentSubcommand => _subcommand;

    public bool CanConfirm
        => Stage == MenuStage.Argument
            ? Candidates.Count > 0 || _query.Length > 0
            : Candidates.Count > 0;

    public void ApplyInput(string text)
    {
        if (!IsActive)
            return;
        if (!text.StartsWith('/'))
        {
            UpdateRoot("");
            return;
        }
        if (!text.StartsWith(_prefix))
        {
            UpdateRoot(text[1..]);
            return;
        }

        _query = text[_prefix.Length..];
        switch (Stage)
        {
            case MenuStage.Root:
                ApplyRootInput();
                break;
            case MenuStage.Subcommand:
                ApplySubcommandInput();
                break;
            case MenuStage.Argument:
                UpdateArgumentCandidates();
                break;
        }
    }

    public void MoveUp()
    {
        if (Candidates.Count == 0)
            return;
        SelectedIndex = Math.Max(0, SelectedIndex - 1);
    }

    public void MoveDown()
    {
        if (Candidates.Count == 0)
            return;
        SelectedIndex = Math.Min(Candidates.Count - 1, SelectedIndex + 1);
    }

    public string? Confirm()
    {
        if (!IsActive)
            return null;
        return Stage switch
        {
            MenuStage.Root => ConfirmRoot(),
            MenuStage.Subcommand => ConfirmSubcommand(),
            MenuStage.Argument => ConfirmArgument(),
            _ => null,
        };
    }

    public bool Back()
    {
        if (!IsActive)
            return false;
        switch (Stage)
        {
            case MenuStage.Root:
                IsActive = false;
                _rootFiltered = [];
                _subFiltered = [];
                _argumentCandidates = [];
                _argumentSchemas = [];
                _argumentValues.Clear();
                _argumentIndex = 0;
                _command = null;
                _subcommand = null;
                _prefix = "/";
                _query = "";
                SelectedIndex = 0;
                return true;
            case MenuStage.Subcommand:
                _subcommand = null;
                _prefix = "/";
                _query = _command?.Name ?? "";
                Stage = MenuStage.Root;
                UpdateRoot(_query);
                return true;
            case MenuStage.Argument:
                if (_argumentIndex > 0)
                {
                    _argumentIndex--;
                    if (_argumentValues.Count > _argumentIndex)
                        _argumentValues.RemoveAt(_argumentValues.Count - 1);
                    _query = "";
                    UpdateArgumentCandidates();
                    return true;
                }
                if (_subcommand is not null)
                {
                    _subcommand = null;
                    _prefix = $"/{_command!.Name} ";
                    _query = "";
                    Stage = MenuStage.Subcommand;
                    UpdateSubcommands();
                }
                else
                {
                    _command = null;
                    _prefix = "/";
                    _query = "";
                    Stage = MenuStage.Root;
                    UpdateRoot("");
                }
                _argumentSchemas = [];
                _argumentValues.Clear();
                _argumentIndex = 0;
                return true;
            default:
                return false;
        }
    }

    public void Close()
    {
        IsActive = false;
        _rootFiltered = [];
        _subFiltered = [];
        _argumentCandidates = [];
        _argumentSchemas = [];
        _argumentValues.Clear();
        _argumentIndex = 0;
        _command = null;
        _subcommand = null;
        _prefix = "/";
        _query = "";
        SelectedIndex = 0;
    }

    private void ApplyRootInput()
    {
        var separatorIndex = _query.IndexOf(' ');
        if (separatorIndex > 0)
        {
            var commandName = _query[..separatorIndex];
            var remainder = _query[(separatorIndex + 1)..];
            var command = _commands.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, commandName, StringComparison.OrdinalIgnoreCase));
            if (command is not null)
            {
                _command = command;
                if (EnterCommandFollowup())
                {
                    _query = remainder;
                    if (_query.StartsWith(_prefix))
                        _query = _query[_prefix.Length..];
                    ApplyCurrentStageFromQuery();
                    return;
                }
            }
        }

        UpdateRoot(_query);
    }

    private void ApplySubcommandInput()
    {
        var separatorIndex = _query.IndexOf(' ');
        if (separatorIndex > 0)
        {
            var subcommandName = _query[..separatorIndex];
            var remainder = _query[(separatorIndex + 1)..];
            var subcommand = _command?.Subcommands?.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, subcommandName, StringComparison.OrdinalIgnoreCase));
            if (subcommand is not null)
            {
                _subcommand = subcommand;
                if (EnterSubcommandFollowup())
                {
                    _query = remainder;
                    ApplyCurrentStageFromQuery();
                    return;
                }
            }
        }

        UpdateSubcommands();
    }

    private void ApplyCurrentStageFromQuery()
    {
        switch (Stage)
        {
            case MenuStage.Subcommand:
                UpdateSubcommands();
                break;
            case MenuStage.Argument:
                UpdateArgumentCandidates();
                break;
            case MenuStage.Root:
                UpdateRoot(_query);
                break;
        }
    }

    private string? ConfirmRoot()
    {
        if (_rootFiltered.Count == 0)
            return null;
        _command = _rootFiltered[SelectedIndex];
        if (EnterCommandFollowup())
            return null;
        return $"/{_command.Name}";
    }

    private string? ConfirmSubcommand()
    {
        if (_subFiltered.Count == 0)
            return null;
        _subcommand = _subFiltered[SelectedIndex];
        if (EnterSubcommandFollowup())
            return null;
        return $"/{_command!.Name} {_subcommand.Name}";
    }

    private string? ConfirmArgument()
    {
        var schema = CurrentArgumentSchema;
        if (schema is null)
            return null;
        string? value;
        if (_argumentCandidates.Count > 0)
        {
            value = _argumentCandidates[SelectedIndex];
        }
        else if (_query.Length > 0)
        {
            value = _query;
        }
        else
        {
            return null;
        }

        _argumentValues.Add(value);
        if (_argumentIndex + 1 < _argumentSchemas.Count)
        {
            _argumentIndex++;
            _query = "";
            UpdateArgumentCandidates();
            return null;
        }

        return BuildCommand();
    }

    private bool EnterCommandFollowup()
    {
        if (_command!.Subcommands is { Count: > 0 })
        {
            _prefix = $"/{_command.Name} ";
            _query = "";
            Stage = MenuStage.Subcommand;
            UpdateSubcommands();
            return true;
        }
        if (_command.MenuSchema is not null || _command.ArgumentSchemas is { Count: > 0 })
        {
            EnterArgument();
            return true;
        }
        return false;
    }

    private bool EnterSubcommandFollowup()
    {
        if (_subcommand!.MenuSchema is not null || _subcommand.ArgumentSchemas is { Count: > 0 })
        {
            EnterArgument();
            return true;
        }
        return false;
    }

    private void EnterArgument()
    {
        var source = _subcommand ?? _command;
        _argumentSchemas = source?.ArgumentSchemas is { Count: > 0 } schemas
            ? schemas
            : source?.MenuSchema is null
                ? [new CommandArgumentSchema("value", "text")]
                : [new CommandArgumentSchema("value", "select")];
        _argumentIndex = 0;
        _argumentValues.Clear();
        _prefix = _subcommand is null
            ? $"/{_command!.Name} "
            : $"/{_command!.Name} {_subcommand.Name} ";
        _query = "";
        Stage = MenuStage.Argument;
        UpdateArgumentCandidates();
    }

    private string BuildCommand()
    {
        var parts = new List<string> { $"/{_command!.Name}" };
        if (_subcommand is not null)
            parts.Add(_subcommand.Name);
        for (var index = 0; index < _argumentSchemas.Count; index++)
        {
            var schema = _argumentSchemas[index];
            var value = _argumentValues[index];
            if (schema.Flag is { } flag)
            {
                parts.Add(flag);
                parts.Add(value);
            }
            else
            {
                parts.Add(value);
            }
        }
        return string.Join(' ', parts);
    }

    private void UpdateRoot(string query)
    {
        Stage = MenuStage.Root;
        _prefix = "/";
        _query = query;
        _command = null;
        _subcommand = null;
        _rootFiltered = _commands
            .Where(command => command.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SelectedIndex = 0;
    }

    private void UpdateSubcommands()
    {
        _subFiltered = (_command?.Subcommands ?? [])
            .Where(command => command.Name.StartsWith(_query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SelectedIndex = 0;
    }

    private void UpdateArgumentCandidates()
    {
        var source = _subcommand ?? _command;
        var schema = CurrentArgumentSchema;
        _argumentCandidates = source is null || schema is null
            ? []
            : (_candidateProvider?.Invoke(source) ?? schema.Choices ?? [])
                .Where(candidate => candidate.StartsWith(_query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        SelectedIndex = 0;
    }
}
