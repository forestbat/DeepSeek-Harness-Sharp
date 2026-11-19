using System.Text;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Benchmarks;

/**
 * 会话夹具: 按真实事件的形状合成"事件流", 让基准跑的是同一条编码/落盘/解析路径,
 * 而不是被合成数据里的失真(比如统一短字符串)骗出好看的压缩比。
 *
 * 四档对应现场最要紧的四类会话, 取自 plans/帧粒度调粗-2026-11-19.md 的取样口径:
 *  - Ordinary: 普通长会话, 大量短 chunk + 少量消息。
 *  - ToolHeavy: 工具/批准密集, 工具调用与结果占多数。
 *  - Compacted: 经过上下文压缩, 段落式长文本 + 压缩检查点。
 *  - LargeToolOutput: 大文件工具输出, 少量但极大的工具结果。
 *
 * surface 规则(见 Surface.PlanSurfaceEvent): 消息与工具结果这几类事件必须带 SurfaceOp,
 * 伪装 system/message 时必须是 system 角色 + 插件来源, 否则 scribe 侧会直接拒绝。夹具照真实写入方做。
 */
public enum SessionShape
{
    Ordinary,
    ToolHeavy,
    Compacted,
    LargeToolOutput,
}

public static class SessionFactory
{
    /** 合成一段可复现的事件流: 同一形状同一事件数得到逐字节相同的输入。 */
    public static List<SessionEvent> Build(SessionShape shape, int eventCount)
    {
        var id = SessionId.Create("bench");
        var session = Session.Create(id, null, Header(id));
        // 事件数由参数决定: 一回合的条数远小于 10 万, 所以按回合累加直到够数, 再截断到精确值。
        for (var turn = 1; session.Seq < eventCount; turn += 1)
        {
            session.Append(new TurnStartPayload(turn));
            EmitTurnBody(session, shape, turn);
            session.Append(new TurnEndPayload(turn, new TurnEndReason.Completed()));
        }
        return [.. session.SnapshotEvents().Take(eventCount)];
    }

    private static void EmitTurnBody(Session session, SessionShape shape, int turn)
    {
        switch (shape)
        {
            case SessionShape.Ordinary:
                EmitOrdinary(session, turn);
                break;
            case SessionShape.ToolHeavy:
                EmitToolHeavy(session, turn);
                break;
            case SessionShape.Compacted:
                EmitCompacted(session, turn);
                break;
            case SessionShape.LargeToolOutput:
                EmitLargeToolOutput(session, turn);
                break;
        }
    }

    /** 普通: 短文本 chunk 流, 每 64 条升一步。 */
    private static void EmitOrdinary(Session session, int turn)
    {
        for (var step = 1; step <= 40; step += 1)
        {
            for (var chunk = 0; chunk < 64; chunk += 1)
            {
                var text = $"turn {turn} step {step}: 第 {chunk} 段行内容, 会话正文的常见长度。\n";
                session.Append(new AssistantChunkPayload(turn, step, new StreamChunk.TextDelta(chunk, text)));
            }
            Append(session, new AssistantMessagePayload(turn, step,
                Assistant(step, new TextBlock($"turn {turn} step {step} 的小结。"))));
            session.Append(new ToolCallPayload(turn, step, ToolCallId.Create($"call-{turn}-{step}"),
                "read_file", """{"path":"src/Dsh.Core/Session.cs","offset":1,"limit":200}"""));
            Append(session, new ToolResultPayload(turn, step, ToolResult(turn, step,
                new TextBlock(Repeat("文件内容行 ", 40)))));
        }
    }

    /** 工具/批准密集: 每步若干工具调用与结果, 带审批类文本。 */
    private static void EmitToolHeavy(Session session, int turn)
    {
        for (var step = 1; step <= 60; step += 1)
        {
            session.Append(new ToolCallPayload(turn, step, ToolCallId.Create($"call-{turn}-{step}"),
                "apply_patch",
                """{"patch":"*** Begin Patch\n*** Update File: src/Example.cs\n@@\n-old\n+new\n*** End Patch"}"""));
            Append(session, new ToolResultPayload(turn, step, ToolResult(turn, step,
                new TextBlock($"ok: patched src/Example.cs (turn {turn} step {step})"))));
            session.Append(new ToolCallPayload(turn, step, ToolCallId.Create($"approve-{turn}-{step}"),
                "ask_user_question",
                """{"questions":[{"id":"confirm","question":"继续?","options":[{"label":"是"},{"label":"否"}]}]}"""));
            Append(session, new ToolResultPayload(turn, step, ToolResult(turn, step,
                new TextBlock("""{"answers":{"confirm":"是"}}"""))));
            Append(session, new SystemMessagePayload(turn, step,
                SystemNote($"safety: 默认放行的决策已记录 (turn {turn} step {step})")));
        }
    }

    /** 压缩过: 段落式长文本 + 压缩检查点消息。 */
    private static void EmitCompacted(Session session, int turn)
    {
        Append(session, new UserMessagePayload(UserMessage($"turn {turn} 的历史摘要检查点: {Repeat("要点 ", 60)}")));
        for (var step = 1; step <= 61; step += 1)
        {
            var text = Repeat("压缩后的段落正文。", 8) + $"\n(turn {turn} step {step})\n";
            session.Append(new AssistantChunkPayload(turn, step, new StreamChunk.TextDelta(step, text)));
        }
        Append(session, new AssistantMessagePayload(turn, 61,
            Assistant(61, new TextBlock(Repeat("压缩后的长段正文, 一次输出较长。", 60)))));
    }

    /**
     * 大工具输出: 每回合一次 20 KB 级工具结果, 中间夹大量小 chunk。
     *
     * 比例取自现场: d 档 328k 事件对应约 5.6–7.2 MB 日志, 也就是说"大"是相对普通事件而言的,
     * 不是"每条都是几十 KB" —— 那样一回合就能产出上百 MB, 既不像真实会话, 跑起来也会直接 OOM。
     *
     * 内容必须带高熵段: 真实工具输出里混着时间戳、GUID、行号、临时路径,
     * 这些是逐行不同的。如果整段都是可以完美重复的文本, 任何帧大小都能压到极小,
     * 规模扫描就会得出"帧预算不影响体积"的假结论(真实 d 档是 +29%)。
     */
    private static void EmitLargeToolOutput(Session session, int turn)
    {
        session.Append(new ToolCallPayload(turn, 1, ToolCallId.Create($"call-{turn}-1"),
            "shell", """{"command":"dotnet test --logger trx"}"""));
        Append(session, new ToolResultPayload(turn, 1, ToolResult(turn, 1,
            new TextBlock(ToolOutput(turn, 260)))));
        for (var step = 2; step <= 61; step += 1)
        {
            var text = $"turn {turn} step {step}: 大输出之间的小段正文。\n";
            session.Append(new AssistantChunkPayload(turn, step, new StreamChunk.TextDelta(step, text)));
        }
    }

    /** 拟真工具输出: 每行都不同(行号、耗时、临时路径), 与现场 baseline 输出同形。 */
    internal static string ToolOutput(int turn, int lines)
    {
        var builder = new StringBuilder(lines * 96);
        for (var index = 0; index < lines; index += 1)
        {
            builder.Append("    at Example.Suite.Case").Append(index)
                .Append("() in C:\\repo\\src\\Case").Append(index * 7 % 97)
                .Append(".cs:line ").Append(100 + (index * 13 % 900))
                .Append(" [turn ").Append(turn)
                .Append(" elapsed ").Append(index * 31 % 997).Append("ms")
                .Append(" id ").Append((index * 2654435761L % 0xFFFFFF).ToString("x6"))
                .Append("]\r\n");
        }
        return builder.ToString();
    }

    /** surface 合格的事件必须带 Append 标记; 没有标记时 Session 会拒绝写入。 */
    private static void Append(Session session, SessionEventPayload payload)
        => session.Append(payload, new SurfaceOp.Append());

    private static string Repeat(string unit, int times)
    {
        var builder = new StringBuilder(unit.Length * times);
        for (var index = 0; index < times; index += 1)
            builder.Append(unit);
        return builder.ToString();
    }

    internal static SessionHeader Header(SessionId id) => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = id,
        Cwd = Path.GetTempPath(),
        CreatedAt = 0,
        IsSeeded = false,
    };

    internal static SessionId NewSessionId() => SessionId.Create(Guid.NewGuid().ToString("N"));

    private static UserMessage UserMessage(string text) => new()
    {
        Id = MessageFactory.NewId(),
        Content = [new TextBlock(text)],
    };

    private static AssistantMessage Assistant(int step, params ContentBlock[] blocks) => new()
    {
        Id = MessageFactory.NewId(),
        Content = blocks,
        ModelSource = new ModelMessageSource("bench", "bench"),
    };

    /** system/message 必须是 system 角色 + 插件来源, 否则 Surface 校验会拒绝。 */
    private static Message SystemNote(string text) => new()
    {
        Id = MessageFactory.NewId(),
        Role = MessageRole.System,
        Content = [new TextBlock(text)],
        Source = new PluginMessageSource("bench"),
    };

    private static ToolResultMessage ToolResult(int turn, int step, params ContentBlock[] blocks)
    {
        var callId = ToolCallId.Create($"call-{turn}-{step}");
        return new ToolResultMessage
        {
            Id = MessageFactory.NewId(),
            Content = [new ToolResultBlock(callId, blocks)],
            ToolSource = new ToolMessageSource(callId),
        };
    }
}
