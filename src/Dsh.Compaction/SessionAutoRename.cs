using System.Text;
using System.Threading.Channels;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Compaction;

/**
 * 会话自动命名: 用命名模型(compaction_model ?? 会话模型)生成短标题,
 * 经 Session.Rename 触发 Renamed 事件(TUI/GUI 即时更新)并写入持久化。
 * 触发时机: 首条用户消息 / 首条 LLM 消息 / 回合结束, 谁先到算谁。
 * 持久化层的"首行兜底"标题会被模型标题覆盖; 手工命名(/rename)不覆盖。
 * 每个会话只尝试一次; 失败保留兜底标题, 不打断会话。
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

    /**
     * 触发时机: 用户的第一条消息、LLM 的第一条消息、或任意回合结束, 谁先到算谁。
     * 只对空标题会话尝试一次; 若此刻会话还没有请求头(拿不到模型)则不标记, 等后续事件再试。
     */
    private void Observe(Session session, SessionEvent sessionEvent)
    {
        if (sessionEvent.Data is not (
            UserMessagePayload { Message.Source: UserMessageSource }
            or AssistantMessagePayload
            or TurnEndPayload))
            return;
        if (HasFinalTitle(session))
            return;
        lock (_sync)
        {
            if (_attempted.Contains(session.Id.Value))
                return;
        }
        _queue.Writer.TryWrite(session);
    }

    /** 已有标题且不是持久化层"首行兜底"时视为已定名(手工 /rename 等), 不覆盖。 */
    private static bool HasFinalTitle(Session session)
    {
        var title = session.Header.Title;
        if (string.IsNullOrWhiteSpace(title))
            return false;
        return !string.Equals(title, ProvisionalTitle(session), StringComparison.Ordinal);
    }

    private static string? ProvisionalTitle(Session session)
    {
        foreach (var sessionEvent in session.SnapshotEvents())
        {
            if (sessionEvent.Data is UserMessagePayload user)
                return SessionTitleFromUserMessage.Derive(user.Message);
        }
        return null;
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
        // 太早(会话还没有请求头, 拿不到模型)就先不占用"只尝试一次"的名额, 等后续事件再试。
        if (CompactionModelSetting.Resolve(HarnessSettings.Load(_options.Home).CompactionModel, session) is not { } target)
            return;
        lock (_sync)
        {
            if (!_attempted.Add(session.Id.Value))
                return;
        }
        var title = await GenerateTitleAsync(session, target, signal);
        if (title.Length == 0)
            return;
        if (HasFinalTitle(session))
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
