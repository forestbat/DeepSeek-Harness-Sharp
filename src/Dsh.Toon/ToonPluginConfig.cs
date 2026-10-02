namespace Dsh.Toon;

/**
 * TOON 插件的参数面(plugins."@deepseek-ai/dsh-toon".parameters):
 * output 控制输出侧(默认开: 提示模型用 TOON 写工具调用,原生工具 schema 不下发);
 * input 是输入侧白名单(列名表: 这些工具的结果值以 TOON 进上下文;缺省=关)。
 */
public sealed record ToonPluginConfig
{
    public const string PackageName = "@deepseek-ai/dsh-toon";

    public bool Output { get; init; } = true;

    public IReadOnlySet<string>? Input { get; init; }

    public static ToonPluginConfig Resolve(object? config)
    {
        if (config is not IReadOnlyDictionary<string, object?> map)
            return new ToonPluginConfig();
        return new ToonPluginConfig
        {
            Output = !map.TryGetValue("output", out var output) || output is not bool flag || flag,
            Input = ResolveInput(map.TryGetValue("input", out var input) ? input : null),
        };
    }

    private static IReadOnlySet<string>? ResolveInput(object? raw)
        => raw switch
        {
            null => null,
            string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal),
            IEnumerable<object?> list => list.OfType<string>().ToHashSet(StringComparer.Ordinal),
            _ => null,
        };
}
