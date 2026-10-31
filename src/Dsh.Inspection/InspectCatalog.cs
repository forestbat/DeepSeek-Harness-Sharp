using System.Globalization;
using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Inspection;

/** Public read-only service, configuration and tool inspection, with event inspection explicitly unavailable. */
public static class InspectCatalog
{
    public const string Service = "service";
    public const string Event = "event";
    public const string Config = "config";
    public const string Tool = "tool";

    public const int MaxDetailLength = 160;

    public static readonly IReadOnlyList<string> Categories = [Service, Event, Config, Tool];

    private const string EventUnavailable =
        "the event category is not wired yet; Context.EventSubscriptions already exposes a public read-only subscription snapshot";

    public static JsonObject List(Context ctx, ScopeKey? scope, string? category)
    {
        var categories = new JsonArray();
        foreach (var id in Categories)
        {
            if (category is not null && !string.Equals(category, id, StringComparison.Ordinal))
                continue;
            categories.Add(ListCategory(ctx, scope, id));
        }
        return new JsonObject { ["categories"] = categories };
    }

    public static JsonObject Query(Context ctx, ScopeKey? scope, string category, string name)
        => category switch
        {
            Service => ServiceQuery(ctx, name),
            Event => throw new InvalidOperationException(EventUnavailable),
            Config => ConfigQuery(ctx, name),
            Tool => ToolQuery(ctx, scope, name),
            _ => throw new InvalidOperationException(
                $"unknown inspect category \"{category}\"; expected one of {string.Join(", ", Categories)}"),
        };

    private static JsonObject ListCategory(Context ctx, ScopeKey? scope, string category)
        => category switch
        {
            Service => Available(Service, ServiceItems(ctx)),
            Event => Unavailable(Event, EventUnavailable),
            Config => ConfigList(ctx),
            Tool => Available(Tool, ToolItems(ctx, scope)),
            _ => throw new InvalidOperationException($"unknown inspect category \"{category}\""),
        };

    /**
     * 配置类别: 同时给出**可用键**(来自编译期配置面 schema: 顶层 settings + 各插件参数, 路径形如
     * `logging.keep_days` 或 `plugins."@deepseek-ai/dsh-toon".input`)与**文件里已写的键**。
     * detail 前缀标明来源: [set]=已设置 / [default]=未设置但可配 / [unknown]=schema 里没有(疑似拼错或已废弃)。
     */
    private static JsonObject ConfigList(Context ctx)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, spec) in ConfigPaths())
        {
            entries[path] = spec.DefaultLiteral is { Length: > 0 } literal
                ? $"[default] {spec.Kind} = {literal}"
                : $"[default] {spec.Kind}";
        }
        if (TryLoadSettings(ctx, out var settings, out var reason))
        {
            foreach (var (path, value) in Flatten(settings, ""))
            {
                entries[path] = entries.TryGetValue(path, out var known)
                    ? known.StartsWith("[default]", StringComparison.Ordinal)
                        ? $"[set] {Describe(value)} ({known})"
                        : $"[set] {Describe(value)}"
                    : $"[unknown] {Describe(value)} (schema 里没有这个键; 疑似拼错或已废弃)";
            }
        }
        else
        {
            entries["(settings)"] = $"[unknown] {reason}";
        }
        var array = new JsonArray();
        foreach (var (path, detail) in entries)
            array.Add(Item(path, detail));
        return Available(Config, array);
    }

    /** 单个配置键(路径): 已设置返回值; 未设置但可配返回 schema 里的默认值; 两者都不中报未知键。 */
    private static JsonObject ConfigQuery(Context ctx, string name)
    {
        var normalized = name.Trim().Trim('"');
        if (TryLoadSettings(ctx, out var settings, out _)
            && Flatten(settings, "").FirstOrDefault(entry => string.Equals(entry.Path, normalized, StringComparison.Ordinal)) is { Path: not null } found)
        {
            return new JsonObject
            {
                ["category"] = Config,
                ["name"] = name,
                ["data"] = new JsonObject
                {
                    ["path"] = found.Path,
                    ["source"] = "settings.yaml",
                    ["value"] = ToNode(found.Value),
                },
            };
        }
        if (ConfigPaths().FirstOrDefault(entry => string.Equals(entry.Path, normalized, StringComparison.Ordinal)) is { Path: not null } spec)
        {
            return new JsonObject
            {
                ["category"] = Config,
                ["name"] = name,
                ["data"] = new JsonObject
                {
                    ["path"] = spec.Path,
                    ["source"] = "default",
                    ["kind"] = spec.Spec.Kind.ToString(),
                    ["default"] = spec.Spec.DefaultLiteral,
                },
            };
        }
        throw new InvalidOperationException(
            $"unknown inspectable config key \"{name}\"; use creative_inspect_list with category \"config\" to see available keys");
    }

    /** schema 里的全部配置路径(顶层文档 + 各插件参数)。 */
    private static List<(string Path, PluginSettingSpec Spec)> ConfigPaths()
    {
        var paths = new List<(string, PluginSettingSpec)>();
        foreach (var provider in SettingsSchemaCatalog.All())
        {
            var prefix = provider.IsPlugin ? $"plugins.\"{provider.Package}\"" : "";
            Collect(provider.Settings, prefix, paths);
        }
        return paths;
    }

    private static void Collect(IReadOnlyList<PluginSettingSpec> settings, string prefix, List<(string, PluginSettingSpec)> paths)
    {
        foreach (var spec in settings)
        {
            var path = prefix.Length == 0 ? spec.Key : $"{prefix}.{spec.Key}";
            paths.Add((path, spec));
            if (spec.Children is { Count: > 0 } children)
                Collect(children, path, paths);
        }
    }

    /** settings.yaml 的原始映射摊平成路径(嵌套映射展开, 列表按 `key[0]` 索引)。 */
    private static List<(string Path, object? Value)> Flatten(Dictionary<string, object?> mapping, string prefix)
    {
        var entries = new List<(string, object?)>();
        foreach (var (key, value) in mapping)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            entries.Add((path, value));
            switch (value)
            {
                case Dictionary<string, object?> nested:
                    entries.AddRange(Flatten(nested, path));
                    break;
                case List<object?> sequence:
                    for (var index = 0; index < sequence.Count && index < 10; index++)
                        entries.Add(($"{path}[{index}]", sequence[index]));
                    break;
            }
        }
        return entries;
    }

    private static JsonArray ServiceItems(Context ctx)
    {
        var items = new List<(string Name, string Detail)>();
        var root = ctx.Root.Activation;
        foreach (var name in root.ProvidedNames)
            items.Add((name, "provided by <root> [active]"));
        foreach (var activation in ctx.Root.Scheduler.Snapshot())
        {
            var detail = $"provided by <{activation.Name}> [{StateName(activation.State)}]";
            foreach (var name in activation.ProvidedNames)
                items.Add((name, detail));
        }
        var array = new JsonArray();
        foreach (var (name, detail) in items.OrderBy(entry => entry.Name, StringComparer.Ordinal))
            array.Add(Item(name, detail));
        return array;
    }

    private static JsonArray ToolItems(Context ctx, ScopeKey? scope)
    {
        var array = new JsonArray();
        foreach (var schema in ToolSchemas(ctx, scope).OrderBy(schema => schema.Name, StringComparer.Ordinal))
            array.Add(Item(schema.Name, Summarize(schema.Description)));
        return array;
    }

    private static JsonObject ServiceQuery(Context ctx, string name)
    {
        var root = ctx.Root.Activation;
        if (root.ProvidedNames.Contains(name, StringComparer.Ordinal))
            return ServiceData(root, name);
        var activation = ctx.Root.Scheduler.Snapshot()
            .FirstOrDefault(candidate => candidate.ProvidedNames.Contains(name, StringComparer.Ordinal));
        if (activation is null)
            throw new InvalidOperationException($"unknown inspectable service \"{name}\"");
        return ServiceData(activation, name);
    }

    private static JsonObject ServiceData(PluginActivation activation, string name)
        => new()
        {
            ["category"] = Service,
            ["name"] = name,
            ["data"] = new JsonObject
            {
                ["provider"] = activation.Name,
                ["providerState"] = StateName(activation.State),
                ["providerError"] = activation.Error,
                ["inject"] = Strings(activation.Inject),
                ["pluginServices"] = Strings(activation.ProvidedNames.OrderBy(value => value, StringComparer.Ordinal)),
            },
        };

    private static JsonObject ToolQuery(Context ctx, ScopeKey? scope, string name)
    {
        var schema = ToolSchemas(ctx, scope).FirstOrDefault(schema => string.Equals(schema.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"unknown inspectable tool \"{name}\"");
        return new JsonObject
        {
            ["category"] = Tool,
            ["name"] = name,
            ["data"] = new JsonObject
            {
                ["name"] = schema.Name,
                ["description"] = schema.Description,
                ["parameters"] = schema.Parameters.DeepClone(),
            },
        };
    }

    private static IReadOnlyList<ToolSchema> ToolSchemas(Context ctx, ScopeKey? scope)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("the tools service is not registered");
        return tools.Schemas(scope);
    }

    private static bool TryLoadSettings(Context ctx, out Dictionary<string, object?> settings, out string reason)
    {
        settings = [];
        reason = "";
        if (ctx.GetProp("dshHomePath") is not string home)
        {
            reason = "no DSH home path is available in this context";
            return false;
        }
        var path = Path.Combine(home, "settings.yaml");
        if (!File.Exists(path))
        {
            reason = $"settings.yaml was not found under {home}";
            return false;
        }
        try
        {
            settings = YamlConfig.LoadMapping(File.ReadAllText(path));
            return true;
        }
        catch (Exception error)
        {
            reason = $"settings.yaml could not be read: {error.Message}";
            return false;
        }
    }

    private static JsonObject Available(string category, JsonArray items)
        => new() { ["category"] = category, ["available"] = true, ["items"] = items };

    private static JsonObject Unavailable(string category, string reason)
        => new() { ["category"] = category, ["available"] = false, ["items"] = new JsonArray(), ["reason"] = reason };

    private static JsonObject Item(string name, string detail)
        => new() { ["name"] = name, ["detail"] = detail };

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        Dictionary<string, object?> mapping => $"mapping ({mapping.Count} entries)",
        List<object?> sequence => $"sequence ({sequence.Count} items)",
        bool flag => flag ? "true" : "false",
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString(CultureInfo.InvariantCulture),
        string text => Summarize(text),
        _ => Summarize(value.ToString() ?? ""),
    };

    private static string Summarize(string text)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= MaxDetailLength ? line : line[..MaxDetailLength] + "...";
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        bool flag => JsonValue.Create(flag),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        string text => JsonValue.Create(text),
        Dictionary<string, object?> mapping => MappingNode(mapping),
        List<object?> sequence => SequenceNode(sequence),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonObject MappingNode(Dictionary<string, object?> mapping)
    {
        var node = new JsonObject();
        foreach (var (key, value) in mapping)
            node[key] = ToNode(value);
        return node;
    }

    private static JsonArray SequenceNode(List<object?> sequence)
    {
        var node = new JsonArray();
        foreach (var value in sequence)
            node.Add(ToNode(value));
        return node;
    }

    private static string StateName(ActivationState state) => state switch
    {
        ActivationState.Pending => "pending",
        ActivationState.Activating => "activating",
        ActivationState.Active => "active",
        ActivationState.Deactivating => "deactivating",
        ActivationState.Failed => "failed",
        ActivationState.Disposed => "disposed",
        _ => state.ToString(),
    };
}
