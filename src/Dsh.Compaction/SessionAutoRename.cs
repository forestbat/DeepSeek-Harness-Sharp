using System.Text;
using System.Threading.Channels;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Compaction;

/**
 * 首轮结束后自动命名: 用命名模型(compaction_model ?? 会话模型)生成短标题,
 * 经 Session.Rename 触发 Renamed 事件(TUI 即时更新)并写入持久化。
 * 空标题才命名; 每个会话只尝试一次; 失败只记日志, 不打断会话。
 */
public sealed class SessionAutoRename : Service, IDisposable
{
    public const string ServiceName = "sessionAutoRename";
    public const int MaxTitleChars = 40;
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);

    private readonly HarnessOptions _options;
    private readonly Channel<Session> _queue = Channel.CreateBounded<Session>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    public SessionAutoRename(Context ctx, HarnessOptions options) : base(ctx, ServiceName)
    {
        _options = options;
        _ = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)
            ?? throw new InvalidOperationException("session auto rename requires the llm service");
        _worker = Task.Run(ProcessAsync);
        ctx.On<SessionEventNotification>(
            notification => Observe(notification.Session, notification.Event),
            new EventOptions { Global = true });
    }

    private void Observe(Session session, SessionEvent sessionEvent)
    {
        if (sessionEvent.Data is not TurnEndPayload { Reason: TurnEndReason.Completed, Turn: 1 })
            return;
        if (!string.IsNullOrWhiteSpace(session.Header.Title))
            return;
        lock (_sync)
        {
            if (!_attempted.Add(session.Id.Value))
                return;
        }
        _queue.Writer.TryWrite(session);
    }

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
                    await RenameAsync(session, _stop.Token);
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

    private async Task RenameAsync(Session session, CancellationToken signal)
    {
        if (CompactionModelSetting.Resolve(HarnessSettings.Load(_options.Home).CompactionModel, session) is not { } target)
            return;
        var title = await GenerateTitleAsync(session, target, signal);
        if (title.Length == 0)
            return;
        session.Rename(title);
        Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false)?.Rename(session.Id, title);
        Ctx.Logger.Info("%s", $"session renamed: {session.Id.Value} -> {title}");
    }

    private async Task<string> GenerateTitleAsync(Session session, (string Provider, string Model) target, CancellationToken signal)
    {
        var transcript = FirstExchange(session);
        if (transcript.Length == 0)
            return "";
        var llm = Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        var assembler = new BlockAssembler();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(signal);
        timeout.CancelAfter(RunTimeout);
        var options = new GenerateOptions
        {
            Provider = target.Provider,
            Model = target.Model,
            Messages = [MessageFactory.CreateUserText(TitlePrompt(transcript), new PluginMessageSource("dsh-compaction"))],
            Purpose = GeneratePurpose.Memory,
            SessionId = session.Id,
            Cancellation = timeout.Token,
        };
        await foreach (var chunk in llm.Stream(options).WithCancellation(timeout.Token))
            assembler.Push(chunk);
        if (assembler.Finish is FinishReason.Error or FinishReason.Aborted)
            return "";
        var text = string.Concat(assembler.Blocks().OfType<TextBlock>().Select(block => block.Text));
        return Normalize(text);
    }

    private static string FirstExchange(Session session)
    {
        var builder = new StringBuilder();
        foreach (var sessionEvent in session.SnapshotEvents())
        {
            switch (sessionEvent.Data)
            {
                case UserMessagePayload { Message.Source: UserMessageSource } user:
                    Append(builder, "USER", user.Message.Content);
                    break;
                case AssistantMessagePayload assistant:
                    Append(builder, "ASSISTANT", assistant.Message.Content);
                    break;
            }
            if (builder.Length >= 4000)
                break;
        }
        var text = builder.ToString();
        return text.Length <= 6000 ? text : text[..6000];
    }

    private static void Append(StringBuilder builder, string role, IReadOnlyList<ContentBlock> content)
    {
        var text = string.Join(' ', content.OfType<TextBlock>().Select(block => block.Text.Trim()).Where(line => line.Length > 0));
        if (text.Length > 0)
            builder.Append(role).Append(": ").AppendLine(text);
    }

    private static string TitlePrompt(string transcript)
        => "为下面的会话起一个不超过 8 个字的短标题, 只输出标题本身, 不要引号、标点或换行:\n\n" + transcript;

    private static string Normalize(string text)
    {
        var line = text.Replace("\r", "\n").Split('\n').Select(candidate => candidate.Trim()).FirstOrDefault(candidate => candidate.Length > 0) ?? "";
        line = line.Trim('"', '\'', ' ', '。', '.', '：', ':');
        return line.Length <= MaxTitleChars ? line : line[..MaxTitleChars];
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
