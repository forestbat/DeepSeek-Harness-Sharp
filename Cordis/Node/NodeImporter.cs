using System.Text.Json.Nodes;

namespace Cordis.Node;

public sealed class JsPluginCallback(NodeHost host, string key) : PluginCallback
{
    public string Key { get; } = key;

    public override object? Invoke(Context ctx, object? config) => ApplyAsync(ctx, config);

    private async Task<object?> ApplyAsync(Context ctx, object? config)
    {
        var result = await host.RequestAsync("apply", new JsonObject
        {
            ["key"] = Key,
            ["ctx"] = host.Handles.Store(ctx),
            ["config"] = NodeMarshal.Marshal(host, config),
        });
        var disposes = new List<long>();
        if (result is Dictionary<string, object?> dict
            && CordisUtils.GetOrNull(dict, "disposes") is IEnumerable<object?> ids)
        {
            disposes.AddRange(ids.OfType<long>());
        }
        return (Func<Task>)(async () =>
        {
            var array = new JsonArray();
            foreach (var handleId in disposes) array.Add(handleId);
            await host.RequestAsync("dispose", new JsonObject { ["handles"] = array });
        });
    }
}

public sealed class JsConfigValidator(NodeHost host, string key) : IConfigValidator
{
    public object? Validate(object? config)
    {
        var result = host.RequestAsync("validate", new JsonObject
        {
            ["key"] = key,
            ["config"] = NodeMarshal.Marshal(host, config),
        }).GetAwaiter().GetResult();
        if (result is Dictionary<string, object?> dict)
        {
            if (CordisUtils.GetOrNull(dict, "issues") is IEnumerable<object?> issues)
            {
                throw new ValidationError(issues.OfType<Dictionary<string, object?>>().Select(issue =>
                    new ValidationIssue
                    {
                        Message = issue.GetOrNull("message")?.ToString() ?? "invalid",
                        Path = (issue.GetOrNull("path") as IEnumerable<object?>)?.Select(p => p?.ToString() ?? "").ToList(),
                    }).ToList());
            }
            return dict.GetOrNull("value");
        }
        return config;
    }
}

public sealed class NodeImporter(NodeHost host) : Cordis.Loader.IModuleImporter
{
    public async Task<object?> Import(string specifier, string? baseUrl)
    {
        var result = await host.RequestAsync("import", new JsonObject
        {
            ["specifier"] = specifier,
            ["baseUrl"] = baseUrl,
        });
        if (result is Dictionary<string, object?> dict && dict.GetOrNull("key") is string key)
        {
            return host.GetPlugin(key, dict.GetOrNull("name") as string, dict.GetOrNull("hasConfig") is true, dict.GetOrNull("inject"));
        }
        throw new JsRemoteException($"invalid plugin module: {specifier}", null);
    }

    public async ValueTask<object?> Evaluate(Context ctx, string expr)
    {
        return await host.RequestAsync("eval", new JsonObject
        {
            ["ctx"] = host.Handles.Store(ctx),
            ["expr"] = expr,
        });
    }
}

public static class NodeHostExtensions
{
    public static void Attach(this Cordis.Loader.Loader loader, NodeHost host)
    {
        loader.Importer = new NodeImporter(host);
        host.RootContext = loader.Ctx.Root;
    }
}
