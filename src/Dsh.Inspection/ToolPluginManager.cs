using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Inspection;

/** Plugin management shares the tool settings scope; read-only management actions retain their explicit approval. */
public static class ToolPluginManager
{
    public const string ToolName = "plugin_manager";
    public const string ServiceName = "pluginManager";

    internal const string ApprovalReason =
        "plugin_manager reads the loaded plugin inventory; mutations are separately reviewed with their real settings diff.";

    private const string Description =
        "List loaded plugins, describe one plugin, enable or disable one, install a plugin from a package name or "
        + "a .dll path, or remove one. Read-only actions require approval; mutations are reviewed with the real settings "
        + "diff at publication. Changes persist in settings.yaml and affect every session. Call action \"list\" "
        + "first to obtain exact package names.";

    internal static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var prompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        return new DisposableBundle(
            tools.Register(Definition(ctx)),
            ApprovalGate(ctx),
            prompt.Section(PromptSection.Literal(
                "tool:plugin_manager",
                "Use plugin_manager to list, describe, enable, disable, add, or remove loaded plugins. "
                + "Read-only calls require approval; mutations use the shared settings diff approval gate. "
                + "Denied settings mutations do not persist changes.")));
    }

    private static IDisposable ApprovalGate(Context ctx)
    {
        var gate = ctx.OnWaterfall<ToolPreExecuteNotification>(
            (notification, next) => string.Equals(notification.Run.Name, ToolName, StringComparison.Ordinal)
                && notification.Run.Arguments.TryGetProperty("action", out var action)
                && action.GetString() is "list" or "describe"
                ? ValueTask.FromResult<object?>(new PreToolDecision.Ask(ApprovalReason))
                : next(),
            new EventOptions { Global = true });
        return new ActionDisposable(() => gate());
    }

    private static ToolDefinition Definition(Context ctx) => new()
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
                  "description": "Management operation; mutations require the shared settings diff approval."
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
        Execute = (args, run) => Execute(ctx, args, run),
    };

    private static async Task<object?> Execute(Context ctx, JsonElement args, ToolRunContext run)
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
            "enable" => await manager.EnableAsync(RequireTarget(target), run),
            "disable" => await manager.DisableAsync(RequireTarget(target), run),
            "add" => await manager.AddAsync(RequireTarget(target), run),
            "remove" => await manager.RemoveAsync(RequireTarget(target), force, run),
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
