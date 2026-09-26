using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;

namespace Dsh.Workflow;

/** 工作流模型程序: 复用 PTC script-host, 宿主侧只提供 agent/phase/log 三个绑定,
 *  parallel/pipeline 是 guest 侧本地助手, 语义与原 workflow 助手保持一致。 */
internal static class WorkflowProgram
{
    public static readonly IReadOnlyList<string> Bindings = ["agent", "log", "phase"];

    public static string Build(string body, object? args, int maxItemsPerCall)
    {
        var argsJson = WorkflowJson.ToNode(args)?.ToJsonString() ?? "null";
        return Template
            .Replace("__MAX_ITEMS__", maxItemsPerCall.ToString(CultureInfo.InvariantCulture))
            .Replace("__ARGS_LITERAL__", SymbolDisplay.FormatLiteral(argsJson, quote: true))
            .Replace("__BODY__", body);
    }

    private const string Template = """
        var __maxItems = __MAX_ITEMS__;
        JsonNode? args = JsonNode.Parse(__ARGS_LITERAL__);

        async Task<JsonNode?> agent(string prompt, JsonObject? opts = null)
        {
            if (string.IsNullOrEmpty(prompt)) throw new ToolCallError("agent", "agent() requires a non-empty prompt string");
            var call = new JsonObject { ["prompt"] = prompt };
            if (opts is not null) call["opts"] = opts.DeepClone();
            var result = await tools.agent(call);
            return result?.DeepClone();
        }

        async Task phase(string title)
        {
            if (string.IsNullOrEmpty(title)) throw new ToolCallError("phase", "phase() requires a non-empty title string");
            await tools.phase(new JsonObject { ["title"] = title });
        }

        async Task log(string message)
        {
            if (message is null) throw new ToolCallError("log", "log() requires a message string");
            await tools.log(new JsonObject { ["message"] = message });
        }

        async Task<JsonNode?> parallel(Func<Task<JsonNode?>>[] thunks)
        {
            if (thunks is null) throw new ToolCallError("parallel", "parallel() requires an array of zero-argument functions");
            if (thunks.Length > __maxItems) throw new ToolCallError("parallel", "parallel() received " + thunks.Length + " items over the per-call cap (" + __maxItems + "); split the work or raise maxItemsPerCall in the engine config");
            var tasks = new Task<JsonNode?>[thunks.Length];
            for (var index = 0; index < thunks.Length; index++)
                tasks[index] = __Thunk(thunks[index]);
            var results = new JsonArray();
            foreach (var value in await Task.WhenAll(tasks))
                results.Add(value?.DeepClone());
            return results;
        }

        async Task<JsonNode?> __Thunk(Func<Task<JsonNode?>> thunk)
        {
            try { return await thunk(); }
            catch (ToolCallError) { throw; }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        async Task<JsonNode?> pipeline(JsonArray items, params Func<JsonNode?, JsonNode?, int, Task<JsonNode?>>[] stages)
        {
            if (items is null) throw new ToolCallError("pipeline", "pipeline() requires an items array");
            if (items.Count > __maxItems) throw new ToolCallError("pipeline", "pipeline() received " + items.Count + " items over the per-call cap (" + __maxItems + "); split the work or raise maxItemsPerCall in the engine config");
            if (stages.Length == 0) throw new ToolCallError("pipeline", "pipeline() requires at least one stage function");
            var tasks = new Task<JsonNode?>[items.Count];
            for (var index = 0; index < items.Count; index++)
                tasks[index] = __PipelineItem(items[index], index, stages);
            var results = new JsonArray();
            foreach (var value in await Task.WhenAll(tasks))
                results.Add(value);
            return results;
        }

        async Task<JsonNode?> __PipelineItem(JsonNode? item, int index, Func<JsonNode?, JsonNode?, int, Task<JsonNode?>>[] stages)
        {
            var value = item;
            try
            {
                foreach (var stage in stages)
                    value = await stage(value, item, index);
                return value?.DeepClone();
            }
            catch (ToolCallError) { throw; }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        __BODY__
        """;
}
