using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Events;

[assembly: DshPlugin(Dsh.Toon.ToonPluginConfig.PackageName)]

namespace Dsh.Toon;

/**
 * TOON 插件(§4 G14): 输出侧注册一个真实工具 `toon`, 由服务端签发工具调用 id;
 * 模型用 `toon` 调用目标工具, 其 `arguments` 是 TOON 文本(省去逐个工具下发 JSON Schema 的 token)。
 * 输入侧把白名单工具的结果值转成 TOON 进上下文。
 */
public sealed class Plugin : IDshPlugin
{
    public const string ToolName = "toon";

    public string[] Inject => [LlmRuntime.ServiceName, SystemPrompt.ServiceName, ToolRuntime.ServiceName];

    public Type ConfigType => typeof(ToonPluginConfig);

    public IDisposable Apply(Context ctx, object? config)
    {
        var resolved = config as ToonPluginConfig ?? ToonPluginConfig.Resolve(config);
        var tools = ctx.Get<ToolRuntime>()!;
        var prompt = ctx.Get<SystemPrompt>()!;
        var global = new EventOptions { Global = true };
        _ = new ToonCodecService(ctx);
        var disposables = new List<IDisposable>();
        if (resolved.Output)
        {
            disposables.Add(tools.Register(Dispatcher(tools)));
            disposables.Add(prompt.Section(new PromptSection(
                "tool:toon",
                context => InstructionText(tools, context.Scope))));
            disposables.Add(new FuncDispose(ctx.OnWaterfall<SystemPromptAssembleNotification>(
                async (notification, next) => StripKnownTools(tools, notification, await next()), global)));
        }
        if (resolved.Input is { } whitelist)
        {
            disposables.Add(new FuncDispose(ctx.OnWaterfall<ToolPostExecuteNotification>(
                async (notification, next) => await ToonifyResult(whitelist, notification, next), global)));
        }
        return new DisposeBundle(disposables);
    }

    /** 真实注册的派发工具: name + TOON 文本 arguments; 内部派发到目标工具, 服务端只看到 `toon` 的调用 id。 */
    private static ToolDefinition Dispatcher(ToolRuntime tools)
        => new()
        {
            Name = ToolName,
            Description = "Call any declared tool. `name` is the target tool; `arguments` is the target's arguments written in TOON syntax.",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "target tool name" },
                    ["arguments"] = new JsonObject { ["type"] = "string", ["description"] = "target arguments in TOON syntax" },
                },
                ["required"] = new JsonArray("name", "arguments"),
            },
            Output = new ToolOutputDefinition(
                new JsonObject { ["type"] = "string" },
                (_, value) => [new TextBlock(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText())]),
            Execute = async (arguments, run) => await Dispatch(tools, arguments, run),
        };

    private static async Task<object?> Dispatch(ToolRuntime tools, JsonElement arguments, ToolRunContext run)
    {
        var target = arguments.TryGetProperty("name", out var name) ? name.GetString() : null;
        var raw = arguments.TryGetProperty("arguments", out var rawArguments) ? rawArguments : default;
        if (string.IsNullOrWhiteSpace(target))
            return """{"error":"missing target tool name"}""";
        if (string.Equals(target, ToolName, StringComparison.Ordinal))
            return """{"error":"toon cannot call itself"}""";
        JsonNode? decoded;
        try
        {
            decoded = raw.ValueKind switch
            {
                // 模型偶尔把目标参数直接写成 JSON 对象/数组(而非 TOON 文本), 两种形态都接受。
                JsonValueKind.Object or JsonValueKind.Array => JsonNode.Parse(raw.GetRawText()),
                JsonValueKind.String => string.IsNullOrWhiteSpace(raw.GetString())
                    ? new JsonObject()
                    : ToonCodec.Decode(raw.GetString()!),
                _ => new JsonObject(),
            };
        }
        catch (FormatException error)
        {
            return JsonSerializer.Serialize(new JsonObject { ["error"] = $"invalid TOON arguments: {error.Message}" });
        }
        var targetArguments = JsonSerializer.SerializeToElement(decoded);
        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"toon-{Guid.NewGuid():N}"),
            RootCallId = run.RootCallId,
            Name = target!,
            Arguments = targetArguments,
            RawArguments = targetArguments.GetRawText(),
            Agent = run.Agent,
            Parent = run.Parent,
            Signal = run.Signal,
        });
        return result switch
        {
            ToolExecutionResult.Success success => string.Join("\n", success.Content.OfType<TextBlock>().Select(block => block.Text)) is { Length: > 0 } text
                ? text
                : success.Value.GetRawText(),
            ToolExecutionResult.Failure failure => JsonSerializer.Serialize(new JsonObject { ["error"] = failure.Error.Message }),
            _ => """{"error":"unknown tool result"}""",
        };
    }

    /** 摘掉原生工具 schema(包括 ToolRuntime 已知工具), 只保留派发工具 `toon`; ad-hoc schema 不在注册表里, 原样保留。 */
    private static object? StripKnownTools(ToolRuntime tools, SystemPromptAssembleNotification notification, object? result)
    {
        if (result is not PromptAssembly { Tools.Count: > 0 } assembly)
            return result;
        var registered = tools.Schemas(notification.Context.Scope).Select(schema => schema.Name).ToHashSet(StringComparer.Ordinal);
        var kept = assembly.Tools.Where(schema => !registered.Contains(schema.Name) || schema.Name == ToolName).ToList();
        return kept.Count == assembly.Tools.Count ? assembly : assembly with { Tools = kept };
    }

    private static async ValueTask<object?> ToonifyResult(
        IReadOnlySet<string> whitelist, ToolPostExecuteNotification notification, Func<ValueTask<object?>> next)
    {
        var decision = await next();
        if (!whitelist.Contains(notification.Run.Name)
            || notification.Result is not ToolExecutionResult.Success success
            || decision is not PostToolDecision.Accept accept)
        {
            return decision;
        }
        var toon = ToonCodec.Encode(JsonNode.Parse(success.Value.GetRawText()));
        return accept with { Content = [new TextBlock(toon)] };
    }

    private static string InstructionText(ToolRuntime tools, ScopeKey? scope)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("""
            Tool calls in this deployment go through the `toon` tool (a real tool call, so ids are assigned by the API).
            Call `toon` with two arguments: `name` is the target tool, `arguments` is the target's arguments written as TOON text.
            Native tool-call arguments example:

            {"name":"bash","arguments":"command: ls -la"}

            TOON syntax: objects are `key: value` lines with 2-space indentation for nesting; arrays of scalars are `key[N]: a,b,c`; arrays of uniform objects are tabular (`key[N]{f1,f2}:` then one row per record); quote strings that contain commas, colons or newlines.

            Available tools (argument names with `?` are optional):

            """);
        foreach (var schema in tools.Schemas(scope))
        {
            if (schema.Name == ToolName)
                continue;
            builder.Append("- ").Append(schema.Name).Append(": ").Append(ShapeOf(schema.Parameters)).Append('\n');
        }
        return builder.ToString();
    }

    /** 把 JSON Schema 的参数表压成一行形状说明: {command: string, timeoutMs?: number}。 */
    private static string ShapeOf(JsonObject parameters)
    {
        if (parameters["properties"] is not JsonObject properties || properties.Count == 0)
            return "(no arguments)";
        var required = (parameters["required"] as JsonArray)?
            .Select(node => node?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var parts = properties.Select(member =>
        {
            var type = member.Value is JsonObject property ? property["type"]?.GetValue<string>() ?? "any" : "any";
            var optional = required.Contains(member.Key) ? "" : "?";
            return $"{member.Key}{optional}: {type}";
        });
        return $"{{{string.Join(", ", parts)}}}";
    }

    private sealed class FuncDispose(Func<bool> unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }

    /** 把 ToonCodec 以 IToonCodec 契约服务暴露给消费方(如 TUI 的窗格读取工具), 消费方不引用本插件。 */
    private sealed class ToonCodecService(Context ctx) : Service(ctx, IToonCodec.ServiceName), IToonCodec
    {
        public string Encode(JsonNode? value) => ToonCodec.Encode(value);
    }

    private sealed class DisposeBundle(List<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
