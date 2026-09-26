using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Ptc;

/** run_code 程序内的子派发: 走注册表完整分段调度器, 结果只回程序 Promise, 审计另落会话事件。 */
internal sealed class PtcSubDispatch(ToolRuntime tools, ToolRunContext runContext)
{
    private const string Source = "ptc-mode";

    public async Task<PtcToolReply> DispatchAsync(PtcToolCall call, CancellationToken signal)
    {
        var callId = ToolCallId.Create($"{runContext.CallId.Value}:{call.Id}");
        AppendStart(call, callId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(signal, runContext.Signal);
        ToolExecutionResult result;
        try
        {
            result = await tools.Execute(new ToolExecutionInput
            {
                CallId = callId,
                RootCallId = runContext.RootCallIdValue,
                Name = call.ToolName,
                Arguments = JsonDocument.Parse(call.Arguments.ToJsonString()).RootElement,
                Agent = runContext.Agent,
                Parent = runContext,
                Signal = linked.Token,
            });
        }
        catch (Exception error)
        {
            AppendEnd(call, callId, true, null, error.Message);
            return new PtcToolReply(false, null, error.Message);
        }
        AppendEnd(call, callId, result.IsError, result, result is ToolExecutionResult.Failure failure ? failure.Error.Message : null);
        if (result is ToolExecutionResult.Success { ConcludesTurn: true })
            runContext.ConcludeTurn();
        DeferImages(result);
        return result switch
        {
            ToolExecutionResult.Success success => new PtcToolReply(true, JsonNode.Parse(success.Value.GetRawText()), null),
            ToolExecutionResult.Failure failed => new PtcToolReply(false, null, failed.Error.Message),
            _ => new PtcToolReply(false, null, "unknown tool result"),
        };
    }

    private void AppendStart(PtcToolCall call, ToolCallId callId)
        => runContext.Agent?.Session.Append(new PtcDispatchStartPayload(
            runContext.CallId.Value,
            call.ToolName,
            callId.Value,
            JsonDocument.Parse(call.Arguments.ToJsonString()).RootElement.Clone()));

    private void AppendEnd(PtcToolCall call, ToolCallId callId, bool isError, ToolExecutionResult? result, string? error)
    {
        var session = runContext.Agent?.Session;
        if (session is null)
            return;
        JsonElement? value = result is ToolExecutionResult.Success success ? success.Value.Clone() : null;
        session.Append(new PtcDispatchPayload(runContext.CallId.Value, call.ToolName, callId.Value, isError, value, error));
    }

    private void DeferImages(ToolExecutionResult result)
    {
        if (runContext.Agent is null)
            return;
        var images = result.Content.OfType<ImageBlock>().Cast<ContentBlock>().ToList();
        if (images.Count == 0)
            return;
        runContext.DeferContext(MessageFactory.CreateUserMessage(images, new PluginMessageSource(Source)));
    }
}
