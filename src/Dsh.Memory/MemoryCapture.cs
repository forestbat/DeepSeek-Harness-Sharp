using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Memory;

/** 回合结束时自动捕获项目记忆:更新会话摘要,并把值得长期保留的内容整固进记忆文件。 */
public sealed class MemoryCapture : Service, IDisposable
{
    public const string ServiceName = "memoryCapture";
    public const int MaxOperationsPerRun = 16;
    public const int MaxTranscriptChars = 24000;
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    private sealed record CaptureOp(string Op, string? Section, string? Key, string? Text);

    private readonly ProjectMemory _memory;
    private readonly HarnessOptions _options;
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new(StringComparer.Ordinal);
    private readonly Channel<Session> _queue = Channel.CreateBounded<Session>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Lock _sync = new();

    public MemoryCapture(Context ctx, ProjectMemory memory, HarnessOptions options) : base(ctx, ServiceName)
    {
        _memory = memory;
        _options = options;
        _ = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)
            ?? throw new InvalidOperationException("memory capture requires the llm service");
        _worker = Task.Run(ProcessAsync);
        ctx.On<SessionEventNotification>(
            notification => Observe(notification.Session, notification.Event),
            new EventOptions { Global = true });
    }

    private void Observe(Session session, SessionEvent sessionEvent)
    {
        if (sessionEvent.Data is not TurnEndPayload { Reason: TurnEndReason.Completed })
            return;
        if (!IsEnabled())
            return;
        lock (_sync)
        {
            if (_lastRun.TryGetValue(session.Id.Value, out var last) && DateTimeOffset.UtcNow - last < MinInterval)
                return;
            _lastRun[session.Id.Value] = DateTimeOffset.UtcNow;
        }
        _queue.Writer.TryWrite(session);
    }

    private bool IsEnabled()
        => MemoryPluginConfig.CaptureEnabled(HarnessSettings.Load(_options.Home));

    private async Task ProcessAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token))
            {
                Session? session = null;
                while (reader.TryRead(out var item))
                    session = item;
                if (session is null)
                    continue;
                try
                {
                    await CaptureAsync(session, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception error)
                {
                    Ctx.Logger.Error("%s", error);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CaptureAsync(Session session, CancellationToken signal)
    {
        var llm = Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        if (ResolveTarget(session) is not { } target)
        {
            Ctx.Logger.Info("%s", "memory capture skipped: no provider/model available");
            return;
        }
        var transcript = ExtractTranscript(session, MaxTranscriptChars);
        if (transcript.Length == 0)
            return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(signal);
        timeout.CancelAfter(RunTimeout);
        var digestText = await CallAsync(llm, target, CapturePrompts.Digest(transcript), session.Id, timeout.Token);
        if (ParseDigest(digestText) is { } digest)
            await _memory.WriteDigestAsync(session.Id, digest.Topic, digest.Summary, signal);
        var index = await _memory.BuildIndexAsync(ProjectMemory.DefaultIndexBudgetBytes, signal);
        var consolidationText = await CallAsync(llm, target, CapturePrompts.Consolidation(index, transcript), session.Id, timeout.Token);
        var applied = await ApplyOpsAsync(ParseOps(consolidationText), signal);
        Ctx.Logger.Info("%s", $"memory capture: session {session.Id.Value}, {applied} operation(s) applied");
    }

    private (string Provider, string Model)? ResolveTarget(Session session)
    {
        if (session.RequestHeader()?.Config is { Provider: { Length: > 0 } provider, Model: { Length: > 0 } model })
            return (provider, model);
        return HarnessSettings.Load(_options.Home).ResolveDefaultModel();
    }

    private static async Task<string> CallAsync(
        LlmRuntime llm,
        (string Provider, string Model) target,
        string prompt,
        SessionId sessionId,
        CancellationToken signal)
    {
        var assembler = new BlockAssembler();
        var options = new GenerateOptions
        {
            Provider = target.Provider,
            Model = target.Model,
            Messages = [MessageFactory.CreateUserText(prompt, new PluginMessageSource("dsh-memory"))],
            Purpose = GeneratePurpose.Memory,
            SessionId = sessionId,
            Cancellation = signal,
        };
        await foreach (var chunk in llm.Stream(options).WithCancellation(signal))
            assembler.Push(chunk);
        switch (assembler.Finish)
        {
            case FinishReason.Error error:
                throw new InvalidOperationException($"memory capture LLM call failed: {error.Failure.Message}");
            case FinishReason.Aborted aborted:
                throw new InvalidOperationException($"memory capture LLM call aborted: {aborted.Failure.Message}");
        }
        return string.Concat(assembler.Blocks().OfType<TextBlock>().Select(block => block.Text));
    }

    private async Task<int> ApplyOpsAsync(IReadOnlyList<CaptureOp> ops, CancellationToken signal)
    {
        var applied = 0;
        foreach (var op in ops.Take(MaxOperationsPerRun))
        {
            try
            {
                switch (op)
                {
                    case { Op: "upsert", Key: { Length: > 0 } key, Text: { Length: > 0 } text }:
                        await _memory.RememberAsync(key, text, op.Section, "capture", signal);
                        applied++;
                        break;
                    case { Op: "remove", Key: { Length: > 0 } key }:
                        await _memory.ForgetAsync(key, "capture", signal);
                        applied++;
                        break;
                }
            }
            catch (InvalidOperationException)
            {
                // 单条失败不拖垮整批。
            }
        }
        return applied;
    }

    private static string ExtractTranscript(Session session, int maxChars)
    {
        var builder = new StringBuilder();
        foreach (var sessionEvent in session.SnapshotEvents())
        {
            switch (sessionEvent.Data)
            {
                case UserMessagePayload { Message.Source: UserMessageSource } user:
                    AppendBlocks(builder, "USER", user.Message.Content);
                    break;
                case AssistantMessagePayload assistant:
                    AppendBlocks(builder, "ASSISTANT", assistant.Message.Content);
                    break;
            }
        }
        var text = builder.ToString();
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    private static void AppendBlocks(StringBuilder builder, string role, IReadOnlyList<ContentBlock> content)
    {
        var text = string.Join('\n', content.OfType<TextBlock>().Select(block => block.Text.Trim()).Where(line => line.Length > 0));
        if (text.Length > 0)
            builder.Append(role).Append(": ").AppendLine(text);
    }

    private static (string Topic, string Summary)? ParseDigest(string text)
    {
        string? topic = null;
        string? summary = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("TOPIC:", StringComparison.OrdinalIgnoreCase))
                topic = line["TOPIC:".Length..].Trim();
            else if (line.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
                summary = line["SUMMARY:".Length..].Trim();
        }
        return topic is { Length: > 0 } && summary is { Length: > 0 } ? (topic, summary) : null;
    }

    private static IReadOnlyList<CaptureOp> ParseOps(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return [];
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
                return [];
            var ops = new List<CaptureOp>();
            foreach (var element in operations.EnumerateArray())
            {
                if (!element.TryGetProperty("op", out var opElement) || opElement.GetString() is not { Length: > 0 } op)
                    continue;
                ops.Add(new CaptureOp(
                    op,
                    element.TryGetProperty("section", out var section) ? section.GetString() : null,
                    element.TryGetProperty("key", out var key) ? key.GetString() : null,
                    element.TryGetProperty("text", out var opText) ? opText.GetString() : null));
            }
            return ops;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}
