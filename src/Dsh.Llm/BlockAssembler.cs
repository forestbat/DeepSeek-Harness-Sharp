namespace Dsh.Llm;

public sealed class BlockAssembler
{
    private sealed class PartialBlock
    {
        public required string BlockType;
        public string Text = "";
        public ToolCallId? ToolCallId;
        public string? ToolCallName;
        public string ToolCallArguments = "";
        public ContentBlock? Block;
    }

    private readonly Dictionary<int, PartialBlock> _partials = [];
    private readonly List<int> _order = [];
    private TokenUsage? _usage;
    private FinishReason? _finish;
    private ReplayEnvelope? _replayState;

    public void Push(StreamChunk chunk)
    {
        switch (chunk)
        {
            case StreamChunk.BlockStart blockStart:
                if (!_partials.ContainsKey(blockStart.Index))
                {
                    _order.Add(blockStart.Index);
                    _partials[blockStart.Index] = new PartialBlock { BlockType = blockStart.BlockType };
                }
                return;
            case StreamChunk.TextDelta textDelta:
            {
                var partial = Ensure(textDelta.Index, "text");
                if (partial.Block is not null)
                    return;
                partial.Text += textDelta.Text;
                return;
            }
            case StreamChunk.ReasoningDelta reasoningDelta:
            {
                var partial = Ensure(reasoningDelta.Index, "reasoning");
                if (partial.Block is not null)
                    return;
                partial.Text += reasoningDelta.Text;
                return;
            }
            case StreamChunk.ToolCallDelta toolCallDelta:
            {
                var partial = Ensure(toolCallDelta.Index, "tool-call");
                if (partial.Block is not null)
                    return;
                partial.ToolCallId = toolCallDelta.Id;
                if (toolCallDelta.Name is not null)
                    partial.ToolCallName = toolCallDelta.Name;
                partial.ToolCallArguments += toolCallDelta.ArgumentsDelta;
                return;
            }
            case StreamChunk.BlockEnd blockEnd:
            {
                var partial = Ensure(blockEnd.Index, blockEnd.Block.Type);
                if (partial.Block is not null)
                    return;
                partial.Block = blockEnd.Block;
                return;
            }
            case StreamChunk.Usage usage:
                _usage = usage.Value;
                return;
            case StreamChunk.Finish finish:
                _finish = finish.Reason;
                _replayState = finish.ReplayState;
                return;
            default:
                throw new ArgumentException($"BlockAssembler.Push: unknown chunk {chunk.GetType().Name}");
        }
    }

    private PartialBlock Ensure(int index, string blockType)
    {
        if (!_partials.TryGetValue(index, out var partial))
        {
            partial = new PartialBlock { BlockType = blockType };
            _partials[index] = partial;
            _order.Add(index);
        }
        return partial;
    }

    private ContentBlock Assemble(PartialBlock partial, int index)
    {
        if (partial.Block is not null)
            return partial.Block;
        return partial.BlockType switch
        {
            "text" => new TextBlock(partial.Text),
            "reasoning" => new ReasoningBlock(partial.Text),
            "tool-call" => new ToolCallBlock(
                partial.ToolCallId ?? ToolCallId.Create($"call-{index}"),
                partial.ToolCallName ?? "",
                partial.ToolCallArguments),
            _ => throw new InvalidOperationException($"cannot assemble incomplete block of type \"{partial.BlockType}\""),
        };
    }

    private PartialBlock MustGet(int index)
        => _partials.TryGetValue(index, out var partial)
            ? partial
            : throw new InvalidOperationException($"BlockAssembler invariant violated: no partial for index {index}");

    private (IReadOnlyList<ContentBlock> Blocks, ReplayEnvelope? Replay) Assembled()
    {
        var all = _order.Select(index => Assemble(MustGet(index), index)).ToList();
        bool[]? kept = Finish.Kind == "max-tokens"
            ? all.Select(block => block is not ToolCallBlock).ToArray()
            : null;
        var blocks = kept is null ? all : all.Where((_, position) => kept[position]).ToList();
        var envelope = _replayState;
        if (envelope?.Blocks is null)
            return (blocks, envelope);
        if (envelope.Blocks.Count != all.Count)
            return (blocks, null);
        if (kept is null || blocks.Count == all.Count)
            return (blocks, envelope);
        return (blocks, new ReplayEnvelope(
            envelope.Response,
            envelope.Blocks.Where((_, position) => kept[position]).ToList()));
    }

    public IReadOnlyList<ContentBlock> Blocks() => Assembled().Blocks;

    public IReadOnlyList<ContentBlock> InterruptedBlocks()
        => _order
            .Select(index =>
            {
                var partial = MustGet(index);
                var type = partial.Block?.Type ?? partial.BlockType;
                if (type is not ("text" or "reasoning"))
                    return null;
                return Assemble(partial, index);
            })
            .OfType<ContentBlock>()
            .Where(block => block is TextBlock { Text: not "" } text && !string.IsNullOrWhiteSpace(text.Text)
                            || block is ReasoningBlock { Text: not "" } reasoning && !string.IsNullOrWhiteSpace(reasoning.Text))
            .ToList();

    public TokenUsage? Usage => _usage;

    public FinishReason Finish => _finish ?? new FinishReason.Stop();

    public ReplayEnvelope? ReplayState => Assembled().Replay;

    public Message Message(MessageSource? source = null)
        => new()
        {
            Id = MessageFactory.NewId(),
            Role = MessageRole.Assistant,
            Content = Blocks(),
            Source = source ?? new PluginMessageSource("dsh-llm/assembler"),
        };
}
