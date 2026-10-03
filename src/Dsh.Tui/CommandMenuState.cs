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
    private readonly Func<CommandDescriptor, CommandDescriptor?, CommandArgumentSchema?, IReadOnlyList<string>>? _candidateProvider;
    private IReadOnlyList<CommandDescriptor> _rootFiltered = [];
    private IReadOnlyList<CommandDescriptor> _subFiltered = [];
    private IReadOnlyList<string> _argumentCandidates = [];
    private IReadOnlyList<CommandArgumentSchema> _argumentSchemas = [];
    private readonly List<string> _argumentValues = [];
    private readonly List<string?> _argumentPrefills = [];
    private CommandDescriptor? _command;
    private CommandDescriptor? _subcommand;
    private string _prefix = "/";
    private string _query = "";
    private int _argumentIndex;

    public CommandMenuState(
        IReadOnlyList<CommandDescriptor> commands,
        Func<CommandDescriptor, CommandDescriptor?, CommandArgumentSchema?, IReadOnlyList<string>>? candidateProvider = null)
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

    /** 与 Candidates 对齐的候选说明(浮层右侧解说词): 根/子命令阶段取命令描述, 参数阶段为空。 */
    public IReadOnlyList<string> CandidateDescriptions => Stage switch
    {
        MenuStage.Root => [.. _rootFiltered.Select(command => command.Description)],
        MenuStage.Subcommand => [.. _subFiltered.Select(command => command.Description)],
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

    /** 参数面板数据: 当前命令的全部参数定义与已填值(与 _argumentIndex 对应)。 */
    public IReadOnlyList<CommandArgumentSchema> ArgumentSchemas => _argumentSchemas;

    public IReadOnlyList<string> ArgumentValues => _argumentValues;

    /** 预置值: 由"先选目录 provider 再自动带出 baseUrl/type/models"这类流程写入; 输入行为空时按 Enter 即采纳。 */
    public string? PrefilledValue(int index)
        => index >= 0 && index < _argumentPrefills.Count ? _argumentPrefills[index] : null;

    /** 给某个参数预置值(按参数名匹配, 含 flag 名与位置名); 找不到该参数时忽略。 */
    public void Prefill(string argumentName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        for (var index = 0; index < _argumentSchemas.Count; index++)
        {
            var schema = _argumentSchemas[index];
            if (!string.Equals(schema.Name, argumentName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(schema.Flag, argumentName, StringComparison.OrdinalIgnoreCase))
                continue;
            while (_argumentPrefills.Count <= index)
                _argumentPrefills.Add(null);
            _argumentPrefills[index] = value;
            if (index == _argumentIndex)
                UpdateArgumentCandidates();
            return;
        }
    }

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

    /** Home/End: 跳候选首/尾(与 PgUp/PgDn 翻页配套)。 */
    public void MoveHome() => SelectedIndex = 0;

    public void MoveEnd() => SelectedIndex = Math.Max(0, Candidates.Count - 1);

    /** 大候选列表(数百模型)翻页; rows 取浮层可见行数。 */
    public void MovePage(int direction, int rows)
    {
        if (Candidates.Count == 0 || rows <= 0)
            return;
        SelectedIndex = Math.Clamp(SelectedIndex + direction * rows, 0, Candidates.Count - 1);
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

    /**
     * Tab 语义: 采纳当前参数后循环到下一条参数(走到最后一条再按回到第一条), 永不结束命令, 也不退回上一级菜单。
     * 循环覆盖所有参数(含可选), 便于用户回头修改(例如把 --model-ids 从 <c>&lt;all&gt;</c> 改成具体几个模型)。
     */
    public bool MoveToNextArgument()
    {
        if (!IsActive || Stage != MenuStage.Argument)
            return false;
        // Tab 离开时只保留"已经有的值", 不替用户挑候选(否则路过 --type 就悄悄变成第一个协议族)。
        KeepArgument();
        var next = NextArgumentIndex(_argumentIndex);
        if (next == _argumentIndex)
            return false;
        _argumentIndex = next;
        _query = "";
        UpdateArgumentCandidates();
        return true;
    }

    /** 保留当前参数已有值: 手输(含高亮候选)或预置; 都没有就什么都不写, 等用户用 Enter 确认。 */
    private void KeepArgument()
    {
        if (_query.Length > 0)
        {
            SetValue(_argumentIndex, _argumentCandidates.Count > 0 ? _argumentCandidates[SelectedIndex] : _query);
            return;
        }

        if (RecordedValue(_argumentIndex) is not null)
            return;
        if (PrefilledValue(_argumentIndex) is { Length: > 0 } prefill)
            SetValue(_argumentIndex, prefill);
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
                _argumentPrefills.Clear();
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
                    while (_argumentValues.Count > _argumentIndex)
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
                _argumentPrefills.Clear();
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
        _argumentPrefills.Clear();
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
        if (!AcceptArgument())
            return null;
        var next = NextPendingIndex(_argumentIndex);
        if (next >= 0)
        {
            for (var index = _argumentIndex + 1; index < next; index++)
                RecordPrefill(index);
            _argumentIndex = next;
            _query = "";
            UpdateArgumentCandidates();
            return null;
        }

        // 后面没有待填参数: 先把跳过的参数按预置值补齐, 再检查还有没有空缺的必填参数。
        for (var index = 0; index < _argumentSchemas.Count; index++)
            RecordPrefill(index);
        var missing = FirstMissingRequired();
        if (missing >= 0)
        {
            _argumentIndex = missing;
            _query = "";
            UpdateArgumentCandidates();
            return null;
        }

        return BuildCommand();
    }

    /**
     * 采纳当前参数: 手输内容 > 高亮候选 > 该参数已记录的值(回头修改时保留, 输入任意字符即可改写) > 预置值 > 候选。
     * 必填且以上皆无时返回 false(停在原地)。
     */
    private bool AcceptArgument()
    {
        var schema = CurrentArgumentSchema;
        if (schema is null)
            return false;
        string value;
        if (_query.Length > 0)
            value = _argumentCandidates.Count > 0 ? _argumentCandidates[SelectedIndex] : _query;
        else if (RecordedValue(_argumentIndex) is { } recorded)
            value = recorded;
        else if (PrefilledValue(_argumentIndex) is { Length: > 0 } prefill)
            value = prefill;
        else if (_argumentCandidates.Count > 0)
            value = _argumentCandidates[SelectedIndex];
        else if (schema.Required)
            return false;
        else
            value = "";
        SetValue(_argumentIndex, value);
        return true;
    }

    /** 该参数已记录的非空值(空的表示还没采纳过)。 */
    private string? RecordedValue(int index)
        => index < _argumentValues.Count && _argumentValues[index].Length > 0 ? _argumentValues[index] : null;

    /** 按位置记录参数值(跳过的参数也要占位, 否则构造命令时错位)。 */
    private void SetValue(int index, string value)
    {
        while (_argumentValues.Count <= index)
            _argumentValues.Add("");
        _argumentValues[index] = value;
    }

    /** 跳过的参数按其预置值补齐; 没有预置就保持空缺(必填空缺由 FirstMissingRequired 兜住)。 */
    private void RecordPrefill(int index)
    {
        if (index < _argumentValues.Count && _argumentValues[index].Length > 0)
            return;
        if (PrefilledValue(index) is { Length: > 0 } prefill)
            SetValue(index, prefill);
    }

    /** 是否已满足: 已记录非空值, 或有非空预置值(预置值可被直接采纳)。 */
    private bool IsSatisfied(int index)
        => RecordedValue(index) is not null || PrefilledValue(index) is { Length: > 0 };

    /** 当前参数之后第一条未满足的参数; 没有则 -1。 */
    private int NextPendingIndex(int from)
    {
        for (var index = from + 1; index < _argumentSchemas.Count; index++)
        {
            if (!IsSatisfied(index))
                return index;
        }
        return -1;
    }

    /** 第一条仍为空缺的必填参数; 没有则 -1。 */
    private int FirstMissingRequired()
    {
        for (var index = 0; index < _argumentSchemas.Count; index++)
        {
            if (_argumentSchemas[index].Required
                && (index >= _argumentValues.Count || _argumentValues[index].Length == 0))
                return index;
        }
        return -1;
    }

    private int NextArgumentIndex(int from) => from + 1 < _argumentSchemas.Count ? from + 1 : 0;

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
                : [new CommandArgumentSchema("value", "select", source.MenuSchema.Prompt)];
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
            var value = index < _argumentValues.Count ? _argumentValues[index] : "";
            if (value.Length == 0)
                continue;   // 跳过的可选参数不出现在命令里
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
        if (source is null || schema is null)
        {
            _argumentCandidates = [];
        }
        else
        {
            var provided = _candidateProvider?.Invoke(_command!, _subcommand, schema);
            var pool = provided is { Count: > 0 } ? provided : schema.Choices ?? [];
            _argumentCandidates = pool
                .Where(candidate => candidate.Contains(_query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        SelectedIndex = 0;
        if (_query.Length == 0)
        {
            // 回看已填参数(或目录预置)时把当前值置为高亮项: 空回车/Tab 保留原值, 输入任意字符才改为筛选。
            HighlightCandidate(RecordedValue(_argumentIndex) ?? PrefilledValue(_argumentIndex));
        }
    }

    private void HighlightCandidate(string? value)
    {
        if (value is not { Length: > 0 })
            return;
        for (var index = 0; index < _argumentCandidates.Count; index++)
        {
            if (!string.Equals(_argumentCandidates[index], value, StringComparison.Ordinal))
                continue;
            SelectedIndex = index;
            return;
        }
    }
}
