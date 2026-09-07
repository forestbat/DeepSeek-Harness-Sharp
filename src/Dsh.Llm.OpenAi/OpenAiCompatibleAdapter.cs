#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Dsh.Llm.OpenAi;

public sealed class OpenAiCompatibleAdapter : LlmAdapter
{
    private static readonly ReasoningEffortTable ReasoningTable = ReasoningEffortTable.Load();
    private static readonly IReadOnlyList<string> ReasoningKeys = ["reasoning", "reasoning_content", "thinking"];

    private readonly string _providerId;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly IReadOnlyList<string> _modelIds;
    private readonly OpenAIClient _openAi;
    private readonly bool _useResponses;

    public OpenAiCompatibleAdapter(
        string providerId,
        string baseUrl,
        string? apiKey,
        IReadOnlyList<string>? modelIds = null,
        HttpClient? httpClient = null,
        bool useResponses = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _providerId = providerId;
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        ProviderInfo = new LlmProviderInfo(providerId, "OpenAI-Compatible");
        _modelIds = modelIds ?? [];
        _useResponses = useResponses;

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl),
        };
        var transportClient = httpClient ?? new HttpClient(new FinishReasonNormalizingHandler(new HttpClientHandler()));
        options.Transport = new HttpClientPipelineTransport(transportClient);
        _openAi = new OpenAIClient(new ApiKeyCredential(apiKey ?? string.Empty), options);
    }

    public override LlmProviderInfo ProviderInfo { get; }

    public override ResolvedRetryPolicy ProviderRetryPolicy { get; } = ResolvedRetryPolicy.Resolve(null, "openai-compatible");

    public override IReadOnlyList<LlmModelInfo> ListModels()
        => _modelIds
            .Select(model => new LlmModelInfo(_providerId, model, model, null, ["text"]))
            .ToList();

    public override LlmResolvedModelInfo ResolveModel(string model)
    {
        var reasoning = ReasoningTable.Resolve(_providerId, model);
        return new LlmResolvedModelInfo(
            _providerId,
            model,
            model,
            null,
            ["text"],
            null,
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
                failure = new LlmException(new LlmFailure(
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
                    case TextReasoningContent reasoning when reasoning.Text.Length > 0:
                    {
                        reasoningBlock ??= Open("reasoning");
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
        yield return new StreamChunk.Finish(pendingFinish ?? new FinishReason.Stop());
    }

    private IReadOnlyList<ChatMessage> ToChatMessages(GenerateOptions options)
    {
        if (options.Messages.Any(message => message.Content.Any(block => block is ImageBlock)))
        {
            throw new LlmException(new LlmFailure(
                "OpenAI-compatible image conversion requires the durable attachment service.",
                "UNSUPPORTED_CONTENT"));
        }

        var messages = new List<ChatMessage>();
        foreach (var message in options.Messages)
        {
            switch (message.Role)
            {
                case MessageRole.System:
                    messages.Add(new ChatMessage(ChatRole.System, FlattenText(message.Content)));
                    break;
                case MessageRole.Assistant:
                    messages.Add(new ChatMessage(ChatRole.Assistant, ToAssistantContents(message.Content)));
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
                    messages.Add(new ChatMessage(ChatRole.User, [new TextContent(FlattenText(message.Content))]));
                    break;
                }
            }
        }
        return messages;
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
                    contents.Add(new TextReasoningContent(reasoning.Text));
                    break;
                case ToolCallBlock call:
                    contents.Add(new FunctionCallContent(call.Id.Value, call.Name, ParseArguments(call.Arguments)));
                    break;
            }
        }
        return contents;
    }

    private static ChatOptions ToChatOptions(GenerateOptions options)
    {
        var chatOptions = new ChatOptions
        {
            ModelId = options.Model,
            Temperature = options.Temperature is { } temperature ? (float)temperature : null,
            MaxOutputTokens = options.MaxTokens,
            StopSequences = options.Stop is { Count: > 0 } stop ? [..stop] : null,
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

    private static ContentBlock CloseBlock(OpenBlock block) => block.Kind switch
    {
        "text" => new TextBlock(block.Text),
        "reasoning" => new ReasoningBlock(block.Text),
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
    }
}