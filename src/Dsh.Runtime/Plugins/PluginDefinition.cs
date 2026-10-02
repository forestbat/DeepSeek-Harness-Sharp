namespace Dsh.Runtime;

public sealed record PluginDefinition
{
    public string? Name { get; init; }
    public IReadOnlyList<string> Inject { get; init; } = [];

    /** 类型化依赖边:按契约类型匹配服务,解析语义见 IDshPlugin.InjectTypes。 */
    public IReadOnlyList<Type> InjectTypes { get; init; } = [];

    /** 插件自带配置类型:非 null 时组合层先把 config 绑定成该类型再进 Apply。 */
    public Type? ConfigType { get; init; }

    public required Func<Context, object?, object?> Apply { get; init; }

    public static PluginDefinition From(
        Func<Context, object?, object?> apply,
        string? name = null,
        IReadOnlyList<string>? inject = null,
        IReadOnlyList<Type>? injectTypes = null)
    {
        return new PluginDefinition
        {
            Name = name ?? apply.Method.Name,
            Inject = inject ?? [],
            InjectTypes = injectTypes ?? [],
            Apply = apply,
        };
    }
}

public enum ActivationState
{
    Pending,
    Activating,
    Active,
    Deactivating,
    Failed,
    Disposed,
}
