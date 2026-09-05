using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

public sealed record SummarizationInput(
    string? System,
    IReadOnlyList<ToolSchema>? Tools,
    IReadOnlyList<Message> Messages);

public sealed record SummaryResult(
    IReadOnlyList<ContentBlock> Summary,
    IReadOnlyList<ContentBlock> RawOutput,
    string Provider,
    string Model,
    int MaxTokens,
    TokenUsage? Usage);

public static class Summarizer
{
    public const string SummaryOpenTag = "<compacted-summary>";
    public const string SummaryCloseTag = "</compacted-summary>";

    public const string CompactionInstruction =
        """
        You are now acting as a compaction engine for this AI coding assistant. Condense the conversation ABOVE into a structured checkpoint that lets another model resume the work with no loss of essential context.

        Output EXACTLY the Markdown structure below: keep every section, in order. Use terse bullets, not prose paragraphs. Write "(none)" for an empty section — never drop a section.

        ## Primary Request and Intent
        - [the user's original and evolving goals; quote verbatim where the exact wording matters]

        ## Key Technical Concepts
        - [technologies, frameworks, patterns, and conventions in play]

        ## Files and Code
        - [exact path: why it matters, key changes or snippets]

        ## Errors and Fixes
        - [error: how it was resolved, plus any related user feedback]

        ## Pending Jobs
        - [explicitly requested work not yet completed]

        ## Current Work
        - [precisely what was in progress at this checkpoint]

        ## Next Step
        - [the single next action, directly in line with the most recent request, or "(none)"]

        ## Critical Context
        - [decisions and their rationale, constraints, user preferences, open questions, data needed to continue]

        Rules:
        - Write concise English engineering prose. Preserve exact file paths, commands, error strings, identifiers, numeric values, function signatures, and syntax fragments.
        - Capture user feedback and explicit instructions faithfully, especially corrections.
        - Do NOT mention this summarization request or that the context was compacted.
        - Output only the checkpoint text: do not call any tool or take any other action.
        - If the conversation already contains a <compacted-summary> block, it is a PRIOR checkpoint. Do not copy it forward verbatim: preserve still-true facts, drop stale ones, and merge newer information into a single consolidated summary under the same structure.
        """;

    public const string CheckpointPreamble =
        "This is an automatically generated checkpoint condensing an earlier span of the conversation to free up context. Treat the captured context as established background and build on it without restating it. Continue the task directly from the messages that follow, without acknowledging this checkpoint.";

    public static async Task<SummaryResult> SummarizeWithLlm(
        LlmRuntime llm,
        string summarizationProvider,
        string summarizationModel,
        int maxTokens,
        SummarizationInput input,
        IAgent agent,
        CancellationToken signal = default)
    {
        var latest = agent.Session.RequestHeader()?.Config;
        var configured = summarizationProvider.Length == 0
            ? ((string Provider, string Model)?)null
            : (summarizationProvider, summarizationModel);
        var agentTarget = !string.IsNullOrEmpty(agent.Options.Provider) && !string.IsNullOrEmpty(agent.Options.Model)
            ? (agent.Options.Provider, agent.Options.Model)
            : ((string Provider, string Model)?)null;
        var target = configured
            ?? (latest is not null ? (latest.Provider, latest.Model) : ((string Provider, string Model)?)null)
            ?? agentTarget
            ?? throw new InvalidOperationException(
                "no provider/model available for summarization: set both BasicCompactionConfig summarization fields, route one request, or set both AgentOptions fields");

        var assembler = new BlockAssembler();
        var messages = new List<Message>(input.Messages)
        {
            MessageFactory.CreateUserMessage(
                [new TextBlock(CompactionInstruction)],
                new PluginMessageSource("dsh-compaction-basic")),
        };
        var options = new GenerateOptions
        {
            Provider = target.Provider,
            Model = target.Model,
            Messages = messages,
            System = input.System,
            Tools = input.Tools,
            MaxTokens = maxTokens,
            SessionId = agent.Session.Id,
            Purpose = GeneratePurpose.Compaction,
            Cancellation = signal,
        };
        await foreach (var chunk in llm.Stream(options).WithCancellation(signal))
            assembler.Push(chunk);
        if (FinishError(assembler.Finish) is { } finishError)
            throw finishError;

        var rawOutput = assembler.Blocks();
        var summary = SummaryText(rawOutput);
        if (!summary.Any(block => block.Text.Trim().Length > 0))
            throw new InvalidOperationException("summarization produced no text summary content");
        return new SummaryResult(summary, rawOutput, target.Provider, target.Model, maxTokens, assembler.Usage);
    }

    public static IReadOnlyList<ContentBlock> FrameSummary(IReadOnlyList<ContentBlock> summary)
        =>
        [
            new TextBlock($"{CheckpointPreamble}\n\n{SummaryOpenTag}"),
            .. summary,
            new TextBlock(SummaryCloseTag),
        ];

    private static HarnessException? FinishError(FinishReason finish) => finish switch
    {
        FinishReason.Error error => new HarnessException(error.Failure.Message, error.Failure.Code),
        FinishReason.Aborted aborted => new HarnessException(aborted.Failure.Message, aborted.Failure.Code),
        FinishReason.MaxTokens => new HarnessException("summarization truncated at the token cap (incomplete checkpoint)", "MAX_TOKENS"),
        _ => null,
    };

    private static IReadOnlyList<TextBlock> SummaryText(IReadOnlyList<ContentBlock> blocks)
    {
        if (ContentHasImage(blocks))
            throw new LlmException(new LlmFailure("compaction summary cannot contain image output", "UNSUPPORTED_CONTENT"));
        return blocks.OfType<TextBlock>().ToList();
    }

    private static bool ContentHasImage(IReadOnlyList<ContentBlock> blocks)
        => blocks.Any(block => block is ImageBlock || (block is ToolResultBlock result && ContentHasImage(result.Content)));
}
