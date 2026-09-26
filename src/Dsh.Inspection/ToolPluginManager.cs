using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Inspection;

/** plugin_manager:包装 IPluginManager(与 /plugins 命令同一服务),每个操作都经 PreToolDecision.Ask 审批。 */
public static class ToolPluginManager
{
    public const string ToolName = "plugin_manager";
    public const string ServiceName = "pluginManager";

    internal const string ApprovalReason =
        "plugin_manager changes the loaded plugin set and persists the change across sessions; approve this operation.";

    private const string Description =
        "List loaded plugins, describe one plugin, enable or disable one, install a plugin from a package name or "
        + "a .dll path, or remove one. Every action requires approval for this call; approval does not change the "
        + "session permission mode. Changes persist in settings.yaml and affect every session. Call action \"list\" "
        + "first to obtain exact package names.";

    internal static IDisposable ApprovalGate(Context ctx)
    {
        var gate = ctx.OnWaterfall<ToolPreExecuteNotification>(
            (notification, next) => string.Equals(notification.Run.Name, ToolName, StringComparison.Ordinal)
                ? ValueTask.FromResult<object?>(new PreToolDecision.Ask(ApprovalReason))
                : next(),
            new EventOptions { Global = true });
        return new ActionDisposable(() => gate());
    }

    internal static ToolDefinition Definition(Context ctx) => new()
    {
        Name = ToolName,
        Description = Description,
        Parameters = JsonNode.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["action"],
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["list", "describe", "enable", "disable", "add", "remove"],
                  "description": "Management operation; every action requires approval."
                },
                "target": {
                  "type": "string",
                  "description": "Exact plugin package name, or for add a package name or .dll path."
                },
                "force": {
                  "type": "boolean",
                  "description": "For remove: force unload without waiting for cooperative settlement."
                }
              }
            }
            """)!.AsObject(),
        Output = new ToolOutputDefinition(
            JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["action", "result"],
                  "properties": {
                    "action": { "type": "string" },
                    "target": { "type": ["string", "null"] },
                    "result": { "type": "string" }
                  }
                }
                """)!.AsObject(),
            (_, value) => [new TextBlock(value.GetProperty("result").GetString() ?? "")]),
        Execute = (args, _) => Execute(ctx, args),
    };

    private static async Task<object?> Execute(Context ctx, JsonElement args)
    {
        var action = RequireAction(args);
        var target = args.TryGetProperty("target", out var targetElement) && targetElement.ValueKind == JsonValueKind.String
            ? targetElement.GetString()
            : null;
        var force = args.TryGetProperty("force", out var forceElement) && forceElement.ValueKind == JsonValueKind.True;
        var manager = ctx.Get<IPluginManager>(ServiceName, false)
            ?? throw new InvalidOperationException("plugin_manager is unavailable: the plugin manager service is not registered");
        var result = action switch
        {
            "list" => ListResult(manager),
            "describe" => manager.Describe(RequireTarget(target)),
            "enable" => await manager.EnableAsync(RequireTarget(target)),
            "disable" => await manager.DisableAsync(RequireTarget(target)),
            "add" => await manager.AddAsync(RequireTarget(target)),
            "remove" => await manager.RemoveAsync(RequireTarget(target), force),
            _ => throw new InvalidOperationException($"unknown plugin_manager action \"{action}\""),
        };
        return new JsonObject
        {
            ["action"] = action,
            ["target"] = target,
            ["result"] = result,
        };
    }

    private static string ListResult(IPluginManager manager)
    {
        var names = manager.PackageNames;
        return names.Count == 0
            ? "(no plugins)"
            : string.Join('\n', names.Select(name => $"{name}: {manager.Describe(name)}"));
    }

    private static string RequireAction(JsonElement args)
    {
        if (!args.TryGetProperty("action", out var element) || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidOperationException("plugin_manager requires a non-empty \"action\"");
        }
        return element.GetString()!;
    }

    private static string RequireTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("plugin_manager requires \"target\" for this action");
        return target;
    }
}
