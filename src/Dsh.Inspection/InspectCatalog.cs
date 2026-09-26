using System.Globalization;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Inspection;

/** 创造模式的只读运行时数据源:服务、事件、配置、工具四类,全部来自公开可解析的运行时状态。
 *  事件表(EventTable)在 Dsh.Runtime 内为 internal,没有公开只读枚举入口,故该类别如实降级为不可用。 */
public static class InspectCatalog
{
    public const string Service = "service";
    public const string Event = "event";
    public const string Config = "config";
    public const string Tool = "tool";

    public const int MaxDetailLength = 160;

    public static readonly IReadOnlyList<string> Categories = [Service, Event, Config, Tool];

    private const string EventUnavailable =
        "the event table is internal to Dsh.Runtime and exposes no read-only enumeration API in this build";

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

    private static JsonObject ConfigList(Context ctx)
        => TryLoadSettings(ctx, out var settings, out var reason)
            ? Available(Config, ConfigItems(settings))
            : Unavailable(Config, reason);

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

    private static JsonArray ConfigItems(Dictionary<string, object?> settings)
    {
        var array = new JsonArray();
        foreach (var (key, value) in settings.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            array.Add(Item(key, Describe(value)));
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

    private static JsonObject ConfigQuery(Context ctx, string name)
    {
        if (!TryLoadSettings(ctx, out var settings, out var reason))
            throw new InvalidOperationException(reason);
        if (!settings.TryGetValue(name, out var value))
            throw new InvalidOperationException($"unknown inspectable config key \"{name}\"");
        return new JsonObject
        {
            ["category"] = Config,
            ["name"] = name,
            ["data"] = ToNode(value),
        };
    }

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
