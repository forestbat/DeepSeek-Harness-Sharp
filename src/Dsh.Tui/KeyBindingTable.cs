namespace Dsh.Tui;

/**
 * TUI 里可改键的动作。
 * 配置键名是枚举名的 camelCase(如 newSession), 它就是 settings.yaml 的契约: 改名必须同时迁移用户配置。
 */
internal enum TuiAction
{
    NewSession,
    SessionMenu,
    Detach,
    DeleteSessionMenu,
    AgentList,
    Overview,
    OpenShell,
    Quit,
    SplitPane,
    ClosePane,
    PrevSession,
    NextSession,
    FocusLeft,
    FocusRight,
    FocusUp,
    FocusDown,
    FocusNextPane,
    Interrupt,
    PromptCommandMenu,

    /** 复制当前结果浮层的全文到系统剪贴板。 */
    CopyOverlay,
}

/** 按键发生时的焦点: 聊天输入认全部动作; shell 窗格只认窗口级动作, 其余按键直达 shell。 */
internal enum KeyContext
{
    Chat,
    Shell,
}

/** 提示文本里的一项: 一组动作共用一个说明(如 `,/. 切会话`)。 */
internal sealed record KeyHint(string Label, params TuiAction[] Actions);

/** 一次加载的结果: 生效的键位表 + 被拒绝的配置项说明(调用方记 WARN)。 */
internal sealed record KeyBindingLoad(KeyBindingTable Table, IReadOnlyList<string> Warnings);

/**
 * 动作 → 按键序列的查找表: 内置默认键位, settings.yaml 的 keys 段逐项覆盖, 缺项回退默认。
 * 按键串语法 `Ctrl+X D`: 修饰键用 `+` 连接, 序列用空格分隔。
 * 加载时校验冲突(同一序列绑了多个动作 / 一个序列是另一个的前缀): 记 WARN, 涉及的自定义项回退默认。
 */
internal sealed class KeyBindingTable
{
    private const string HintSeparator = " - ";
    private const string AlternativeSeparator = "/";
    private const string ArrowGroupName = "方向键";

    private static readonly string[] ArrowKeys = ["Left", "Right", "Up", "Down"];

    private static readonly Dictionary<TuiAction, string> DefaultKeys = new()
    {
        [TuiAction.NewSession] = "Ctrl+X N",
        [TuiAction.SessionMenu] = "Ctrl+X S",
        [TuiAction.Detach] = "Ctrl+X D",
        [TuiAction.DeleteSessionMenu] = "Ctrl+X K",
        [TuiAction.AgentList] = "Ctrl+X A",
        [TuiAction.Overview] = "Ctrl+X W",
        [TuiAction.OpenShell] = "Ctrl+X T",
        [TuiAction.Quit] = "Ctrl+X Q",
        [TuiAction.SplitPane] = "Ctrl+X +",
        [TuiAction.ClosePane] = "Ctrl+X -",
        [TuiAction.PrevSession] = "Ctrl+X ,",
        [TuiAction.NextSession] = "Ctrl+X .",
        [TuiAction.FocusLeft] = "Ctrl+X Left",
        [TuiAction.FocusRight] = "Ctrl+X Right",
        [TuiAction.FocusUp] = "Ctrl+X Up",
        [TuiAction.FocusDown] = "Ctrl+X Down",
        [TuiAction.FocusNextPane] = "Ctrl+X O",
        [TuiAction.Interrupt] = "Ctrl+C",
        [TuiAction.PromptCommandMenu] = "Ctrl+P",
        // 避开 Ctrl+Y(Word/Cherry Studio 的 redo)与终端的 Ctrl+Shift+C(复制选择)。
        [TuiAction.CopyOverlay] = "Alt+C",
    };

    private static readonly Dictionary<string, TuiAction> ActionsByName = Enum.GetValues<TuiAction>()
        .ToDictionary(ConfigName, StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<TuiAction, KeyChord[]> _bindings;
    private readonly HashSet<TuiAction> _customized;

    private KeyBindingTable(Dictionary<TuiAction, KeyChord[]> bindings, HashSet<TuiAction> customized)
    {
        _bindings = bindings;
        _customized = customized;
    }

    public static KeyBindingTable Default { get; } = new(DefaultBindings(), []);

    /** 配置里的动作名: 枚举名的 camelCase。 */
    public static string ConfigName(TuiAction action)
    {
        var name = action.ToString();
        return $"{char.ToLowerInvariant(name[0])}{name[1..]}";
    }

    /** 窗口级动作在 shell 窗格里也生效; 中断与命令选单只属于聊天输入(shell 里 Ctrl+C 要交给 shell)。 */
    public static bool AppliesIn(TuiAction action, KeyContext context)
        => context == KeyContext.Chat || action is not (TuiAction.Interrupt or TuiAction.PromptCommandMenu);

    /** 读 keys 段(动作名 → 按键串); 不合法的项与冲突项记进 Warnings 并保留默认键位。 */
    public static KeyBindingLoad Load(IReadOnlyDictionary<string, object?>? configured)
    {
        var bindings = DefaultBindings();
        var customized = new HashSet<TuiAction>();
        var warnings = new List<string>();
        foreach (var (name, value) in configured ?? new Dictionary<string, object?>())
        {
            if (Override(name, value) is not { } entry)
            {
                warnings.Add(RejectionReason(name, value));
                continue;
            }
            bindings[entry.Action] = entry.Sequence;
            customized.Add(entry.Action);
        }
        RevertConflicts(bindings, customized, warnings);
        return new KeyBindingLoad(new KeyBindingTable(bindings, customized), warnings);
    }

    /** 解析按键串(`Ctrl+X D`); 语法不对返回 null。 */
    public static KeyChord[]? ParseSequence(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var chords = tokens.Select(KeyChord.Parse).ToArray();
        return chords.Length > 0 && chords.All(chord => chord is not null) ? [.. chords.Select(chord => chord!.Value)] : null;
    }

    public static string Format(IEnumerable<KeyChord> sequence) => string.Join(' ', sequence);

    public IReadOnlyList<KeyChord> Sequence(TuiAction action) => _bindings[action];

    /** 已按下的序列在该焦点下: 恰好是某个动作(Action) / 还是某些动作的前缀(IsPrefix) / 都不是。 */
    public (TuiAction? Action, bool IsPrefix) Lookup(IReadOnlyList<KeyChord> typed, KeyContext context)
    {
        TuiAction? exact = null;
        var prefix = false;
        foreach (var (action, sequence) in _bindings)
        {
            if (!AppliesIn(action, context) || !StartsWith(sequence, typed))
                continue;
            if (sequence.Length == typed.Count)
                exact = action;
            else
                prefix = true;
        }
        return (exact, prefix);
    }

    /** 一组动作的完整按键写法: 共用前缀时合并(`Ctrl+X ,/.`、`Ctrl+X 方向键/O`), 否则各自完整写出再用 `/` 连接。 */
    public string Keys(params TuiAction[] actions)
    {
        var sequences = actions.Select(action => _bindings[action]).ToList();
        var leader = SharedLeader(sequences);
        return leader.Length > 0
            ? $"{Format(leader)} {Alternatives(sequences, leader.Length)}"
            : Alternatives(sequences, 0);
    }

    /** 动作所在的前缀键(`Ctrl+X`); 单键动作就是它自己。 */
    public string Leader(TuiAction action)
    {
        var sequence = _bindings[action];
        return Format(sequence.Length > 1 ? sequence[..^1] : sequence);
    }

    /** 一行帮助: 首项写完整按键, 之后与首项同前缀的项只写后续键(`Ctrl+X N 新会话 - S 会话`)。 */
    public string Line(params KeyHint[] hints)
    {
        var leader = SharedLeader([.. hints[0].Actions.Select(action => _bindings[action])]);
        var parts = hints.Select((hint, index) =>
        {
            var sequences = hint.Actions.Select(action => _bindings[action]).ToList();
            var underLeader = index > 0 && leader.Length > 0 && sequences.All(sequence => sequence.Length == leader.Length + 1 && StartsWith(sequence, leader));
            return $"{(underLeader ? Alternatives(sequences, leader.Length) : Keys(hint.Actions))} {hint.Label}";
        });
        return string.Join(HintSeparator, parts);
    }

    /** 前缀等待中的提示: 列出该前缀之下的动作, 只写后续键(`Ctrl+X: N 新会话 - ...`)。 */
    public string PrefixHint(IReadOnlyList<KeyChord> prefix, IEnumerable<KeyHint> hints)
    {
        var parts = hints
            .Select(hint => (hint.Label, Sequences: hint.Actions
                .Select(action => _bindings[action])
                .Where(sequence => sequence.Length > prefix.Count && StartsWith(sequence, prefix))
                .ToList()))
            .Where(item => item.Sequences.Count > 0)
            .Select(item => $"{Alternatives(item.Sequences, prefix.Count)} {item.Label}");
        return $"{Format(prefix)}: {string.Join(HintSeparator, parts)}";
    }

    /** `/keys` 的列表: 每个动作一行, 自定义过的标出来。 */
    public IReadOnlyList<string> Describe()
    {
        var width = ActionsByName.Keys.Max(name => name.Length);
        return
        [
            .. Enum.GetValues<TuiAction>().Select(action =>
                $"{ConfigName(action).PadRight(width)}  {Format(_bindings[action])}{(_customized.Contains(action) ? "  (自定义)" : "")}"),
        ];
    }

    private static Dictionary<TuiAction, KeyChord[]> DefaultBindings()
        => DefaultKeys.ToDictionary(entry => entry.Key, entry => ParseSequence(entry.Value)!);

    private static (TuiAction Action, KeyChord[] Sequence)? Override(string name, object? value)
    {
        if (!ActionsByName.TryGetValue(name, out var action) || value is not string text || ParseSequence(text) is not { } sequence)
            return null;
        return OwnsTypingKey(sequence[0]) ? null : (action, sequence);
    }

    /** 首键是无修饰的普通键时, 绑定会把用户正常打字吃掉: 只允许 Ctrl/Alt 组合或 F 键起头。 */
    private static bool OwnsTypingKey(KeyChord first) => !first.HasCommandModifier && !first.IsFunctionKey;

    private static string RejectionReason(string name, object? value)
    {
        if (!ActionsByName.ContainsKey(name))
            return $"tui keys: 未知动作 \"{name}\", 已忽略(可用动作见 /keys)";
        if (value is not string text || ParseSequence(text) is not { } sequence)
            return $"tui keys: {name} 的按键串 \"{value}\" 无法解析, 保留默认键位(语法如 \"Ctrl+X D\")";
        return $"tui keys: {name} 的按键串 \"{Format(sequence)}\" 以普通键开头会吞掉正常输入, 保留默认键位(首键需带 Ctrl/Alt 或为 F 键)";
    }

    /** 冲突里的自定义项回退默认; 默认键位彼此无冲突, 所以每轮至少回退一项, 必然收敛。 */
    private static void RevertConflicts(Dictionary<TuiAction, KeyChord[]> bindings, HashSet<TuiAction> customized, List<string> warnings)
    {
        while (FindConflict(bindings, customized) is { } conflict)
        {
            var (first, second) = conflict;
            warnings.Add(ConflictReason(first, bindings[first], second, bindings[second]));
            foreach (var action in (TuiAction[])[first, second])
            {
                if (customized.Remove(action))
                    bindings[action] = ParseSequence(DefaultKeys[action])!;
            }
        }
    }

    private static (TuiAction First, TuiAction Second)? FindConflict(Dictionary<TuiAction, KeyChord[]> bindings, HashSet<TuiAction> customized)
    {
        var actions = Enum.GetValues<TuiAction>();
        foreach (var first in actions)
        {
            foreach (var second in actions.Where(candidate => candidate > first))
            {
                if ((customized.Contains(first) || customized.Contains(second)) && Overlaps(bindings[first], bindings[second]))
                    return (first, second);
            }
        }
        return null;
    }

    private static string ConflictReason(TuiAction first, KeyChord[] firstKeys, TuiAction second, KeyChord[] secondKeys)
    {
        if (firstKeys.Length == secondKeys.Length)
            return $"tui keys: 按键串 \"{Format(firstKeys)}\" 同时绑定了 {ConfigName(first)} 与 {ConfigName(second)}, 自定义项保留默认键位";
        var (shortAction, shortKeys, longAction, longKeys) = firstKeys.Length < secondKeys.Length
            ? (first, firstKeys, second, secondKeys)
            : (second, secondKeys, first, firstKeys);
        return $"tui keys: {ConfigName(shortAction)} 的 \"{Format(shortKeys)}\" 是 {ConfigName(longAction)} 的 \"{Format(longKeys)}\" 的前缀, 自定义项保留默认键位";
    }

    /** 两个序列相同, 或一个是另一个的前缀: 都会让较长的那个永远按不到。 */
    private static bool Overlaps(KeyChord[] first, KeyChord[] second)
        => first.Length <= second.Length ? StartsWith(second, first) : StartsWith(first, second);

    private static bool StartsWith(IReadOnlyList<KeyChord> sequence, IReadOnlyList<KeyChord> prefix)
    {
        if (sequence.Count < prefix.Count)
            return false;
        for (var index = 0; index < prefix.Count; index++)
        {
            if (sequence[index] != prefix[index])
                return false;
        }
        return true;
    }

    /** 多个序列共用的前缀(除最后一键外完全相同且长度一致); 不共用返回空。 */
    private static KeyChord[] SharedLeader(IReadOnlyList<KeyChord[]> sequences)
    {
        var first = sequences[0];
        if (first.Length < 2)
            return [];
        var leader = first[..^1];
        return sequences.All(sequence => sequence.Length == first.Length && StartsWith(sequence, leader)) ? leader : [];
    }

    /** 各序列跳过前 skip 键后的写法用 `/` 连接; 四个方向键齐全且相邻时合写成"方向键"。 */
    private static string Alternatives(IEnumerable<KeyChord[]> sequences, int skip)
    {
        var names = sequences.Select(sequence => Format(sequence.Skip(skip))).ToList();
        var arrows = names.IndexOf(ArrowKeys[0]);
        if (arrows >= 0 && names.Skip(arrows).Take(ArrowKeys.Length).SequenceEqual(ArrowKeys))
        {
            names.RemoveRange(arrows, ArrowKeys.Length);
            names.Insert(arrows, ArrowGroupName);
        }
        return string.Join(AlternativeSeparator, names);
    }
}
