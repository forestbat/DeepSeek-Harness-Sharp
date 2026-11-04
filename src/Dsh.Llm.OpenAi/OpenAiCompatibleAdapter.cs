#pragma warning disable OPENAI001
#pragma warning disable SCME0001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAiAssistantChatMessage = OpenAI.Chat.AssistantChatMessage;
using OpenAiChatToolCall = OpenAI.Chat.ChatToolCall;

namespace Dsh.Llm.OpenAi;

public sealed class OpenAiCompatibleAdapter : LlmAdapter
{
    private static readonly IReadOnlyList<string> ReasoningKeys = ["reasoning", "reasoning_content", "thinking"];

    /** MEAI 在 TextReasoningContent 上保存 reasoning item id 的键名(与 Responses 客户端出站时读取的键一致)。 */
    private const string ReasoningItemIdKey = "reasoningItemId";

    private readonly string _providerId;
    private readonly IReadOnlyList<ProviderModelSpec> _models;
    private readonly ModelsDevReasoningSource _modelsDev;
    private readonly IModelReasoningSource _metadata;
    private readonly OpenAIClient _openAi;
    private readonly bool _useResponses;

    public OpenAiCompatibleAdapter(
        string providerId,
        string baseUrl,
        string? apiKey,
        IReadOnlyList<ProviderModelSpec>? models = null,
        HttpClient? httpClient = null,
        bool useResponses = false,
        IModelReasoningSource? metadataSource = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _providerId = providerId;
        ProviderInfo = new LlmProviderInfo(providerId, "OpenAI-Compatible");
        _models = models ?? [];
        _modelsDev = new ModelsDevReasoningSource(baseUrl: baseUrl);
        _useResponses = useResponses;

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl),
        };
        var transportClient = httpClient ?? new HttpClient(new FinishReasonNormalizingHandler(new HttpClientHandler()));
        options.Transport = new HttpClientPipelineTransport(transportClient);
        _openAi = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        if (metadataSource is not null)
        {
            _metadata = metadataSource;
        }
        else
        {
            var source = new ModelMetadataReasoningSource(baseUrl, apiKey, transportClient);
            _metadata = source;
            source.RefreshInBackground();
        }
    }

    public override LlmProviderInfo ProviderInfo { get; }

    public override ResolvedRetryPolicy ProviderRetryPolicy { get; } = ResolvedRetryPolicy.Resolve(null, "openai-compatible");

    public override IReadOnlyList<LlmModelInfo> ListModels()
        => _models
            .Select(model => new LlmModelInfo(_providerId, model.Id, model.Name ?? model.Id, null, _modelsDev.InputModalitiesFor(model.Id)))
            .ToList();

    /** 推理强度解析顺序: 端点 /models 的实时元数据 → 内置 models.dev 快照(baseUrl 主机名命中 provider); 上下文窗口与输入模态取自 models.dev 快照。 */
    public override LlmResolvedModelInfo ResolveModel(string model)
    {
        var reasoning = _metadata.ReasoningFor(model) ?? _modelsDev.ReasoningFor(model);
        return new LlmResolvedModelInfo(
            _providerId,
            model,
            model,
            null,
            _modelsDev.InputModalitiesFor(model),
            _modelsDev.ContextWindowFor(model),
            null,
            reasoning);
    }

    public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
        => StreamStandard(options, cancellationToken);

    private async IAsyncEnumerable<StreamChunk> StreamStandard(
        GenerateOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = _useResponses
            ? _openAi.GetResponsesClient().AsIChatClient(options.Model)
            : _openAi.GetChatClient(options.Model).AsIChatClient();

        IAsyncEnumerable<ChatResponseUpdate> updates;
        try
        {
            updates = client.GetStreamingResponseAsync(ToChatMessages(options), ToChatOptions(options), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new LlmException(new LlmFailure("OpenAI-compatible request aborted by caller", "ABORTED"));
        }
        catch (Exception error)
        {
            throw new LlmException(new LlmFailure(
                $"OpenAI-compatible API request to {_openAi.Endpoint} failed: {error.Message}",
                LlmFailureCodes.Transport), error);
        }

        await using var iterator = Translate(updates, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool moved;
            StreamChunk? current = null;
            LlmException? failure = null;
            try
            {
                moved = await iterator.MoveNextAsync();
                if (moved)
                    current = iterator.Current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                failure = new LlmException(new LlmFailure("OpenAI-compatible request aborted by caller", "ABORTED"));
                moved = false;
            }
            catch (Exception error)
            {
                // 保留适配器已经分类好的 LlmException(如缺终止符的 STREAM_CLOSED), 不要一律降级成 Transport。
                failure = error as LlmException ?? new LlmException(new LlmFailure(
                    $"OpenAI-compatible API stream from {_openAi.Endpoint} failed: {error.Message}",
                    LlmFailureCodes.Transport), error);
                moved = false;
            }
            if (failure is not null)
                throw failure;
            if (!moved)
                yield break;
            yield return current!;
        }
    }

    private static async IAsyncEnumerable<StreamChunk> Translate(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var nextIndex = 0;
        OpenBlock? textBlock = null;
        OpenBlock? reasoningBlock = null;
        var toolBlocks = new Dictionary<string, OpenBlock>();
        var blocks = new List<OpenBlock>();
        FinishReason? pendingFinish = null;
        TokenUsage? pendingUsage = null;

        OpenBlock Open(string kind)
        {
            var block = new OpenBlock { Index = nextIndex++, Kind = kind };
            blocks.Add(block);
            return block;
        }

        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            if (update.FinishReason is { } finishReason)
                pendingFinish = MapFinishReason(finishReason);

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text when text.Text.Length > 0:
                        {
                            textBlock ??= Open("text");
                            if (textBlock.Text.Length == 0)
                                yield return new StreamChunk.BlockStart(textBlock.Index, "text");
                            textBlock.Text += text.Text;
                            yield return new StreamChunk.TextDelta(textBlock.Index, text.Text);
                            break;
                        }
                    case TextReasoningContent reasoning when reasoning.Text.Length > 0
                                                            || reasoning.ProtectedData?.Length > 0
                                                            || ReasoningItemId(reasoning) is not null:
                        {
                            reasoningBlock ??= Open("reasoning");
                            CaptureReasoningCredentials(reasoningBlock, reasoning);
                            if (reasoning.Text.Length == 0)
                                break;
                            if (reasoningBlock.Text.Length == 0)
                                yield return new StreamChunk.BlockStart(reasoningBlock.Index, "reasoning");
                            reasoningBlock.Text += reasoning.Text;
                            yield return new StreamChunk.ReasoningDelta(reasoningBlock.Index, reasoning.Text);
                            break;
                        }
                    case FunctionCallContent call:
                        {
                            var key = call.CallId;
                            if (!toolBlocks.TryGetValue(key, out var block))
                            {
                                block = Open("tool-call");
                                toolBlocks[key] = block;
                                yield return new StreamChunk.BlockStart(block.Index, "tool-call");
                            }
                            if (!string.IsNullOrEmpty(call.CallId))
                                block.CallId = ToolCallId.Create(call.CallId);
                            if (!string.IsNullOrEmpty(call.Name))
                                block.Name = call.Name;
                            var arguments = JsonSerializer.Serialize(call.Arguments);
                            block.Arguments += arguments;
                            yield return new StreamChunk.ToolCallDelta(
                                block.Index,
                                block.CallId,
                                block.Name,
                                arguments);
                            break;
                        }
                    case UsageContent usage:
                        pendingUsage = ToTokenUsage(usage.Details);
                        break;
                }
            }

            if (update.AdditionalProperties is { } properties)
            {
                foreach (var key in ReasoningKeys)
                {
                    if (!properties.TryGetValue(key, out var value))
                        continue;
                    var reasoning = value switch
                    {
                        string text => text,
                        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                        _ => null,
                    };
                    if (string.IsNullOrEmpty(reasoning))
                        continue;
                    reasoningBlock ??= Open("reasoning");
                    if (reasoningBlock.Text.Length == 0)
                        yield return new StreamChunk.BlockStart(reasoningBlock.Index, "reasoning");
                    reasoningBlock.Text += reasoning;
                    yield return new StreamChunk.ReasoningDelta(reasoningBlock.Index, reasoning);
                }
            }
        }

        foreach (var block in blocks)
            yield return new StreamChunk.BlockEnd(block.Index, CloseBlock(block));
        if (pendingUsage is not null)
            yield return new StreamChunk.Usage(pendingUsage);
        // 只有供应商给出了明确的 finish_reason 才算正常结束; 流尾缺终止符视为中断, 交给上层重试。
        if (pendingFinish is null)
            throw new LlmException(new LlmFailure(
                "OpenAI-compatible stream ended without a finish reason", LlmFailureCodes.StreamClosed));
        yield return new StreamChunk.Finish(pendingFinish);
    }

    private IReadOnlyList<ChatMessage> ToChatMessages(GenerateOptions options)
    {
        var messages = new List<ChatMessage>();
        foreach (var message in ToolResultImages.Relay(options.Messages))
        {
            switch (message.Role)
            {
                case MessageRole.System:
                    messages.Add(new ChatMessage(ChatRole.System, FlattenText(message.Content)));
                    break;
                case MessageRole.Assistant:
                    messages.Add(ToAssistantMessage(message.Content));
                    break;
                default:
                    {
                        var toolResults = message.Content.OfType<ToolResultBlock>().ToList();
                        if (toolResults.Count > 0)
                        {
                            foreach (var result in toolResults)
                            {
                                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                                result.ToolCallId.Value,
                                FlattenText(result.Content))]));
                            }
                            break;
                        }
                        messages.Add(new ChatMessage(ChatRole.User, ToUserContents(options, message.Content)));
                        break;
                    }
            }
        }
        return messages;
    }

    private static IList<AIContent> ToUserContents(GenerateOptions options, IReadOnlyList<ContentBlock> blocks)
    {
        var contents = new List<AIContent>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextBlock text when text.Text.Length > 0:
                    contents.Add(new TextContent(text.Text));
                    break;
                case ImageBlock image:
                    contents.Add(new DataContent(
                        AttachmentResolution.Resolve(options, image.Attachment),
                        image.Attachment.MediaType));
                    break;
            }
        }
        if (contents.Count == 0)
            contents.Add(new TextContent(FlattenText(blocks)));
        return contents;
    }

    private static IList<AIContent> ToAssistantContents(IReadOnlyList<ContentBlock> blocks)
    {
        var contents = new List<AIContent>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextBlock text:
                    contents.Add(new TextContent(text.Text));
                    break;
                case ReasoningBlock reasoning:
                    contents.Add(ToReasoningContent(reasoning));
                    break;
                case ToolCallBlock call:
                    contents.Add(new FunctionCallContent(call.Id.Value, call.Name, ParseArguments(call.Arguments)));
                    break;
            }
        }
        return contents;
    }

    private ChatMessage ToAssistantMessage(IReadOnlyList<ContentBlock> blocks)
    {
        var content = ToAssistantContents(blocks);
        if (_useResponses)
            return new ChatMessage(ChatRole.Assistant, content);
        // MEAI 的 Chat Completions 客户端会原样转发 RawRepresentation, 这是把 DeepSeek 的
        // reasoning_content 放进历史 assistant 消息的唯一入口(MEAI 自身不序列化 TextReasoningContent)。
        var text = string.Concat(blocks.OfType<TextBlock>().Select(block => block.Text));
        var calls = blocks.OfType<ToolCallBlock>()
            .Select(call => OpenAiChatToolCall.CreateFunctionToolCall(
                call.Id.Value,
                call.Name,
                BinaryData.FromString(call.Arguments)))
            .ToList();
        // 工具调用轮次发 content:null; 空字符串会让模型把这一轮当成一次空的文本回复。
        OpenAiAssistantChatMessage raw = text.Length == 0 && calls.Count > 0
            ? new OpenAiAssistantChatMessage(calls)
            : new OpenAiAssistantChatMessage(text);
        if (text.Length > 0)
        {
            foreach (var call in calls)
                raw.ToolCalls.Add(call);
        }
        var reasoning = string.Concat(blocks.OfType<ReasoningBlock>().Select(block => block.Text));
        if (reasoning.Length > 0)
            raw.Patch.Set("$.reasoning_content"u8, reasoning);
        return new ChatMessage(ChatRole.Assistant, content) { RawRepresentation = raw };
    }

    /** 还原推理凭据: Responses 客户端会据此重建带 id/加密内容的 reasoning item, 缺凭据项会被服务端拒绝。 */
    private static TextReasoningContent ToReasoningContent(ReasoningBlock reasoning)
    {
        var content = new TextReasoningContent(reasoning.Text) { ProtectedData = reasoning.ProtectedData };
        if (reasoning.ItemId is { } itemId)
            (content.AdditionalProperties ??= [])[ReasoningItemIdKey] = itemId;
        return content;
    }

    private static ChatOptions ToChatOptions(GenerateOptions options)
    {
        var chatOptions = new ChatOptions
        {
            ModelId = options.Model,
            Temperature = options.Temperature is { } temperature ? (float)temperature : null,
            MaxOutputTokens = options.MaxTokens,
            StopSequences = options.Stop is { Count: > 0 } stop ? [.. stop] : null,
            Tools = options.Tools is { Count: > 0 } tools
                ? tools.Select(tool => AIFunctionFactory.CreateDeclaration(
                    tool.Name,
                    tool.Description,
                    JsonSerializer.SerializeToElement(tool.Parameters))).ToList<AITool>()
                : null,
            Instructions = options.System,
        };
        if (options.ReasoningEffort is { } effort)
            chatOptions.Reasoning = new ReasoningOptions { Effort = MapReasoningEffort(effort) };
        return chatOptions;
    }

    private static IDictionary<string, object?> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static string FlattenText(IReadOnlyList<ContentBlock> blocks)
        => string.Concat(blocks.OfType<TextBlock>().Select(block => block.Text));

    private static ReasoningEffort MapReasoningEffort(ReasoningEffortId effort) => effort.Value switch
    {
        "off" => ReasoningEffort.None,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        "max" => ReasoningEffort.ExtraHigh,
        _ => throw new LlmException(new LlmFailure(
            $"OpenAI-compatible provider does not support reasoning effort \"{effort}\"",
            "UNSUPPORTED_REASONING_EFFORT")),
    };

    private static FinishReason MapFinishReason(ChatFinishReason reason) => reason.Value switch
    {
        "stop" => new FinishReason.Stop(),
        "tool_calls" => new FinishReason.ToolCalls(),
        "length" => new FinishReason.MaxTokens(),
        "content_filter" => new FinishReason.Error(new LlmFailure("content filtered", "CONTENT_FILTER")),
        _ => new FinishReason.Unknown(reason.Value, JsonSerializer.SerializeToElement(reason.Value)),
    };

    private static TokenUsage ToTokenUsage(UsageDetails details)
        => new(
            details.InputTokenCount ?? 0,
            details.OutputTokenCount ?? 0,
            details.TotalTokenCount,
            details.CachedInputTokenCount,
            null,
            details.ReasoningTokenCount);

    /** 读取 MEAI 挂在 reasoning 内容上的 item id; 兼容进程内 string 与会话回放后的 JsonElement。 */
    private static string? ReasoningItemId(TextReasoningContent content)
        => content.AdditionalProperties is { } properties
            && properties.TryGetValue(ReasoningItemIdKey, out var value)
                ? value switch
                {
                    string text when text.Length > 0 => text,
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                    _ => null,
                }
                : null;

    /** 记住服务端要求原样回传的推理凭据; 后续回合只发文本、不发凭据时, 保留先到的凭据。 */
    private static void CaptureReasoningCredentials(OpenBlock block, TextReasoningContent content)
    {
        if (content.ProtectedData is { Length: > 0 } protectedData)
            block.ProtectedData = protectedData;
        if (ReasoningItemId(content) is { } itemId)
            block.ItemId = itemId;
    }

    private static ContentBlock CloseBlock(OpenBlock block) => block.Kind switch
    {
        "text" => new TextBlock(block.Text),
        "reasoning" => new ReasoningBlock(block.Text, block.ProtectedData, block.ItemId),
        "tool-call" => new ToolCallBlock(
            block.CallId,
            block.Name ?? "",
            block.Arguments),
        _ => throw new InvalidOperationException($"cannot close block of kind \"{block.Kind}\""),
    };

    private sealed class OpenBlock
    {
        public required int Index;
        public required string Kind;
        public string Text = "";
        public ToolCallId CallId;
        public string? Name;
        public string Arguments = "";
        public string? ProtectedData;
        public string? ItemId;
    }
}
